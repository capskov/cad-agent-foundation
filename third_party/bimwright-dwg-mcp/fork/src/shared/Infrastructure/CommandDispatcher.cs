// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bimwright.Dwg.Plugin.Cad;
using Bimwright.Dwg.Plugin.Handlers;
using Bimwright.Dwg.Plugin.ToolCatalog;
#if ACAD2026
using Bimwright.Dwg.Plugin.Execution;
using Bimwright.Dwg.Shared.Execution;
#endif

namespace Bimwright.Dwg.Plugin
{
    public class CommandDispatcher
    {
        private const int MaxEntityPropertiesJobResultBytes = 128 * 1024;
        private readonly Dictionary<string, IAcadCommand> _commands;
        private readonly string _authToken;

        public static bool SendCodeEnabled { get; private set; }

        public static void SetSendCodeEnabled(bool enabled)
        {
            SendCodeEnabled = enabled;
        }

        public CommandDispatcher(string authToken)
        {
            _authToken = authToken;
            _commands = new Dictionary<string, IAcadCommand>
            {
                { "get_drawing_info",        new GetDrawingInfoHandler() },
                { "get_entity_properties",   new GetEntityPropertiesHandler() },
                { "get_selected_texts",      new GetSelectedTextsHandler() },
                { "list_layers",             new ListLayersHandler() },
                { "query_entities",          new QueryEntitiesHandler() },
                { "count_entities",          new CountEntitiesHandler() },
                { "select_by_layer",         new SelectByLayerHandler() },
                { "select_by_type",          new SelectByTypeHandler() },
                { "create_layer",            new CreateLayerHandler() },
                { "create_line",             new CreateLineHandler() },
                { "create_circle",           new CreateCircleHandler() },
                { "create_point",            new CreatePointHandler() },
                { "create_polyline",         new CreatePolylineHandler() },
                { "create_rectangle",        new CreateRectangleHandler() },
                { "create_arc",              new CreateArcHandler() },
                { "create_ellipse",          new CreateEllipseHandler() },
                { "create_text",             new CreateTextHandler() },
                { "create_mtext",            new CreateMTextHandler() },
                { "create_leader",           new CreateLeaderHandler() },
                { "create_table",            new CreateTableHandler() },
                { "list_blocks",             new ListBlocksHandler() },
                { "get_block_attributes",    new GetBlockAttributesHandler() },
                { "insert_block",            new InsertBlockHandler() },
                { "set_block_attributes",    new SetBlockAttributesHandler() },
                { "explode_block",           new ExplodeBlockHandler() },
                { "create_linear_dimension", new CreateLinearDimensionHandler() },
                { "create_aligned_dimension", new CreateAlignedDimensionHandler() },
                { "create_radial_dimension", new CreateRadialDimensionHandler() },
                { "create_diameter_dimension", new CreateDiameterDimensionHandler() },
                { "change_layer",            new ChangeLayerHandler() },
                { "change_color",            new ChangeColorHandler() },
                { "move_entities",           new MoveEntitiesHandler() },
                { "rotate_entities",         new RotateEntitiesHandler() },
                { "scale_entities",          new ScaleEntitiesHandler() },
                { "copy_entities",           new CopyEntitiesHandler() },
                { "erase_entities",          new EraseEntitiesHandler() },
                { "offset_entities",         new OffsetEntitiesHandler() },
                { "update_texts",            new UpdateTextsHandler() },
                { "send_code",               new SendCodeHandler() },
                { "run_lisp",                new RunLispHandler() },
                { "apply_unicode_style",     new ApplyUnicodeStyleHandler() },
                { "collapse_and_rewrite",    new CollapseAndRewriteHandler() },
                { "translate_and_rewrite",   new TranslateAndRewriteHandler() },
                { "list_baked_tools",        new ListBakedToolsHandler() },
                { "zoom_extents",            new ZoomExtentsHandler() },
                { "zoom_window",             new ZoomWindowHandler() },
                { "zoom_to_entity",          new ZoomToEntityHandler() },
                { "capture_view_image",      new CaptureViewImageHandler() },
                { "inspect_view_region",     new InspectViewRegionHandler() },
                { "restore_view",            new RestoreViewHandler() },
                { "export_dxf",              new ExportDxfHandler() },
                { "get_variables",           new GetVariablesHandler() },
                { "set_system_variable",     new SetSystemVariableHandler() },
                { "save_drawing",            new SaveDrawingHandler() },
                { "purge_drawing",           new PurgeDrawingHandler() },
            };
            _commands.Add("apply_bake", new ApplyBakeSuggestionHandler((cmd, parameters) => ValidateCommand(cmd, parameters, out _)));
            _commands.Add("batch_execute", new BatchExecuteHandler(ExecuteCommand));
            _commands.Add("run_baked_tool", new RunBakedToolHandler(ExecuteCommand));
        }

