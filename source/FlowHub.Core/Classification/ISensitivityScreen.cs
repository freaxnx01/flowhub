namespace FlowHub.Core.Classification;

/// <summary>Three-way sensitivity verdict. Only <see cref="Safe"/> may be routed.</summary>
public enum Sensitivity
{
    /// <summary>Must not leave the machine.</summary>
    Sensitive,

    /// <summary>Could not be decided — parks, per the asymmetry in issue #93.</summary>
    Unsure,

    /// <summary>Safe to classify and route.</summary>
    Safe,
}

/// <summary>
/// Outcome of <see cref="ISensitivityScreen.ScreenAsync"/>. <paramref name="Reason"/> is
/// empty for <see cref="Sensitivity.Safe"/> and otherwise names why the capture parked —
/// it is written to the capture's FailureReason so a park can be audited without reading
/// the content back out.
/// </summary>
public sealed record SensitivityVerdict(
    Sensitivity Verdict,
    string Reason,
    ClassifierTrace? Trace = null);

/// <summary>
/// Driving port for the sensitivity pre-pass (issue #93). Runs before classification.
/// </summary>
/// <remarks>
/// Implementations MUST NOT throw for any provider, parsing, or timeout failure — they
/// return <see cref="Sensitivity.Unsure"/> instead. A leaked exception reaches
/// LifecycleFaultObserver, which marks the capture Unhandled — a stage the retry endpoint
/// accepts — putting an unscreened capture one button press from being routed.
/// Cancellation originating from the caller's own token is the sole exception and
/// propagates normally: that is shutdown, not a screen failure.
/// </remarks>
public interface ISensitivityScreen
{
    Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken);
}
