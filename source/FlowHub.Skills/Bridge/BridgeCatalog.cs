using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.Skills.Bridge;

/// <summary>
/// Fetches the repo aliases from bridge's <c>GET /api/repos</c> catalog and caches the
/// lowercased set for <see cref="BridgeOptions.CatalogTtl"/>. Resilient: on a fetch failure
/// it returns the last-known set (or empty) rather than throwing, so classification never
/// breaks on a bridge outage.
/// </summary>
public sealed partial class BridgeCatalog : IBridgeCatalog, IDisposable
{
    private readonly HttpClient _http;
    private readonly BridgeOptions _options;
    private readonly ILogger<BridgeCatalog> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlySet<string>? _cache;
    private IReadOnlyList<BridgeRepo>? _repoCache;
    private DateTimeOffset _fetchedAt;

    public BridgeCatalog(
        HttpClient http,
        IOptions<BridgeOptions> options,
        ILogger<BridgeCatalog> log,
        TimeProvider time)
    {
        _http = http;
        _options = options.Value;
        _log = log;
        _time = time;
    }

    public async Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_cache is not null && now - _fetchedAt < _options.CatalogTtl)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = _time.GetUtcNow();
            if (_cache is not null && now - _fetchedAt < _options.CatalogTtl)
            {
                return _cache;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/repos");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
                using var response = await _http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var repos = await response.Content.ReadFromJsonAsync<BridgeRepoDto[]>(cancellationToken)
                    ?? Array.Empty<BridgeRepoDto>();

                _cache = BuildAliasSet(repos);
                _repoCache = BuildRepoList(repos);
                _fetchedAt = now;
                return _cache;
            }
            catch (Exception ex)
            {
                if (_cache is not null)
                {
                    LogRefreshFailedKeepingCache(ex.GetType().Name);
                    return _cache;
                }

                LogFirstFetchFailed(ex.GetType().Name);
                var empty = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
                _cache = empty;
                _repoCache = Array.Empty<BridgeRepo>();
                _fetchedAt = now;
                return empty;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BridgeRepo>> GetReposAsync(CancellationToken cancellationToken)
    {
        await GetAliasesAsync(cancellationToken);
        return _repoCache ?? Array.Empty<BridgeRepo>();
    }

    private static HashSet<string> BuildAliasSet(BridgeRepoDto[] repos)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repo in repos)
        {
            if (!string.IsNullOrWhiteSpace(repo.Alias))
            {
                set.Add(repo.Alias.Trim().ToLowerInvariant());
            }
        }
        return set;
    }

    private static List<BridgeRepo> BuildRepoList(BridgeRepoDto[] repos) =>
        repos
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new BridgeRepo(
                r.Name!.Trim(),
                string.IsNullOrWhiteSpace(r.Alias) ? null : r.Alias.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(r.Desc) ? null : r.Desc.Trim(),
                (IReadOnlyList<string>)(r.Topics ?? Array.Empty<string>()),
                r.Last_Used))
            .ToList();

    public void Dispose() => _gate.Dispose();

    private sealed record BridgeRepoDto(
        string? Name,
        string? Alias,
        string? Desc,
        string[]? Topics,
        DateTimeOffset? Last_Used);

    [LoggerMessage(EventId = 3050, Level = LogLevel.Warning,
        Message = "Bridge catalog first fetch failed (reason={Reason}); no aliases available")]
    private partial void LogFirstFetchFailed(string reason);

    [LoggerMessage(EventId = 3051, Level = LogLevel.Warning,
        Message = "Bridge catalog refresh failed (reason={Reason}); keeping last-known aliases")]
    private partial void LogRefreshFailedKeepingCache(string reason);
}
