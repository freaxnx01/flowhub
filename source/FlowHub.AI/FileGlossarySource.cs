using System.Text.Json;
using System.Text.Json.Serialization;
using FlowHub.Core.Classification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.AI;

/// <summary>
/// Reads the glossary from a file mounted into the container. The vault-backed
/// implementation (#84) replaces this without touching the classifier.
/// </summary>
internal sealed partial class FileGlossarySource : IGlossary, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlossaryOptions _options;
    private readonly ILogger<FileGlossarySource> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Snapshot and its timestamp live in one immutable object behind a single reference,
    // so the lock-free fast path is one atomic read. Two separate fields could not be
    // read consistently outside the gate — DateTimeOffset is a multi-field struct, so a
    // torn read of the timestamp was possible while ReloadAsync wrote it under the lock.
    private CacheEntry? _cache;

    public FileGlossarySource(
        IOptions<GlossaryOptions> options,
        ILogger<FileGlossarySource> log,
        TimeProvider time)
    {
        _options = options.Value;
        _log = log;
        _time = time;
    }

    public async Task<GlossarySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Path))
        {
            return GlossarySnapshot.Empty;
        }

        var cached = Volatile.Read(ref _cache);
        if (IsFresh(cached, _time.GetUtcNow()))
        {
            // IsFresh returns true only for a non-null entry — invariant documented per house rule.
            return cached!.Snapshot;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read the clock inside the lock: a waiter that blocked through
            // another thread's successful read must see the fresh _readAt.
            var now = _time.GetUtcNow();
            cached = Volatile.Read(ref _cache);
            if (IsFresh(cached, now))
            {
                return cached!.Snapshot;
            }

            return await ReloadAsync(now, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(CacheEntry? entry, DateTimeOffset now) =>
        entry is not null && now - entry.ReadAt < _options.RefreshInterval;

    private async Task<GlossarySnapshot> ReloadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // GetAsync guards on Path being null/whitespace before this call — safe to forgive.
        try
        {
            var json = await File.ReadAllTextAsync(_options.Path!, cancellationToken);
            var dto = JsonSerializer.Deserialize<GlossaryDto>(json, JsonOptions)
                ?? throw new JsonException("glossary document was null");

            var snapshot = new GlossarySnapshot(
                dto.People ?? new Dictionary<string, string>(),
                dto.Acronyms ?? new Dictionary<string, string>(),
                dto.Prefixes ?? new Dictionary<string, string>());
            Volatile.Write(ref _cache, new CacheEntry(snapshot, now));
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var previous = Volatile.Read(ref _cache);
            if (previous is not null)
            {
                LogReloadFailedKeepingCache(ex.GetType().Name);
                Volatile.Write(ref _cache, previous with { ReadAt = now });
                return previous.Snapshot;
            }

            LogFirstReadFailed(ex.GetType().Name);
            Volatile.Write(ref _cache, new CacheEntry(GlossarySnapshot.Empty, now));
            return GlossarySnapshot.Empty;
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record CacheEntry(GlossarySnapshot Snapshot, DateTimeOffset ReadAt);

    private sealed record GlossaryDto(
        [property: JsonPropertyName("people")] Dictionary<string, string>? People,
        [property: JsonPropertyName("acronyms")] Dictionary<string, string>? Acronyms,
        [property: JsonPropertyName("prefixes")] Dictionary<string, string>? Prefixes);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning,
        Message = "Glossary reload failed ({Reason}); keeping the previous snapshot")]
    private partial void LogReloadFailedKeepingCache(string reason);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning,
        Message = "Glossary could not be read ({Reason}); continuing without a glossary")]
    private partial void LogFirstReadFailed(string reason);
}
