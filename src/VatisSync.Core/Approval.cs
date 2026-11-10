using System.Text.Json.Serialization;

namespace VatisSync.Core;

/// <summary>When the controller must approve a change before it reaches a connected ATIS.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalMode
{
    /// <summary>Apply every refresh without asking.</summary>
    Never,

    /// <summary>Ask only when the runways or approach types in the text change. The default.</summary>
    RunwayChanges,

    /// <summary>Ask whenever the Airport Conditions text would change.</summary>
    Always,
}

public enum ApprovalDecision
{
    /// <summary>Send the update.</summary>
    Apply,

    /// <summary>Leave the ATIS as it is. Not asked again until the new text changes.</summary>
    Disregard,

    /// <summary>
    /// Leave the ATIS as it is, and do not ask about this airport again until the sync is restarted. Nothing is ever applied
    /// without the controller choosing Apply, so a runway change cannot slip through unseen.
    /// </summary>
    DisregardForSession,
}

/// <summary>What a refresh would change on one connected station.</summary>
/// <param name="Station">Station key, e.g. "KPHX" or "KATL_A".</param>
/// <param name="FlowChanged">True when the runways or approaches differ.</param>
public sealed record StationChange(
    string Station,
    string CurrentConditions,
    string NewConditions,
    bool FlowChanged,
    FlowSignature CurrentFlow,
    FlowSignature NewFlow)
{
    public string Summary => FlowChanged
        ? $"The runway configuration changed: {CurrentFlow.Describe()} -> {NewFlow.Describe()}."
        : "The ATIS text changed.";
}

/// <summary>
/// One question per airport: an airport's stations are synced together or not at all, so the answer covers every
/// station of the airport and the question lists every connected station that would change.
/// </summary>
/// <param name="Airport">The four-letter airport code, e.g. "KPHX".</param>
public sealed record ApprovalRequest(string Airport, IReadOnlyList<StationChange> Changes)
{
    /// <summary>True when the runways or approaches differ on any of the stations.</summary>
    public bool FlowChanged => Changes.Any(c => c.FlowChanged);

    public string Summary => Changes.Count == 1
        ? Changes[0].Summary
        : string.Join(" ", Changes.Select(c => $"{c.Station}: {c.Summary}"));
}

/// <summary>Asks the controller whether a pending update may go out. Implemented by the UI.</summary>
public interface IUpdateApprover
{
    /// <summary>Completes when the controller decides. Must honour cancellation (the sync is being stopped).</summary>
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}
