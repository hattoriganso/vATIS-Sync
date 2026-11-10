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
    /// Leave the ATIS as it is, and do not ask about this station again until the sync is restarted. Nothing is ever applied
    /// without the controller choosing Apply, so a runway change cannot slip through unseen.
    /// </summary>
    DisregardForSession,
}

/// <param name="Station">Station key, e.g. "KPHX" or "KATL_A".</param>
/// <param name="FlowChanged">True when the runways or approaches differ.</param>
public sealed record ApprovalRequest(
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

/// <summary>Asks the controller whether a pending update may go out. Implemented by the UI.</summary>
public interface IUpdateApprover
{
    /// <summary>Completes when the controller decides. Must honour cancellation (the sync is being stopped).</summary>
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}
