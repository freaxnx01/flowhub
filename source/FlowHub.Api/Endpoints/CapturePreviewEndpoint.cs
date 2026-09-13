using FluentValidation;
using FlowHub.Api.Requests;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace FlowHub.Api.Endpoints;

internal static class CapturePreviewEndpoint
{
    public static void MapCapturePreviewEndpoint(this RouteGroupBuilder captures)
    {
        captures.MapPost("/preview", PreviewAsync)
            .WithName("PreviewCapture")
            .Produces<CapturePreviewResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem();
    }

    internal static async Task<Results<Ok<CapturePreviewResponse>, ValidationProblem>> PreviewAsync(
        CreateCaptureRequest request,
        IValidator<CreateCaptureRequest> validator,
        IClassifier classifier,
        IVikunjaProjectCatalog projects,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            var errors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            return TypedResults.ValidationProblem(errors);
        }

        // Classify only. No ICaptureService, no bus, no persistence — see D2.
        var result = await classifier.ClassifyAsync(request.Content, ct);

        var logger = loggerFactory.CreateLogger(typeof(CapturePreviewEndpoint).FullName!);
        var (projectId, resolved) = await ResolveProjectAsync(projects, result.VikunjaProject, logger, ct);

        return TypedResults.Ok(new CapturePreviewResponse(
            result.MatchedSkill,
            result.Title,
            result.Tags,
            result.Entities,
            result.VikunjaProject,
            projectId,
            resolved,
            result.BridgeAlias,
            result.BridgeTarget,
            result.BridgeAction,
            result.BridgeBody,
            result.UnknownShorthand,
            result.Trace));
    }

    /// <summary>
    /// Read-only catalogue lookup. An unreachable Vikunja reports "unresolved" rather
    /// than failing the preview.
    ///
    /// Only <see cref="HttpRequestException"/> is caught, and it is logged. The real
    /// <c>VikunjaProjectCatalog</c> already handles unreachability internally — keeping
    /// its last good snapshot and logging — so a broader catch here would mostly be
    /// hiding genuine bugs in resolution behind a silent "unresolved", with no
    /// diagnostic trail. Anything else propagates and becomes a 500, which is correct.
    /// </summary>
    private static async Task<(int? Id, bool Resolved)> ResolveProjectAsync(
        IVikunjaProjectCatalog projects, string? name, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, false);
        }

        try
        {
            var catalog = await projects.GetAsync(ct);
            return catalog.TryGetValue(name, out var id) ? (id, true) : (null, false);
        }
        catch (HttpRequestException ex)
        {
            LogCatalogueUnavailable(logger, ex.GetType().Name, ex);
            return (null, false);
        }
    }

    // Hand-written delegate rather than [LoggerMessage]: the source generator's output
    // in this assembly stops coverlet instrumenting FlowHub.Api entirely on the CI
    // runner — the whole assembly reports no coverage data. Same allocation-free
    // behaviour, satisfies CA1848, no generator involved.
    private static readonly Action<ILogger, string, Exception?> LogCatalogueUnavailable =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1400, nameof(LogCatalogueUnavailable)),
            "Vikunja catalogue unavailable during preview ({Reason}); project reported as unresolved");
}