        public string Dispatch(string requestLine)
        {
            string id = null;
            string cmd = null;
            JToken parameters = null;
            try
            {
                var request = JObject.Parse(requestLine);
                id = (string)request["id"];

                var auth = (string)request["auth"];
                if (!string.Equals(auth, _authToken, StringComparison.Ordinal))
                    return ErrorJson(id, "unauthorized");

                cmd = (string)request["cmd"];
                parameters = request["params"];

                if (string.Equals(cmd, "set_tool_catalog", StringComparison.Ordinal))
                {
                    var catalogError = ToolCatalogStore.AcceptJson(parameters);
                    if (catalogError != null)
                        return ErrorJson(id, catalogError);
                    return SerializeResponse(id, cmd, CommandResult.Success(new { accepted = ToolCatalogStore.Snapshot().Length }));
                }

                if (PipeControlFrameProtocol.IsControlPipeOnlyCommand(cmd))
                    return ErrorJson(id, "job control commands require the control pipe");

#if ACAD2026
                if (string.Equals(cmd, "bind_active_document", StringComparison.Ordinal))
                    return SerializeResponse(id, cmd, DispatchBindActiveDocument(parameters));
                if (string.Equals(cmd, "job_submit", StringComparison.Ordinal))
                    return SerializeResponse(id, cmd, DispatchJobSubmit(parameters));
                if (string.Equals(cmd, "job_get", StringComparison.Ordinal))
                    return ErrorJson(id, "job_get requires the control pipe");
                if (string.Equals(cmd, "job_cancel", StringComparison.Ordinal))
                    return ErrorJson(id, "job_cancel requires the control pipe");

                return SerializeResponse(id, cmd, CommandResult.Fail(
                    "Direct CAD command execution is disabled on AutoCAD 2026. Use the plugin execution queue."));
#else

                var preflight = ValidateCommand(cmd, parameters, out var preflightHandler);
                if (!preflight.Ok)
                {
                    NotifyCompleted(cmd, null, false, preflight.Error, preflightHandler?.Description);
                    return SerializeResponse(id, cmd, preflight);
                }

                var result = DocumentInvoker.Invoke(doc => ExecuteCommand(doc, cmd, parameters));

                string resultJson = null;
                if (result.Ok)
                {
                    try
                    {
                        var filtered = McpResponsePrivacy.FilterResult(cmd, result.Result);
                        if (filtered != null)
                            resultJson = JsonConvert.SerializeObject(filtered);
                    }
                    catch { }
                }

                NotifyCompleted(cmd, resultJson, result.Ok, result.Error, preflightHandler?.Description);
                return SerializeResponse(id, cmd, result);
#endif
            }
            catch (Exception ex)
            {
                NotifyCompleted(cmd, null, false, ex.Message, null);
                return ErrorJson(id, ex.Message);
            }
        }

        /// <summary>
        /// Handles the deliberately narrow control-pipe protocol. It authenticates
        /// before dispatch and exposes only immediate job status, queued cancel,
        /// and read-only historical snapshots.
        /// </summary>
        public string DispatchControl(string requestLine)
        {
            string id = null;
            try
            {
                JObject request = JObject.Parse(requestLine);
                id = (string)request["id"];

                string auth = (string)request["auth"];
                if (!string.Equals(auth, _authToken, StringComparison.Ordinal))
                    return ErrorJson(id, "unauthorized");

                if (request.Properties().Any(property => property.Name != "id"
                    && property.Name != "auth" && property.Name != "cmd" && property.Name != "params"))
                {
                    return ErrorJson(id, "invalid_control_request");
                }

                string cmd = (string)request["cmd"];
                JToken parameters = request["params"];
                if (!PipeControlFrameProtocol.IsAllowedControlCommand(cmd))
                    return ErrorJson(id, "command_not_allowed_on_control_pipe");
#if ACAD2026
                if (string.Equals(cmd, "job_get", StringComparison.Ordinal))
                    return SerializeResponse(id, cmd, DispatchJobGet(parameters));
                if (string.Equals(cmd, "job_cancel", StringComparison.Ordinal))
                    return SerializeResponse(id, cmd, DispatchJobCancel(parameters));
                if (string.Equals(cmd, "job_get_historical", StringComparison.Ordinal))
                    return SerializeResponse(id, cmd, DispatchJobGetHistorical(parameters));
                if (string.Equals(cmd, "job_write_finalize", StringComparison.Ordinal))
                    return SerializeResponse(id, cmd, DispatchWriteFinalize(parameters));
#endif
                return ErrorJson(id, "command_not_allowed_on_control_pipe");
            }
            catch (Exception ex)
            {
                return ErrorJson(id, ex.Message);
            }
        }

#if ACAD2026
        private static CommandResult DispatchBindActiveDocument(JToken parameters)
        {
            if (!TryGetStrictObject(parameters, Array.Empty<string>(), out _))
                return CommandResult.Fail("invalid_internal_params");

            try
            {
                JObject documentInfo=null;
                AcadDocumentContextBinding binding = DocumentInvoker.Invoke(document =>
                {
                    var activeDocument = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    if (!ReferenceEquals(document, activeDocument))
                        throw new InvalidOperationException("The active document changed while binding.");
                    documentInfo=FoundationDocumentInfo.Read(document);
                    return App.DocumentContextRegistry.BindActiveDocument();
                });

                return CommandResult.Success(new
                {
                    context_id = binding.ContextId,
                    document_info = documentInfo,
                    complete_target_json = binding.CompleteTargetJson,
                    journal_scope_id = App.PluginJournal.JournalScopeId,
                    journal_path_digest = App.PluginJournal.JournalPathDigest
                });
            }
            catch
            {
                return CommandResult.Fail("document_bind_failed");
            }
        }

