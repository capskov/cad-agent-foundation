#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Bimwright.Dwg.Server.Execution;

[JsonConverter(typeof(JsonStringEnumConverter<JobPhase>))]
public enum JobPhase
{
    Queued,
    Running,
    CancellationRequested,
    Completed,
    Failed,
    Cancelled,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<EffectState>))]
public enum EffectState
{
    None,
    Unknown,
    Committed
}

[JsonConverter(typeof(JsonStringEnumConverter<VerificationState>))]
public enum VerificationState
{
    NotChecked,
    Passed,
    Failed,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservationState>))]
public enum ObservationState
{
    None,
    PluginSnapshot,
    AuthoritativePluginSnapshot,
    IndependentReadback,
    Recovery
}

[JsonConverter(typeof(JsonStringEnumConverter<ReserveDisposition>))]
public enum ReserveDisposition
{
    Created,
    Existing,
    RequestIdConflict,
    FenceConflict
}

[JsonConverter(typeof(JsonStringEnumConverter<AdvanceDisposition>))]
public enum AdvanceDisposition
{
    Applied,
    Duplicate,
    StaleSequence,
    RejectedTransition,
    NotFound
}

[JsonConverter(typeof(JsonStringEnumConverter<VerificationDisposition>))]
public enum VerificationDisposition
{
    Applied,
    Duplicate,
    StaleRevision,
    RejectedState,
    NotFound
}

/// <summary>
/// One logical MCP operation. Parameters and target are JSON values; the ledger canonicalizes them
/// before computing the global request-id digest.
/// </summary>
public sealed record ReserveRequest(
    string RequestId,
    string ContextId,
    string ToolName,
    string CanonicalParametersJson,
    string CompleteTargetJson,
    bool IsWrite,
    string ProviderGeneration,
    IReadOnlyCollection<string>? RiskKeys = null,
    bool GlobalFence = false);

public sealed record AdvanceRequest(
    string RequestId,
    string ProviderGeneration,
    long PluginSequence,
    JobPhase Phase,
    EffectState Effects,
    VerificationState Verification,
    ObservationState Observation,
    string? ResultJson = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    DateTimeOffset? ObservedAt = null);

/// <summary>
/// Independent verification is recorded on its own monotonic revision stream. It cannot alter
/// the plugin phase, effects, or plugin sequence.
/// </summary>
public sealed record VerificationRequest(
    string RequestId,
    string ContextId,
    string CompleteTargetJson,
    string ProviderGeneration,
    long ReadbackRevision,
    VerificationState Verification,
    string? ResultJson = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    DateTimeOffset? ObservedAt = null,
    long? ExpectedPluginSequence = null);

/// <summary>
/// Releases the Server-side risk fence only after the plugin has durably
/// finalized the same staged readback and advanced its sequence exactly once.
/// </summary>
public sealed record FinalizeVerificationRequest(
    string RequestId,
    string ContextId,
    string CompleteTargetJson,
    string ProviderGeneration,
    long StagedPluginSequence,
    long FinalizedPluginSequence,
    long ReadbackRevision,
    string ReadbackDigest,
    string CommittedResultDigest,
    DateTimeOffset? ObservedAt = null);

public sealed record LedgerJob(
    string RequestId,
    string ContextId,
    string ToolName,
    string CanonicalParametersJson,
    string CompleteTargetJson,
    string PayloadDigest,
    bool IsWrite,
    JobPhase Phase,
    EffectState Effects,
    VerificationState Verification,
    ObservationState Observation,
    long? PluginSequence,
    string? ResultJson,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ObservedAt,
    bool WriteFenceReleased,
    IReadOnlyList<string> HeldRiskKeys,
    JobPhase? PhaseBeforeUnknown,
    string ProviderGeneration,
    bool GlobalFence = false,
    long? ReadbackRevision = null,
    string? VerificationResultJson = null,
    string? VerificationErrorCode = null,
    string? VerificationErrorMessage = null,
    bool ServerUnavailable = false,
    string? PluginSnapshotDigest = null,
    string? ReadbackDigest = null,
    long? VerificationStagedPluginSequence = null);

public sealed record ReserveResult(
    ReserveDisposition Disposition,
    LedgerJob? Job,
    IReadOnlyList<string> ConflictingRiskKeys);

public sealed record AdvanceResult(
    AdvanceDisposition Disposition,
    LedgerJob? Job,
    string? Reason = null);

public sealed record VerificationResult(
    VerificationDisposition Disposition,
    LedgerJob? Job,
    string? Reason = null);
