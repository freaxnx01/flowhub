using System.Text.Json.Serialization;
using FlowHub.Core.Classification;

namespace FlowHub.Api.Requests;

/// <summary>
/// What a capture's classification *would* decide, with nothing written. Every field is
/// emitted even when null so a survey can diff rows without special-casing absence.
/// </summary>
/// <remarks>
/// <para>
/// <c>VikunjaProjectId</c> is the project id that would actually be used, resolved
/// against the live catalogue. Null when the classifier named no project, when the name
/// does not match, or when the catalogue is unreachable.
/// </para>
/// <para>
/// <c>VikunjaProjectResolved: false</c> means the classifier named a project that does
/// not exist and the task would silently land in the fallback project instead.
/// </para>
/// </remarks>
public sealed record CapturePreviewResponse(
    string MatchedSkill,
    string? Title,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string>? Entities,
    string? VikunjaProject,
    int? VikunjaProjectId,
    bool VikunjaProjectResolved,
    string? BridgeAlias,
    string? BridgeTarget,
    BridgeAction BridgeAction,
    string? BridgeBody,
    IReadOnlyList<string>? UnknownShorthand,
    ClassifierTrace? Trace,
    // Sensitivity.Sensitive is the zero value on purpose — an uninitialised verdict
    // must fail closed. JsonRequired covers the opposite hazard: a client omitting the
    // field would otherwise receive that default by accident and be unable to tell it
    // apart from a real judgement.
    [property: JsonRequired] Sensitivity Sensitivity,
    string? SensitivityReason);
