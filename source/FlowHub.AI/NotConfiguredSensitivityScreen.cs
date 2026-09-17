using FlowHub.Core.Classification;

namespace FlowHub.AI;

/// <summary>
/// Used when no AI provider is configured. Without a model there is no way to judge
/// sensitivity, so every capture parks (issue #93). This makes a misconfigured
/// deployment obviously broken rather than quietly unsafe. There is intentionally no
/// switch to disable the screen — that switch would be the vulnerability.
/// </summary>
internal sealed class NotConfiguredSensitivityScreen : ISensitivityScreen
{
    public Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken) =>
        Task.FromResult(new SensitivityVerdict(
            Sensitivity.Unsure,
            "sensitivity screen not configured — no AI provider"));
}
