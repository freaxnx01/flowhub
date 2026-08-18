using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.Skills.Bridge;

/// <summary>
/// Routes a Bridge-classified capture to the <c>bridge</c> REST API: creates an issue
/// (<c>POST /api/capture/issue</c>) or appends to the repo's ideas.md
/// (<c>POST /api/capture/idea</c>), with bridge resolving the alias internally. Failure is
/// signalled by throwing, per the ISkillIntegration convention.
/// </summary>
public sealed class BridgeSkillIntegration : ISkillIntegration
{
    private const int FallbackTitleMaxLength = 120;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly BridgeOptions _options;
    private readonly ILogger<BridgeSkillIntegration> _log;

    public BridgeSkillIntegration(
        HttpClient http,
        IOptions<BridgeOptions> options,
        ILogger<BridgeSkillIntegration> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public string Name => "Bridge";

    public async Task<SkillResult> HandleAsync(Capture capture, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capture.BridgeAlias))
        {
            throw new InvalidOperationException($"Capture {capture.Id} routed to Bridge without an alias.");
        }

        return capture.BridgeAction switch
        {
            BridgeAction.Issue => await SendAsync("/api/capture/issue", IssueBody(capture), cancellationToken),
            BridgeAction.Idea => await SendAsync("/api/capture/idea", IdeaBody(capture), cancellationToken),
            _ => throw new InvalidOperationException(
                $"Capture {capture.Id} routed to Bridge with undetermined action '{capture.BridgeAction}'."),
        };
    }

    private static object IssueBody(Capture capture) => new
    {
        alias = capture.BridgeAlias,
        title = !string.IsNullOrWhiteSpace(capture.Title)
            ? capture.Title!.Trim()
            : Truncate(capture.BridgeBody ?? capture.Content, FallbackTitleMaxLength),
        body = capture.BridgeBody ?? string.Empty,
    };

    private static object IdeaBody(Capture capture) => new
    {
        alias = capture.BridgeAlias,
        text = !string.IsNullOrWhiteSpace(capture.BridgeBody) ? capture.BridgeBody!.Trim() : capture.Content.Trim(),
    };

    private async Task<SkillResult> SendAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<BridgeCaptureResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Bridge response body was empty.");

        var reference = payload.Url
            ?? throw new InvalidOperationException("Bridge response did not include a 'url' field.");

        return new SkillResult(Success: true, ExternalRef: reference);
    }

    private static string Truncate(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private sealed record BridgeCaptureResponse(string? Url, long? Number);
}