        private static CommandResult DispatchJobSubmit(JToken parameters)
        {
            if (!TryGetStrictObject(parameters,
                new[] { "request_id", "context_id", "tool_name", "canonical_parameters_json" },
                out JObject input)
                || !TryGetRequiredString(input, "request_id", out string requestId)
                || !TryGetRequiredString(input, "context_id", out string contextId)
                || !TryGetRequiredString(input, "tool_name", out string toolName)
                || !TryGetRequiredString(input, "canonical_parameters_json", out string canonicalParametersJson))
            {
                return CommandResult.Fail("invalid_internal_params");
            }

            if (!IsValidPluginIdentifier(requestId))
                return CommandResult.Fail("invalid_request_id");

            bool isFoundationWrite = toolName == FoundationDrawingPlan.PluginToolName;
            bool isH1Write = toolName == H1FixedHatchWriteContract.PluginToolName;
            bool isD1Write = string.Equals(toolName, Road08R01D1WriteContract.PluginToolName, StringComparison.Ordinal);
            bool isD1Preflight = string.Equals(toolName, Road08R01D1WriteContract.PreflightPluginToolName, StringComparison.Ordinal);
            bool isH1Preflight = string.Equals(toolName, H1FixedHatchPreflightContract.PluginToolName, StringComparison.Ordinal);
            string writeRiskKey = null;
            if (isFoundationWrite)
            {
                if(!App.FoundationWriteEnabled||!FoundationDrawingPlan.IsCanonicalValid(canonicalParametersJson))return CommandResult.Fail("foundation_plan_disabled_or_invalid");
            }
            else if (isD1Write)
            {
                if (!App.D1WritePilotEnabled)
                    return CommandResult.Fail("d1_write_pilot_disabled_in_plugin");
                if (!string.Equals(canonicalParametersJson, Road08R01D1WriteContract.CanonicalParametersJson,
                    StringComparison.Ordinal))
                    return CommandResult.Fail("d1_write_parameters_not_allowlisted");
            }
            else if (isH1Write)
            {
                if (!App.H1WritePilotEnabled || canonicalParametersJson != H1FixedHatchWriteContract.CanonicalParametersJson)
                    return CommandResult.Fail("h1_write_disabled_or_parameters_invalid");
            }
            else if (isH1Preflight)
            {
                if (!App.H1PreflightPilotEnabled) return CommandResult.Fail("h1_preflight_disabled_in_plugin");
                if (canonicalParametersJson != H1FixedHatchPreflightContract.CanonicalParametersJson)
                    return CommandResult.Fail("h1_preflight_parameters_not_allowlisted");
            }
            else if (isD1Preflight)
            {
                if (!App.D1PreflightPilotEnabled)
                    return CommandResult.Fail("d1_preflight_pilot_disabled_in_plugin");
                if (!string.Equals(canonicalParametersJson, Road08R01D1WriteContract.PreflightCanonicalParametersJson,
                    StringComparison.Ordinal))
                    return CommandResult.Fail("d1_preflight_parameters_not_allowlisted");
            }
            else
            {
                if (toolName is not ("list_layers" or "get_entity_properties" or "query_entities"))
                    return CommandResult.Fail("tool_not_allowlisted");

                if (!TryParseCanonicalReadParameters(toolName, canonicalParametersJson,
                    out _, out string parameterError))
                    return CommandResult.Fail(parameterError);
            }

            string completeTargetJson;
            try
            {
                // This registry lookup reads only its immutable binding table and
                // is explicitly safe from the pipe worker thread.
                completeTargetJson = App.DocumentContextRegistry.GetCanonicalTargetJson(contextId);
                if (isD1Write || isH1Write || isFoundationWrite)
                {
                    string savedPath = App.DocumentContextRegistry.GetSavedPathForWrite(contextId);
                    if (!DrawingRiskKey.TryNormalizePath(savedPath, out writeRiskKey))
                        return CommandResult.Fail("d1_write_requires_a_saved_local_path");
                }
            }
            catch
            {
                return CommandResult.Fail("invalid_document_context");
            }

            try
            {
                if (!App.PluginExecutionQueue.TrySubmit(
                    new PluginJobSubmission(
                        requestId,
                        contextId,
                        toolName,
                        canonicalParametersJson,
                        completeTargetJson,
                        isWrite: isD1Write || isH1Write || isFoundationWrite,
                        riskKeys: isD1Write || isH1Write || isFoundationWrite ? new[] { writeRiskKey } : Array.Empty<string>(),
                        globalFence: false),
                    DateTimeOffset.UtcNow,
                    out PluginSubmitResult submitted)
                    || submitted == null)
                {
                    return CommandResult.Fail(PluginExecutionQueue.QueueHaltedErrorCode);
                }

                return CommandResult.Success(new
                {
                    disposition = ToProtocolValue(submitted.Disposition),
                    job = submitted.Job == null ? null : ToProtocolSnapshot(submitted.Job)
                });
            }
            catch
            {
                return CommandResult.Fail("job_submit_failed");
            }
        }

