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

    private GlossarySnapshot? _cache;
    private DateTimeOffset _readAt;

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

        // IsFresh returns true only when _cache is non-null, so the non-null forgiving
        // operator is safe here — this comment documents the invariant per house rule.
        if (IsFresh(_time.GetUtcNow()))
        {
            return _cache!;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read the clock inside the lock: a waiter that blocked through
            // another thread's successful read must see the fresh _readAt.
            var now = _time.GetUtcNow();
            if (IsFresh(now))
            {
                return _cache!;
            }

            return await ReloadAsync(now, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(DateTimeOffset now) =>
        _cache is not null && now - _readAt < _options.RefreshInterval;

    private async Task<GlossarySnapshot> ReloadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // GetAsync guards on Path being null/whitespace before this call — safe to forgive.
        try
        {
            var json = await File.ReadAllTextAsync(_options.Path!, cancellationToken);
            var dto = JsonSerializer.Deserialize<GlossaryDto>(json, JsonOptions)
                ?? throw new JsonException("glossary document was null");

            _cache = new GlossarySnapshot(
                dto.People ?? new Dictionary<string, string>(),
                dto.Acronyms ?? new Dictionary<string, string>(),
                dto.Prefixes ?? new Dictionary<string, string>());
            _readAt = now;
            return _cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _readAt = now;
            if (_cache is not null)
            {
                LogReloadFailedKeepingCache(ex.GetType().Name);
                return _cache;
            }

            LogFirstReadFailed(ex.GetType().Name);
            _cache = GlossarySnapshot.Empty;
            return _cache;
        }
    }

    public void Dispose() => _gate.Dispose();

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
