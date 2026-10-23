using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server.Execution
{
    public sealed partial class BoundPluginExecutionGateway
    {
        private string D1ReadbackValidatorVersion => ActiveWriteValidator;

        /// <summary>Runs the fixed D1 candidate inspection against the exact configured drawing binding.</summary>
        public Task<string> SubmitD1PreflightAsync(ToolInvocationEnvelope invocation)
        {
            if (invocation == null || invocation.ToolName != Road08R01D1WriteContract.PreflightMcpToolName
                || invocation.BusinessArguments == null || invocation.BusinessArguments.Count != 0
                || !IsValidIdentifier(invocation.ContextId) || !IsValidIdentifier(invocation.RequestId))
                return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "the fixed D1 preflight invocation is invalid"));
            if ((_d1PreflightPilotScope == null && ActiveFixedWriteScope == null) || _ledger == null
                || !ServerState.IsToolRegistered(Road08R01D1WriteContract.PreflightMcpToolName))
                return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "the isolated D1 preflight pilot is not enabled"));

            BoundPluginContext bound = null;
            BoundPluginOwnerIdentity identity = null;
            string bindError = null;
            if (!_contexts.TryGetContext(invocation.ContextId, out bound)
                || !TryValidateD1PreflightBoundContext(bound, out identity, out bindError))
                return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"), false, null,
                    bindError ?? "unknown or out-of-scope context_id; bind the configured AutoCAD 2026 test copy first"));

            if (identity == null)
                return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "the bound D1 context owner identity is invalid"));

            // The generic governed-read path supplies the same durable request
            // ownership, plugin queue sequencing, and immediate snapshot flow.
            return SubmitGovernedReadAsync(invocation, new JObject(), new JObject());
        }

        /// <summary>Submits the one fixed D1 write only inside its configured AutoCAD 2026 pipe scope.</summary>
        private async Task<string> SubmitFixedWriteAsync(ToolInvocationEnvelope invocation, string planParameters = null)
        {
            if (invocation == null || invocation.ToolName != ActiveWriteMcpName
                || invocation.BusinessArguments == null || (IsFoundationWriteProfile ? planParameters == null || !FoundationDrawingPlan.IsCanonicalValid(planParameters) : invocation.BusinessArguments.Count != 0)
                || !IsValidIdentifier(invocation.ContextId) || !IsValidIdentifier(invocation.RequestId))
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "the fixed D1 write invocation is invalid");
            if (ActiveFixedWriteScope == null || _ledger == null
                || !ServerState.IsToolRegistered(ActiveWriteMcpName))
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "the isolated D1 write pilot is not enabled");
            BoundPluginContext bound = null;
            BoundPluginOwnerIdentity identity = null;
            string bindError = null;
            if (!_contexts.TryGetContext(invocation.ContextId, out bound)
                || !TryValidateD1BoundContext(bound, out identity, out bindError))
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    bindError ?? "unknown or out-of-scope context_id; bind the configured AutoCAD 2026 test copy first");

            string submittedParameters = IsFoundationWriteProfile ? planParameters : ActiveWriteParameters;
            string canonicalTarget;
            try { canonicalTarget = RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "completeTargetJson"); }
            catch { return Envelope(Guid.NewGuid().ToString("N"), false, null, "the bound drawing target is invalid"); }

            ReserveResult ledgerReservation;
            try
            {
                ledgerReservation = _ledger.Reserve(new ReserveRequest(
                    invocation.RequestId, invocation.ContextId, ActiveWriteMcpName,
                    submittedParameters, canonicalTarget,
                    IsWrite: true, ProviderGeneration: identity.ProviderGeneration,
                    RiskKeys: new[] { ActiveFixedWriteScope.DrawingRiskKey }, GlobalFence: false));
            }
            catch
            {
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "the D1 write fence could not be durably reserved");
            }

            if (ledgerReservation.Disposition == ReserveDisposition.Existing)
            {
                if (!_contexts.TryGetOwnedRequest(invocation.RequestId, invocation.ContextId, requireConfirmed: false,
                        out BoundPluginContext existingBound, out BoundPluginRequestOwnership existingOwner)
                    || !ReferenceEquals(existingBound, bound)
                    || !MatchesD1Ownership(existingOwner, bound, invocation.RequestId, identity))
                    return Envelope(Guid.NewGuid().ToString("N"), false, null,
                        "this request ID already has an immutable D1 reservation; its owner cannot be recovered for submission");
                return await GetD1WriteJobAsync(invocation.ContextId, invocation.RequestId, 0).ConfigureAwait(false);
            }
            if (ledgerReservation.Disposition != ReserveDisposition.Created || ledgerReservation.Job == null
                || !MatchesD1LedgerReservation(ledgerReservation.Job, bound, invocation.RequestId, identity))
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "the D1 request or drawing risk fence conflicts with an existing operation");

            if (!ReserveFixedWriteOwnership(invocation.RequestId, invocation.ContextId,
                    submittedParameters, ActiveFixedWriteScope.DrawingRiskKey,
                    out BoundPluginContext reservedBound, out RequestSubmissionLease reservation,
                    out string ownerError, allowCreate: true)
                || !ReferenceEquals(reservedBound, bound))
            {
                TryMarkD1LedgerUnavailable(invocation.RequestId, identity.ProviderGeneration, null, bound);
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    ownerError ?? "D1 request ownership could not be durably pinned; the drawing risk fence remains held");
            }

            bool catalogReady = await EnsurePluginCatalogAsync(bound.Discovery).ConfigureAwait(false);
            if (!catalogReady)
            {
                CompleteD1ReservationUnknown(reservation, invocation.RequestId,
                    identity.ProviderGeneration, CaptureD1LedgerSequence(invocation.RequestId, bound), bound);
                return D1Unknown(invocation.RequestId, invocation.ContextId,
                    "the plugin catalog could not be confirmed; the original D1 request remains fenced for reconciliation");
            }

            McpResponse submitted;
            try
            {
                submitted = await _send(bound.Discovery, "job_submit", new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    tool_name = ActiveWritePluginName,
                    canonical_parameters_json = submittedParameters
                }).ConfigureAwait(false);
            }
            catch
            {
                CompleteD1ReservationUnknown(reservation, invocation.RequestId, identity.ProviderGeneration, null, bound);
                return D1Unknown(invocation.RequestId, invocation.ContextId, "the submit reply was lost; the write may have been accepted");
            }

            if (submitted == null || !submitted.Ok || submitted.Result is not JObject submitResult
                || !TryGetString(submitResult, "disposition", out string disposition))
            {
                CompleteD1ReservationUnknown(reservation, invocation.RequestId, identity.ProviderGeneration, null, bound);
                return Envelope(submitted?.Id, true, new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    status = "unknown",
                    message = "the plugin did not confirm the write submission; the drawing risk fence remains held"
                }, "plugin communication state is unknown");
            }

            if (disposition is "request_id_conflict" or "fence_conflict")
            {
                CompleteD1ReservationUnknown(reservation, invocation.RequestId, identity.ProviderGeneration, null, bound);
                return Envelope(submitted.Id, true, new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    disposition,
                    status = "unknown",
                    message = "the plugin rejected this reservation; reconcile the original job before trying another write"
                }, null);
            }

            JObject submittedJob = submitResult["job"] as JObject;
            string snapshotError = null;
            if (disposition is not ("created" or "existing") || submittedJob == null
                || !TryAcceptD1PluginSnapshot(invocation.RequestId, bound, identity, submittedJob, out snapshotError))
            {
                CompleteD1ReservationUnknown(reservation, invocation.RequestId, identity.ProviderGeneration, null, bound);
                return Envelope(submitted.Id, true, new
                {
                    request_id = invocation.RequestId,
                    context_id = invocation.ContextId,
                    status = "unknown",
                    message = "the plugin snapshot did not match the immutable D1 reservation; the risk fence remains held"
                }, snapshotError ?? "D1 plugin snapshot could not be accepted");
            }

            if (!TryCompleteD1Reservation(reservation,
                    invocation.RequestId, identity.ProviderGeneration, CaptureD1LedgerSequence(invocation.RequestId, bound), bound))
                return D1Unknown(invocation.RequestId, invocation.ContextId, "the submission acknowledgement could not be persisted");

            return await GetD1WriteJobAsync(invocation.ContextId, invocation.RequestId, 0).ConfigureAwait(false);
        }

        internal async Task<string> GetD1WriteJobAsync(string contextId, string requestId, int waitMs)
        {
            if (waitMs != 0)
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "job_get supports only wait_ms 0");
            if (ActiveFixedWriteScope == null || _ledger == null
                || !_contexts.TryGetOwnedRequest(requestId, contextId, requireConfirmed: false,
                    out BoundPluginContext bound, out BoundPluginRequestOwnership ownership)
                || !MatchesD1Ownership(ownership, bound, requestId, null)
                || !TryValidateD1BoundContext(bound, out BoundPluginOwnerIdentity identity, out _))
                return Envelope(Guid.NewGuid().ToString("N"), false, null,
                    "request_id is not an owned D1 request in the configured pipe scope");

            long? sequenceBeforeRead = CaptureD1LedgerSequence(requestId, bound);
            McpResponse response;
            try { response = await _send(bound.Discovery, "job_get", new { request_id = requestId, wait_ms = 0 }).ConfigureAwait(false); }
            catch
            {
                TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration, sequenceBeforeRead, bound);
                return D1Unknown(requestId, contextId, "plugin communication state is unknown; the drawing risk fence remains held");
            }
            if (response == null || !response.Ok || response.Result is not JObject result
                || !TryGetBoolean(result, "found", out bool found))
            {
                TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration, sequenceBeforeRead, bound);
                return Envelope(response?.Id, false, null, response?.Error ?? "plugin returned an invalid D1 job snapshot");
            }
            if (!found || result["job"] is not JObject job)
            {
                TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration, sequenceBeforeRead, bound);
                return D1Unknown(requestId, contextId, "the plugin has no authoritative snapshot for this reserved write");
            }
            if (!TryAcceptD1PluginSnapshot(requestId, bound, identity, job, out string acceptError))
            {
                TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration, sequenceBeforeRead, bound);
                return Envelope(response.Id, false, null, acceptError ?? "plugin D1 snapshot could not be independently reconciled");
            }
            _contexts.MarkRequestOutcome(requestId, contextId, confirmed: true);

            if (HasD1ReadbackStage(job))
            {
                if (!TryStageD1ServerVerification(requestId, bound, identity, job,
                        out LedgerJob stagedLedger, out string verificationError))
                {
                    TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration,
                        CaptureD1LedgerSequence(requestId, bound), bound);
                    return D1Pending(requestId, contextId, job, stagedLedger,
                        verificationError ?? "server readback verification has not completed");
                }

                if (stagedLedger.Verification == VerificationState.Failed)
                    return D1Pending(requestId, contextId, job, stagedLedger,
                        stagedLedger.VerificationErrorMessage ?? "readback verification failed; the drawing risk fence remains held");
            }

            LedgerJob finalLedger = null;
            string finalizeError = null;
            if (TryGetBoolean(job, "write_fence_finalized", out bool pluginFinalized) && pluginFinalized)
            {
                if (!TryFinalizeD1ServerLedger(requestId, bound, identity, job, out finalLedger, out finalizeError))
                    return D1Pending(requestId, contextId, job, finalLedger, finalizeError);
                return D1Completed(response.Id, requestId, contextId, job, finalLedger);
            }

            if (HasD1ReadbackStage(job)
                && TryGetString(job, "verification", out string pluginVerification)
                && pluginVerification == "passed"
                && TryGetInt64(job, "verification_staged_sequence", out long stageSequence)
                && TryGetInt64(job, "readback_revision", out long readbackRevision)
                && TryGetString(job, "readback_digest", out string readbackDigest)
                && TryGetString(job, "committed_result_digest", out string committedResultDigest))
            {
                McpResponse finalizedResponse;
                try
                {
                    finalizedResponse = await _send(bound.Discovery, "job_write_finalize", new
                    {
                        request_id = requestId,
                        context_id = contextId,
                        staged_plugin_sequence = stageSequence,
                        readback_revision = readbackRevision,
                        readback_digest = readbackDigest,
                        committed_result_digest = committedResultDigest
                    }).ConfigureAwait(false);
                }
                catch
                {
                    TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration,
                        CaptureD1LedgerSequence(requestId, bound), bound);
                    return D1Pending(requestId, contextId, job, _ledger.Get(requestId),
                        "plugin finalization reply was lost; retry status lookup, never resubmit the CAD write");
                }
                JObject finalizedResult = finalizedResponse?.Result as JObject;
                JObject finalizedJob = finalizedResult?["job"] as JObject;
                if (finalizedResponse == null || !finalizedResponse.Ok || finalizedJob == null
                    || !TryAcceptD1PluginSnapshot(requestId, bound, identity, finalizedJob, out finalizeError))
                {
                    TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration,
                        CaptureD1LedgerSequence(requestId, bound), bound);
                    return D1Pending(requestId, contextId, job, _ledger.Get(requestId),
                        finalizeError ?? finalizedResponse?.Error ?? "plugin did not confirm final fence release");
                }
                if (!TryFinalizeD1ServerLedger(requestId, bound, identity, finalizedJob,
                        out finalLedger, out finalizeError))
                    return D1Pending(requestId, contextId, finalizedJob, finalLedger, finalizeError);
                return D1Completed(response.Id, requestId, contextId, finalizedJob, finalLedger);
            }

            LedgerJob visible = _ledger.Get(requestId);
            if (visible == null)
            {
                TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration, sequenceBeforeRead, bound);
                return D1Unknown(requestId, contextId, "server request ledger is unavailable");
            }
            return D1Pending(requestId, contextId, job, visible,
                visible.Phase == JobPhase.Completed && visible.Effects == EffectState.Committed
                    ? "write committed; waiting for a complete independent readback stage"
                    : null);
        }

        internal async Task<string> CancelD1WriteJobAsync(string contextId, string requestId)
        {
            if (ActiveFixedWriteScope == null || _ledger == null
                || !_contexts.TryGetOwnedRequest(requestId, contextId, requireConfirmed: true,
                    out BoundPluginContext bound, out BoundPluginRequestOwnership ownership)
                || ownership.RecoveryQueryOnly
                || !MatchesD1Ownership(ownership, bound, requestId, null)
                || !TryValidateD1BoundContext(bound, out BoundPluginOwnerIdentity identity, out _))
                return Envelope(Guid.NewGuid().ToString("N"), false, null, "D1 cancellation is unavailable for this request owner");
            long? before = CaptureD1LedgerSequence(requestId, bound);
            McpResponse response;
            try { response = await _send(bound.Discovery, "job_cancel", new { request_id = requestId }).ConfigureAwait(false); }
            catch
            {
                TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration, before, bound);
                return D1Unknown(requestId, contextId, "plugin cancellation state is unknown; the drawing risk fence remains held");
            }
            JObject result = response?.Result as JObject;
            if (response == null || !response.Ok || !TryGetString(result, "disposition", out string disposition))
            {
                TryMarkD1LedgerUnavailable(requestId, identity.ProviderGeneration, before, bound);
                return Envelope(response?.Id, false, null, response?.Error ?? "plugin returned an invalid D1 cancel response");
            }
            JObject job = result["job"] as JObject;
            if (job != null && !TryAcceptD1PluginSnapshot(requestId, bound, identity, job, out string error))
                return Envelope(response.Id, false, null, error ?? "plugin D1 cancel snapshot could not be reconciled");
            return job == null
                ? D1Unknown(requestId, contextId, "cancel outcome has no authoritative job snapshot").WithEnvelopeId(response.Id)
                : Envelope(response.Id, true, new { disposition, job = ProjectD1PluginSnapshot(job) }, null);
        }

        private bool TryValidateD1BoundContext(BoundPluginContext bound,
            out BoundPluginOwnerIdentity identity, out string error)
        {
            identity = null;
            error = null;
            if (ActiveFixedWriteScope == null || bound == null || !ActiveWriteScopeMatches(bound)
                || !bound.TryGetOwnerIdentity(out identity, out error))
            {
                error ??= "bound context does not match the configured local Road08 AutoCAD 2026 pipe scope";
                return false;
            }
            return true;
        }

        private bool TryValidateD1PreflightBoundContext(BoundPluginContext bound,
            out BoundPluginOwnerIdentity identity, out string error)
        {
            identity = null;
            error = null;
            bool matchesReadOnlyScope = _d1PreflightPilotScope != null
                ? _d1PreflightPilotScope.Matches(bound)
                : ActiveFixedWriteScope != null && ActiveWriteScopeMatches(bound);
            if (!matchesReadOnlyScope || bound == null
                || !bound.TryGetOwnerIdentity(out identity, out error))
            {
                error ??= "bound context does not match the configured local Road08 AutoCAD 2026 pipe scope";
                return false;
            }
            return true;
        }

        private bool TryAcceptD1PluginSnapshot(string requestId, BoundPluginContext bound,
            BoundPluginOwnerIdentity identity, JObject snapshot, out string error)
        {
            error = null;
            if (!TryValidateD1SnapshotIdentity(requestId, bound, identity, snapshot, out error)
                || !TryMapD1PluginSnapshot(snapshot, out AdvanceRequest advance, out error))
                return false;
            if (!_contexts.TryRecordPluginSnapshot(requestId, snapshot, out error))
                return false;

            LedgerJob current = _ledger.Get(requestId);
            if (!MatchesD1LedgerJob(current, bound, requestId, identity, out error))
                return false;

            if (IsFoundationWriteProfile && current.Verification==VerificationState.Failed && !current.WriteFenceReleased
                && TryGetInt64(snapshot,"verification_staged_sequence",out long recoveryStage) && recoveryStage==current.PluginSequence+1
                && snapshot.Value<long>("plugin_sequence")==recoveryStage+1 && snapshot.Value<bool>("write_fence_finalized") && snapshot.Value<bool>("write_fence_released")
                && snapshot.Value<string>("verification")=="passed" && snapshot.Value<string>("result_json")==current.ResultJson
                && ComputePluginReadbackDigest(snapshot)==snapshot.Value<string>("readback_digest")
                && FoundationDrawingPlan.VerifyReadback(current.ResultJson,snapshot.Value<string>("verification_result_json"),current.CanonicalParametersJson))
            {
                string proof=snapshot.Value<string>("verification_result_json");
                string verified=JsonConvert.SerializeObject(new {validator_version=D1ReadbackValidatorVersion,passed=true,failure_codes=Array.Empty<string>(),source_handles=ActiveWriteSourceHandles,plugin_readback_stage_digest=snapshot.Value<string>("readback_digest"),readback_sha256=ComputeContentDigest(proof)},Formatting.None);
                var recovery=new VerificationRequest(requestId,bound.ContextId,bound.CompleteTargetJson,identity.ProviderGeneration,recoveryStage,VerificationState.Passed,verified,ExpectedPluginSequence:recoveryStage);
                if(!_ledger.RecoverArchiveRoundoffStage(recovery,proof,current.PluginSequence.Value)){error="archive_roundoff_recovery_stage_rejected";return false;}
                current=_ledger.Get(requestId);
            }
            if (current.VerificationStagedPluginSequence.HasValue)
            {
                long stagedSequence = current.VerificationStagedPluginSequence.Value;
                if (!TryGetInt64(snapshot, "verification_staged_sequence", out long pluginStageSequence)
                    || pluginStageSequence != stagedSequence
                    || !TryGetInt64(snapshot, "plugin_sequence", out long pluginSequence)
                    || !TryGetInt64(snapshot, "readback_revision", out long pluginReadbackRevision)
                    || !TryGetString(snapshot, "readback_digest", out string pluginReadbackDigest)
                    || !TryGetString(snapshot, "verification_result_json", out string pluginReadbackJson)
                    || !MatchesD1StagedReadback(current, pluginStageSequence, pluginReadbackRevision,
                        pluginReadbackDigest, pluginReadbackJson))
                {
                    error = "plugin snapshot does not match the server's staged D1 readback";
                    return false;
                }
                bool atStage = pluginSequence == stagedSequence
                    && !snapshot.Value<bool>("write_fence_finalized")
                    && !snapshot.Value<bool>("write_fence_released");
                bool finalized = pluginSequence == stagedSequence + 1
                    && current.Verification == VerificationState.Passed
                    && snapshot.Value<bool>("write_fence_finalized") && snapshot.Value<bool>("write_fence_released");
                if (!atStage && !finalized)
                {
                    error = "plugin snapshot sequence conflicts with the staged D1 readback";
                    return false;
                }
                return true;
            }

            AdvanceResult advanced;
            try
            {
                advanced = _ledger.Advance(advance with
                {
                    RequestId = requestId,
                    ProviderGeneration = identity.ProviderGeneration
                });
            }
            catch
            {
                error = "server ledger could not mirror the authoritative D1 plugin snapshot";
                return false;
            }
            if (advanced.Disposition is not (AdvanceDisposition.Applied or AdvanceDisposition.Duplicate))
            {
                error = "server ledger rejected the authoritative D1 plugin snapshot: " + (advanced.Reason ?? "unknown transition");
                return false;
            }
            return true;
        }

        private bool TryValidateD1SnapshotIdentity(string requestId, BoundPluginContext bound,
            BoundPluginOwnerIdentity identity, JObject job, out string error)
        {
            error = "plugin snapshot identity does not match the immutable D1 write reservation";
            if (IsFoundationWriteProfile && job != null)
            {
                if (!FoundationDrawingPlan.IsCanonicalValid(job.Value<string>("canonical_parameters_json")))
                { error = "foundation_snapshot_parameters_invalid"; return false; }
                if (job.Value<string>("payload_digest") != ComputeExpectedPluginSubmissionDigest(requestId, bound, identity))
                { error = "foundation_snapshot_submission_digest_mismatch"; return false; }
            }
            if (job == null || !_contexts.TryGetRequestOwnership(requestId, bound.ContextId,
                    out BoundPluginContext ownerContext, out BoundPluginRequestOwnership ownership)
                || !ReferenceEquals(ownerContext, bound)
                || !MatchesD1Ownership(ownership, bound, requestId, identity)
                || !TryGetString(job, "request_id", out string actualRequest)
                || actualRequest != requestId
                || !TryGetString(job, "context_id", out string actualContext)
                || actualContext != bound.ContextId
                || !TryGetString(job, "tool_name", out string toolName)
                || toolName != ActiveWritePluginName
                || !TryGetString(job, "canonical_parameters_json", out string parameters)
                || !D1ParametersEqual(parameters)
                || !TryGetString(job, "complete_target_json", out string target)
                || RequestLedger.CanonicalizeJsonObject(target, "complete_target_json")
                    != RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "completeTargetJson")
                || !TryGetString(job, "payload_digest", out string payloadDigest)
                || payloadDigest != ComputeExpectedPluginSubmissionDigest(requestId, bound, identity)
                || !TryGetString(job, "owner_id", out string ownerId)
                || !OwnerGuidEquals(ownerId, identity.OwnerId)
                || !TryGetString(job, "provider_generation", out string generation)
                || generation != identity.ProviderGeneration
                || job["is_write"]?.Type != JTokenType.Boolean || !job.Value<bool>("is_write")
                || job["global_fence"]?.Type != JTokenType.Boolean || job.Value<bool>("global_fence")
                || job["queue_deadline_utc"] == null || job["queue_deadline_utc"].Type != JTokenType.Null
                || job["held_risk_keys"] is not JArray riskKeys
                || !TryGetBoolean(job, "write_fence_released", out bool fenceReleased)
                || !TryGetBoolean(job, "write_fence_finalized", out bool fenceFinalized))
                return false;

            string[] expectedKeys = fenceReleased ? Array.Empty<string>() : new[] { ActiveFixedWriteScope.DrawingRiskKey };
            if (!riskKeys.Values<string>().SequenceEqual(expectedKeys, StringComparer.Ordinal))
                return false;
            if (fenceFinalized && !fenceReleased)
                return false;
            if (!TryGetInt64(job, "plugin_sequence", out long sequence) || sequence < 0
                || !TryGetString(job, "phase", out string phase)
                || !TryGetString(job, "effects", out string effects)
                || !TryGetString(job, "verification", out string verification)
                || !TryGetRequiredNullableString(job, "result_json", out string resultJson)
                || !TryGetRequiredNullableString(job, "error_code", out _)
                || !TryGetRequiredNullableString(job, "error_message", out _)
                || !TryGetRequiredNullableString(job, "readback_digest", out string readbackDigest)
                || !TryGetRequiredNullableString(job, "verification_result_json", out string readbackJson)
                || !TryGetRequiredNullableString(job, "verification_error_code", out _)
                || !TryGetRequiredNullableString(job, "verification_error_message", out _)
                || !TryGetRequiredNullableString(job, "committed_result_digest", out string committedDigest)
                || !TryGetNullableInt64(job, "readback_revision", out long? readbackRevision)
                || !TryGetNullableInt64(job, "verification_staged_sequence", out long? stagedSequence)
                || !TryGetNullableInt64(job, "no_effect_claim_sequence", out long? noEffectSequence)
                || !TryGetRequiredNullableString(job, "no_effect_outcome", out string noEffectOutcome))
                return false;

            if (resultJson != null && !IsJson(resultJson)) return false;
            if (readbackJson != null && !IsJson(readbackJson)) return false;
            if (effects == "committed")
            {
                if (phase != "completed" || fenceReleased != fenceFinalized || resultJson == null
                    || !IsSha256(committedDigest)
                    || !string.Equals(committedDigest, ComputeContentDigest(resultJson), StringComparison.Ordinal)
                    || noEffectSequence.HasValue || noEffectOutcome != null)
                    return false;
                bool hasStage = readbackRevision.HasValue || stagedSequence.HasValue
                    || readbackDigest != null || readbackJson != null;
                if (!hasStage)
                    return verification == "not_checked" && !fenceFinalized && committedDigest != null;
                if (!readbackRevision.HasValue || !stagedSequence.HasValue
                    || stagedSequence.Value <= 0 || stagedSequence.Value != readbackRevision.Value
                    || !IsSha256(readbackDigest)
                    || verification is not ("passed" or "failed" or "unknown")
                    || readbackRevision.Value != (fenceFinalized ? sequence - 1 : sequence)
                    || fenceFinalized && verification != "passed"
                    || !string.Equals(ComputePluginReadbackDigest(job), readbackDigest, StringComparison.Ordinal))
                    return false;
                return true;
            }

            if (effects == "unknown")
                return (phase is "running" or "cancellation_requested" or "unknown")
                    && !fenceReleased && !fenceFinalized && verification == "not_checked"
                    && resultJson == null && readbackRevision == null && stagedSequence == null
                    && readbackDigest == null && readbackJson == null && committedDigest == null
                    && noEffectSequence == null && noEffectOutcome == null;

            if (effects != "none" || fenceFinalized || readbackRevision.HasValue || stagedSequence.HasValue
                || readbackDigest != null || readbackJson != null || committedDigest != null
                || verification != "not_checked")
                return false;
            if (phase == "failed")
                return fenceReleased && resultJson == null && noEffectSequence.HasValue
                    && noEffectOutcome is "rejected_before_transaction" or "rolled_back_before_commit";
            if (phase == "cancelled")
                return fenceReleased && resultJson == null && noEffectSequence == null && noEffectOutcome == null;
            return phase is "queued" or "running" or "cancellation_requested"
                && !fenceReleased && resultJson == null && noEffectSequence == null && noEffectOutcome == null;
        }

        private bool TryMapD1PluginSnapshot(JObject job, out AdvanceRequest advance, out string error)
        {
            advance = null;
            error = null;
            if (!TryGetString(job, "phase", out string phaseToken)
                || !TryGetString(job, "effects", out string effectsToken)
                || !TryGetInt64(job, "plugin_sequence", out long sequence)
                || !TryGetRequiredNullableString(job, "result_json", out string resultJson)
                || !TryGetRequiredNullableString(job, "error_code", out string errorCode)
                || !TryGetRequiredNullableString(job, "error_message", out string errorMessage))
            {
                error = "D1 plugin snapshot omitted required state fields";
                return false;
            }
            JobPhase phase = phaseToken switch
            {
                "queued" => JobPhase.Queued,
                "running" => JobPhase.Running,
                "cancellation_requested" => JobPhase.CancellationRequested,
                "completed" => JobPhase.Completed,
                "failed" => JobPhase.Failed,
                "cancelled" => JobPhase.Cancelled,
                "unknown" => JobPhase.Unknown,
                _ => (JobPhase)(-1)
            };
            EffectState effects = effectsToken switch
            {
                "none" => EffectState.None,
                "committed" => EffectState.Committed,
                "unknown" => EffectState.Unknown,
                _ => (EffectState)(-1)
            };
            if (!Enum.IsDefined(phase) || !Enum.IsDefined(effects)
                || resultJson != null && !IsJson(resultJson))
            {
                error = "D1 plugin snapshot contains an unsupported phase, effect, or result";
                return false;
            }
            // Plugin verification is an observation only. The Server writes its own
            // staged verification after checking the full raw readback evidence.
            advance = new AdvanceRequest(string.Empty, string.Empty, sequence, phase, effects,
                VerificationState.NotChecked, ObservationState.AuthoritativePluginSnapshot,
                resultJson, errorCode, errorMessage);
            return true;
        }

        private bool TryStageD1ServerVerification(string requestId, BoundPluginContext bound,
            BoundPluginOwnerIdentity identity, JObject pluginJob, out LedgerJob stagedLedger, out string error)
        {
            stagedLedger = null;
            error = null;
            if (!TryGetMatchingD1LedgerJob(requestId, bound, identity, out LedgerJob current, out error))
                return false;
            if (current.VerificationStagedPluginSequence.HasValue)
            {
                stagedLedger = current;
                if ((current.Verification is VerificationState.Passed or VerificationState.Failed)
                    && TryGetInt64(pluginJob, "verification_staged_sequence", out long pluginStageSequence)
                    && TryGetInt64(pluginJob, "readback_revision", out long pluginReadbackRevision)
                    && TryGetString(pluginJob, "readback_digest", out string pluginReadbackDigest)
                    && TryGetString(pluginJob, "verification_result_json", out string pluginReadbackJson)
                    && MatchesD1StagedReadback(current, pluginStageSequence, pluginReadbackRevision,
                        pluginReadbackDigest, pluginReadbackJson))
                    return true;
                error = "plugin readback stage does not match the server's durable D1 verification stage";
                return false;
            }

            if (!IsD1CommittedStage(pluginJob)
                || !TryGetInt64(pluginJob, "plugin_sequence", out long pluginSequence)
                || !TryGetInt64(pluginJob, "readback_revision", out long readbackRevision)
                || readbackRevision != pluginSequence)
            {
                error = "plugin has not persisted a complete committed readback stage";
                return false;
            }

            D1ReadbackResult readback = VerifyActiveWriteReadback(pluginJob);
            bool passed = readback.Passed
                && TryGetString(pluginJob, "verification", out string pluginVerification)
                && pluginVerification == "passed";
            string[] failures = passed ? Array.Empty<string>()
                : readback.Errors.Concat(new[] { "plugin_stage_not_passed" }).Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string resultJson = JsonConvert.SerializeObject(new
            {
                validator_version = D1ReadbackValidatorVersion,
                passed,
                failure_codes = failures,
                source_handles = ActiveWriteSourceHandles,
                plugin_readback_stage_digest = TryGetString(pluginJob, "readback_digest", out string pluginStageDigest)
                    ? pluginStageDigest : null,
                readback_sha256 = TryGetString(pluginJob, "verification_result_json", out string rawReadback)
                    ? ComputeContentDigest(rawReadback) : null
            }, Formatting.None);
            var request = new VerificationRequest(requestId, bound.ContextId, bound.CompleteTargetJson,
                identity.ProviderGeneration, readbackRevision,
                passed ? VerificationState.Passed : VerificationState.Failed,
                resultJson, passed ? null : "d1_readback_verification_failed",
                passed ? null : string.Join(",", failures), ExpectedPluginSequence: pluginSequence);
            VerificationResult result;
            try { result = _ledger.RecordVerificationPendingRelease(request); }
            catch
            {
                error = "server could not durably record the D1 readback stage; the drawing risk fence remains held";
                return false;
            }
            stagedLedger = result.Job;
            if (result.Disposition is not (VerificationDisposition.Applied or VerificationDisposition.Duplicate)
                || result.Job == null || result.Job.Verification != request.Verification
                || result.Job.VerificationStagedPluginSequence != pluginSequence
                || result.Job.WriteFenceReleased)
            {
                error = "server ledger rejected the D1 readback stage";
                return false;
            }
            return true;
        }

        private bool TryFinalizeD1ServerLedger(string requestId, BoundPluginContext bound,
            BoundPluginOwnerIdentity identity, JObject pluginJob, out LedgerJob finalLedger, out string error)
        {
            finalLedger = null;
            error = null;
            if (!TryGetMatchingD1LedgerJob(requestId, bound, identity, out LedgerJob current, out error))
                return false;
            if (!TryGetBoolean(pluginJob, "write_fence_finalized", out bool pluginFinalized)
                || !pluginFinalized || !TryGetBoolean(pluginJob, "write_fence_released", out bool pluginReleased)
                || !pluginReleased
                || !TryGetInt64(pluginJob, "plugin_sequence", out long finalSequence)
                || !TryGetInt64(pluginJob, "verification_staged_sequence", out long pluginStageSequence)
                || !TryGetInt64(pluginJob, "readback_revision", out long pluginReadbackRevision)
                || !TryGetString(pluginJob, "committed_result_digest", out string committedDigest)
                || !TryGetString(pluginJob, "readback_digest", out string pluginReadbackDigest)
                || current.Verification != VerificationState.Passed
                || !current.VerificationStagedPluginSequence.HasValue
                || current.VerificationStagedPluginSequence.Value != pluginStageSequence
                || !current.ReadbackRevision.HasValue || current.ReadbackRevision.Value != pluginReadbackRevision
                || (current.WriteFenceReleased
                    ? current.PluginSequence != finalSequence
                    : current.PluginSequence != pluginStageSequence)
                || current.ReadbackDigest == null
                || !TryGetString(pluginJob, "verification_result_json", out string pluginReadbackJson)
                || !MatchesD1StagedReadback(current, pluginStageSequence, pluginReadbackRevision,
                    pluginReadbackDigest, pluginReadbackJson)
                || finalSequence != pluginStageSequence + 1)
            {
                error = "plugin final fence release does not match the Server's durable readback stage";
                return false;
            }

            VerificationResult result;
            try
            {
                result = _ledger.FinalizeVerifiedWriteFence(new FinalizeVerificationRequest(
                    requestId, bound.ContextId, bound.CompleteTargetJson, identity.ProviderGeneration,
                    pluginStageSequence, finalSequence, pluginReadbackRevision, current.ReadbackDigest,
                    committedDigest));
            }
            catch
            {
                error = "server could not durably finalize the D1 risk fence";
                return false;
            }
            finalLedger = result.Job;
            if (result.Disposition is not (VerificationDisposition.Applied or VerificationDisposition.Duplicate)
                || result.Job == null || !result.Job.WriteFenceReleased
                || result.Job.PluginSequence != finalSequence)
            {
                error = "server ledger rejected the plugin's final D1 fence release";
                return false;
            }
            return true;
        }

        private bool TryGetMatchingD1LedgerJob(string requestId, BoundPluginContext bound,
            BoundPluginOwnerIdentity identity, out LedgerJob job, out string error)
        {
            job = null;
            error = null;
            try { job = _ledger.Get(requestId); }
            catch { error = "server D1 ledger is unavailable"; return false; }
            if (!MatchesD1LedgerJob(job, bound, requestId, identity, out error))
            {
                job = null;
                return false;
            }
            return true;
        }

        private bool MatchesD1LedgerJob(LedgerJob job, BoundPluginContext bound,
            string requestId, BoundPluginOwnerIdentity identity, out string error)
        {
            error = "server ledger identity does not match the immutable D1 request";
            if (job == null || bound == null || identity == null
                || !_contexts.TryGetRequestOwnership(requestId, bound.ContextId,
                    out BoundPluginContext ownerContext, out BoundPluginRequestOwnership ownership)
                || !ReferenceEquals(ownerContext, bound)
                || !MatchesD1Ownership(ownership, bound, requestId, identity)
                || job.RequestId != requestId || job.ContextId != bound.ContextId
                || job.ToolName != ActiveWriteMcpName
                || !D1ParametersEqual(job.CanonicalParametersJson)
                || RequestLedger.CanonicalizeJsonObject(job.CanonicalParametersJson,"ledger parameters") != RequestLedger.CanonicalizeJsonObject(ownership.CanonicalParametersJson,"owned parameters")
                || RequestLedger.CanonicalizeJsonObject(job.CompleteTargetJson, "completeTargetJson")
                    != RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "completeTargetJson")
                || !job.IsWrite || job.GlobalFence
                || job.ProviderGeneration != identity.ProviderGeneration
                || !job.HeldRiskKeys.SequenceEqual(job.WriteFenceReleased
                    ? Array.Empty<string>() : new[] { ActiveFixedWriteScope.DrawingRiskKey }, StringComparer.Ordinal))
                return false;
            error = null;
            return true;
        }

        private bool MatchesD1Ownership(BoundPluginRequestOwnership ownership, BoundPluginContext bound,
            string requestId, BoundPluginOwnerIdentity expectedIdentity)
        {
            if (ownership == null || bound == null || ActiveFixedWriteScope == null
                || !ActiveWriteScopeMatches(bound)
                || ownership.RequestId != requestId || ownership.ContextId != bound.ContextId
                || ownership.ToolName != ActiveWritePluginName
                || !D1ParametersEqual(ownership.CanonicalParametersJson)
                || ownership.CompleteTargetJson != bound.CompleteTargetJson
                || !ownership.IsWrite || ownership.RecoveryQueryOnly && ownership.State == BoundPluginRequestState.Reserved
                || ownership.RiskKeys.Count != 1 || ownership.RiskKeys[0] != ActiveFixedWriteScope.DrawingRiskKey
                || ownership.JournalScopeId != ActiveFixedWriteScope.JournalScopeId
                || ownership.JournalPathDigest != ActiveFixedWriteScope.JournalPathDigest
                || ownership.PayloadVersion != 2
                || !string.Equals(ownership.PayloadDigest,
                    SqliteBoundPluginOwnershipStore.CreatePayloadDigest(ownership), StringComparison.Ordinal))
                return false;
            if (expectedIdentity != null
                && (!OwnerGuidEquals(ownership.OwnerId, expectedIdentity.OwnerId)
                    || ownership.ProviderGeneration != expectedIdentity.ProviderGeneration))
                return false;
            return true;
        }

        private bool MatchesD1LedgerReservation(LedgerJob job, BoundPluginContext bound,
            string requestId, BoundPluginOwnerIdentity identity) => job != null
            && job.RequestId == requestId && job.ContextId == bound.ContextId
            && job.ToolName == ActiveWriteMcpName
            && D1ParametersEqual(job.CanonicalParametersJson)
            && RequestLedger.CanonicalizeJsonObject(job.CompleteTargetJson, "completeTargetJson")
                == RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "completeTargetJson")
            && job.IsWrite && !job.GlobalFence && job.ProviderGeneration == identity.ProviderGeneration
            && job.HeldRiskKeys.SequenceEqual(new[] { ActiveFixedWriteScope.DrawingRiskKey }, StringComparer.Ordinal);

        private bool D1ParametersEqual(string actualJson)
        {
            if (IsFoundationWriteProfile) return FoundationDrawingPlan.IsCanonicalValid(actualJson);
            try
            {
                string expected = RequestLedger.CanonicalizeJsonObject(
                    ActiveWriteParameters, "D1 canonical parameters");
                return string.Equals(RequestLedger.CanonicalizeJsonObject(actualJson,
                    "plugin D1 canonical parameters"), expected, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private string ComputeExpectedPluginSubmissionDigest(string requestId, BoundPluginContext bound,
            BoundPluginOwnerIdentity identity)
        {
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            using (JsonDocument parameters = JsonDocument.Parse(RequestLedger.CanonicalizeJsonObject(
                OwnedWriteParameters(requestId, bound), "D1 canonical parameters")))
            using (JsonDocument target = JsonDocument.Parse(
                RequestLedger.CanonicalizeJsonObject(bound.CompleteTargetJson, "completeTargetJson")))
            {
                writer.WriteStartObject();
                writer.WriteNumber("digest_version", 1);
                writer.WriteString("request_id", requestId);
                writer.WriteString("context_id", bound.ContextId);
                writer.WriteString("tool_name", ActiveWritePluginName);
                writer.WriteString("owner_id", Guid.Parse(identity.OwnerId).ToString("N"));
                writer.WriteNumber("owner_pid", identity.ProcessId);
                writer.WriteString("owner_process_started_at_utc",
                    identity.ProcessStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("provider_generation", identity.ProviderGeneration);
                writer.WritePropertyName("parameters"); parameters.RootElement.WriteTo(writer);
                writer.WritePropertyName("complete_target"); target.RootElement.WriteTo(writer);
                writer.WriteBoolean("is_write", true);
                writer.WriteBoolean("global_fence", false);
                writer.WritePropertyName("risk_keys");
                writer.WriteStartArray(); writer.WriteStringValue(ActiveFixedWriteScope.DrawingRiskKey); writer.WriteEndArray();
                writer.WriteNull("queue_deadline_utc");
                writer.WriteEndObject();
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }

        private static string ComputePluginReadbackDigest(JObject job)
        {
            if (!TryGetInt64(job, "verification_staged_sequence", out long stagedSequence)
                || !TryGetInt64(job, "readback_revision", out long readbackRevision)
                || !TryGetInt64(job, "plugin_sequence", out long pluginSequence)
                || !TryGetString(job, "request_id", out string requestId)
                || !TryGetString(job, "context_id", out string contextId)
                || !TryGetString(job, "complete_target_json", out string targetJson)
                || !TryGetString(job, "owner_id", out string ownerId)
                || !TryGetString(job, "provider_generation", out string generation)
                || !TryGetString(job, "payload_digest", out string payloadDigest)
                || !TryGetString(job, "committed_result_digest", out string committedDigest)
                || !TryGetString(job, "verification", out string verification)
                || !TryGetRequiredNullableString(job, "verification_result_json", out string readbackJson)
                || !TryGetRequiredNullableString(job, "verification_error_code", out string errorCode)
                || !TryGetRequiredNullableString(job, "verification_error_message", out string errorMessage)
                || stagedSequence != readbackRevision
                || (pluginSequence != stagedSequence && pluginSequence != stagedSequence + 1))
                return null;

            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            using (JsonDocument target = JsonDocument.Parse(
                RequestLedger.CanonicalizeJsonObject(targetJson, "complete_target_json")))
            {
                writer.WriteStartObject();
                writer.WriteString("request_id", requestId);
                writer.WriteString("context_id", contextId);
                writer.WritePropertyName("complete_target"); target.RootElement.WriteTo(writer);
                writer.WriteString("owner_id", ownerId);
                writer.WriteString("provider_generation", generation);
                writer.WriteString("payload_digest", payloadDigest);
                writer.WriteNumber("expected_plugin_sequence", stagedSequence - 1);
                writer.WriteString("committed_result_digest", committedDigest);
                writer.WriteNumber("readback_revision", readbackRevision);
                writer.WriteString("verification", ToPluginVerificationValue(verification));
                WriteNullable(writer, "readback_result", readbackJson);
                WriteNullable(writer, "error_code", errorCode);
                WriteNullable(writer, "error_message", errorMessage);
                writer.WriteEndObject();
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }

        private static string ToPluginVerificationValue(string value) => value switch
        {
            "not_checked" => "not_checked",
            "passed" => "passed",
            "failed" => "failed",
            "unknown" => "unknown",
            _ => throw new InvalidOperationException("Unknown plugin verification state.")
        };

        private static void WriteNullable(Utf8JsonWriter writer, string name, string value)
        {
            if (value == null) writer.WriteNull(name);
            else writer.WriteString(name, value);
        }

        private static bool TryGetNullableInt64(JObject value, string name, out long? result)
        {
            result = null;
            JToken token = value?[name];
            if (token == null) return false;
            if (token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.Integer) return false;
            try { result = token.Value<long>(); return !result.HasValue || result.Value >= 0; }
            catch { return false; }
        }

        private static bool IsD1CommittedStage(JObject job) =>
            job != null && TryGetString(job, "phase", out string phase) && phase == "completed"
            && TryGetString(job, "effects", out string effects) && effects == "committed";

        private static bool HasD1ReadbackStage(JObject job) => IsD1CommittedStage(job)
            && TryGetInt64(job, "verification_staged_sequence", out _)
            && TryGetInt64(job, "readback_revision", out _)
            && TryGetString(job, "readback_digest", out _)
            && TryGetString(job, "verification_result_json", out _);

        private static bool MatchesD1StagedReadback(LedgerJob ledgerJob, long pluginStageSequence,
            long pluginReadbackRevision, string pluginReadbackDigest, string pluginReadbackJson)
        {
            if (ledgerJob == null || !ledgerJob.VerificationStagedPluginSequence.HasValue
                || ledgerJob.VerificationStagedPluginSequence.Value != pluginStageSequence
                || !ledgerJob.ReadbackRevision.HasValue || ledgerJob.ReadbackRevision.Value != pluginReadbackRevision
                || !IsSha256(pluginReadbackDigest) || string.IsNullOrWhiteSpace(pluginReadbackJson)
                || !IsJson(pluginReadbackJson) || string.IsNullOrWhiteSpace(ledgerJob.VerificationResultJson))
                return false;
            try
            {
                JObject serverStage = JObject.Parse(ledgerJob.VerificationResultJson);
                return serverStage.Value<string>("plugin_readback_stage_digest") == pluginReadbackDigest
                    && serverStage.Value<string>("readback_sha256") == ComputeContentDigest(pluginReadbackJson);
            }
            catch { return false; }
        }

        private static bool IsSha256(string value) => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

        private static string ComputeContentDigest(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

        private static bool IsJson(string value)
        {
            try { JToken.Parse(value); return true; }
            catch { return false; }
        }

        private long? CaptureD1LedgerSequence(string requestId, BoundPluginContext bound) =>
            TryGetMatchingD1LedgerJob(requestId, bound,
                bound != null && bound.TryGetOwnerIdentity(out BoundPluginOwnerIdentity identity, out _) ? identity : null,
                out LedgerJob job, out _) ? job.PluginSequence : null;

        private void TryMarkD1LedgerUnavailable(string requestId, string generation, long? expectedSequence,
            BoundPluginContext bound)
        {
            if (_ledger == null || bound == null || string.IsNullOrWhiteSpace(generation)
                || !TryGetMatchingD1LedgerJob(requestId, bound,
                    bound.TryGetOwnerIdentity(out BoundPluginOwnerIdentity identity, out _) ? identity : null,
                    out LedgerJob current, out _)
                || current.ProviderGeneration != generation)
                return;
            try { _ledger.MarkUnavailable(requestId, generation, expectedSequence); }
            catch { }
        }

        private void CompleteD1ReservationUnknown(RequestSubmissionLease reservation, string requestId,
            string generation, long? sequence, BoundPluginContext bound)
        {
            try { reservation?.Complete(RequestSubmissionOutcome.OutcomeUnknown); }
            catch { }
            TryMarkD1LedgerUnavailable(requestId, generation, sequence, bound);
        }

        private bool TryCompleteD1Reservation(RequestSubmissionLease reservation, string requestId,
            string generation, long? sequence, BoundPluginContext bound)
        {
            if (reservation == null) return false;
            try
            {
                reservation.Complete(RequestSubmissionOutcome.Confirmed);
                return true;
            }
            catch
            {
                TryMarkD1LedgerUnavailable(requestId, generation, sequence, bound);
                return false;
            }
        }

        private string D1Unknown(string requestId, string contextId, string message)
        {
            JObject source = CreateD1SourceIdentity(requestId, contextId, null, null);
            if (IsH1WriteProfile) source["schema"] = "bimwright-h1-source-identity-v1";
            if (IsFoundationWriteProfile) source["schema"] = "bimwright-foundation-source-identity-v1";
            return Envelope(Guid.NewGuid().ToString("N"), true, new
            {
                request_id = requestId, context_id = contextId, status = "unknown", pending = (bool?)null, effects = "unknown",
                source_identity = source,
                outcome_summary = IsFoundationWriteProfile ? CreateFoundationOutcomeSummary(null,null,false) : IsH1WriteProfile ? CreateH1OutcomeSummary(null, null, false) : CreateD1OutcomeSummary(null, null, false),
                message
            }, (IsH1WriteProfile ? "H1" : "D1") + " write state is unknown; the drawing risk fence remains held");
        }

        private static string D1Pending(string requestId, string contextId, JObject pluginJob,
            LedgerJob ledgerJob, string message)
        {
            string phase = pluginJob?.Value<string>("phase") ?? "unknown";
            string verification = ledgerJob?.Verification.ToString().ToLowerInvariant() ?? "unknown";
            return Envelope(Guid.NewGuid().ToString("N"), true, new
            {
                request_id = requestId,
                context_id = contextId,
                status = phase == "completed" && ledgerJob?.Effects == EffectState.Committed
                    ? "verification_pending" : phase,
                phase,
                effects = pluginJob?.Value<string>("effects") ?? "unknown",
                verification,
                pending = !(ledgerJob?.WriteFenceReleased ?? false),
                write_fence_released = ledgerJob?.WriteFenceReleased ?? false,
                plugin_sequence = pluginJob?.Value<long?>("plugin_sequence"),
                result_json = pluginJob?.Value<string>("result_json"),
                verification_message = message,
                job = ProjectD1PluginSnapshot(pluginJob),
                source_identity = CreateD1SourceIdentity(requestId, contextId, pluginJob, ledgerJob),
                outcome_summary = CreateD1OutcomeSummary(pluginJob, ledgerJob, finalized: false)
            }, null);
        }

        private static string D1Completed(string envelopeId, string requestId, string contextId,
            JObject pluginJob, LedgerJob ledgerJob) =>
            Envelope(envelopeId, true, new
            {
                request_id = requestId,
                context_id = contextId,
                status = "completed",
                phase = "completed",
                effects = "committed",
                verification = ledgerJob.Verification.ToString().ToLowerInvariant(),
                pending = false,
                write_fence_released = true,
                result_json = pluginJob.Value<string>("result_json"),
                job = ProjectD1PluginSnapshot(pluginJob),
                outcome_summary = CreateD1OutcomeSummary(pluginJob, ledgerJob, finalized: true),
                source_identity = CreateD1SourceIdentity(requestId, contextId, pluginJob, ledgerJob)
            }, null);

        // Only an accepted snapshot and its matching immutable reservation can
        // supply origin. Unknown responses never borrow an active document.
        private static JObject CreateD1SourceIdentity(string requestId, string contextId,
            JObject pluginJob, LedgerJob ledgerJob)
        {
            bool foundation = ledgerJob?.ToolName == FoundationDrawingPlan.McpToolName;
            bool h1 = ledgerJob?.ToolName == H1FixedHatchWriteContract.McpToolName;
            string expectedMcp = foundation ? FoundationDrawingPlan.McpToolName : h1 ? H1FixedHatchWriteContract.McpToolName : Road08R01D1WriteContract.McpToolName;
            string expectedPlugin = foundation ? FoundationDrawingPlan.PluginToolName : h1 ? H1FixedHatchWriteContract.PluginToolName : Road08R01D1WriteContract.PluginToolName;
            var source = new JObject
            {
                ["schema"] = foundation ? "bimwright-foundation-source-identity-v1" : h1 ? "bimwright-h1-source-identity-v1" : "bimwright-d1-source-identity-v1",
                ["observation"] = "immutable_request_target",
                ["identity_status"] = "unknown",
                ["request_id"] = requestId,
                ["context_id"] = contextId,
                ["binding_identity"] = JValue.CreateNull()
            };
            try
            {
                if (ledgerJob == null || !ledgerJob.IsWrite || ledgerJob.Effects != EffectState.Committed
                    || ledgerJob.RequestId != requestId || ledgerJob.ContextId != contextId
                    || ledgerJob.ToolName != expectedMcp
                    || !TryGetString(pluginJob, "request_id", out string actualRequest) || actualRequest != requestId
                    || !TryGetString(pluginJob, "context_id", out string actualContext) || actualContext != contextId
                    || !TryGetString(pluginJob, "tool_name", out string tool) || tool != expectedPlugin
                    || !TryGetString(pluginJob, "effects", out string effects) || effects != "committed"
                    || !TryGetString(pluginJob, "complete_target_json", out string targetJson)
                    || RequestLedger.CanonicalizeJsonObject(targetJson, "target")
                        != RequestLedger.CanonicalizeJsonObject(ledgerJob.CompleteTargetJson, "target"))
                    return source;
                using var reader = new JsonTextReader(new System.IO.StringReader(targetJson))
                    { DateParseHandling = DateParseHandling.None };
                JObject target = JObject.Load(reader);
                if (target.Value<string>("schema") != "bimwright-acad26-document-target-v1"
                    || target.Value<string>("context_id") != contextId
                    || target.Value<string>("owner_generation") != ledgerJob.ProviderGeneration
                    || !TryGetString(pluginJob, "provider_generation", out string generation)
                    || generation != ledgerJob.ProviderGeneration
                    || target["owner_pid"]?.Type != JTokenType.Integer || target.Value<int>("owner_pid") <= 0
                    || !TryGetString(target, "owner_started_at_utc", out string started)
                    || !DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || !TryGetString(target, "drawing_path", out string path)
                    || !DrawingRiskKey.TryNormalizePath(path, out _))
                    return source;
                foreach (string field in new[] { "document_session_id", "database_session_id", "database_fingerprint" })
                    if (!TryGetString(target, field, out string id) || !Guid.TryParse(id, out Guid guid) || guid == Guid.Empty)
                        return source;
                var identity = new JObject();
                foreach (string field in new[] { "owner_pid", "owner_started_at_utc", "owner_generation",
                    "document_session_id", "database_session_id", "database_fingerprint", "drawing_path" })
                    identity[field] = target[field].DeepClone();
                source["binding_identity"] = identity;
                source["identity_status"] = "known";
            }
            catch { /* Missing or invalid provenance remains unknown. */ }
            return source;
        }

        // Pure projection after existing identity/digest reconciliation. It
        // neither advances a ledger nor stages, finalizes, or releases a fence.
        private static JObject CreateD1OutcomeSummary(JObject pluginJob, LedgerJob ledgerJob, bool finalized)
        {
            if (pluginJob?.Value<string>("tool_name") == FoundationDrawingPlan.PluginToolName) return CreateFoundationOutcomeSummary(pluginJob,ledgerJob,finalized);
            if (pluginJob?.Value<string>("tool_name") == H1FixedHatchWriteContract.PluginToolName)
                return CreateH1OutcomeSummary(pluginJob, ledgerJob, finalized);
            string phase = TryGetString(pluginJob, "phase", out string observedPhase) ? observedPhase : "unknown";
            string effects = TryGetString(pluginJob, "effects", out string observedEffects)
                && observedEffects is "none" or "committed" or "unknown" ? observedEffects : "unknown";
            string pluginVerification = TryGetString(pluginJob, "verification", out string observedVerification)
                && observedVerification is "passed" or "failed" or "unknown" or "not_checked"
                ? observedVerification : "unknown";
            string serverVerification = ledgerJob?.Verification switch
            {
                VerificationState.Passed => "passed",
                VerificationState.Failed => "failed",
                VerificationState.NotChecked => "not_checked",
                _ => "unknown"
            };
            bool released = TryGetBoolean(pluginJob, "write_fence_released", out bool pluginReleased)
                && pluginReleased && ledgerJob?.WriteFenceReleased == true;
            bool complete = finalized && phase == "completed" && effects == "committed"
                && pluginVerification == "passed" && serverVerification == "passed" && released;
            bool? awaitingExecution = phase switch
            {
                "queued" or "running" or "cancellation_requested" => true,
                "completed" or "failed" or "cancelled" => false,
                _ => null
            };

            string outcome = "effect_unknown";
            if (complete)
                outcome = "completed_verified";
            else if (effects == "committed")
            {
                outcome = pluginVerification == "failed" || serverVerification == "failed"
                    ? "committed_verification_failed"
                    : pluginVerification == "unknown" || serverVerification == "unknown"
                        ? "committed_verification_unknown"
                        : pluginVerification == "passed" && serverVerification == "passed"
                            ? "committed_release_pending" : "committed_verification_pending";
            }
            else if (effects == "none")
            {
                if (phase == "queued") outcome = "queued";
                else if (phase == "cancelled") outcome = "cancelled_no_effect";
                else if (phase == "failed")
                {
                    string noEffectOutcome = TryGetString(pluginJob, "no_effect_outcome", out string reason) ? reason : null;
                    outcome = noEffectOutcome switch
                    {
                        "rejected_before_transaction" => "rejected_no_effect",
                        "rolled_back_before_commit" => "rolled_back_no_effect",
                        _ => "no_effect_terminal"
                    };
                }
            }

            string leaderHandle = null;
            string mtextHandle = null;
            if (effects == "committed" && TryGetString(pluginJob, "result_json", out string rawResult))
            {
                try
                {
                    JObject result = JObject.Parse(rawResult);
                    if (result["schema_version"]?.Type == JTokenType.Integer
                        && result.Value<int>("schema_version") == Road08R01D1WriteContract.EvidenceSchemaVersion
                        && TryGetString(result, "plan_id", out string planId) && planId == Road08R01D1WriteContract.PlanId)
                    {
                        if (TryGetString(result, "leader_handle", out string leader))
                            leaderHandle = Road08R01D1WriteContract.NormalizeCapturedEntityHandle(leader);
                        if (TryGetString(result, "mtext_handle", out string text))
                            mtextHandle = Road08R01D1WriteContract.NormalizeCapturedEntityHandle(text);
                    }
                }
                catch { /* Keep unavailable identities explicit; never invent a handle. */ }
            }
            bool conflicting = leaderHandle != null && leaderHandle == mtextHandle;
            string handleStatus = conflicting ? "conflicting"
                : leaderHandle != null && mtextHandle != null ? "complete"
                : leaderHandle != null || mtextHandle != null ? "partial" : "unavailable";
            JObject handles = conflicting || handleStatus == "unavailable" ? null : new JObject
            {
                ["leader_handle"] = leaderHandle == null ? JValue.CreateNull() : new JValue(leaderHandle),
                ["mtext_handle"] = mtextHandle == null ? JValue.CreateNull() : new JValue(mtextHandle)
            };
            string nextAction = complete ? "none"
                : effects == "committed" && handles != null ? "inspect_existing_handles"
                : outcome == "committed_verification_failed" ? "review_verification_failure"
                : outcome is "rejected_no_effect" or "rolled_back_no_effect" or "cancelled_no_effect" or "no_effect_terminal"
                    ? "review_no_effect_result" : "query_original_request";

            return new JObject
            {
                ["schema"] = "bimwright-d1-outcome-v1",
                ["outcome"] = outcome,
                ["effects"] = effects,
                ["plugin_verification"] = pluginVerification,
                ["server_verification"] = serverVerification,
                ["awaiting_execution"] = awaitingExecution.HasValue ? new JValue(awaitingExecution.Value) : JValue.CreateNull(),
                ["write_blocked"] = !released || effects == "unknown" || effects == "committed" && !complete,
                ["created_handles"] = handles == null ? JValue.CreateNull() : handles,
                ["handle_status"] = handleStatus,
                ["handle_evidence"] = handles == null ? "unavailable" : complete ? "independently_verified" : "captured_unverified",
                ["next_action"] = nextAction,
                ["repeat_creation_allowed"] = false
            };
        }

        private static JObject ProjectD1PluginSnapshot(JObject job)
        {
            if (job == null) return null;
            var result = new JObject();
            foreach (string name in new[]
            {
                "request_id", "context_id", "tool_name", "phase", "effects", "verification",
                "observation", "result_json", "error_code", "error_message"
            })
                if (job[name] != null) result[name] = job[name].DeepClone();
            foreach (string name in new[]
            {
                "plugin_sequence", "readback_revision", "write_fence_released", "write_fence_finalized"
            })
                if (job[name] != null) result[name] = job[name].DeepClone();
            return result;
        }

        private static string EnvelopeId(string response)
        {
            try { return JObject.Parse(response)?.Value<string>("id") ?? Guid.NewGuid().ToString("N"); }
            catch { return Guid.NewGuid().ToString("N"); }
        }
    }

    internal static class D1EnvelopeResponseExtensions
    {
        internal static string WithEnvelopeId(this string response, string id)
        {
            try
            {
                JObject parsed = JObject.Parse(response);
                parsed["id"] = id;
                return parsed.ToString(Formatting.None);
            }
            catch { return response; }
        }
    }
}