        private static CommandResult DispatchJobGet(JToken parameters)
        {
            if (!TryGetStrictObject(parameters, new[] { "request_id", "wait_ms" }, out JObject input)
                || !TryGetRequiredString(input, "request_id", out string requestId))
            {
                return CommandResult.Fail("invalid_internal_params");
            }

            if (!IsValidPluginIdentifier(requestId))
                return CommandResult.Fail("invalid_request_id");

            // The control pipe performs immediate snapshots only. Keeping this
            // lookup non-blocking bounds each control request and leaves queue
            // execution to AutoCAD's Idle pump.
            if (input["wait_ms"] != null)
            {
                if (input["wait_ms"].Type != JTokenType.Integer)
                {
                    return CommandResult.Fail("invalid_wait_ms");
                }

                try
                {
                    if (input["wait_ms"].Value<long>() != 0)
                        return CommandResult.Fail("invalid_wait_ms");
                }
                catch
                {
                    return CommandResult.Fail("invalid_wait_ms");
                }
            }

            try
            {
                PluginJobSnapshot snapshot = App.PluginJournal.GetSnapshot(requestId);
                return CommandResult.Success(new
                {
                    found = snapshot != null,
                    job = snapshot == null ? null : ToProtocolSnapshot(snapshot)
                });
            }
            catch
            {
                return CommandResult.Fail("job_get_failed");
            }
        }

        private static CommandResult DispatchJobCancel(JToken parameters)
        {
            if (!TryGetStrictObject(parameters, new[] { "request_id" }, out JObject input)
                || !TryGetRequiredString(input, "request_id", out string requestId))
            {
                return CommandResult.Fail("invalid_internal_params");
            }

            if (!IsValidPluginIdentifier(requestId))
                return CommandResult.Fail("invalid_request_id");

            try
            {
                PluginCancelResult cancelled = App.PluginJournal.CancelQueued(requestId, DateTimeOffset.UtcNow);
                return CommandResult.Success(new
                {
                    disposition = ToProtocolValue(cancelled.Disposition),
                    reason = cancelled.Reason,
                    job = cancelled.Job == null ? null : ToProtocolSnapshot(cancelled.Job)
                });
            }
            catch
            {
                return CommandResult.Fail("job_cancel_failed");
            }
        }

        private static CommandResult DispatchJobGetHistorical(JToken parameters)
        {
            if (!TryGetStrictObject(parameters, new[] { "request_id" }, out JObject input)
                || !TryGetRequiredString(input, "request_id", out string requestId))
                return CommandResult.Fail("invalid_internal_params");
            if (!IsValidPluginIdentifier(requestId))
                return CommandResult.Fail("invalid_request_id");

            try
            {
                PluginHistoricalJobResult historical = App.PluginJournal.GetHistoricalSnapshot(requestId);
                PluginOwnerIdentity observer = historical.ObserverIdentity;
                return CommandResult.Success(new
                {
                    found = historical.Found,
                    job = historical.Job == null ? null : ToProtocolSnapshot(historical.Job),
                    observer_identity = new
                    {
                        owner_id = observer.OwnerId,
                        process_id = observer.ProcessId,
                        process_started_at_utc = observer.ProcessStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        provider_generation = observer.PluginGeneration
                    },
                    old_owner_liveness = ToProtocolValue(historical.OldOwnerLiveness)
                });
            }
            catch
            {
                return CommandResult.Fail("job_get_historical_failed");
            }
        }

