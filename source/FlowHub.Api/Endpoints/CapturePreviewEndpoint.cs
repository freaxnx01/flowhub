using FluentValidation;
using FlowHub.Api.Requests;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

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

    private static async Task<Results<Ok<CapturePreviewResponse>, ValidationProblem>> PreviewAsync(
        CreateCaptureRequest request,
        IValidator<CreateCaptureRequest> validator,
        IClassifier classifier,
        IVikunjaProjectCatalog projects,
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

        var (projectId, resolved) = await ResolveProjectAsync(projects, result.VikunjaProject, ct);

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
    /// Read-only catalogue lookup. A preview must never fail because Vikunja is down, so
    /// an unreachable catalogue reports "unresolved" rather than throwing. The catch is
    /// deliberately broad — the catalogue is an HTTP call behind an interface, its
    /// failure modes are not enumerable from here, and the spec (D3) requires a preview
    /// to survive any of them. <see cref="OperationCanceledException"/> is excluded so
    /// cancellation still propagates.
    /// </summary>
    private static async Task<(int? Id, bool Resolved)> ResolveProjectAsync(
        IVikunjaProjectCatalog projects, string? name, CancellationToken ct)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, false);
        }
    }
}