        private static CommandResult DispatchWriteFinalize(JToken parameters)
        {
            if (!App.D1WritePilotEnabled && !App.H1WritePilotEnabled && !App.FoundationWriteEnabled)
                return CommandResult.Fail("d1_write_pilot_disabled_in_plugin");
            if (!TryGetStrictObject(parameters,
                new[] { "request_id", "context_id", "staged_plugin_sequence", "readback_revision", "readback_digest", "committed_result_digest" },
                out JObject input)
                || !TryGetRequiredString(input, "request_id", out string requestId)
                || !TryGetRequiredString(input, "context_id", out string contextId)
                || !TryGetInt64(input, "staged_plugin_sequence", out long stagedSequence)
                || !TryGetInt64(input, "readback_revision", out long readbackRevision)
                || !TryGetRequiredString(input, "readback_digest", out string readbackDigest)
                || !TryGetRequiredString(input, "committed_result_digest", out string committedResultDigest)
                || !IsValidPluginIdentifier(requestId) || !IsValidPluginIdentifier(contextId)
                || stagedSequence < 0 || readbackRevision < 0)
                return CommandResult.Fail("invalid_internal_params");

            try
            {
                PluginJobSnapshot snapshot = App.PluginJournal.GetSnapshot(requestId);
                if (snapshot == null || !snapshot.IsWrite
                    || !(App.D1WritePilotEnabled && snapshot.ToolName == Road08R01D1WriteContract.PluginToolName || App.H1WritePilotEnabled && snapshot.ToolName == H1FixedHatchWriteContract.PluginToolName || App.FoundationWriteEnabled && snapshot.ToolName == FoundationDrawingPlan.PluginToolName)
                    || snapshot.ContextId != contextId
                    || snapshot.Phase != PluginJobPhase.Completed
                    || snapshot.Effects != PluginEffectState.Committed
                    || snapshot.Verification != PluginVerificationState.Passed
                    || snapshot.VerificationStagedSequence != stagedSequence
                    || snapshot.ReadbackRevision != readbackRevision
                    || !string.Equals(snapshot.ReadbackDigest, readbackDigest, StringComparison.Ordinal)
                    || !string.Equals(snapshot.CommittedResultDigest, committedResultDigest, StringComparison.Ordinal))
                    return CommandResult.Fail("write_finalize_stage_mismatch");

                if (snapshot.ToolName == H1FixedHatchWriteContract.PluginToolName
                    && !(App.J10WritePlan==null?H1FixedHatchWriteContract.VerifyReadback(snapshot.ResultJson, snapshot.VerificationResultJson)
                        :J10FixedHatchWriteContract.VerifyReadback(snapshot.ResultJson,snapshot.VerificationResultJson,App.J10WritePlan)))
                    return CommandResult.Fail("h1_finalize_readback_mismatch");
                if(snapshot.ToolName==FoundationDrawingPlan.PluginToolName&&!FoundationDrawingPlan.VerifyReadback(snapshot.ResultJson,snapshot.VerificationResultJson,snapshot.CanonicalParametersJson))return CommandResult.Fail("foundation_finalize_readback_mismatch");
                bool stagePending = snapshot.PluginSequence == stagedSequence
                    && !snapshot.WriteFenceFinalized && !snapshot.WriteFenceReleased;
                bool alreadyFinalized = snapshot.PluginSequence == checked(stagedSequence + 1)
                    && snapshot.WriteFenceFinalized && snapshot.WriteFenceReleased;
                if (!stagePending && !alreadyFinalized)
                    return CommandResult.Fail("write_finalize_stage_mismatch");

                var finalize = new PluginFinalizeVerifiedWriteFenceRequest(
                    snapshot.RequestId, snapshot.ContextId, snapshot.CompleteTargetJson,
                    snapshot.OwnerId, snapshot.ProviderGeneration, snapshot.PayloadDigest,
                    snapshot.CommittedResultDigest, stagedSequence, readbackRevision, readbackDigest);
                PluginTransitionResult result = App.PluginJournal.FinalizeVerifiedWriteFence(
                    finalize, DateTimeOffset.UtcNow);
                if (result.Disposition is not (PluginTransitionDisposition.Applied or PluginTransitionDisposition.Duplicate)
                    || result.Job is not { WriteFenceFinalized: true, WriteFenceReleased: true } finalized
                    || finalized.PluginSequence != checked(stagedSequence + 1))
                    return CommandResult.Fail("write_finalize_not_confirmed");

                return CommandResult.Success(new
                {
                    disposition = ToProtocolValue(result.Disposition),
                    job = ToProtocolSnapshot(finalized)
                });
            }
            catch
            {
                return CommandResult.Fail("job_write_finalize_failed");
            }
        }

        internal static bool VerifyExactPluginContext(string contextId, string completeTargetJson)
        {
            try
            {
                App.DocumentContextRegistry.VerifyExact(contextId, completeTargetJson);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static PluginReadOnlyJobResult ExecuteReadOnlyJob(PluginJobSnapshot job)
        {
            if (job == null || job.IsWrite
                || job.ToolName is not ("list_layers" or "get_entity_properties" or "query_entities" or "d1_preflight_r01" or "h1_preflight_fixed"))
                return PluginReadOnlyJobResult.Failure("tool_not_allowlisted", "The job is not an allowlisted read-only tool.");
            if (job.ToolName == H1FixedHatchPreflightContract.PluginToolName && !App.H1PreflightPilotEnabled)
                return PluginReadOnlyJobResult.Failure("h1_preflight_disabled", "The fixed H1 preflight is disabled in this process.");
            if (string.Equals(job.ToolName, "d1_preflight_r01", StringComparison.Ordinal)
                && !App.D1PreflightPilotEnabled)
                return PluginReadOnlyJobResult.Failure("d1_preflight_pilot_disabled", "The fixed D1 preflight is disabled in this AutoCAD process.");

            if (!TryParseCanonicalReadParameters(job.ToolName, job.CanonicalParametersJson,
                out JToken parameters, out _))
                return PluginReadOnlyJobResult.Failure("invalid_tool_params", "The read-only tool parameters are invalid.");

            try
            {
                return App.DocumentContextRegistry.WithExactDocumentLock(
                    job.ContextId,
                    job.CompleteTargetJson,
                    document =>
                    {
                        IAcadCommand handler = CreateReadOnlyJobHandler(job.ToolName);
                        CommandResult commandResult = handler.Execute(document, parameters);
                        if (commandResult == null || !commandResult.Ok)
                        {
                            return PluginReadOnlyJobResult.Failure(
                                job.ToolName + "_failed",
                                McpResponsePrivacy.SanitizeError(commandResult?.Error ?? "The read-only tool failed."));
                        }

                        object privacyFiltered = string.Equals(job.ToolName, "get_entity_properties", StringComparison.Ordinal)
                            ? McpResponsePrivacy.FilterResult(job.ToolName, commandResult.Result,
                                MaxEntityPropertiesJobResultBytes)
                            : McpResponsePrivacy.FilterResult(job.ToolName, commandResult.Result);
                        string resultJson = JsonConvert.SerializeObject(privacyFiltered, Formatting.None);
                        return PluginReadOnlyJobResult.Success(resultJson);
                    });
            }
            catch
            {
                return PluginReadOnlyJobResult.Failure(
                    job.ToolName + "_failed",
                    "The read-only job could not be completed for its bound document.");
            }
        }

        internal static PluginWriteJobResult ExecuteFoundationWriteJob(PluginJobSnapshot job)
        {
            if(!App.FoundationWriteEnabled||job==null||!job.IsWrite||job.ToolName!=FoundationDrawingPlan.PluginToolName||!FoundationDrawingPlan.IsCanonicalValid(job.CanonicalParametersJson))return PluginWriteJobResult.RejectedBeforeTransaction("foundation_plan_disabled_or_invalid","Drawing plan invalid or disabled.");
            return PluginWriteOutcomeGuard.Execute(invokeHandler=>App.DocumentContextRegistry.WithExactDocumentLock(job.ContextId,job.CompleteTargetJson,document=>invokeHandler(()=>FoundationPlanWriteHandler.Execute(document,job))));
        }

        internal static PluginWriteJobResult ExecuteH1FixedWriteJob(PluginJobSnapshot job)
        {
            if (!App.H1WritePilotEnabled || job == null || !job.IsWrite || job.ToolName != H1FixedHatchWriteContract.PluginToolName || job.CanonicalParametersJson != "{}")
                return PluginWriteJobResult.RejectedBeforeTransaction("h1_write_disabled_or_invalid", "Only the frozen H1 contract is allowed.");
            return PluginWriteOutcomeGuard.Execute(invokeHandler => App.DocumentContextRegistry.WithExactDocumentLock(
                job.ContextId, job.CompleteTargetJson, document => invokeHandler(() => H1FixedHatchWriteHandler.Execute(document, job))));
        }

        internal static PluginWriteJobResult ExecuteD1R01WriteJob(PluginJobSnapshot job)
        {
            if (!App.D1WritePilotEnabled)
                return PluginWriteJobResult.RejectedBeforeTransaction(
                    "d1_write_pilot_disabled_in_plugin", "The fixed D1 write pilot is disabled in this AutoCAD process.");
            if (job == null || !job.IsWrite
                || job.ToolName != Road08R01D1WriteContract.PluginToolName
                || !D1ParametersMatchContract(job.CanonicalParametersJson))
                return PluginWriteJobResult.RejectedBeforeTransaction(
                    "d1_write_contract_mismatch", "Only the frozen Road08 R01 callout contract is allowed.");

            return PluginWriteOutcomeGuard.Execute(invokeHandler =>
                App.DocumentContextRegistry.WithExactDocumentLock(
                    job.ContextId, job.CompleteTargetJson,
                    document => invokeHandler(() => Road08R01WriteHandler.Execute(document, job))));
        }

        private static bool D1ParametersMatchContract(string json)
        {
            try
            {
                // The journal canonicalizes JSON before persisting it. Parse both
                // representations so escaped and literal Unicode compare equally;
                // keep DispatchJobSubmit's raw frozen-contract gate unchanged.
                return JToken.DeepEquals(JObject.Parse(json),
                    JObject.Parse(Road08R01D1WriteContract.CanonicalParametersJson));
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseCanonicalReadParameters(string toolName, string canonicalJson,
            out JToken parameters, out string error)
        {
            parameters = null;
            if (string.Equals(toolName, "list_layers", StringComparison.Ordinal))
            {
                bool valid = TryParseCanonicalListLayersParameters(canonicalJson, out error);
                if (valid) parameters = JToken.Parse(canonicalJson);
                return valid;
            }

            if (string.Equals(toolName, "d1_preflight_r01", StringComparison.Ordinal)
                || toolName == H1FixedHatchPreflightContract.PluginToolName)
            {
                bool valid = TryParseCanonicalListLayersParameters(canonicalJson, out error);
                if (valid) parameters = JToken.Parse(canonicalJson);
                return valid;
            }

            if (string.Equals(toolName, "query_entities", StringComparison.Ordinal))
                return PluginReadOnlyJobParameters.TryParseCanonicalQueryEntities(canonicalJson,
                    out parameters, out error);

            error = "invalid_tool_params";
            if (!string.Equals(toolName, "get_entity_properties", StringComparison.Ordinal)
                || !HandleBatchRequest.TryParseCanonicalJson(canonicalJson, out _, out _))
                return false;

            try
            {
                JToken parsed = JToken.Parse(canonicalJson);
                if (!string.Equals(JsonConvert.SerializeObject(parsed, Formatting.None), canonicalJson,
                    StringComparison.Ordinal))
                    return false;

                SchemaValidationResult validation = SchemaValidator.Validate(
                    "get_entity_properties", parsed, CommandSchemas.GetEntityProperties);
                if (!validation.Ok)
                    return false;

                parameters = parsed;
                error = null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IAcadCommand CreateReadOnlyJobHandler(string toolName)
        {
            switch (toolName)
            {
                case "list_layers":
                    return new ListLayersHandler();
                case "get_entity_properties":
                    return new GetEntityPropertiesHandler();
                case "query_entities":
                    return new QueryEntitiesHandler();
                case "d1_preflight_r01":
                    return new D1Road08PreflightHandler();
                case "h1_preflight_fixed":
                    return new H1FixedHatchPreflightHandler();
                default:
                    throw new InvalidOperationException("The tool is not an allowlisted read-only tool.");
            }
        }

        private static bool TryParseCanonicalListLayersParameters(string canonicalJson, out string error)
        {
            error = "invalid_tool_params";
            if (!TryParseListLayersParameters(canonicalJson, out JToken parameters))
                return false;

            string normalized = JsonConvert.SerializeObject(parameters, Formatting.None);
            if (!string.Equals(normalized, canonicalJson, StringComparison.Ordinal))
                return false;

            SchemaValidationResult validation = SchemaValidator.Validate(
                "list_layers", parameters, CommandSchemas.ListLayers);
            return validation.Ok;
        }

        private static bool TryParseListLayersParameters(string json, out JToken parameters)
        {
            parameters = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                JToken parsed = JToken.Parse(json);
                if (parsed.Type != JTokenType.Object || ((JObject)parsed).HasValues)
                    return false;

                SchemaValidationResult validation = SchemaValidator.Validate(
                    "list_layers", parsed, CommandSchemas.ListLayers);
                if (!validation.Ok)
                    return false;

                parameters = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetStrictObject(JToken token, IReadOnlyCollection<string> allowedNames, out JObject value)
        {
            value = token as JObject;
            if (value == null)
                return false;

            foreach (JProperty property in value.Properties())
            {
                if (!allowedNames.Contains(property.Name))
                    return false;
            }
            return true;
        }

        private static bool TryGetRequiredString(JObject input, string propertyName, out string value)
        {
            value = null;
            JToken token = input[propertyName];
            if (token == null || token.Type != JTokenType.String)
                return false;

            value = token.Value<string>();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetInt64(JObject input, string propertyName, out long value)
        {
            value = 0;
            JToken token = input?[propertyName];
            if (token == null || token.Type != JTokenType.Integer)
                return false;
            try
            {
                value = token.Value<long>();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidPluginIdentifier(string value) =>
            !string.IsNullOrEmpty(value)
            && value.Length <= 128
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[value.Length - 1])
            && !value.Any(char.IsControl);

        private static object ToProtocolSnapshot(PluginJobSnapshot job) => new
        {
            request_id = job.RequestId,
            context_id = job.ContextId,
            tool_name = job.ToolName,
            canonical_parameters_json = job.CanonicalParametersJson,
            complete_target_json = job.CompleteTargetJson,
            payload_digest = job.PayloadDigest,
            owner_id = job.OwnerId,
            provider_generation = job.ProviderGeneration,
            is_write = job.IsWrite,
            phase = ToProtocolValue(job.Phase),
            effects = ToProtocolValue(job.Effects),
            verification = ToProtocolValue(job.Verification),
            observation = ToProtocolValue(job.Observation),
            plugin_sequence = job.PluginSequence,
            result_json = job.ResultJson,
            error_code = job.ErrorCode,
            error_message = job.ErrorMessage,
            created_at_utc = job.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            updated_at_utc = job.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            queue_deadline_utc = job.QueueDeadlineUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            write_fence_released = job.WriteFenceReleased,
            global_fence = job.GlobalFence,
            held_risk_keys = job.HeldRiskKeys,
            readback_revision = job.ReadbackRevision,
            readback_digest = job.ReadbackDigest,
            verification_result_json = job.VerificationResultJson,
            verification_error_code = job.VerificationErrorCode,
            verification_error_message = job.VerificationErrorMessage,
            committed_result_digest = job.CommittedResultDigest,
            verification_staged_sequence = job.VerificationStagedSequence,
            write_fence_finalized = job.WriteFenceFinalized,
            no_effect_claim_sequence = job.NoEffectClaimSequence,
            no_effect_outcome = job.NoEffectOutcome.HasValue ? ToProtocolValue(job.NoEffectOutcome.Value) : null
        };

        private static string ToProtocolValue<T>(T value) where T : struct, Enum
        {
            string name = value.ToString();
            var result = new System.Text.StringBuilder(name.Length + 4);
            for (int index = 0; index < name.Length; index++)
            {
                char character = name[index];
                if (char.IsUpper(character) && index > 0)
                    result.Append('_');
                result.Append(char.ToLowerInvariant(character));
            }
            return result.ToString();
        }
#endif

        /// <summary>
        /// Fire-and-forget completion toast. Uses the privacy-filtered result
        /// (same policy as the wire response) — never raw result data.
        /// </summary>
        private static void NotifyCompleted(string cmd, string resultJson,
            bool success, string error, string description)
        {
            var notifier = App.ToastNotifier;
            if (notifier == null)
                return;
            try
            {
                notifier.OnCompleted(
                    cmd,
                    resultJson,
                    success,
                    success ? null : McpResponsePrivacy.SanitizeError(error),
                    description);
            }
            catch (Exception ex)
            {
                PluginLog.Debug("McpToastNotifier.OnCompleted failed: " + ex.Message);
            }
        }

        private CommandResult ExecuteCommand(Autodesk.AutoCAD.ApplicationServices.Document doc, string cmd, JToken parameters)
        {
            var preflight = ValidateCommand(cmd, parameters, out var handler);
            if (!preflight.Ok)
                return preflight;
            return handler.Execute(doc, parameters);
        }

        private CommandResult ValidateCommand(string cmd, JToken parameters, out IAcadCommand handler)
        {
            handler = null;
            // Reject before DocumentInvoker/LockDocument, including direct wire
            // callers and older servers. MCPENABLECODE cannot authorize LISP.
            if (string.Equals(cmd, "run_lisp", StringComparison.Ordinal))
                return CommandResult.Fail(LispExecutionPolicy.Refusal);

            if (string.Equals(cmd, "send_code", StringComparison.Ordinal) && !SendCodeEnabled)
                return CommandResult.Fail("code execution is disabled for this AutoCAD session. Run MCPENABLECODE in AutoCAD to re-enable.");

            if (!_commands.TryGetValue(cmd, out handler))
                return CommandResult.Fail($"unknown command: {cmd}");

            var validation = SchemaValidator.Validate(cmd, parameters, handler.Schema);
            if (!validation.Ok)
                return CommandResult.Fail(validation.Error);

            return CommandResult.Success(null);
        }

        private static string SerializeResponse(string id, string cmd, CommandResult result)
        {
            return JsonConvert.SerializeObject(new
            {
                id,
                ok = result.Ok,
                result = result.Ok ? McpResponsePrivacy.FilterResult(cmd, result.Result) : null,
                error = result.Ok ? null : McpResponsePrivacy.SanitizeError(result.Error)
            });
        }

        public static string ErrorJson(string id, string error) =>
            JsonConvert.SerializeObject(new
            {
                id,
                ok = false,
                result = (object)null,
                error = McpResponsePrivacy.SanitizeError(error)
            });
    }
}
