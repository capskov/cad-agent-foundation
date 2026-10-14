using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using Bimwright.Dwg.Plugin.Execution;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
    /// <summary>
    /// Executes the single frozen Road08 R01 D1 write. This intentionally creates
    /// an ordinary LEADER with a separately stored, associated MText entity.
    /// </summary>
    internal static class Road08R01WriteHandler
    {
        public static PluginWriteJobResult Execute(Document document, PluginJobSnapshot job)
        {
            if (document == null)
                return PluginWriteJobResult.RejectedBeforeTransaction(
                    "d1_document_unavailable", "The bound AutoCAD document is unavailable.");

            if (job == null || !job.IsWrite
                || !string.Equals(job.ToolName, Road08R01D1WriteContract.PluginToolName, StringComparison.Ordinal)
                || !HasExactContractParameters(job.CanonicalParametersJson))
            {
                return PluginWriteJobResult.RejectedBeforeTransaction(
                    "d1_contract_mismatch", "Only the frozen Road08 R01 callout contract is allowed.");
            }

            Database database;
            try
            {
                database = document.Database;
                if (database == null)
                    throw new InvalidOperationException();
            }
            catch
            {
                return PluginWriteJobResult.RejectedBeforeTransaction(
                    "d1_database_unavailable", "The bound drawing database is unavailable.");
            }

            PreflightState preflight;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                    preflight = InspectPreflight(database, transaction);
            }
            catch (WriteFailureException ex)
            {
                return PluginWriteJobResult.RejectedBeforeTransaction(ex.Code, ex.Message);
            }
            catch
            {
                return PluginWriteJobResult.RejectedBeforeTransaction(
                    "d1_preflight_failed", "The fixed Road08 R01 prerequisites could not be established.");
            }

            string preflightJson = null;
            ObjectId modelSpaceId;
            try
            {
                modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(database);
                if (modelSpaceId.IsNull)
                    throw new InvalidOperationException();
            }
            catch
            {
                return PluginWriteJobResult.RejectedBeforeTransaction(
                    "d1_model_space_unavailable", "The bound drawing model space is unavailable.");
            }

            Transaction writeTransaction = null;
            bool transactionStarted = false;
            bool commitInvoked = false;
            bool commitReturned = false;
            string leaderHandle = null;
            string mtextHandle = null;
            Exception writeException = null;
            string writeFailurePhase = "start_transaction";
            Action<string> setWritePhase = phase => writeFailurePhase = phase;

            try
            {
                writeTransaction = database.TransactionManager.StartTransaction();
                transactionStarted = true;

                // Re-run every read-side gate inside the write transaction so
                // the resource plan cannot drift between inspection and use.
                writeFailurePhase = "recheck_preflight";
                PreflightState writePreflight = InspectPreflight(database, writeTransaction);
                if (!PreflightSnapshotMatches(preflight, writePreflight))
                    throw new WriteFailureException("d1_preflight_changed",
                        "The fixed D1 prerequisites changed after the initial preflight.");
                preflight = writePreflight;

                writeFailurePhase = "choose_text_style";
                preflight.TextStyleId = EnsureSelectedTextStyle(
                    database, writeTransaction, preflight, setWritePhase);

                writeFailurePhase = "create_resources";
                PrepareD1Resources(database, writeTransaction, preflight, setWritePhase);

                writeFailurePhase = "update_text_style_evidence";
                UpdateSelectedTextStyleEvidence(database, writeTransaction, preflight, setWritePhase);

                writeFailurePhase = "serialize_final_preflight_evidence";
                preflightJson = Serialize(preflight.Evidence);

                writeFailurePhase = "open_model_space";
                BlockTableRecord modelSpace = (BlockTableRecord)writeTransaction.GetObject(
                    modelSpaceId, OpenMode.ForWrite);

                writeFailurePhase = "create_mtext";
                MText mtext = CreateCalloutMText(preflight.TextStyleId, setWritePhase);

                writeFailurePhase = "append_mtext";
                ObjectId mtextId = modelSpace.AppendEntity(mtext);

                writeFailurePhase = "register_mtext";
                writeTransaction.AddNewlyCreatedDBObject(mtext, true);

                writeFailurePhase = "read_mtext_handle";
                mtextHandle = HandleOf(mtext);

                writeFailurePhase = "create_leader";
                Leader leader = CreateCalloutLeader(preflight, setWritePhase);

                writeFailurePhase = "append_leader";
                ObjectId leaderId = modelSpace.AppendEntity(leader);

                writeFailurePhase = "register_leader";
                writeTransaction.AddNewlyCreatedDBObject(leader, true);

                writeFailurePhase = "configure_leader_annotation";
                leader.Annotation = mtextId;

                writeFailurePhase = "read_leader_handle";
                leaderHandle = HandleOf(leader);

                writeFailurePhase = "validate_appended_entities";
                if (mtextId.IsNull || leaderId.IsNull
                    || string.IsNullOrWhiteSpace(mtextHandle)
                    || string.IsNullOrWhiteSpace(leaderHandle))
                {
                    throw new WriteFailureException(
                        "d1_entity_append_failed", "The Leader and associated MText could not be appended.");
                }

                // From this line onward any exception before Commit returns is ambiguous.
                writeFailurePhase = "invoke_commit";
                commitInvoked = true;
                writeTransaction.Commit();
                writeFailurePhase = "commit_returned";
                commitReturned = true;
            }
            catch (Exception ex)
            {
                writeException = ex;
            }

            if (!commitInvoked)
            {
                bool rollbackConfirmed = false;
                if (transactionStarted && writeTransaction != null)
                {
                    try
                    {
                        writeTransaction.Abort();
                        rollbackConfirmed = true;
                    }
                    catch
                    {
                        rollbackConfirmed = false;
                    }
                }

                bool disposeConfirmed = TryDispose(writeTransaction);
                if (!transactionStarted)
                {
                    WriteFailureException expected = writeException as WriteFailureException;
                    return PluginWriteJobResult.RejectedBeforeTransaction(
                        expected?.Code ?? "d1_write_transaction_unavailable",
                        expected?.Message ?? SafeFailureDiagnostic(writeFailurePhase, writeException));
                }

                if (rollbackConfirmed && disposeConfirmed)
                {
                    WriteFailureException expected = writeException as WriteFailureException;
                    return PluginWriteJobResult.RolledBackBeforeCommit(
                        expected?.Code ?? "d1_write_rolled_back",
                        expected?.Message ?? SafeFailureDiagnostic(writeFailurePhase, writeException));
                }

                return PluginWriteJobResult.OutcomeUnknown(
                    "d1_write_cleanup_unconfirmed",
                    writeException is WriteFailureException
                        ? "The write transaction failed before Commit and rollback plus disposal could not both be confirmed."
                        : SafeFailureDiagnostic(writeFailurePhase, writeException));
            }

            if (!commitReturned)
            {
                TryDispose(writeTransaction);
                return PluginWriteJobResult.OutcomeUnknown(
                    "d1_commit_outcome_unknown",
                    SafeFailureDiagnostic(writeFailurePhase, writeException));
            }

            // A successful Commit return is authoritative for the transaction. A
            // later readback failure therefore preserves Committed with Unknown verification.
            bool committedTransactionDisposed = TryDispose(writeTransaction);
            try
            {
                return BuildCommittedResult(database, preflight, preflightJson, leaderHandle, mtextHandle,
                    allowReadback: committedTransactionDisposed);
            }
            catch
            {
                // Commit has already returned successfully. Never let failures while
                // assembling post-commit evidence escape to the dispatcher as a
                // pre-transaction rejection; the queue must retain the write fence.
                return CommittedUnknownEvidenceFallback(leaderHandle, mtextHandle);
            }
        }

        private static PluginWriteJobResult CommittedUnknownEvidenceFallback(string leaderHandle, string mtextHandle) =>
            PluginWriteJobResult.Committed(
                Road08R01D1WriteContract.BuildCommittedUnknownEvidenceJson(leaderHandle, mtextHandle),
                PluginVerificationState.Unknown,
                readbackJson: null,
                verificationErrorCode: "d1_post_commit_evidence_build_failed",
                verificationErrorMessage: "Commit returned successfully, but independent readback evidence could not be assembled.");

        internal static JObject InspectCandidatePreflight(Database database, Transaction transaction)
        {
            var blockers = new JArray();
            var required = new JObject();
            var evidence = new JObject
            {
                ["schema_version"] = Road08R01D1WriteContract.EvidenceSchemaVersion,
                ["plan_id"] = Road08R01D1WriteContract.PlanId,
                ["candidate_only"] = true,
                ["write_authorized"] = false,
                ["source_handles"] = new JArray(Road08R01D1WriteContract.SourceHandles)
            };

            try
            {
                evidence["target_layer_linetype"] = InspectTargetLayerLinetype(database, transaction);
            }
            catch
            {
                evidence["target_layer_linetype"] = new JObject
                {
                    ["expected_name"] = Road08R01D1WriteContract.TargetLayerLinetypeName,
                    ["exists"] = false,
                    ["name_matches"] = false,
                    ["inspection_status"] = "unavailable"
                };
            }

            JArray targetBlockMembers = new JArray();
            BlockTableRecord targetBlock = null;
            string targetBlockDigest = null;
            string targetBlockShapeDigest = null;
            bool targetBlockExists = false;
            JObject targetBlockEvidence = null;
            JObject sourceBlockEvidence = null;
            try
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                targetBlockExists = blocks.Has(Road08R01D1WriteContract.ArrowBlockName);
                if (!targetBlockExists)
                {
                    required["arrow_block"] = new JObject
                    {
                        ["name"] = Road08R01D1WriteContract.ArrowBlockName,
                        ["exists"] = false,
                        ["fully_inspected"] = false,
                        ["inspection_status"] = "not_found"
                    };
                    sourceBlockEvidence = InspectFixedArrowSource(database, transaction);
                    if (!IsApprovedArrowSource(sourceBlockEvidence))
                        AddCandidateBlocker(blockers, "arrow_block_source_unavailable_or_mismatched",
                            "The fixed _Dot source is missing, not static/local, incomplete, or differs from the approved shape.");
                }
                else
                {
                    targetBlock = transaction.GetObject(blocks[Road08R01D1WriteContract.ArrowBlockName],
                        OpenMode.ForRead) as BlockTableRecord;
                    if (targetBlock == null)
                        throw new InvalidOperationException();
                    targetBlockMembers = InspectBlockMembers(database, transaction, targetBlock);
                    if (targetBlockMembers.Count == 0)
                        throw new InvalidOperationException();
                    targetBlockDigest = HashBlockDefinition(targetBlock, targetBlockMembers);
                    targetBlockShapeDigest = HashBlockShape(targetBlock, targetBlockMembers);
                    targetBlockEvidence = BlockDefinitionEvidence(targetBlock, targetBlockMembers,
                        targetBlockDigest, includeMembers: true);
                    targetBlockEvidence["shape_sha256"] = targetBlockShapeDigest;
                    required["arrow_block"] = targetBlockEvidence.DeepClone();
                    if (IsNonStaticLocalBlock(targetBlock))
                        AddCandidateBlocker(blockers, "arrow_block_not_static_local",
                            "The fixed target arrow block is not a static local definition.");
                    if (!string.Equals(targetBlockShapeDigest,
                        Road08R01D1WriteContract.ArrowBlockShapeSha256, StringComparison.Ordinal))
                        AddCandidateBlocker(blockers, "arrow_block_shape_conflict",
                            "The fixed target arrow block does not match the approved complete shape.");
                    sourceBlockEvidence = InspectFixedArrowSource(database, transaction);
                    if (!IsApprovedArrowSource(sourceBlockEvidence)
                        || !BlockMetadataMatches(sourceBlockEvidence, targetBlockEvidence))
                        AddCandidateBlocker(blockers, "arrow_block_metadata_conflict",
                            "The target arrow-block metadata is unavailable, unsupported, or differs from the fixed _Dot source.");
                }
            }
            catch
            {
                AddCandidateBlocker(blockers, "arrow_block_uninspectable",
                    "The fixed target arrow block could not be fully inspected.");
                targetBlockEvidence = new JObject
                {
                    ["name"] = Road08R01D1WriteContract.ArrowBlockName,
                    ["exists"] = targetBlockExists,
                    ["fully_inspected"] = false,
                    ["inspection_status"] = "incomplete"
                };
                required["arrow_block"] = targetBlockEvidence.DeepClone();
            }

            try
            {
                evidence["arrow_block_reference_candidates"] = InspectReferenceArrowBlocks(database, transaction);
            }
            catch
            {
                evidence["arrow_block_reference_candidates"] = new JArray();
                AddCandidateBlocker(blockers, "arrow_reference_inspection_failed",
                    "The fixed reference arrow-block definitions could not be inspected.");
            }
            evidence["block_members"] = targetBlockMembers;
            evidence["block_origin"] = targetBlock == null ? JValue.CreateNull() : PointJson(targetBlock.Origin);
            evidence["block_shape_sha256"] = targetBlockShapeDigest == null
                ? JValue.CreateNull() : new JValue(targetBlockShapeDigest);
            evidence["block_geometry_sha256"] = targetBlockDigest == null
                ? JValue.CreateNull() : new JValue(targetBlockDigest);

            int duplicateCount = -1;
            JArray duplicateHandles = new JArray();
            try
            {
                duplicateCount = CountDuplicateCallouts(database, transaction, out duplicateHandles);
                if (duplicateCount > 0)
                    AddCandidateBlocker(blockers, "duplicate_callout_present",
                        "A fixed R01 duplicate marker is already present in the drawing.");
            }
            catch
            {
                AddCandidateBlocker(blockers, "duplicate_scan_failed",
                    "The drawing could not be fully checked for a duplicate R01 marker.");
            }
            evidence["duplicate_count"] = duplicateCount < 0 ? JValue.CreateNull() : new JValue(duplicateCount);
            evidence["duplicate_handles"] = duplicateHandles;

            JObject textLayerPlan = InspectCandidateLayer(database, transaction,
                Road08R01D1WriteContract.TextLayer, "text_layer", required, blockers);
            JObject leaderLayerPlan = InspectCandidateLayer(database, transaction,
                Road08R01D1WriteContract.LeaderLayer, "leader_layer", required, blockers);
            evidence["resource_creation_policy"] = Road08R01D1WriteContract.ResourceCreationPolicy;
            evidence["resource_plan"] = new JObject
            {
                ["text_layer"] = textLayerPlan,
                ["leader_layer"] = leaderLayerPlan,
                ["arrow_block"] = CreateArrowBlockResourcePlan(
                    targetBlockExists, targetBlockEvidence, sourceBlockEvidence)
            };
            evidence["block_baseline"] = new JObject
            {
                ["approved_shape_sha256"] = Road08R01D1WriteContract.ArrowBlockShapeSha256,
                ["candidate_shape_sha256"] = targetBlockShapeDigest == null
                    ? sourceBlockEvidence?.Value<string>("shape_sha256") : targetBlockShapeDigest,
                ["matches_approved_shape"] = targetBlockShapeDigest != null
                    ? string.Equals(targetBlockShapeDigest,
                        Road08R01D1WriteContract.ArrowBlockShapeSha256, StringComparison.Ordinal)
                    : IsApprovedArrowSource(sourceBlockEvidence),
                ["per_run_geometry_sha256"] = targetBlockDigest == null
                    ? JValue.CreateNull() : new JValue(targetBlockDigest),
                ["write_gate_open"] = false
            };
            JObject textStyle = InspectCandidateTextStyle(database, transaction, blockers);
            required["text_style"] = textStyle;
            InspectCandidateDimStyle(database, transaction, required, blockers);
            evidence["required"] = required;
            evidence["font_fallback_pair"] = new JObject
            {
                ["small_font_file"] = Road08R01D1WriteContract.FallbackTextFontFile,
                ["big_font_file"] = Road08R01D1WriteContract.FallbackBigFontFile,
                ["small_font_slot"] = "FileName",
                ["big_font_slot"] = "BigFontFileName",
                ["slot_mapping_source"] = "Road08 STYLE group 3/4 records and Autodesk DXF STYLE reference"
            };
            evidence["source_polylines"] = InspectCandidateSourcePolylines(database, transaction, blockers);
            evidence["blockers"] = blockers;
            evidence["write_prerequisites_clear"] = blockers.Count == 0;
            return evidence;
        }

        private static JObject InspectCandidateLayer(Database database, Transaction transaction, string name,
            string resultKey, JObject required, JArray blockers)
        {
            bool exists = false;
            bool existenceObserved = false;
            JObject before = null;
            try
            {
                LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                exists = layers.Has(name);
                existenceObserved = true;
                before = exists
                    ? LayerResourceEvidence(database, transaction,
                        (LayerTableRecord)transaction.GetObject(layers[name], OpenMode.ForRead))
                    : null;
                bool linetypeAvailable = HasTargetLayerLinetype(database, transaction);
                bool matches = exists
                    ? string.Equals(before?.Value<string>("name"), name, StringComparison.Ordinal)
                        && LayerMatchesPolicy(before)
                    : linetypeAvailable;
                if (!matches)
                    AddCandidateBlocker(blockers, exists ? resultKey + "_policy_conflict" : resultKey + "_creation_unavailable",
                        exists
                            ? "The fixed D1 layer exists but does not match the exact resource policy: " + name + "."
                            : "The Continuous linetype needed to create the fixed D1 layer is unavailable.");
                required[resultKey] = before ?? new JObject
                {
                    ["name"] = name,
                    ["exists"] = false,
                    ["fully_inspected"] = true
                };
                return new JObject
                {
                    ["name"] = name,
                    ["before_exists"] = exists,
                    ["action"] = exists ? "existing_verified" : "created",
                    ["expected"] = ExpectedLayerResource(name),
                    ["before"] = before == null ? JValue.CreateNull() : before.DeepClone(),
                    ["policy_match"] = matches,
                    ["linetype_available"] = linetypeAvailable
                };
            }
            catch
            {
                bool beforeExists = existenceObserved && exists;
                JObject beforeEvidence = before ?? new JObject
                {
                    ["name"] = name,
                    ["exists"] = beforeExists,
                    ["fully_inspected"] = false,
                    ["inspection_status"] = "unavailable"
                };
                required[resultKey] = new JObject
                {
                    ["name"] = name,
                    ["exists"] = beforeExists,
                    ["inspection_status"] = "unavailable"
                };
                AddCandidateBlocker(blockers, resultKey + "_unavailable", "A required D1 layer could not be inspected: " + name + ".");
                return new JObject
                {
                    ["name"] = name,
                    ["before_exists"] = beforeExists,
                    ["action"] = beforeExists ? "existing_verified" : "created",
                    ["expected"] = ExpectedLayerResource(name),
                    ["before"] = beforeExists || !existenceObserved ? beforeEvidence : JValue.CreateNull(),
                    ["inspection_status"] = "unavailable",
                    ["policy_match"] = false,
                    ["linetype_available"] = false
                };
            }
        }

        private static JObject ExpectedLayerResource(string name)
        {
            short colorIndex = string.Equals(name, Road08R01D1WriteContract.TextLayer, StringComparison.Ordinal)
                ? Road08R01D1WriteContract.TextLayerColorIndex
                : Road08R01D1WriteContract.LeaderLayerColorIndex;
            return new JObject
            {
                ["name"] = name,
                ["color_index"] = colorIndex,
                ["color_method"] = ColorMethod.ByAci.ToString(),
                ["linetype"] = Road08R01D1WriteContract.TargetLayerLinetypeName,
                ["is_off"] = Road08R01D1WriteContract.TargetLayerIsOff,
                ["is_frozen"] = Road08R01D1WriteContract.TargetLayerIsFrozen,
                ["is_locked"] = Road08R01D1WriteContract.TargetLayerIsLocked,
                ["is_plottable"] = Road08R01D1WriteContract.TargetLayerIsPlottable
            };
        }

        private static JObject LayerResourceEvidence(Database database, Transaction transaction,
            LayerTableRecord layer)
        {
            if (layer == null)
                throw new InvalidOperationException("A fixed D1 layer could not be inspected.");
            LinetypeTableRecord linetype = layer.LinetypeObjectId.IsNull
                ? null : transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord;
            if (linetype == null)
                throw new InvalidOperationException("A fixed D1 layer linetype could not be inspected.");
            Color color = layer.Color;
            return new JObject
            {
                ["name"] = layer.Name,
                ["exists"] = true,
                ["fully_inspected"] = true,
                ["handle"] = HandleOf(layer),
                ["color_index"] = color.ColorIndex,
                ["color_method"] = color.ColorMethod.ToString(),
                ["linetype"] = linetype.Name,
                ["linetype_handle"] = HandleOf(linetype),
                ["is_off"] = layer.IsOff,
                ["is_frozen"] = layer.IsFrozen,
                ["is_locked"] = layer.IsLocked,
                ["is_plottable"] = layer.IsPlottable
            };
        }

        private static JObject MissingLayerEvidence(string name) => new JObject
        {
            ["name"] = name,
            ["exists"] = false,
            ["fully_inspected"] = true
        };

        private static bool LayerMatchesPolicy(JObject evidence)
        {
            if (evidence == null || evidence.Value<bool?>("exists") != true
                || (evidence.Value<string>("name") != Road08R01D1WriteContract.TextLayer
                    && evidence.Value<string>("name") != Road08R01D1WriteContract.LeaderLayer))
                return false;
            JObject expected = ExpectedLayerResource(evidence.Value<string>("name"));
            return evidence.Value<int?>("color_index") == expected.Value<int?>("color_index")
                && evidence.Value<string>("color_method") == expected.Value<string>("color_method")
                && Road08R01D1WriteContract.LayerLinetypeNameMatches(evidence.Value<string>("linetype"))
                && evidence.Value<bool?>("is_off") == expected.Value<bool?>("is_off")
                && evidence.Value<bool?>("is_frozen") == expected.Value<bool?>("is_frozen")
                && evidence.Value<bool?>("is_locked") == expected.Value<bool?>("is_locked")
                && evidence.Value<bool?>("is_plottable") == expected.Value<bool?>("is_plottable");
        }

        private static bool HasTargetLayerLinetype(Database database, Transaction transaction)
        {
            return !FindTargetLayerLinetype(database, transaction).IsNull;
        }

        private static JObject InspectTargetLayerLinetype(Database database, Transaction transaction)
        {
            ObjectId id = FindTargetLayerLinetype(database, transaction);
            if (id.IsNull)
                return new JObject
                {
                    ["expected_name"] = Road08R01D1WriteContract.TargetLayerLinetypeName,
                    ["exists"] = false,
                    ["name_matches"] = false
                };

            LinetypeTableRecord record = transaction.GetObject(id, OpenMode.ForRead) as LinetypeTableRecord;
            if (record == null)
                throw new InvalidOperationException("The target D1 linetype record could not be read.");
            return new JObject
            {
                ["expected_name"] = Road08R01D1WriteContract.TargetLayerLinetypeName,
                ["exists"] = true,
                ["actual_name"] = record.Name,
                ["handle"] = HandleOf(record),
                ["name_matches"] = Road08R01D1WriteContract.LayerLinetypeNameMatches(record.Name)
            };
        }

        private static ObjectId FindTargetLayerLinetype(Database database, Transaction transaction)
        {
            LinetypeTable linetypes = (LinetypeTable)transaction.GetObject(
                database.LinetypeTableId, OpenMode.ForRead);
            foreach (ObjectId id in linetypes)
            {
                LinetypeTableRecord record = transaction.GetObject(id, OpenMode.ForRead) as LinetypeTableRecord;
                if (record != null && Road08R01D1WriteContract.LayerLinetypeNameMatches(record.Name))
                    return id;
            }
            return ObjectId.Null;
        }

        private static JObject CreateLayerResourcePlan(string name, JObject before)
        {
            bool exists = before != null;
            return new JObject
            {
                ["name"] = name,
                ["before_exists"] = exists,
                ["action"] = exists ? "existing_verified" : "created",
                ["expected"] = ExpectedLayerResource(name),
                ["before"] = before == null ? JValue.CreateNull() : before.DeepClone(),
                ["policy_match"] = !exists || LayerMatchesPolicy(before)
            };
        }

        private static JObject CreateArrowBlockResourcePlan(bool beforeExists,
            JObject before, JObject sourceCandidate)
        {
            var plan = new JObject
            {
                ["name"] = Road08R01D1WriteContract.ArrowBlockName,
                ["before_exists"] = beforeExists,
                ["action"] = beforeExists ? "existing_verified" : "created",
                ["expected"] = new JObject
                {
                    ["shape_sha256"] = Road08R01D1WriteContract.ArrowBlockShapeSha256
                },
                ["before"] = beforeExists && before != null ? before.DeepClone() : JValue.CreateNull()
            };
            if (sourceCandidate != null)
                plan["source"] = sourceCandidate.DeepClone();
            return plan;
        }

        private static JObject InspectFixedArrowSource(Database database, Transaction transaction)
        {
            const string sourceName = "_Dot";
            try
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                if (!blocks.Has(sourceName))
                    return new JObject
                    {
                        ["name"] = sourceName,
                        ["exists"] = false,
                        ["fully_inspected"] = false,
                        ["inspection_status"] = "not_found"
                    };
                BlockTableRecord definition = transaction.GetObject(blocks[sourceName], OpenMode.ForRead)
                    as BlockTableRecord;
                if (definition == null)
                    throw new InvalidOperationException();
                JArray members = InspectBlockMembers(database, transaction, definition);
                string geometryDigest = HashBlockDefinition(definition, members);
                JObject evidence = BlockDefinitionEvidence(definition, members,
                    geometryDigest, includeMembers: true);
                evidence["shape_sha256"] = HashBlockShape(definition, members);
                evidence["inspection_status"] = "complete";
                return evidence;
            }
            catch
            {
                return new JObject
                {
                    ["name"] = sourceName,
                    ["exists"] = true,
                    ["fully_inspected"] = false,
                    ["inspection_status"] = "incomplete"
                };
            }
        }

        private static bool IsApprovedArrowSource(JObject evidence)
        {
            if (evidence == null
                || evidence.Value<string>("name") != "_Dot"
                || evidence.Value<bool?>("exists") != true
                || evidence.Value<bool?>("fully_inspected") != true
                || evidence.Value<string>("inspection_status") != "complete"
                || IsNonStaticLocalEvidence(evidence)
                || !IsSupportedBlockMetadata(evidence)
                || evidence.Value<int?>("member_count") != 2
                || evidence["members"] is not JArray members
                || members.Count != 2
                || !string.Equals(evidence.Value<string>("shape_sha256"),
                    Road08R01D1WriteContract.ArrowBlockShapeSha256, StringComparison.Ordinal))
                return false;

            string[] types = members.OfType<JObject>().Select(member => member.Value<string>("type")).ToArray();
            if (types.Length != 2
                || !types.Contains(typeof(Polyline).FullName, StringComparer.Ordinal)
                || !types.Contains(typeof(Line).FullName, StringComparer.Ordinal))
                return false;

            string recomputed = Road08R01D1WriteContract.ComputeArrowBlockShapeSha256(evidence, members);
            return string.Equals(recomputed, Road08R01D1WriteContract.ArrowBlockShapeSha256,
                StringComparison.Ordinal);
        }

        private static bool IsNonStaticLocalBlock(BlockTableRecord definition) => definition == null
            || definition.IsLayout || definition.IsAnonymous || definition.IsFromExternalReference
            || definition.IsFromOverlayReference || definition.IsDynamicBlock;

        private static bool IsNonStaticLocalEvidence(JObject evidence) => evidence == null
            || evidence.Value<bool?>("is_layout") != false
            || evidence.Value<bool?>("is_anonymous") != false
            || evidence.Value<bool?>("is_external_reference") != false
            || evidence.Value<bool?>("is_overlay_reference") != false
            || evidence.Value<bool?>("is_dynamic") != false;

        private static bool IsSupportedBlockMetadata(JObject evidence)
        {
            if (evidence == null
                || evidence["explodable"]?.Type != JTokenType.Boolean
                || evidence["comments"]?.Type != JTokenType.String
                || !Enum.TryParse(evidence.Value<string>("units"), out UnitsValue units)
                || !Enum.IsDefined(typeof(UnitsValue), units)
                || !Enum.TryParse(evidence.Value<string>("block_scaling"), out BlockScaling scaling)
                || !Enum.IsDefined(typeof(BlockScaling), scaling)
                || !Enum.TryParse(evidence.Value<string>("annotative"), out AnnotativeStates annotative)
                || !Enum.IsDefined(typeof(AnnotativeStates), annotative)
                || !Enum.TryParse(evidence.Value<string>("merge_style"), out DuplicateRecordCloning mergeStyle)
                || !Enum.IsDefined(typeof(DuplicateRecordCloning), mergeStyle))
                return false;
            return true;
        }

        private static bool BlockMetadataMatches(JObject source, JObject target)
        {
            if (!IsSupportedBlockMetadata(source) || !IsSupportedBlockMetadata(target))
                return false;
            string[] properties = { "units", "block_scaling", "explodable", "comments", "annotative", "merge_style" };
            return properties.All(name => JToken.DeepEquals(source[name], target[name]));
        }

        private static JObject InspectCandidateTextStyle(Database database, Transaction transaction,
            JArray blockers)
        {
            try
            {
                TextStyleTable textStyles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
                if (!textStyles.Has(Road08R01D1WriteContract.TextStyle))
                {
                    AddCandidateBlocker(blockers, "text_style_missing", "The fixed D1 text style is missing.");
                    return new JObject
                    {
                        ["name"] = Road08R01D1WriteContract.TextStyle,
                        ["exists"] = false
                    };
                }

                TextStyleTableRecord style = (TextStyleTableRecord)transaction.GetObject(
                    textStyles[Road08R01D1WriteContract.TextStyle], OpenMode.ForRead);
                JObject fontResolution = InspectFontResolution(database, style.FileName, style.BigFontFileName);
                bool projectPairConfigured = IsConfiguredFontPair(style.FileName, style.BigFontFileName,
                    Road08R01D1WriteContract.TextFontFile, Road08R01D1WriteContract.TextBigFontFile);
                bool fallbackPairConfigured = IsConfiguredFontPair(style.FileName, style.BigFontFileName,
                    Road08R01D1WriteContract.FallbackTextFontFile, Road08R01D1WriteContract.FallbackBigFontFile);
                bool projectPairAvailable = IsFontPairResolved(fontResolution, "project_pair");
                bool fallbackPairAvailable = IsFontPairResolved(fontResolution, "fallback_pair");
                bool canUseFonts = (projectPairConfigured && projectPairAvailable)
                    || ((projectPairConfigured || fallbackPairConfigured) && fallbackPairAvailable);
                if (!canUseFonts)
                    AddCandidateBlocker(blockers, "text_font_pair_unresolved",
                        "Neither the exact project font pair nor the hash-verified fallback pair is available.");

                bool useFallback = !projectPairConfigured || !projectPairAvailable;
                bool fallbackStyleExists = textStyles.Has(Road08R01D1WriteContract.FallbackTextStyle);
                TextStyleTableRecord fallbackStyle = fallbackStyleExists
                    ? transaction.GetObject(textStyles[Road08R01D1WriteContract.FallbackTextStyle], OpenMode.ForRead)
                        as TextStyleTableRecord
                    : null;
                bool fallbackStyleCompatible = !fallbackStyleExists
                    || (fallbackStyle != null && IsFallbackStyleCompatible(style, fallbackStyle));
                if (useFallback && !fallbackStyleCompatible)
                    AddCandidateBlocker(blockers, "fallback_text_style_conflict",
                        "The dedicated D1 fallback style exists but does not match the base style and approved font pair.");

                return new JObject
                {
                    ["name"] = Road08R01D1WriteContract.TextStyle,
                    ["exists"] = true,
                    ["handle"] = HandleOf(style),
                    ["font_file"] = style.FileName,
                    ["big_font_file"] = style.BigFontFileName,
                    ["font_resolution"] = fontResolution,
                    ["selected_style_name"] = useFallback
                        ? Road08R01D1WriteContract.FallbackTextStyle : Road08R01D1WriteContract.TextStyle,
                    ["selected_pair"] = useFallback ? "fallback_pair" : "project_pair",
                    ["style_creation_required"] = useFallback && !fallbackStyleExists,
                    ["fallback_style_exists"] = fallbackStyleExists,
                    ["fallback_style_compatible"] = fallbackStyleCompatible,
                    ["fallback_pair_available"] = fallbackPairAvailable
                };
            }
            catch
            {
                AddCandidateBlocker(blockers, "text_style_unavailable", "The fixed D1 text style could not be inspected.");
                return new JObject
                {
                    ["name"] = Road08R01D1WriteContract.TextStyle,
                    ["exists"] = false,
                    ["inspection_status"] = "unavailable"
                };
            }
        }

        private static void InspectCandidateDimStyle(Database database, Transaction transaction,
            JObject required, JArray blockers)
        {
            try
            {
                DimStyleTable dimStyles = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
                bool exists = dimStyles.Has(Road08R01D1WriteContract.LeaderStyle);
                var evidence = new JObject
                {
                    ["name"] = Road08R01D1WriteContract.LeaderStyle,
                    ["exists"] = exists
                };
                if (exists)
                    evidence["handle"] = HandleOf((DimStyleTableRecord)transaction.GetObject(
                        dimStyles[Road08R01D1WriteContract.LeaderStyle], OpenMode.ForRead));
                else
                    AddCandidateBlocker(blockers, "dimstyle_missing", "The fixed D1 leader dimension style is missing.");
                required["dimstyle"] = evidence;
            }
            catch
            {
                required["dimstyle"] = new JObject
                {
                    ["name"] = Road08R01D1WriteContract.LeaderStyle,
                    ["exists"] = false,
                    ["inspection_status"] = "unavailable"
                };
                AddCandidateBlocker(blockers, "dimstyle_unavailable", "The fixed D1 leader dimension style could not be inspected.");
            }
        }

        private static JArray InspectCandidateSourcePolylines(Database database, Transaction transaction,
            JArray blockers)
        {
            var sources = new JArray();
            foreach (string handleText in Road08R01D1WriteContract.SourceHandles)
            {
                JObject sourceEvidence;
                try
                {
                    long value = long.Parse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    ObjectId id = database.GetObjectId(false, new Handle(value), 0);
                    Entity entity = id.IsNull ? null : transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity is not Polyline polyline)
                    {
                        sourceEvidence = new JObject
                        {
                            ["handle"] = handleText,
                            ["status"] = entity == null ? "not_found" : "wrong_entity_type",
                            ["actual_type"] = entity?.GetType().FullName,
                            ["actual_layer"] = entity?.Layer,
                            ["expected_type"] = typeof(Polyline).FullName,
                            ["expected_layer"] = Road08R01D1WriteContract.ExpectedSourceLayer
                        };
                        AddCandidateBlocker(blockers, "source_geometry_invalid",
                            "A fixed source handle does not resolve to the required ROAD polyline.");
                    }
                    else
                    {
                        sourceEvidence = InspectSourcePolyline(polyline);
                        string actualDigest = sourceEvidence.Value<string>("source_geometry_sha256");
                        string expectedDigest = Road08R01D1WriteContract.ExpectedSourceGeometrySha256(handleText);
                        bool matches = !string.IsNullOrWhiteSpace(expectedDigest)
                            && string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal);
                        sourceEvidence["expected_source_geometry_sha256"] = expectedDigest;
                        sourceEvidence["baseline_matches"] = matches;
                        sourceEvidence["status"] = matches ? "matches_baseline" : "candidate_mismatch";
                        if (!string.Equals(polyline.Layer, Road08R01D1WriteContract.ExpectedSourceLayer,
                            StringComparison.OrdinalIgnoreCase) || !matches)
                            AddCandidateBlocker(blockers, "source_geometry_mismatch",
                                "A fixed source polyline differs from the frozen Road08 geometry baseline.");
                    }
                }
                catch
                {
                    sourceEvidence = new JObject
                    {
                        ["handle"] = handleText,
                        ["status"] = "inspection_failed"
                    };
                    AddCandidateBlocker(blockers, "source_geometry_unavailable",
                        "A fixed source polyline could not be inspected.");
                }

                sources.Add(sourceEvidence);
            }
            return sources;
        }

        private static void AddCandidateBlocker(JArray blockers, string code, string message)
        {
            if (!blockers.OfType<JObject>().Any(item =>
                string.Equals(item.Value<string>("code"), code, StringComparison.Ordinal)))
            {
                blockers.Add(new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                });
            }
        }

        private static PreflightState InspectPreflight(Database database, Transaction transaction)
        {
            ObjectId textLayerId = ObjectId.Null;
            ObjectId leaderLayerId = ObjectId.Null;
            ObjectId textStyleId;
            ObjectId dimStyleId;
            ObjectId arrowBlockId = ObjectId.Null;
            ObjectId baseTextStyleId;
            string selectedTextStyleName;
            bool usesFallbackTextStyle;
            JObject fontResolution;
            JObject textLayerBefore = null;
            JObject leaderLayerBefore = null;
            JObject arrowBlockBefore = null;
            JObject arrowSourceEvidence = null;
            BlockTableRecord arrowDefinition = null;
            JArray blockMembers = new JArray();
            string blockGeometrySha256 = null;
            string blockShapeSha256 = null;

            try
            {
                LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                TextStyleTable textStyles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
                DimStyleTable dimStyles = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
                BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);

                if (!textStyles.Has(Road08R01D1WriteContract.TextStyle)
                    || !dimStyles.Has(Road08R01D1WriteContract.LeaderStyle))
                    throw new WriteFailureException(
                        "d1_prerequisite_missing", "The fixed D1 text or leader dimension style is missing.");

                if (layers.Has(Road08R01D1WriteContract.TextLayer))
                {
                    textLayerId = layers[Road08R01D1WriteContract.TextLayer];
                    textLayerBefore = LayerResourceEvidence(database, transaction,
                        (LayerTableRecord)transaction.GetObject(textLayerId, OpenMode.ForRead));
                    if (!string.Equals(textLayerBefore.Value<string>("name"),
                            Road08R01D1WriteContract.TextLayer, StringComparison.Ordinal)
                        || !LayerMatchesPolicy(textLayerBefore))
                        throw new WriteFailureException("d1_text_layer_policy_conflict",
                            "MCP08_TEXT exists but does not match the fixed D1 layer policy.");
                }
                if (layers.Has(Road08R01D1WriteContract.LeaderLayer))
                {
                    leaderLayerId = layers[Road08R01D1WriteContract.LeaderLayer];
                    leaderLayerBefore = LayerResourceEvidence(database, transaction,
                        (LayerTableRecord)transaction.GetObject(leaderLayerId, OpenMode.ForRead));
                    if (!string.Equals(leaderLayerBefore.Value<string>("name"),
                            Road08R01D1WriteContract.LeaderLayer, StringComparison.Ordinal)
                        || !LayerMatchesPolicy(leaderLayerBefore))
                        throw new WriteFailureException("d1_leader_layer_policy_conflict",
                            "MCP08_LEADER exists but does not match the fixed D1 layer policy.");
                }

                if ((textLayerBefore == null || leaderLayerBefore == null)
                    && !HasTargetLayerLinetype(database, transaction))
                    throw new WriteFailureException("d1_layer_linetype_missing",
                        "The fixed Continuous linetype is unavailable; no layer can be created.");

                baseTextStyleId = textStyles[Road08R01D1WriteContract.TextStyle];
                dimStyleId = dimStyles[Road08R01D1WriteContract.LeaderStyle];
                TextStyleTableRecord baseTextStyle = (TextStyleTableRecord)transaction.GetObject(baseTextStyleId, OpenMode.ForRead);
                fontResolution = InspectFontResolution(database, baseTextStyle.FileName, baseTextStyle.BigFontFileName);
                bool projectPairConfigured = IsConfiguredFontPair(baseTextStyle.FileName, baseTextStyle.BigFontFileName,
                    Road08R01D1WriteContract.TextFontFile, Road08R01D1WriteContract.TextBigFontFile);
                bool fallbackPairConfigured = IsConfiguredFontPair(baseTextStyle.FileName, baseTextStyle.BigFontFileName,
                    Road08R01D1WriteContract.FallbackTextFontFile, Road08R01D1WriteContract.FallbackBigFontFile);
                bool projectPairResolved = IsFontPairResolved(fontResolution, "project_pair");
                bool fallbackPairResolved = IsFontPairResolved(fontResolution, "fallback_pair");

                if (projectPairConfigured && projectPairResolved)
                {
                    textStyleId = baseTextStyleId;
                    selectedTextStyleName = Road08R01D1WriteContract.TextStyle;
                    usesFallbackTextStyle = false;
                }
                else if ((projectPairConfigured || fallbackPairConfigured) && fallbackPairResolved)
                {
                    usesFallbackTextStyle = true;
                    selectedTextStyleName = Road08R01D1WriteContract.FallbackTextStyle;
                    if (textStyles.Has(selectedTextStyleName))
                    {
                        ObjectId fallbackStyleId = textStyles[selectedTextStyleName];
                        TextStyleTableRecord fallbackStyle = transaction.GetObject(fallbackStyleId, OpenMode.ForRead)
                            as TextStyleTableRecord;
                        if (fallbackStyle == null || !IsFallbackStyleCompatible(baseTextStyle, fallbackStyle))
                            throw new WriteFailureException("d1_fallback_text_style_conflict",
                                "The dedicated D1 fallback text style conflicts with the approved source style or fonts.");
                        textStyleId = fallbackStyleId;
                    }
                    else
                    {
                        textStyleId = ObjectId.Null;
                    }
                }
                else
                {
                    throw new WriteFailureException("d1_text_font_unresolved",
                        "The project style is unsupported or neither project fonts nor the exact hash-verified fallback pair resolve.");
                }

                if (blocks.Has(Road08R01D1WriteContract.ArrowBlockName))
                {
                    arrowBlockId = blocks[Road08R01D1WriteContract.ArrowBlockName];
                    arrowDefinition = transaction.GetObject(arrowBlockId, OpenMode.ForRead) as BlockTableRecord;
                    if (arrowDefinition == null || IsNonStaticLocalBlock(arrowDefinition))
                        throw new WriteFailureException("d1_arrow_block_uninspectable",
                            "MCP08_YELLOW_DOT is not a static local block definition.");
                    blockMembers = InspectBlockMembers(database, transaction, arrowDefinition);
                    blockGeometrySha256 = HashBlockDefinition(arrowDefinition, blockMembers);
                    blockShapeSha256 = HashBlockShape(arrowDefinition, blockMembers);
                    if (blockMembers.Count == 0 || !string.Equals(blockShapeSha256,
                        Road08R01D1WriteContract.ArrowBlockShapeSha256, StringComparison.Ordinal))
                        throw new WriteFailureException("d1_arrow_block_shape_mismatch",
                            "MCP08_YELLOW_DOT does not match the approved complete shape.");
                    arrowBlockBefore = BlockDefinitionEvidence(arrowDefinition, blockMembers,
                        blockGeometrySha256, includeMembers: true);
                    arrowBlockBefore["shape_sha256"] = blockShapeSha256;
                    arrowSourceEvidence = InspectFixedArrowSource(database, transaction);
                    if (!IsApprovedTargetArrowBlock(arrowBlockBefore)
                        || !IsApprovedArrowSource(arrowSourceEvidence)
                        || !BlockMetadataMatches(arrowSourceEvidence, arrowBlockBefore))
                        throw new WriteFailureException("d1_arrow_block_metadata_conflict",
                            "MCP08_YELLOW_DOT has unsupported metadata or differs from the fixed _Dot source.");
                }
                else
                {
                    arrowSourceEvidence = InspectFixedArrowSource(database, transaction);
                    if (!IsApprovedArrowSource(arrowSourceEvidence))
                        throw new WriteFailureException("d1_arrow_block_source_mismatch",
                            "The fixed _Dot source is missing, not static/local, incomplete, or differs from the approved shape.");
                }
            }
            catch (WriteFailureException)
            {
                throw;
            }
            catch
            {
                throw new WriteFailureException(
                    "d1_prerequisite_unavailable", "A required D1 layer, style, or arrow resource could not be inspected.");
            }

            int duplicateCount = CountDuplicateCallouts(database, transaction, out JArray duplicateHandles);
            if (duplicateCount != 0)
                throw new WriteFailureException(
                    "d1_duplicate_callout_present", "An R01 callout with the fixed D1 label already exists.");

            JArray sources = InspectSourcePolylines(database, transaction);
            JObject textLayerPlan = CreateLayerResourcePlan(Road08R01D1WriteContract.TextLayer, textLayerBefore);
            JObject leaderLayerPlan = CreateLayerResourcePlan(Road08R01D1WriteContract.LeaderLayer, leaderLayerBefore);
            JObject arrowBlockPlan = CreateArrowBlockResourcePlan(
                !arrowBlockId.IsNull, arrowBlockBefore, arrowSourceEvidence);
            JObject requiredArrowBlock = arrowBlockBefore == null
                ? new JObject
                {
                    ["name"] = Road08R01D1WriteContract.ArrowBlockName,
                    ["exists"] = false,
                    ["fully_inspected"] = false,
                    ["inspection_status"] = "not_found"
                }
                : (JObject)arrowBlockBefore.DeepClone();
            JObject evidence = new JObject
            {
                ["schema_version"] = Road08R01D1WriteContract.EvidenceSchemaVersion,
                ["plan_id"] = Road08R01D1WriteContract.PlanId,
                ["target_layer_linetype"] = InspectTargetLayerLinetype(database, transaction),
                ["source_handles"] = new JArray(Road08R01D1WriteContract.SourceHandles),
                ["duplicate_count"] = duplicateCount,
                ["duplicate_handles"] = duplicateHandles,
                ["resource_creation_policy"] = Road08R01D1WriteContract.ResourceCreationPolicy,
                ["resource_plan"] = new JObject
                {
                    ["text_layer"] = textLayerPlan,
                    ["leader_layer"] = leaderLayerPlan,
                    ["arrow_block"] = arrowBlockPlan
                },
                ["required"] = new JObject
                {
                    ["text_layer"] = textLayerBefore ?? MissingLayerEvidence(Road08R01D1WriteContract.TextLayer),
                    ["leader_layer"] = leaderLayerBefore ?? MissingLayerEvidence(Road08R01D1WriteContract.LeaderLayer),
                    ["text_style"] = textStyleId.IsNull
                        ? PlannedFallbackTextStyleEvidence(database,
                            (TextStyleTableRecord)transaction.GetObject(baseTextStyleId, OpenMode.ForRead))
                        : TextStyleEvidence(database,
                            (TextStyleTableRecord)transaction.GetObject(textStyleId, OpenMode.ForRead),
                            usesFallbackTextStyle,
                            usesFallbackTextStyle ? HandleOf((TextStyleTableRecord)transaction.GetObject(baseTextStyleId, OpenMode.ForRead)) : null),
                    ["dimstyle"] = new JObject
                    {
                        ["name"] = Road08R01D1WriteContract.LeaderStyle,
                        ["exists"] = true,
                        ["handle"] = HandleOf((DimStyleTableRecord)transaction.GetObject(dimStyleId, OpenMode.ForRead))
                    },
                    ["arrow_block"] = requiredArrowBlock
                },
                ["base_text_style"] = TextStyleSourceEvidence(
                    (TextStyleTableRecord)transaction.GetObject(baseTextStyleId, OpenMode.ForRead)),
                ["font_fallback_pair"] = new JObject
                {
                    ["small_font_file"] = Road08R01D1WriteContract.FallbackTextFontFile,
                    ["big_font_file"] = Road08R01D1WriteContract.FallbackBigFontFile,
                    ["small_font_slot"] = "FileName",
                    ["big_font_slot"] = "BigFontFileName",
                    ["small_font_sha256"] = Road08R01D1WriteContract.FallbackTextFontSha256,
                    ["big_font_sha256"] = Road08R01D1WriteContract.FallbackBigFontSha256,
                    ["resolution"] = InspectOneFont(database, Road08R01D1WriteContract.FallbackTextFontFile,
                            "AutoCAD HostApplicationServices.FindFile").DeepClone(),
                    ["big_font_resolution"] = InspectOneFont(database, Road08R01D1WriteContract.FallbackBigFontFile,
                            "AutoCAD HostApplicationServices.FindFile").DeepClone(),
                    ["resolved"] = IsVerifiedFallbackFont(true, InspectOneFont(database,
                            Road08R01D1WriteContract.FallbackTextFontFile, "AutoCAD HostApplicationServices.FindFile"))
                        && IsVerifiedFallbackFont(false, InspectOneFont(database,
                            Road08R01D1WriteContract.FallbackBigFontFile, "AutoCAD HostApplicationServices.FindFile"))
                },
                ["arrow_block_reference_candidates"] = InspectReferenceArrowBlocks(database, transaction),
                ["source_polylines"] = sources,
                ["block_members"] = blockMembers,
                ["block_origin"] = arrowDefinition == null ? JValue.CreateNull() : PointJson(arrowDefinition.Origin),
                ["block_shape_sha256"] = blockShapeSha256 == null ? JValue.CreateNull() : new JValue(blockShapeSha256),
                ["block_geometry_sha256"] = blockGeometrySha256 == null ? JValue.CreateNull() : new JValue(blockGeometrySha256),
                ["block_baseline"] = new JObject
                {
                    ["approved_shape_sha256"] = Road08R01D1WriteContract.ArrowBlockShapeSha256,
                    ["candidate_shape_sha256"] = arrowDefinition == null
                        ? arrowSourceEvidence?.Value<string>("shape_sha256") : blockShapeSha256,
                    ["matches_approved_shape"] = true,
                    ["per_run_geometry_sha256"] = blockGeometrySha256 == null
                        ? JValue.CreateNull() : new JValue(blockGeometrySha256),
                    ["write_gate_open"] = false
                }
            };

            return new PreflightState(evidence, textLayerId, leaderLayerId, textStyleId,
                baseTextStyleId, selectedTextStyleName, usesFallbackTextStyle,
                dimStyleId, arrowBlockId, blockMembers, blockGeometrySha256);
        }

        private static bool PreflightSnapshotMatches(PreflightState before, PreflightState current)
        {
            if (before == null || current == null)
                return false;
            JObject left = before.Evidence;
            JObject right = current.Evidence;
            string[] exactSections = { "resource_plan", "source_polylines", "duplicate_handles", "required", "base_text_style" };
            return exactSections.All(name => JToken.DeepEquals(left[name], right[name]))
                && Road08R01D1WriteContract.SameTargetLayerLinetypeIdentity(
                    left["target_layer_linetype"] as JObject,
                    right["target_layer_linetype"] as JObject)
                && left.Value<int?>("duplicate_count") == right.Value<int?>("duplicate_count");
        }

        private static void PrepareD1Resources(Database database, Transaction transaction,
            PreflightState preflight, Action<string> setPhase)
        {
            setPhase?.Invoke("validate_resource_plan");
            if (preflight == null
                || preflight.Evidence.Value<string>("resource_creation_policy") != Road08R01D1WriteContract.ResourceCreationPolicy
                || preflight.Evidence["resource_plan"] is not JObject plan
                || plan["text_layer"] is not JObject textLayerPlan
                || plan["leader_layer"] is not JObject leaderLayerPlan
                || plan["arrow_block"] is not JObject arrowBlockPlan)
                throw new WriteFailureException("d1_resource_plan_invalid",
                    "The fixed D1 resource plan is unavailable or does not match the frozen policy.");

            preflight.TextLayerId = EnsureLayerResource(database, transaction,
                Road08R01D1WriteContract.TextLayer, textLayerPlan, isTextLayer: true, setPhase);
            preflight.LeaderLayerId = EnsureLayerResource(database, transaction,
                Road08R01D1WriteContract.LeaderLayer, leaderLayerPlan, isTextLayer: false, setPhase);
            preflight.ArrowBlockId = EnsureArrowBlockResource(database, transaction, arrowBlockPlan,
                out BlockTableRecord arrowDefinition, out JArray blockMembers, setPhase);

            setPhase?.Invoke("hash_prepared_arrow_block_shape");
            string blockShapeSha256 = HashBlockShape(arrowDefinition, blockMembers);
            setPhase?.Invoke("hash_prepared_arrow_block_geometry");
            string blockGeometrySha256 = HashBlockDefinition(arrowDefinition, blockMembers);
            setPhase?.Invoke("verify_prepared_arrow_block_shape");
            if (!string.Equals(blockShapeSha256, Road08R01D1WriteContract.ArrowBlockShapeSha256,
                StringComparison.Ordinal))
                throw new WriteFailureException("d1_arrow_block_shape_mismatch",
                    "The prepared MCP08_YELLOW_DOT does not match the approved complete shape.");

            JObject required = preflight.Evidence["required"] as JObject;
            if (required == null)
                throw new WriteFailureException("d1_preflight_evidence_invalid",
                    "The required D1 resource evidence is missing.");
            setPhase?.Invoke("readback_text_layer_resource_evidence");
            required["text_layer"] = LayerResourceEvidence(database, transaction,
                (LayerTableRecord)transaction.GetObject(preflight.TextLayerId, OpenMode.ForRead));
            setPhase?.Invoke("readback_leader_layer_resource_evidence");
            required["leader_layer"] = LayerResourceEvidence(database, transaction,
                (LayerTableRecord)transaction.GetObject(preflight.LeaderLayerId, OpenMode.ForRead));

            setPhase?.Invoke("build_arrow_block_resource_evidence_and_hash_shape");
            JObject arrowEvidence = BlockDefinitionEvidence(arrowDefinition, blockMembers,
                blockGeometrySha256, includeMembers: true);
            arrowEvidence["shape_sha256"] = blockShapeSha256;
            required["arrow_block"] = arrowEvidence.DeepClone();

            preflight.BlockMembers = blockMembers;
            preflight.BlockGeometrySha256 = blockGeometrySha256;
            preflight.Evidence["block_members"] = blockMembers.DeepClone();
            preflight.Evidence["block_origin"] = PointJson(arrowDefinition.Origin);
            preflight.Evidence["block_shape_sha256"] = blockShapeSha256;
            preflight.Evidence["block_geometry_sha256"] = blockGeometrySha256;
            preflight.Evidence["block_baseline"] = new JObject
            {
                ["approved_shape_sha256"] = Road08R01D1WriteContract.ArrowBlockShapeSha256,
                ["candidate_shape_sha256"] = blockShapeSha256,
                ["matches_approved_shape"] = true,
                ["per_run_geometry_sha256"] = blockGeometrySha256,
                ["write_gate_open"] = true
            };
        }

        private static ObjectId EnsureLayerResource(Database database, Transaction transaction,
            string name, JObject plan, bool isTextLayer, Action<string> setPhase)
        {
            SetLayerResourcePhase(setPhase, isTextLayer, "validate_plan");
            if (plan == null || plan.Value<string>("name") != name
                || plan["expected"] is not JObject expected
                || !JToken.DeepEquals(expected, ExpectedLayerResource(name))
                || plan["before_exists"]?.Type != JTokenType.Boolean)
                throw new WriteFailureException("d1_layer_resource_plan_mismatch",
                    "A fixed D1 layer resource plan does not match the frozen policy.");

            bool beforeExists = plan.Value<bool>("before_exists");
            string expectedAction = beforeExists ? "existing_verified" : "created";
            if (plan.Value<string>("action") != expectedAction
                || (beforeExists && plan["before"] is not JObject)
                || (!beforeExists && plan["before"]?.Type != JTokenType.Null))
                throw new WriteFailureException("d1_layer_resource_plan_mismatch",
                    "A fixed D1 layer resource plan is internally inconsistent.");

            SetLayerResourcePhase(setPhase, isTextLayer, "lookup_linetype");
            ObjectId expectedLinetypeId = FindTargetLayerLinetype(database, transaction);
            if (expectedLinetypeId.IsNull)
                throw new WriteFailureException("d1_layer_linetype_missing",
                    "The fixed Continuous linetype is unavailable; no layer was created.");

            SetLayerResourcePhase(setPhase, isTextLayer, "open_layer_table");
            LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            SetLayerResourcePhase(setPhase, isTextLayer, "lookup_layer_name");
            bool existsNow = layers.Has(name);
            if (existsNow != beforeExists)
                throw new WriteFailureException("d1_layer_resource_changed",
                    "A fixed D1 layer changed after preflight.");
            if (existsNow)
            {
                SetLayerResourcePhase(setPhase, isTextLayer, "lookup_existing_layer_id");
                ObjectId existingId = layers[name];
                SetLayerResourcePhase(setPhase, isTextLayer, "open_existing_layer");
                LayerTableRecord existing = (LayerTableRecord)transaction.GetObject(existingId, OpenMode.ForRead);
                SetLayerResourcePhase(setPhase, isTextLayer, "readback_existing_layer_evidence");
                JObject current = LayerResourceEvidence(database, transaction, existing);
                SetLayerResourcePhase(setPhase, isTextLayer, "verify_existing_layer_policy");
                if (!LayerMatchesPolicy(current) || existing.LinetypeObjectId != expectedLinetypeId
                    || !JToken.DeepEquals(plan["before"], current))
                    throw new WriteFailureException("d1_layer_policy_conflict",
                        "An existing fixed D1 layer changed or conflicts with the approved layer policy. "
                        + LayerPolicyMismatchDetails(name, current, expectedLinetypeId));
                return existingId;
            }

            if (!string.Equals(name, Road08R01D1WriteContract.TextLayer, StringComparison.Ordinal)
                && !string.Equals(name, Road08R01D1WriteContract.LeaderLayer, StringComparison.Ordinal))
                throw new WriteFailureException("d1_layer_resource_name_not_allowlisted",
                    "Only the two fixed D1 layers can be created.");

            short colorIndex = string.Equals(name, Road08R01D1WriteContract.TextLayer, StringComparison.Ordinal)
                ? Road08R01D1WriteContract.TextLayerColorIndex
                : Road08R01D1WriteContract.LeaderLayerColorIndex;

            SetLayerResourcePhase(setPhase, isTextLayer, "upgrade_layer_table");
            layers.UpgradeOpen();
            SetLayerResourcePhase(setPhase, isTextLayer, "create_layer_record");
            var created = new LayerTableRecord();
            SetLayerResourcePhase(setPhase, isTextLayer, "set_name");
            created.Name = name;
            SetLayerResourcePhase(setPhase, isTextLayer, "set_color");
            created.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
            SetLayerResourcePhase(setPhase, isTextLayer, "set_linetype_object_id");
            created.LinetypeObjectId = expectedLinetypeId;
            SetLayerResourcePhase(setPhase, isTextLayer, "set_off_state");
            created.IsOff = Road08R01D1WriteContract.TargetLayerIsOff;
            SetLayerResourcePhase(setPhase, isTextLayer, "set_frozen_state");
            created.IsFrozen = Road08R01D1WriteContract.TargetLayerIsFrozen;
            SetLayerResourcePhase(setPhase, isTextLayer, "set_locked_state");
            created.IsLocked = Road08R01D1WriteContract.TargetLayerIsLocked;
            SetLayerResourcePhase(setPhase, isTextLayer, "set_plottable_state");
            created.IsPlottable = Road08R01D1WriteContract.TargetLayerIsPlottable;
            SetLayerResourcePhase(setPhase, isTextLayer, "add_layer_record");
            ObjectId createdId = layers.Add(created);
            SetLayerResourcePhase(setPhase, isTextLayer, "register_layer_record");
            transaction.AddNewlyCreatedDBObject(created, true);
            SetLayerResourcePhase(setPhase, isTextLayer, "readback_created_layer");
            LayerTableRecord createdLayer = (LayerTableRecord)transaction.GetObject(createdId, OpenMode.ForRead);
            SetLayerResourcePhase(setPhase, isTextLayer, "readback_created_layer_evidence");
            JObject after = LayerResourceEvidence(database, transaction, createdLayer);
            SetLayerResourcePhase(setPhase, isTextLayer, "verify_created_layer_policy");
            if (!LayerMatchesPolicy(after) || createdLayer.LinetypeObjectId != expectedLinetypeId)
                throw new WriteFailureException("d1_layer_create_readback_mismatch",
                    "A newly created fixed D1 layer did not read back with the exact approved properties. "
                    + LayerPolicyMismatchDetails(name, after, expectedLinetypeId));
            return createdId;
        }

        private static void SetLayerResourcePhase(Action<string> setPhase, bool isTextLayer, string operation)
        {
            string phase = operation switch
            {
                "validate_plan" => isTextLayer ? "text_layer_validate_plan" : "leader_layer_validate_plan",
                "lookup_linetype" => isTextLayer ? "text_layer_lookup_linetype" : "leader_layer_lookup_linetype",
                "open_layer_table" => isTextLayer ? "text_layer_open_layer_table" : "leader_layer_open_layer_table",
                "lookup_layer_name" => isTextLayer ? "text_layer_lookup_name" : "leader_layer_lookup_name",
                "lookup_existing_layer_id" => isTextLayer ? "text_layer_lookup_existing_id" : "leader_layer_lookup_existing_id",
                "open_existing_layer" => isTextLayer ? "text_layer_open_existing" : "leader_layer_open_existing",
                "readback_existing_layer_evidence" => isTextLayer ? "text_layer_readback_existing_evidence" : "leader_layer_readback_existing_evidence",
                "verify_existing_layer_policy" => isTextLayer ? "text_layer_verify_existing_policy" : "leader_layer_verify_existing_policy",
                "upgrade_layer_table" => isTextLayer ? "text_layer_upgrade_table" : "leader_layer_upgrade_table",
                "create_layer_record" => isTextLayer ? "text_layer_create_record" : "leader_layer_create_record",
                "add_layer_record" => isTextLayer ? "text_layer_add_record" : "leader_layer_add_record",
                "register_layer_record" => isTextLayer ? "text_layer_register_record" : "leader_layer_register_record",
                "readback_created_layer" => isTextLayer ? "text_layer_readback_created" : "leader_layer_readback_created",
                "readback_created_layer_evidence" => isTextLayer ? "text_layer_readback_created_evidence" : "leader_layer_readback_created_evidence",
                "verify_created_layer_policy" => isTextLayer ? "text_layer_verify_created_policy" : "leader_layer_verify_created_policy",
                "set_name" => isTextLayer ? "text_layer_set_name" : "leader_layer_set_name",
                "set_color" => isTextLayer ? "text_layer_set_color" : "leader_layer_set_color",
                "set_linetype_object_id" => isTextLayer ? "text_layer_set_linetype_object_id" : "leader_layer_set_linetype_object_id",
                "set_off_state" => isTextLayer ? "text_layer_set_off_state" : "leader_layer_set_off_state",
                "set_frozen_state" => isTextLayer ? "text_layer_set_frozen_state" : "leader_layer_set_frozen_state",
                "set_locked_state" => isTextLayer ? "text_layer_set_locked_state" : "leader_layer_set_locked_state",
                "set_plottable_state" => isTextLayer ? "text_layer_set_plottable_state" : "leader_layer_set_plottable_state",
                _ => "unknown_layer_resource_operation"
            };
            setPhase?.Invoke(phase);
        }

        private static string LayerPolicyMismatchDetails(string name, JObject actual, ObjectId expectedLinetypeId)
        {
            JObject expected = ExpectedLayerResource(name);
            expected["linetype_handle"] = IdHandle(expectedLinetypeId);
            JObject observed = actual == null ? new JObject() : (JObject)actual.DeepClone();
            string expectedName = Road08R01D1WriteContract.TargetLayerLinetypeName;
            string actualName = observed.Value<string>("linetype");
            string expectedHandle = IdHandle(expectedLinetypeId);
            string actualHandle = observed.Value<string>("linetype_handle");
            var mismatches = new JArray();

            AddLayerMismatch(mismatches, "name", observed.Value<string>("name") == name);
            AddLayerMismatch(mismatches, "color_index",
                observed.Value<int?>("color_index") == expected.Value<int?>("color_index"));
            AddLayerMismatch(mismatches, "color_method",
                observed.Value<string>("color_method") == expected.Value<string>("color_method"));
            AddLayerMismatch(mismatches, "linetype_name",
                Road08R01D1WriteContract.LayerLinetypeNameMatches(actualName));
            AddLayerMismatch(mismatches, "linetype_object_id",
                string.Equals(actualHandle, expectedHandle, StringComparison.OrdinalIgnoreCase));
            AddLayerMismatch(mismatches, "is_off",
                observed.Value<bool?>("is_off") == expected.Value<bool?>("is_off"));
            AddLayerMismatch(mismatches, "is_frozen",
                observed.Value<bool?>("is_frozen") == expected.Value<bool?>("is_frozen"));
            AddLayerMismatch(mismatches, "is_locked",
                observed.Value<bool?>("is_locked") == expected.Value<bool?>("is_locked"));
            AddLayerMismatch(mismatches, "is_plottable",
                observed.Value<bool?>("is_plottable") == expected.Value<bool?>("is_plottable"));

            return new JObject
            {
                ["layer"] = name,
                ["expected"] = expected,
                ["actual"] = observed,
                ["expected_linetype_record_name"] = expectedName,
                ["actual_linetype_record_name"] = actualName,
                ["expected_linetype_record_handle"] = expectedHandle,
                ["actual_linetype_record_handle"] = actualHandle,
                ["mismatched_fields"] = mismatches
            }.ToString(Formatting.None);
        }

        private static void AddLayerMismatch(JArray mismatches, string field, bool matches)
        {
            if (!matches)
                mismatches.Add(field);
        }

        private static ObjectId EnsureArrowBlockResource(Database database, Transaction transaction,
            JObject plan, out BlockTableRecord targetDefinition, out JArray targetMembers,
            Action<string> setPhase)
        {
            targetDefinition = null;
            targetMembers = new JArray();
            setPhase?.Invoke("validate_arrow_block_plan");
            if (plan == null
                || plan.Value<string>("name") != Road08R01D1WriteContract.ArrowBlockName
                || plan["expected"] is not JObject expected
                || expected.Value<string>("shape_sha256") != Road08R01D1WriteContract.ArrowBlockShapeSha256
                || plan["before_exists"]?.Type != JTokenType.Boolean)
                throw new WriteFailureException("d1_arrow_block_resource_plan_mismatch",
                    "The fixed D1 arrow-block resource plan does not match the approved shape.");

            bool beforeExists = plan.Value<bool>("before_exists");
            if (plan.Value<string>("action") != (beforeExists ? "existing_verified" : "created")
                || (beforeExists && plan["before"] is not JObject)
                || (!beforeExists && plan["before"]?.Type != JTokenType.Null))
                throw new WriteFailureException("d1_arrow_block_resource_plan_mismatch",
                    "The fixed D1 arrow-block resource plan is internally inconsistent.");

            setPhase?.Invoke("open_arrow_block_table");
            BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            setPhase?.Invoke("lookup_arrow_block_name");
            bool existsNow = blocks.Has(Road08R01D1WriteContract.ArrowBlockName);
            if (existsNow != beforeExists)
                throw new WriteFailureException("d1_arrow_block_resource_changed",
                    "MCP08_YELLOW_DOT changed after preflight.");

            if (existsNow)
            {
                setPhase?.Invoke("lookup_existing_arrow_block_id");
                ObjectId existingId = blocks[Road08R01D1WriteContract.ArrowBlockName];
                setPhase?.Invoke("open_existing_arrow_block");
                targetDefinition = transaction.GetObject(existingId, OpenMode.ForRead) as BlockTableRecord;
                setPhase?.Invoke("verify_existing_arrow_block_type");
                if (targetDefinition == null || IsNonStaticLocalBlock(targetDefinition))
                    throw new WriteFailureException("d1_arrow_block_not_static_local",
                        "MCP08_YELLOW_DOT is not a static local block definition.");
                setPhase?.Invoke("read_existing_arrow_block_members");
                targetMembers = InspectBlockMembers(database, transaction, targetDefinition);
                setPhase?.Invoke("hash_existing_arrow_block_geometry");
                string existingGeometrySha256 = HashBlockDefinition(targetDefinition, targetMembers);
                setPhase?.Invoke("build_existing_arrow_block_evidence_and_hash_shape");
                JObject current = BlockDefinitionEvidence(targetDefinition, targetMembers,
                    existingGeometrySha256, includeMembers: true);
                setPhase?.Invoke("hash_existing_arrow_block_shape");
                current["shape_sha256"] = HashBlockShape(targetDefinition, targetMembers);
                setPhase?.Invoke("inspect_fixed_arrow_source_for_existing_block");
                JObject currentSource = InspectFixedArrowSource(database, transaction);
                setPhase?.Invoke("verify_existing_arrow_block_evidence");
                if (!IsApprovedTargetArrowBlock(current)
                    || !IsApprovedArrowSource(currentSource)
                    || !BlockMetadataMatches(currentSource, current)
                    || !JToken.DeepEquals(plan["before"], current))
                    throw new WriteFailureException("d1_arrow_block_shape_conflict",
                        "The existing MCP08_YELLOW_DOT changed, has unsupported metadata, or differs from the approved complete shape.");
                return existingId;
            }

            if (plan["source"] is not JObject plannedSource)
                throw new WriteFailureException("d1_arrow_block_source_unavailable",
                    "The fixed _Dot source evidence is missing from the approved resource plan.");
            setPhase?.Invoke("inspect_fixed_arrow_source_for_clone");
            JObject sourceEvidence = InspectFixedArrowSource(database, transaction);
            setPhase?.Invoke("verify_fixed_arrow_source_for_clone");
            if (!IsApprovedArrowSource(sourceEvidence)
                || !JToken.DeepEquals(plannedSource, sourceEvidence))
                throw new WriteFailureException("d1_arrow_block_source_changed",
                    "The fixed _Dot source changed or no longer matches the approved complete shape.");

            setPhase?.Invoke("lookup_fixed_arrow_source_id");
            ObjectId sourceDefinitionId = blocks["_Dot"];
            setPhase?.Invoke("open_fixed_arrow_source");
            BlockTableRecord sourceDefinition = transaction.GetObject(sourceDefinitionId,
                OpenMode.ForRead) as BlockTableRecord;
            if (sourceDefinition == null)
                throw new WriteFailureException("d1_arrow_block_source_unavailable",
                    "The fixed _Dot source could not be opened for cloning.");

            setPhase?.Invoke("upgrade_arrow_block_table");
            blocks.UpgradeOpen();
            setPhase?.Invoke("create_arrow_block_definition");
            var createdDefinition = new BlockTableRecord();
            ObjectId createdId;
            try
            {
                setPhase?.Invoke("set_arrow_block_name");
                createdDefinition.Name = Road08R01D1WriteContract.ArrowBlockName;
                setPhase?.Invoke("read_arrow_source_origin");
                var sourceOrigin = sourceDefinition.Origin;
                setPhase?.Invoke("set_arrow_block_origin");
                createdDefinition.Origin = sourceOrigin;
                setPhase?.Invoke("read_arrow_source_units");
                var sourceUnits = sourceDefinition.Units;
                setPhase?.Invoke("set_arrow_block_units");
                createdDefinition.Units = sourceUnits;
                setPhase?.Invoke("read_arrow_source_block_scaling");
                var sourceBlockScaling = sourceDefinition.BlockScaling;
                setPhase?.Invoke("set_arrow_block_scaling");
                createdDefinition.BlockScaling = sourceBlockScaling;
                setPhase?.Invoke("read_arrow_source_explodable");
                var sourceExplodable = sourceDefinition.Explodable;
                setPhase?.Invoke("set_arrow_block_explodable");
                createdDefinition.Explodable = sourceExplodable;
                setPhase?.Invoke("read_arrow_source_comments");
                var sourceComments = sourceDefinition.Comments;
                setPhase?.Invoke("set_arrow_block_comments");
                createdDefinition.Comments = sourceComments;
                setPhase?.Invoke("read_arrow_source_annotative");
                var sourceAnnotative = sourceDefinition.Annotative;
                setPhase?.Invoke("set_arrow_block_annotative");
                createdDefinition.Annotative = sourceAnnotative;
                setPhase?.Invoke("read_arrow_source_merge_style");
                DuplicateRecordCloning sourceMergeStyle;
                try
                {
                    sourceMergeStyle = sourceDefinition.MergeStyle;
                }
                catch (Exception ex)
                {
                    throw new WriteFailureException("d1_arrow_block_merge_style_unavailable",
                        "The fixed _Dot MergeStyle could not be read before append.", ex);
                }
                setPhase?.Invoke("read_created_arrow_block_merge_style");
                DuplicateRecordCloning createdMergeStyle;
                try
                {
                    createdMergeStyle = createdDefinition.MergeStyle;
                }
                catch (Exception ex)
                {
                    throw new WriteFailureException("d1_arrow_block_merge_style_unavailable",
                        "The new MCP08_YELLOW_DOT MergeStyle could not be read before append.", ex);
                }
                setPhase?.Invoke("verify_created_arrow_block_merge_style");
                if (createdMergeStyle != sourceMergeStyle)
                    throw new WriteFailureException("d1_arrow_block_merge_style_mismatch",
                        "The new MCP08_YELLOW_DOT default MergeStyle does not match the fixed _Dot source.");
                setPhase?.Invoke("add_arrow_block_definition");
            }
            catch
            {
                try
                {
                    createdDefinition.Dispose();
                }
                catch
                {
                    // Preserve the original pre-append exception.
                }
                throw;
            }

            createdId = blocks.Add(createdDefinition);
            setPhase?.Invoke("register_arrow_block_definition");
            transaction.AddNewlyCreatedDBObject(createdDefinition, true);

            setPhase?.Invoke("enumerate_fixed_arrow_source_members");
            foreach (ObjectId sourceMemberId in sourceDefinition)
            {
                setPhase?.Invoke("open_fixed_arrow_source_member");
                Entity sourceMember = transaction.GetObject(sourceMemberId, OpenMode.ForRead, false) as Entity;
                setPhase?.Invoke("verify_fixed_arrow_source_member_type");
                if (sourceMember == null
                    || (sourceMember.GetType() != typeof(Polyline) && sourceMember.GetType() != typeof(Line)))
                    throw new WriteFailureException("d1_arrow_block_source_member_not_allowlisted",
                        "The fixed _Dot source contains a member outside the Polyline+Line whitelist.");
                setPhase?.Invoke("clone_fixed_arrow_source_member");
                Entity clone = sourceMember.Clone() as Entity;
                if (clone == null)
                    throw new WriteFailureException("d1_arrow_block_member_clone_failed",
                        "A fixed _Dot member could not be cloned into MCP08_YELLOW_DOT.");
                setPhase?.Invoke("append_arrow_block_member");
                createdDefinition.AppendEntity(clone);
                setPhase?.Invoke("register_arrow_block_member");
                transaction.AddNewlyCreatedDBObject(clone, true);
            }

            setPhase?.Invoke("readback_created_arrow_block");
            targetDefinition = transaction.GetObject(createdId, OpenMode.ForRead) as BlockTableRecord;
            setPhase?.Invoke("verify_created_arrow_block_type");
            if (targetDefinition == null || IsNonStaticLocalBlock(targetDefinition))
                throw new WriteFailureException("d1_arrow_block_create_readback_failed",
                    "The new MCP08_YELLOW_DOT definition could not be read back as a static local block.");
            setPhase?.Invoke("readback_created_arrow_block_members");
            targetMembers = InspectBlockMembers(database, transaction, targetDefinition);
            setPhase?.Invoke("hash_created_arrow_block_geometry");
            string createdGeometrySha256 = HashBlockDefinition(targetDefinition, targetMembers);
            setPhase?.Invoke("build_created_arrow_block_evidence_and_hash_shape");
            JObject createdEvidence = BlockDefinitionEvidence(targetDefinition, targetMembers,
                createdGeometrySha256, includeMembers: true);
            setPhase?.Invoke("hash_created_arrow_block_shape");
            createdEvidence["shape_sha256"] = HashBlockShape(targetDefinition, targetMembers);
            setPhase?.Invoke("verify_created_arrow_block_evidence");
            if (!IsApprovedTargetArrowBlock(createdEvidence)
                || !BlockMetadataMatches(plannedSource, createdEvidence))
                throw new WriteFailureException("d1_arrow_block_create_shape_mismatch",
                    "The new MCP08_YELLOW_DOT did not preserve the approved _Dot shape and block metadata.");
            return createdId;
        }

        private static bool IsApprovedTargetArrowBlock(JObject evidence) => evidence != null
            && evidence.Value<string>("name") == Road08R01D1WriteContract.ArrowBlockName
            && evidence.Value<bool?>("exists") == true
            && evidence.Value<bool?>("fully_inspected") == true
            && !IsNonStaticLocalEvidence(evidence)
            && IsSupportedBlockMetadata(evidence)
            && evidence.Value<int?>("member_count") == 2
            && evidence["members"] is JArray members
            && IsWhitelistedArrowMembers(members)
            && string.Equals(evidence.Value<string>("shape_sha256"),
                Road08R01D1WriteContract.ArrowBlockShapeSha256, StringComparison.Ordinal)
            && string.Equals(Road08R01D1WriteContract.ComputeArrowBlockShapeSha256(evidence, members),
                Road08R01D1WriteContract.ArrowBlockShapeSha256, StringComparison.Ordinal);

        private static bool IsWhitelistedArrowMembers(JArray members)
        {
            if (members == null || members.Count != 2)
                return false;
            string[] types = members.OfType<JObject>().Select(member => member.Value<string>("type")).ToArray();
            return types.Length == 2
                && types.Contains(typeof(Polyline).FullName, StringComparer.Ordinal)
                && types.Contains(typeof(Line).FullName, StringComparer.Ordinal);
        }

        private static JObject InspectFontResolution(Database database, string configuredSmallFont,
            string configuredBigFont)
        {
            JObject projectSmall = InspectOneFont(database, Road08R01D1WriteContract.TextFontFile,
                "AutoCAD HostApplicationServices.FindFile");
            JObject projectBig = InspectOneFont(database, Road08R01D1WriteContract.TextBigFontFile,
                "AutoCAD HostApplicationServices.FindFile");
            JObject fallbackSmall = InspectOneFont(database, Road08R01D1WriteContract.FallbackTextFontFile,
                "AutoCAD HostApplicationServices.FindFile");
            JObject fallbackBig = InspectOneFont(database, Road08R01D1WriteContract.FallbackBigFontFile,
                "AutoCAD HostApplicationServices.FindFile");

            bool usesProjectPair =
                string.Equals(configuredSmallFont, Road08R01D1WriteContract.TextFontFile, StringComparison.OrdinalIgnoreCase)
                && string.Equals(configuredBigFont, Road08R01D1WriteContract.TextBigFontFile, StringComparison.OrdinalIgnoreCase);
            bool usesFallbackPair =
                string.Equals(configuredSmallFont, Road08R01D1WriteContract.FallbackTextFontFile, StringComparison.OrdinalIgnoreCase)
                && string.Equals(configuredBigFont, Road08R01D1WriteContract.FallbackBigFontFile, StringComparison.OrdinalIgnoreCase);

            bool projectPairResolved = IsFontResolved(projectSmall) && IsFontResolved(projectBig);
            bool fallbackPairResolved = IsVerifiedFallbackFont(true, fallbackSmall)
                && IsVerifiedFallbackFont(false, fallbackBig);

            return new JObject
            {
                ["configured_font_file"] = configuredSmallFont,
                ["configured_big_font_file"] = configuredBigFont,
                ["configured_pair_kind"] = usesProjectPair ? "project_pair" : usesFallbackPair ? "fallback_pair" : "unrecognized_pair",
                ["configured_pair_resolved"] = usesProjectPair ? projectPairResolved
                    : usesFallbackPair && fallbackPairResolved,
                ["project_pair"] = new JObject
                {
                    ["small_font_file"] = Road08R01D1WriteContract.TextFontFile,
                    ["big_font_file"] = Road08R01D1WriteContract.TextBigFontFile,
                    ["small_font_slot"] = "FileName",
                    ["big_font_slot"] = "BigFontFileName",
                    ["small_font_resolution"] = projectSmall,
                    ["big_font_resolution"] = projectBig,
                    ["resolved"] = projectPairResolved
                },
                ["fallback_pair"] = new JObject
                {
                    ["small_font_file"] = Road08R01D1WriteContract.FallbackTextFontFile,
                    ["big_font_file"] = Road08R01D1WriteContract.FallbackBigFontFile,
                    ["small_font_slot"] = "FileName",
                    ["big_font_slot"] = "BigFontFileName",
                    ["small_font_sha256"] = Road08R01D1WriteContract.FallbackTextFontSha256,
                    ["big_font_sha256"] = Road08R01D1WriteContract.FallbackBigFontSha256,
                    ["small_font_resolution"] = fallbackSmall,
                    ["big_font_resolution"] = fallbackBig,
                    ["resolved"] = fallbackPairResolved
                }
            };
        }

        private static JObject InspectOneFont(Database database, string fileName, string resolver)
        {
            string path = null;
            string hash = null;
            string resolvedBaseName = null;
            string expectedHash = ExpectedFallbackFontHash(fileName);
            try
            {
                path = HostApplicationServices.Current.FindFile(fileName, database, FindFileHint.FontFile);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    path = null;
                if (path != null)
                {
                    resolvedBaseName = Path.GetFileName(path);
                    using (FileStream stream = File.OpenRead(path))
                    using (SHA256 algorithm = SHA256.Create())
                        hash = Convert.ToHexString(algorithm.ComputeHash(stream));
                }
            }
            catch
            {
                path = null;
                hash = null;
            }

            return new JObject
            {
                ["requested_file"] = fileName,
                ["resolver"] = resolver,
                ["resolved_basename"] = resolvedBaseName == null ? JValue.CreateNull() : new JValue(resolvedBaseName),
                ["basename_matches"] = resolvedBaseName != null
                    && string.Equals(resolvedBaseName, fileName, StringComparison.OrdinalIgnoreCase),
                ["expected_sha256"] = expectedHash == null ? JValue.CreateNull() : new JValue(expectedHash),
                ["sha256_matches_expected"] = expectedHash == null || string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase),
                ["resolved"] = path != null && hash != null && resolvedBaseName != null
                    && string.Equals(resolvedBaseName, fileName, StringComparison.OrdinalIgnoreCase),
                ["resolved_path"] = path == null ? JValue.CreateNull() : new JValue(path),
                ["sha256"] = hash == null ? JValue.CreateNull() : new JValue(hash)
            };
        }

        private static bool IsFontResolved(JObject fontEvidence) =>
            fontEvidence?["resolved"]?.Type == JTokenType.Boolean
            && fontEvidence.Value<bool>("resolved")
            && fontEvidence["basename_matches"]?.Type == JTokenType.Boolean
            && fontEvidence.Value<bool>("basename_matches");

        private static string ExpectedFallbackFontHash(string fileName)
        {
            if (string.Equals(fileName, Road08R01D1WriteContract.FallbackTextFontFile,
                StringComparison.OrdinalIgnoreCase))
                return Road08R01D1WriteContract.FallbackTextFontSha256;
            if (string.Equals(fileName, Road08R01D1WriteContract.FallbackBigFontFile,
                StringComparison.OrdinalIgnoreCase))
                return Road08R01D1WriteContract.FallbackBigFontSha256;
            return null;
        }

        private static bool IsVerifiedFallbackFont(bool projectSmall, JObject fontEvidence)
        {
            string expectedFile = projectSmall
                ? Road08R01D1WriteContract.FallbackTextFontFile
                : Road08R01D1WriteContract.FallbackBigFontFile;
            string expectedHash = projectSmall
                ? Road08R01D1WriteContract.FallbackTextFontSha256
                : Road08R01D1WriteContract.FallbackBigFontSha256;
            return IsFontResolved(fontEvidence)
                && string.Equals(fontEvidence.Value<string>("requested_file"), expectedFile, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fontEvidence.Value<string>("sha256"), expectedHash, StringComparison.OrdinalIgnoreCase)
                && fontEvidence.Value<bool?>("sha256_matches_expected") == true;
        }

        private static bool IsFontPairResolved(JObject fontResolution, string pairName)
        {
            JObject pair = fontResolution?[pairName] as JObject;
            return pair?["resolved"]?.Type == JTokenType.Boolean
                && pair.Value<bool>("resolved");
        }

        private static bool IsConfiguredFontPair(string smallFont, string bigFont,
            string expectedSmall, string expectedBig) =>
            string.Equals(smallFont, expectedSmall, StringComparison.OrdinalIgnoreCase)
            && string.Equals(bigFont, expectedBig, StringComparison.OrdinalIgnoreCase);

        private static JObject TextStyleSourceEvidence(TextStyleTableRecord style) => new JObject
        {
            ["name"] = style.Name,
            ["handle"] = HandleOf(style),
            ["font_file"] = style.FileName,
            ["big_font_file"] = style.BigFontFileName,
            ["text_size"] = style.TextSize,
            ["x_scale"] = style.XScale,
            ["obliquing_angle"] = style.ObliquingAngle,
            ["is_shape_file"] = style.IsShapeFile,
            ["is_vertical"] = style.IsVertical
        };

        private static JObject PlannedFallbackTextStyleEvidence(Database database, TextStyleTableRecord sourceStyle)
        {
            JObject evidence = TextStyleSourceEvidence(sourceStyle);
            evidence["name"] = Road08R01D1WriteContract.FallbackTextStyle;
            evidence["handle"] = JValue.CreateNull();
            evidence["font_file"] = Road08R01D1WriteContract.FallbackTextFontFile;
            evidence["big_font_file"] = Road08R01D1WriteContract.FallbackBigFontFile;
            evidence["exists"] = false;
            evidence["style_creation_required"] = true;
            evidence["selected_pair"] = "fallback_pair";
            evidence["source_style_name"] = Road08R01D1WriteContract.TextStyle;
            evidence["source_style_handle"] = HandleOf(sourceStyle);
            evidence["font_resolution"] = InspectFontResolution(database,
                Road08R01D1WriteContract.FallbackTextFontFile,
                Road08R01D1WriteContract.FallbackBigFontFile);
            return evidence;
        }

        private static JObject TextStyleEvidence(Database database,
            TextStyleTableRecord style, bool isFallback, string sourceStyleHandle = null)
        {
            JObject evidence = TextStyleSourceEvidence(style);
            evidence["exists"] = true;
            evidence["style_creation_required"] = false;
            evidence["selected_pair"] = isFallback ? "fallback_pair" : "project_pair";
            evidence["source_style_name"] = isFallback ? Road08R01D1WriteContract.TextStyle : JValue.CreateNull();
            evidence["source_style_handle"] = isFallback && sourceStyleHandle != null
                ? new JValue(sourceStyleHandle) : JValue.CreateNull();
            evidence["font_resolution"] = InspectFontResolution(database, style.FileName, style.BigFontFileName);
            return evidence;
        }

        private static bool IsFallbackStyleCompatible(TextStyleTableRecord sourceStyle,
            TextStyleTableRecord fallbackStyle)
        {
            return fallbackStyle != null
                && string.Equals(fallbackStyle.Name, Road08R01D1WriteContract.FallbackTextStyle, StringComparison.Ordinal)
                && IsConfiguredFontPair(fallbackStyle.FileName, fallbackStyle.BigFontFileName,
                    Road08R01D1WriteContract.FallbackTextFontFile, Road08R01D1WriteContract.FallbackBigFontFile)
                && sourceStyle.TextSize.Equals(fallbackStyle.TextSize)
                && sourceStyle.XScale.Equals(fallbackStyle.XScale)
                && sourceStyle.ObliquingAngle.Equals(fallbackStyle.ObliquingAngle)
                && sourceStyle.IsShapeFile == fallbackStyle.IsShapeFile
                && sourceStyle.IsVertical == fallbackStyle.IsVertical;
        }

        private static ObjectId EnsureSelectedTextStyle(Database database, Transaction transaction,
            PreflightState preflight, Action<string> setPhase)
        {
            setPhase?.Invoke("open_text_style_table");
            TextStyleTable styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            setPhase?.Invoke("open_source_text_style");
            TextStyleTableRecord sourceStyle = transaction.GetObject(preflight.BaseTextStyleId, OpenMode.ForRead)
                as TextStyleTableRecord;
            setPhase?.Invoke("verify_source_text_style_evidence");
            if (sourceStyle == null || !JToken.DeepEquals(preflight.Evidence["base_text_style"],
                    TextStyleSourceEvidence(sourceStyle)))
                throw new WriteFailureException("d1_source_text_style_changed",
                    "The shared project text style changed after preflight.");

            if (!preflight.UsesFallbackTextStyle)
            {
                setPhase?.Invoke("resolve_project_font_pair");
                JObject projectResolution = InspectFontResolution(database, sourceStyle.FileName, sourceStyle.BigFontFileName);
                setPhase?.Invoke("verify_project_font_pair");
                if (!IsConfiguredFontPair(sourceStyle.FileName, sourceStyle.BigFontFileName,
                        Road08R01D1WriteContract.TextFontFile, Road08R01D1WriteContract.TextBigFontFile)
                    || !IsFontPairResolved(projectResolution, "project_pair"))
                    throw new WriteFailureException("d1_project_font_changed",
                        "The project font pair no longer resolves exactly as it did during preflight.");
                return sourceStyle.ObjectId;
            }

            setPhase?.Invoke("resolve_fallback_font_pair");
            JObject fallbackResolution = InspectFontResolution(database,
                Road08R01D1WriteContract.FallbackTextFontFile,
                Road08R01D1WriteContract.FallbackBigFontFile);
            setPhase?.Invoke("verify_fallback_font_pair");
            if (!IsFontPairResolved(fallbackResolution, "fallback_pair"))
                throw new WriteFailureException("d1_fallback_font_changed",
                    "The approved fallback font pair no longer resolves to the bundled files and hashes.");

            setPhase?.Invoke("lookup_fallback_text_style");
            if (styles.Has(Road08R01D1WriteContract.FallbackTextStyle))
            {
                setPhase?.Invoke("lookup_existing_fallback_text_style_id");
                ObjectId existingId = styles[Road08R01D1WriteContract.FallbackTextStyle];
                setPhase?.Invoke("open_existing_fallback_text_style");
                TextStyleTableRecord existing = transaction.GetObject(existingId, OpenMode.ForRead)
                    as TextStyleTableRecord;
                setPhase?.Invoke("verify_existing_fallback_text_style");
                if (existing == null || !IsFallbackStyleCompatible(sourceStyle, existing))
                    throw new WriteFailureException("d1_fallback_text_style_conflict",
                        "The existing dedicated D1 fallback style does not match the source style and exact font pair.");
                return existingId;
            }

            setPhase?.Invoke("upgrade_text_style_table");
            styles.UpgradeOpen();
            setPhase?.Invoke("create_fallback_text_style");
            var fallbackStyle = new TextStyleTableRecord();
            setPhase?.Invoke("copy_source_text_style");
            fallbackStyle.CopyFrom(sourceStyle);
            setPhase?.Invoke("set_fallback_text_style_name");
            fallbackStyle.Name = Road08R01D1WriteContract.FallbackTextStyle;
            setPhase?.Invoke("set_fallback_text_style_font");
            fallbackStyle.FileName = Road08R01D1WriteContract.FallbackTextFontFile;
            setPhase?.Invoke("set_fallback_text_style_big_font");
            fallbackStyle.BigFontFileName = Road08R01D1WriteContract.FallbackBigFontFile;
            setPhase?.Invoke("add_fallback_text_style");
            ObjectId fallbackId = styles.Add(fallbackStyle);
            setPhase?.Invoke("register_fallback_text_style");
            transaction.AddNewlyCreatedDBObject(fallbackStyle, true);
            return fallbackId;
        }

        private static void UpdateSelectedTextStyleEvidence(Database database, Transaction transaction,
            PreflightState preflight, Action<string> setPhase)
        {
            setPhase?.Invoke("open_selected_text_style_for_evidence");
            TextStyleTableRecord selectedStyle = transaction.GetObject(preflight.TextStyleId, OpenMode.ForRead)
                as TextStyleTableRecord;
            setPhase?.Invoke("open_base_text_style_for_evidence");
            TextStyleTableRecord baseStyle = transaction.GetObject(preflight.BaseTextStyleId, OpenMode.ForRead)
                as TextStyleTableRecord;
            setPhase?.Invoke("verify_base_text_style_evidence");
            if (selectedStyle == null || baseStyle == null
                || !JToken.DeepEquals(preflight.Evidence["base_text_style"], TextStyleSourceEvidence(baseStyle)))
                throw new WriteFailureException("d1_text_style_evidence_unavailable",
                    "The selected or shared text style could not be rechecked before writing.");

            setPhase?.Invoke("serialize_selected_text_style_evidence");
            JObject selectedEvidence = TextStyleEvidence(database, selectedStyle,
                preflight.UsesFallbackTextStyle,
                preflight.UsesFallbackTextStyle ? HandleOf(baseStyle) : null);
            string requiredPair = preflight.UsesFallbackTextStyle ? "fallback_pair" : "project_pair";
            setPhase?.Invoke("verify_selected_text_style_font_pair");
            if (!IsFontPairResolved(selectedEvidence["font_resolution"] as JObject, requiredPair))
                throw new WriteFailureException("d1_selected_font_pair_unresolved",
                    "The selected text style's fonts did not remain fully resolved before commit.");
            JObject required = preflight.Evidence["required"] as JObject;
            if (required == null)
                throw new WriteFailureException("d1_preflight_evidence_invalid",
                    "The required D1 preflight evidence is missing.");
            setPhase?.Invoke("update_selected_text_style_evidence");
            required["text_style"] = selectedEvidence;
            setPhase?.Invoke("read_selected_text_style_name");
            preflight.TextStyleName = selectedStyle.Name;
        }

        private static JArray InspectReferenceArrowBlocks(Database database, Transaction transaction)
        {
            var candidates = new JArray();
            BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (string name in Road08R01D1WriteContract.ArrowBlockReferenceNames)
            {
                var candidate = new JObject
                {
                    ["name"] = name,
                    ["exists"] = blocks.Has(name)
                };
                if (!blocks.Has(name))
                {
                    candidate["inspection_status"] = "not_found";
                    candidates.Add(candidate);
                    continue;
                }

                try
                {
                    BlockTableRecord definition = transaction.GetObject(blocks[name], OpenMode.ForRead) as BlockTableRecord;
                    if (definition == null)
                        throw new InvalidOperationException();
                    JArray members = InspectBlockMembers(database, transaction, definition);
                    candidate.Merge(BlockDefinitionEvidence(definition, members,
                        HashBlockDefinition(definition, members), includeMembers: true));
                    candidate["shape_sha256"] = HashBlockShape(definition, members);
                    candidate["inspection_status"] = "complete";
                }
                catch
                {
                    candidate["inspection_status"] = "incomplete";
                    candidate["fully_inspected"] = false;
                }

                candidates.Add(candidate);
            }

            return candidates;
        }

        private static JObject BlockDefinitionEvidence(BlockTableRecord definition, JArray members,
            string geometrySha256, bool includeMembers = false)
        {
            var evidence = new JObject
            {
                ["name"] = definition.Name,
                ["exists"] = true,
                ["fully_inspected"] = true,
                ["handle"] = HandleOf(definition),
                ["origin"] = PointJson(definition.Origin),
                ["is_layout"] = definition.IsLayout,
                ["is_anonymous"] = definition.IsAnonymous,
                ["is_external_reference"] = definition.IsFromExternalReference,
                ["is_overlay_reference"] = definition.IsFromOverlayReference,
                ["is_dynamic"] = definition.IsDynamicBlock,
                ["units"] = definition.Units.ToString(),
                ["block_scaling"] = definition.BlockScaling.ToString(),
                ["explodable"] = definition.Explodable,
                ["comments"] = definition.Comments ?? string.Empty,
                ["annotative"] = definition.Annotative.ToString(),
                ["merge_style"] = definition.MergeStyle.ToString(),
                ["member_count"] = members.Count,
                ["geometry_sha256"] = geometrySha256,
                ["shape_sha256"] = HashBlockShape(definition, members)
            };
            if (includeMembers)
                evidence["members"] = members.DeepClone();
            return evidence;
        }

        private static string HashBlockDefinition(BlockTableRecord definition, JArray members)
        {
            var signature = new JObject
            {
                ["name"] = definition.Name,
                ["origin"] = PointJson(definition.Origin),
                ["is_layout"] = definition.IsLayout,
                ["is_anonymous"] = definition.IsAnonymous,
                ["is_external_reference"] = definition.IsFromExternalReference,
                ["is_overlay_reference"] = definition.IsFromOverlayReference,
                ["is_dynamic"] = definition.IsDynamicBlock,
                ["members"] = members.DeepClone()
            };
            return HashCanonicalJson(signature);
        }

        private static string HashBlockShape(BlockTableRecord definition, JArray members)
        {
            var shapeMembers = new List<JObject>();
            foreach (JObject member in members.OfType<JObject>())
            {
                JObject shapeMember = (JObject)member.DeepClone();
                RemoveTargetSpecificIdentifiers(shapeMember);
                shapeMembers.Add(shapeMember);
            }
            shapeMembers.Sort((left, right) => string.CompareOrdinal(Serialize(left), Serialize(right)));

            var signature = new JObject
            {
                ["origin"] = PointJson(definition.Origin),
                ["is_layout"] = definition.IsLayout,
                ["is_anonymous"] = definition.IsAnonymous,
                ["is_external_reference"] = definition.IsFromExternalReference,
                ["is_overlay_reference"] = definition.IsFromOverlayReference,
                ["is_dynamic"] = definition.IsDynamicBlock,
                ["members"] = new JArray(shapeMembers)
            };
            return HashCanonicalJson(signature);
        }

        private static void RemoveTargetSpecificIdentifiers(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties().ToArray())
                {
                    if (string.Equals(property.Name, "handle", StringComparison.OrdinalIgnoreCase)
                        || property.Name.EndsWith("_handle", StringComparison.OrdinalIgnoreCase))
                    {
                        property.Remove();
                        continue;
                    }
                    RemoveTargetSpecificIdentifiers(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken child in array)
                    RemoveTargetSpecificIdentifiers(child);
            }
        }

        private static JArray InspectSourcePolylines(Database database, Transaction transaction)
        {
            JArray sources = new JArray();
            HashSet<string> distinctHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string handleText in Road08R01D1WriteContract.SourceHandles)
            {
                ObjectId id;
                try
                {
                    long value = long.Parse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    id = database.GetObjectId(false, new Handle(value), 0);
                }
                catch
                {
                    throw new WriteFailureException(
                        "d1_source_geometry_invalid", "A required ROAD source polyline handle is unavailable.");
                }

                if (id.IsNull || !distinctHandles.Add(handleText))
                    throw new WriteFailureException(
                        "d1_source_geometry_invalid", "A required ROAD source polyline handle is unavailable or duplicated.");

                Entity entity;
                try
                {
                    entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                }
                catch
                {
                    entity = null;
                }

                Polyline polyline = entity as Polyline;
                if (polyline == null || !string.Equals(polyline.Layer,
                    Road08R01D1WriteContract.ExpectedSourceLayer, StringComparison.OrdinalIgnoreCase))
                    throw new WriteFailureException(
                        "d1_source_geometry_invalid", "Each fixed source handle must resolve to a ROAD polyline.");

                JObject signature;
                string actualDigest;
                try
                {
                    signature = InspectSourcePolyline(polyline);
                    actualDigest = signature.Value<string>("source_geometry_sha256");
                }
                catch
                {
                    throw new WriteFailureException(
                        "d1_source_geometry_invalid", "A required ROAD source polyline cannot be fully inspected.");
                }

                string expectedDigest = Road08R01D1WriteContract.ExpectedSourceGeometrySha256(handleText);
                if (string.IsNullOrWhiteSpace(expectedDigest)
                    || !string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
                {
                    throw new WriteFailureException(
                        "d1_source_geometry_mismatch", "A fixed ROAD source polyline does not match its frozen geometry baseline.");
                }

                sources.Add(signature);
            }

            return sources;
        }

        private static int CountDuplicateCallouts(Database database, Transaction transaction, out JArray handles)
        {
            var matches = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId recordId in blockTable)
                {
                    BlockTableRecord record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                    if (record == null)
                        throw new InvalidOperationException();

                    foreach (ObjectId entityId in record)
                    {
                        Entity entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                        if (entity == null)
                            continue;

                        bool containsMarker = false;
                        if (entity is MText mtext)
                            containsMarker = HasDuplicateMarker(mtext.Contents);
                        else if (entity is DBText dbText)
                            containsMarker = HasDuplicateMarker(dbText.TextString);
                        else if (entity is MLeader mleader)
                            containsMarker = mleader.MText != null && HasDuplicateMarker(mleader.MText.Contents);

                        if (containsMarker)
                            matches.Add(HandleOf(entity));
                    }
                }
            }
            catch
            {
                throw new WriteFailureException(
                    "d1_duplicate_scan_failed", "The drawing could not be fully scanned for an existing R01 callout.");
            }

            handles = new JArray(matches.Select(value => new JValue(value)));
            return matches.Count;
        }

        private static bool HasDuplicateMarker(string value) =>
            !string.IsNullOrEmpty(value)
            && value.IndexOf(Road08R01D1WriteContract.DuplicateMarkerText, StringComparison.OrdinalIgnoreCase) >= 0;

        private static JArray InspectBlockMembers(Database database, Transaction transaction,
            BlockTableRecord definition)
        {
            var members = new List<JObject>();
            foreach (ObjectId memberId in definition)
            {
                Entity entity = transaction.GetObject(memberId, OpenMode.ForRead, false) as Entity;
                if (entity == null)
                    throw new InvalidOperationException("Non-entity block members are not supported.");
                members.Add(InspectEntity(entity));
            }

            members.Sort((left, right) => string.CompareOrdinal(
                (string)left["handle"], (string)right["handle"]));
            return new JArray(members);
        }

        private static JObject InspectEntity(Entity entity)
        {
            Extents3d extents = entity.GeometricExtents;
            JObject color = ColorSignature(entity.Color);
            var vertices = new JArray();
            var bulges = new JArray();
            var startWidths = new JArray();
            var endWidths = new JArray();
            JToken closed = JValue.CreateNull();
            var geometry = new JObject();

            if (entity is Polyline polyline)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                {
                    vertices.Add(PointJson(polyline.GetPoint3dAt(index)));
                    bulges.Add(polyline.GetBulgeAt(index));
                    startWidths.Add(polyline.GetStartWidthAt(index));
                    endWidths.Add(polyline.GetEndWidthAt(index));
                }
                closed = new JValue(polyline.Closed);
                geometry["elevation"] = polyline.Elevation;
                geometry["normal"] = VectorJson(polyline.Normal);
                geometry["constant_width"] = polyline.ConstantWidth;
                geometry["thickness"] = polyline.Thickness;
            }
            else if (entity is Line line)
            {
                vertices.Add(PointJson(line.StartPoint));
                vertices.Add(PointJson(line.EndPoint));
                geometry["normal"] = VectorJson(line.Normal);
                geometry["thickness"] = line.Thickness;
            }
            else if (entity is Circle circle)
            {
                vertices.Add(PointJson(circle.Center));
                geometry["center"] = PointJson(circle.Center);
                geometry["radius"] = circle.Radius;
                geometry["normal"] = VectorJson(circle.Normal);
                geometry["thickness"] = circle.Thickness;
            }
            else if (entity is Arc arc)
            {
                vertices.Add(PointJson(arc.StartPoint));
                vertices.Add(PointJson(arc.EndPoint));
                geometry["center"] = PointJson(arc.Center);
                geometry["radius"] = arc.Radius;
                geometry["start_angle"] = arc.StartAngle;
                geometry["end_angle"] = arc.EndAngle;
                geometry["normal"] = VectorJson(arc.Normal);
                geometry["thickness"] = arc.Thickness;
            }
            else if (entity is Ellipse ellipse)
            {
                vertices.Add(PointJson(ellipse.StartPoint));
                vertices.Add(PointJson(ellipse.EndPoint));
                geometry["center"] = PointJson(ellipse.Center);
                geometry["major_axis"] = VectorJson(ellipse.MajorAxis);
                geometry["minor_axis"] = VectorJson(ellipse.MinorAxis);
                geometry["major_radius"] = ellipse.MajorRadius;
                geometry["minor_radius"] = ellipse.MinorRadius;
                geometry["radius_ratio"] = ellipse.RadiusRatio;
                geometry["start_param"] = ellipse.StartParam;
                geometry["end_param"] = ellipse.EndParam;
                geometry["closed"] = ellipse.Closed;
            }
            else if (entity is DBPoint dbPoint)
            {
                vertices.Add(PointJson(dbPoint.Position));
                geometry["ecs_rotation"] = dbPoint.EcsRotation;
            }
            else if (entity is Solid solid)
            {
                AddFourVertices(vertices, solid.GetPointAt);
                geometry["normal"] = VectorJson(solid.Normal);
                geometry["thickness"] = solid.Thickness;
            }
            else if (entity is Trace trace)
            {
                AddFourVertices(vertices, trace.GetPointAt);
                geometry["normal"] = VectorJson(trace.Normal);
                geometry["thickness"] = trace.Thickness;
            }
            else if (entity is DBText dbText)
            {
                vertices.Add(PointJson(dbText.Position));
                vertices.Add(PointJson(dbText.AlignmentPoint));
                geometry["text"] = dbText.TextString;
                geometry["height"] = dbText.Height;
                geometry["rotation"] = dbText.Rotation;
                geometry["text_style_handle"] = IdHandle(dbText.TextStyleId);
                geometry["horizontal_mode"] = dbText.HorizontalMode.ToString();
                geometry["vertical_mode"] = dbText.VerticalMode.ToString();
            }
            else if (entity is MText mtext)
            {
                vertices.Add(PointJson(mtext.Location));
                Point3dCollection boundingPoints = mtext.GetBoundingPoints();
                foreach (Point3d point in boundingPoints)
                    vertices.Add(PointJson(point));
                geometry["contents"] = mtext.Contents;
                geometry["text_height"] = mtext.TextHeight;
                geometry["width"] = mtext.Width;
                geometry["rotation"] = mtext.Rotation;
                geometry["attachment"] = mtext.Attachment.ToString();
                geometry["text_style_handle"] = IdHandle(mtext.TextStyleId);
            }
            else
            {
                throw new InvalidOperationException("Unsupported entity member prevents complete block inspection.");
            }

            var signature = new JObject
            {
                ["handle"] = HandleOf(entity),
                ["type"] = entity.GetType().FullName,
                ["layer"] = entity.Layer,
                ["color"] = color,
                ["visible"] = entity.Visible,
                ["linetype"] = entity.Linetype,
                ["linetype_scale"] = entity.LinetypeScale,
                ["lineweight"] = entity.LineWeight.ToString(),
                ["extents"] = ExtentsJson(extents),
                ["vertices"] = vertices,
                ["bulges"] = bulges,
                ["start_widths"] = startWidths,
                ["end_widths"] = endWidths,
                ["closed"] = closed,
                ["geometry"] = geometry
            };
            EnsureFinite(signature);
            return signature;
        }

        private static JObject InspectSourcePolyline(Polyline polyline)
        {
            JObject signature = InspectEntity(polyline);
            string digest = Road08R01D1WriteContract.ComputeSourceGeometrySha256(signature);
            if (string.IsNullOrWhiteSpace(digest))
                throw new InvalidOperationException("Source polyline geometry could not be digested.");
            signature["source_geometry_sha256"] = digest;
            return signature;
        }

        private static void AddFourVertices(JArray vertices, Func<short, Point3d> pointAt)
        {
            for (short index = 0; index < 4; index++)
                vertices.Add(PointJson(pointAt(index)));
        }

        private static MText CreateCalloutMText(ObjectId textStyleId, Action<string> setPhase)
        {
            setPhase?.Invoke("construct_mtext");
            var mtext = new MText();
            setPhase?.Invoke("set_mtext_database_defaults");
            mtext.SetDatabaseDefaults();
            setPhase?.Invoke("set_mtext_contents");
            mtext.Contents = Road08R01D1WriteContract.TextContents;
            setPhase?.Invoke("set_mtext_layer");
            mtext.Layer = Road08R01D1WriteContract.TextLayer;
            setPhase?.Invoke("set_mtext_text_style");
            mtext.TextStyleId = textStyleId;
            setPhase?.Invoke("set_mtext_location");
            mtext.Location = ContractTextLocation();
            setPhase?.Invoke("set_mtext_height");
            mtext.TextHeight = Road08R01D1WriteContract.TextHeight;
            setPhase?.Invoke("set_mtext_width");
            mtext.Width = Road08R01D1WriteContract.TextWidth;
            setPhase?.Invoke("set_mtext_rotation");
            mtext.Rotation = Road08R01D1WriteContract.TextRotation;
            setPhase?.Invoke("set_mtext_attachment");
            mtext.Attachment = AttachmentPoint.BottomRight;
            setPhase?.Invoke("set_mtext_color");
            mtext.Color = Color.FromColorIndex(ColorMethod.ByAci,
                Road08R01D1WriteContract.TextColorIndex);
            return mtext;
        }

        private static Leader CreateCalloutLeader(PreflightState preflight, Action<string> setPhase)
        {
            setPhase?.Invoke("construct_leader");
            var leader = new Leader();
            setPhase?.Invoke("set_leader_database_defaults");
            leader.SetDatabaseDefaults();
            setPhase?.Invoke("set_leader_layer");
            leader.Layer = Road08R01D1WriteContract.LeaderLayer;
            setPhase?.Invoke("set_leader_color");
            leader.Color = Color.FromColorIndex(ColorMethod.ByAci,
                Road08R01D1WriteContract.LeaderColorIndex);
            setPhase?.Invoke("set_leader_dimension_line_color");
            leader.Dimclrd = Color.FromColorIndex(ColorMethod.ByAci,
                Road08R01D1WriteContract.LeaderColorIndex);
            setPhase?.Invoke("set_leader_dimension_style");
            leader.DimensionStyle = preflight.DimStyleId;
            setPhase?.Invoke("set_leader_text_style");
            leader.TextStyleId = preflight.TextStyleId;
            setPhase?.Invoke("set_leader_arrow_block");
            leader.Dimldrblk = preflight.ArrowBlockId;
            setPhase?.Invoke("set_leader_arrow_size");
            leader.Dimasz = Road08R01D1WriteContract.LeaderArrowSize;
            setPhase?.Invoke("set_leader_scale");
            leader.Dimscale = Road08R01D1WriteContract.LeaderScale;
            setPhase?.Invoke("set_leader_gap");
            leader.Dimgap = Road08R01D1WriteContract.LeaderGap;
            setPhase?.Invoke("set_leader_text_above");
            leader.Dimtad = Road08R01D1WriteContract.LeaderTextAbove;
            setPhase?.Invoke("set_leader_text_height");
            leader.Dimtxt = Road08R01D1WriteContract.LeaderTextHeight;
            setPhase?.Invoke("set_leader_arrow_head");
            leader.HasArrowHead = true;
            setPhase?.Invoke("set_leader_spline_mode");
            leader.IsSplined = false;

            setPhase?.Invoke("enumerate_fixed_leader_vertices");
            foreach (double[] vertex in Road08R01D1WriteContract.LeaderVerticesWcs)
            {
                setPhase?.Invoke("construct_fixed_leader_vertex");
                var point = new Point3d(vertex[0], vertex[1], vertex[2]);
                setPhase?.Invoke("append_fixed_leader_vertex");
                if (!leader.AppendVertex(point))
                    throw new WriteFailureException(
                        "d1_leader_vertex_failed", "The fixed Leader geometry could not be created.");
            }
            return leader;
        }

        private static PluginWriteJobResult BuildCommittedResult(Database database,
            PreflightState preflight, string preflightJson, string leaderHandle, string mtextHandle,
            bool allowReadback)
        {
            JObject committed = CreateCommittedEvidence(database, preflight,
                leaderHandle, mtextHandle, readbackComplete: false);
            PluginVerificationState verification = PluginVerificationState.Unknown;
            string verificationCode = "d1_readback_unavailable";
            string verificationMessage = "A complete independent readback could not be established.";

            if (!allowReadback)
            {
                committed["readback_complete"] = false;
                committed["exact_readback_passed"] = false;
                committed["readback_error_code"] = "d1_write_transaction_dispose_failed";
                committed["readback_error_message"] = "The committed write transaction could not be disposed; readback was skipped.";
            }
            else try
            {
                using (Transaction readbackTransaction = database.TransactionManager.StartTransaction())
                {
                    JObject actualLeader;
                    JObject actualMText;
                    JObject readbackBlockEvidence;
                    JObject resourceReadback;
                    JObject selectedTextStyleAfter;
                    JObject baseTextStyleAfter;
                    JArray sourcePolylinesAfter;
                    bool exact = InspectCommittedState(database, readbackTransaction, preflight,
                        leaderHandle, mtextHandle, out actualLeader, out actualMText,
                        out readbackBlockEvidence, out resourceReadback, out selectedTextStyleAfter,
                        out baseTextStyleAfter, out sourcePolylinesAfter);

                    committed["readback_complete"] = true;
                    committed["leader"] = actualLeader;
                    committed["mtext"] = actualMText;
                    committed["selected_text_style_after"] = selectedTextStyleAfter;
                    committed["selected_text_style_matches_preflight"] = JToken.DeepEquals(
                        preflight.Evidence["required"]?["text_style"], selectedTextStyleAfter);
                    committed["base_text_style_after"] = baseTextStyleAfter;
                    committed["base_text_style_unchanged"] = JToken.DeepEquals(
                        preflight.Evidence["base_text_style"], baseTextStyleAfter);
                    committed["source_polylines_after"] = sourcePolylinesAfter;
                    committed["resource_readback"] = resourceReadback;
                    committed["post_block_members"] = readbackBlockEvidence["block_members"].DeepClone();
                    committed["post_block_geometry_sha256"] = (string)readbackBlockEvidence["block_geometry_sha256"];
                    committed["block_geometry_matches_preflight"] =
                        JToken.DeepEquals(preflight.Evidence["block_members"], readbackBlockEvidence["block_members"])
                        && string.Equals(preflight.BlockGeometrySha256,
                            (string)readbackBlockEvidence["block_geometry_sha256"], StringComparison.Ordinal);
                    committed["exact_readback_passed"] = exact;

                    if (exact)
                    {
                        verification = PluginVerificationState.Passed;
                        verificationCode = null;
                        verificationMessage = null;
                    }
                    else
                    {
                        verification = PluginVerificationState.Failed;
                        verificationCode = "d1_readback_mismatch";
                        verificationMessage = "Independent readback did not match every fixed D1 entity and block property.";
                    }
                }
            }
            catch
            {
                verification = PluginVerificationState.Unknown;
                verificationCode = "d1_readback_unavailable";
                verificationMessage = "A complete independent readback could not be established.";
                committed["readback_complete"] = false;
                committed["exact_readback_passed"] = false;
                committed["readback_error_code"] = "d1_readback_unavailable";
                committed["readback_error_message"] = "The independent readback transaction could not be completed.";
            }

            JObject readbackJson = new JObject
            {
                ["schema_version"] = Road08R01D1WriteContract.EvidenceSchemaVersion,
                ["plan_id"] = Road08R01D1WriteContract.PlanId,
                ["preflight"] = JToken.Parse(preflightJson),
                ["committed"] = committed
            };

            JObject resultJson = new JObject
            {
                ["schema_version"] = Road08R01D1WriteContract.EvidenceSchemaVersion,
                ["plan_id"] = Road08R01D1WriteContract.PlanId,
                ["leader_handle"] = leaderHandle,
                ["mtext_handle"] = mtextHandle,
                ["preflight"] = JToken.Parse(preflightJson)
            };

            return PluginWriteJobResult.Committed(
                Serialize(resultJson), verification, Serialize(readbackJson),
                verificationCode, verificationMessage);
        }

        private static JObject CreateCommittedEvidence(Database database, PreflightState preflight,
            string leaderHandle, string mtextHandle, bool readbackComplete)
        {
            return new JObject
            {
                ["leader_handle"] = leaderHandle,
                ["mtext_handle"] = mtextHandle,
                ["readback_complete"] = readbackComplete,
                ["expected"] = new JObject
                {
                    ["leader_type"] = typeof(Leader).FullName,
                    ["leader_layer"] = Road08R01D1WriteContract.LeaderLayer,
                    ["leader_color_index"] = Road08R01D1WriteContract.LeaderColorIndex,
                    ["leader_style"] = Road08R01D1WriteContract.LeaderStyle,
                ["leader_arrow_block"] = Road08R01D1WriteContract.ArrowBlockName,
                ["leader_arrow_size"] = Road08R01D1WriteContract.LeaderArrowSize,
                ["leader_scale"] = Road08R01D1WriteContract.LeaderScale,
                ["leader_gap"] = Road08R01D1WriteContract.LeaderGap,
                ["leader_text_above"] = Road08R01D1WriteContract.LeaderTextAbove,
                ["leader_text_height"] = Road08R01D1WriteContract.LeaderTextHeight,
                    ["leader_vertices_wcs"] = PointsJson(Road08R01D1WriteContract.LeaderVerticesWcs),
                    ["mtext_type"] = typeof(MText).FullName,
                    ["mtext_layer"] = Road08R01D1WriteContract.TextLayer,
                    ["mtext_contents"] = Road08R01D1WriteContract.TextContents,
                    ["mtext_location_wcs"] = ContractTextPointJson(),
                    ["mtext_height"] = Road08R01D1WriteContract.TextHeight,
                    ["mtext_width"] = Road08R01D1WriteContract.TextWidth,
                    ["mtext_rotation"] = Road08R01D1WriteContract.TextRotation,
                    ["mtext_attachment"] = Road08R01D1WriteContract.Attachment,
                    ["mtext_color_index"] = Road08R01D1WriteContract.TextColorIndex,
                    ["mtext_style"] = preflight.TextStyleName,
                    ["leader_annotation_handle"] = mtextHandle,
                    ["arrow_block_geometry_sha256"] = preflight.BlockGeometrySha256,
                    ["arrow_block"] = Road08R01D1WriteContract.ArrowBlockName
                }
            };
        }

        private static bool InspectCommittedState(Database database, Transaction transaction,
            PreflightState preflight, string leaderHandle, string mtextHandle,
            out JObject leaderEvidence, out JObject mtextEvidence, out JObject blockEvidence,
            out JObject resourceReadback,
            out JObject selectedTextStyleEvidence, out JObject baseTextStyleEvidence,
            out JArray sourcePolylinesAfter)
        {
            leaderEvidence = null;
            mtextEvidence = null;
            blockEvidence = null;
            resourceReadback = null;
            selectedTextStyleEvidence = null;
            baseTextStyleEvidence = null;
            sourcePolylinesAfter = new JArray();

            ObjectId leaderId = ResolveHandle(database, leaderHandle);
            ObjectId mtextId = ResolveHandle(database, mtextHandle);
            Leader leader = transaction.GetObject(leaderId, OpenMode.ForRead) as Leader;
            MText mtext = transaction.GetObject(mtextId, OpenMode.ForRead) as MText;
            if (leader == null || mtext == null)
                return false;

            leaderEvidence = LeaderEvidence(database, transaction, leader);
            mtextEvidence = MTextEvidence(mtext);
            TextStyleTableRecord selectedTextStyle = transaction.GetObject(mtext.TextStyleId, OpenMode.ForRead)
                as TextStyleTableRecord;
            TextStyleTableRecord baseTextStyle = transaction.GetObject(preflight.BaseTextStyleId, OpenMode.ForRead)
                as TextStyleTableRecord;
            if (selectedTextStyle == null || baseTextStyle == null)
                return false;
            selectedTextStyleEvidence = TextStyleEvidence(database, selectedTextStyle,
                preflight.UsesFallbackTextStyle,
                preflight.UsesFallbackTextStyle ? HandleOf(baseTextStyle) : null);
            baseTextStyleEvidence = TextStyleSourceEvidence(baseTextStyle);

            BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (!blockTable.Has(Road08R01D1WriteContract.ArrowBlockName))
                return false;
            ObjectId arrowBlockId = blockTable[Road08R01D1WriteContract.ArrowBlockName];
            BlockTableRecord definition = transaction.GetObject(arrowBlockId, OpenMode.ForRead) as BlockTableRecord;
            if (definition == null)
                return false;
            JArray postBlockMembers = InspectBlockMembers(database, transaction, definition);
            string postBlockDigest = HashBlockDefinition(definition, postBlockMembers);
            string postBlockShapeDigest = HashBlockShape(definition, postBlockMembers);
            JObject postBlockDefinition = BlockDefinitionEvidence(definition, postBlockMembers,
                postBlockDigest, includeMembers: true);
            postBlockDefinition["shape_sha256"] = postBlockShapeDigest;
            blockEvidence = new JObject
            {
                ["block_members"] = postBlockMembers,
                ["block_origin"] = PointJson(definition.Origin),
                ["block_shape_sha256"] = postBlockShapeDigest,
                ["block_geometry_sha256"] = postBlockDigest
            };

            LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(Road08R01D1WriteContract.TextLayer)
                || !layerTable.Has(Road08R01D1WriteContract.LeaderLayer))
                return false;
            JObject textLayerAfter = LayerResourceEvidence(database, transaction,
                (LayerTableRecord)transaction.GetObject(layerTable[Road08R01D1WriteContract.TextLayer], OpenMode.ForRead));
            JObject leaderLayerAfter = LayerResourceEvidence(database, transaction,
                (LayerTableRecord)transaction.GetObject(layerTable[Road08R01D1WriteContract.LeaderLayer], OpenMode.ForRead));
            resourceReadback = new JObject
            {
                ["text_layer"] = textLayerAfter,
                ["leader_layer"] = leaderLayerAfter,
                ["arrow_block"] = postBlockDefinition
            };

            bool sourcesStillExact = SourceSignaturesStillMatch(database, transaction,
                preflight.Evidence["source_polylines"] as JArray, out sourcePolylinesAfter);
            bool exact = ExactLeader(database, transaction, leader, mtext, preflight)
                && ExactMText(mtext, leader, preflight)
                && LayerMatchesPolicy(textLayerAfter)
                && LayerMatchesPolicy(leaderLayerAfter)
                && IsApprovedTargetArrowBlock(postBlockDefinition)
                && JToken.DeepEquals(preflight.Evidence["required"]?["text_layer"], textLayerAfter)
                && JToken.DeepEquals(preflight.Evidence["required"]?["leader_layer"], leaderLayerAfter)
                && JToken.DeepEquals(preflight.Evidence["required"]?["arrow_block"], postBlockDefinition)
                && JToken.DeepEquals(preflight.Evidence["required"]?["text_style"], selectedTextStyleEvidence)
                && JToken.DeepEquals(preflight.Evidence["base_text_style"], baseTextStyleEvidence)
                && sourcesStillExact
                && JToken.DeepEquals(preflight.Evidence["block_members"], postBlockMembers)
                && string.Equals(preflight.BlockGeometrySha256, postBlockDigest, StringComparison.Ordinal);
            return exact;
        }

        private static bool SourceSignaturesStillMatch(Database database, Transaction transaction,
            JArray expected, out JArray actual)
        {
            actual = new JArray();
            if (expected == null || expected.Count != Road08R01D1WriteContract.SourceHandles.Length)
                return false;

            foreach (string handle in Road08R01D1WriteContract.SourceHandles)
            {
                ObjectId id = ResolveHandle(database, handle);
                Polyline polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                if (polyline == null || !string.Equals(polyline.Layer,
                    Road08R01D1WriteContract.ExpectedSourceLayer, StringComparison.OrdinalIgnoreCase))
                    return false;
                actual.Add(InspectSourcePolyline(polyline));
            }
            return JToken.DeepEquals(expected, actual);
        }

        private static bool ExactLeader(Database database, Transaction transaction, Leader leader,
            MText mtext, PreflightState preflight)
        {
            if (!string.Equals(leader.Layer, Road08R01D1WriteContract.LeaderLayer, StringComparison.Ordinal)
                || !ExactColor(leader.Color, Road08R01D1WriteContract.LeaderColorIndex)
                || !ExactColor(leader.Dimclrd, Road08R01D1WriteContract.LeaderColorIndex)
                || leader.DimensionStyle != preflight.DimStyleId
                || !string.Equals(leader.DimensionStyleName, Road08R01D1WriteContract.LeaderStyle, StringComparison.Ordinal)
                || leader.TextStyleId != preflight.TextStyleId
                || leader.Dimldrblk != preflight.ArrowBlockId
                || !leader.Dimasz.Equals(Road08R01D1WriteContract.LeaderArrowSize)
                || !leader.Dimscale.Equals(Road08R01D1WriteContract.LeaderScale)
                || !leader.Dimgap.Equals(Road08R01D1WriteContract.LeaderGap)
                || leader.Dimtad != Road08R01D1WriteContract.LeaderTextAbove
                || !leader.Dimtxt.Equals(Road08R01D1WriteContract.LeaderTextHeight)
                || !string.Equals(BlockName(database, transaction, leader.Dimldrblk),
                    Road08R01D1WriteContract.ArrowBlockName, StringComparison.Ordinal)
                || leader.Annotation != mtext.ObjectId
                || !leader.HasArrowHead
                || leader.IsSplined
                || leader.NumVertices != Road08R01D1WriteContract.LeaderVerticesWcs.Length)
            {
                return false;
            }

            for (int index = 0; index < Road08R01D1WriteContract.LeaderVerticesWcs.Length; index++)
            {
                double[] expected = Road08R01D1WriteContract.LeaderVerticesWcs[index];
                Point3d actual = leader.VertexAt(index);
                if (!ExactPoint(actual, expected[0], expected[1], expected[2]))
                    return false;
            }
            return true;
        }

        private static bool ExactMText(MText mtext, Leader leader, PreflightState preflight) =>
            string.Equals(mtext.Layer, Road08R01D1WriteContract.TextLayer, StringComparison.Ordinal)
            && string.Equals(mtext.Contents, Road08R01D1WriteContract.TextContents, StringComparison.Ordinal)
            && ExactPoint(mtext.Location,
                Road08R01D1WriteContract.TextLocationWcs[0],
                Road08R01D1WriteContract.TextLocationWcs[1],
                Road08R01D1WriteContract.TextLocationWcs[2])
            && mtext.TextHeight.Equals(Road08R01D1WriteContract.TextHeight)
            && mtext.Width.Equals(Road08R01D1WriteContract.TextWidth)
            && mtext.Rotation.Equals(Road08R01D1WriteContract.TextRotation)
            && mtext.Attachment == AttachmentPoint.BottomRight
            && ExactColor(mtext.Color, Road08R01D1WriteContract.TextColorIndex)
            && mtext.TextStyleId == preflight.TextStyleId
            && leader.Annotation == mtext.ObjectId;

        private static JObject LeaderEvidence(Database database, Transaction transaction, Leader leader)
        {
            var vertices = new JArray();
            for (int index = 0; index < leader.NumVertices; index++)
                vertices.Add(PointJson(leader.VertexAt(index)));

            return new JObject
            {
                ["type"] = leader.GetType().FullName,
                ["handle"] = HandleOf(leader),
                ["layer"] = leader.Layer,
                ["color"] = ColorSignature(leader.Color),
                ["dimclrd"] = ColorSignature(leader.Dimclrd),
                ["dimasz"] = leader.Dimasz,
                ["dimscale"] = leader.Dimscale,
                ["dimgap"] = leader.Dimgap,
                ["dimtad"] = leader.Dimtad,
                ["dimtxt"] = leader.Dimtxt,
                ["dimension_style"] = leader.DimensionStyleName,
                ["dimension_style_handle"] = IdHandle(leader.DimensionStyle),
                ["text_style_handle"] = IdHandle(leader.TextStyleId),
                ["arrow_block"] = BlockName(database, transaction, leader.Dimldrblk),
                ["arrow_block_handle"] = IdHandle(leader.Dimldrblk),
                ["has_arrow_head"] = leader.HasArrowHead,
                ["is_splined"] = leader.IsSplined,
                ["annotation_type"] = leader.AnnoType.ToString(),
                ["annotation_handle"] = IdHandle(leader.Annotation),
                ["vertices"] = vertices
            };
        }

        private static JObject MTextEvidence(MText mtext) => new JObject
        {
            ["type"] = mtext.GetType().FullName,
            ["handle"] = HandleOf(mtext),
            ["layer"] = mtext.Layer,
            ["contents"] = mtext.Contents,
            ["location"] = PointJson(mtext.Location),
            ["text_height"] = mtext.TextHeight,
            ["width"] = mtext.Width,
            ["rotation"] = mtext.Rotation,
            ["attachment"] = mtext.Attachment.ToString(),
            ["color"] = ColorSignature(mtext.Color),
            ["text_style"] = mtext.TextStyleName,
            ["text_style_handle"] = IdHandle(mtext.TextStyleId)
        };

        private static ObjectId ResolveHandle(Database database, string handleText)
        {
            long value = long.Parse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ObjectId id = database.GetObjectId(false, new Handle(value), 0);
            if (id.IsNull)
                throw new InvalidOperationException("A committed entity handle cannot be resolved.");
            return id;
        }

        private static string BlockName(Database database, Transaction transaction, ObjectId id)
        {
            if (id.IsNull)
                return null;
            BlockTableRecord record = transaction.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
            return record?.Name;
        }

        private static bool ExactColor(Color color, short expectedAci) =>
            color != null && color.ColorMethod == ColorMethod.ByAci && color.ColorIndex == expectedAci;

        private static JObject ColorSignature(Color color) => new JObject
        {
            ["method"] = color.ColorMethod.ToString(),
            ["aci"] = color.ColorIndex,
            ["argb"] = color.ColorValue.ToArgb(),
            ["name"] = color.ColorName ?? string.Empty,
            ["book"] = color.BookName ?? string.Empty
        };

        private static JObject ExtentsJson(Extents3d extents) => new JObject
        {
            ["min"] = PointJson(extents.MinPoint),
            ["max"] = PointJson(extents.MaxPoint)
        };

        private static JObject PointJson(Point3d point) => new JObject
        {
            ["x"] = point.X,
            ["y"] = point.Y,
            ["z"] = point.Z
        };

        private static JObject VectorJson(Vector3d vector) => new JObject
        {
            ["x"] = vector.X,
            ["y"] = vector.Y,
            ["z"] = vector.Z
        };

        private static JArray PointsJson(IEnumerable<double[]> points)
        {
            var array = new JArray();
            foreach (double[] point in points)
                array.Add(PointJson(new Point3d(point[0], point[1], point[2])));
            return array;
        }

        private static JObject ContractTextPointJson() => PointJson(ContractTextLocation());

        private static Point3d ContractTextLocation() => new Point3d(
            Road08R01D1WriteContract.TextLocationWcs[0],
            Road08R01D1WriteContract.TextLocationWcs[1],
            Road08R01D1WriteContract.TextLocationWcs[2]);

        private static bool ExactPoint(Point3d point, double x, double y, double z) =>
            point.X.Equals(x) && point.Y.Equals(y) && point.Z.Equals(z);

        private static string HandleOf(DBObject value) =>
            value.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

        private static string IdHandle(ObjectId id) =>
            id.IsNull ? null : id.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

        private static string HashCanonicalJson(JToken token)
        {
            // The server recomputes SHA-256 over the compact JSON signature supplied
            // by the caller. HashBlockDefinition includes the definition name, origin,
            // flags, and full members; HashBlockShape supplies its separately
            // normalized cross-drawing signature. The digest is uppercase hex.
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(token, Formatting.None));
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private static bool HasExactContractParameters(string json)
        {
            try
            {
                return JToken.DeepEquals(JObject.Parse(json),
                    JObject.Parse(Road08R01D1WriteContract.CanonicalParametersJson));
            }
            catch
            {
                return false;
            }
        }

        private static string Serialize(JToken token) =>
            JsonConvert.SerializeObject(token, Formatting.None);

        private static void EnsureFinite(JToken token)
        {
            if (token is JValue value && value.Type == JTokenType.Float)
            {
                double number = value.Value<double>();
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new InvalidOperationException("Non-finite geometry cannot be attested.");
                return;
            }

            if (token is JContainer container)
            {
                foreach (JToken child in container.Children())
                    EnsureFinite(child);
            }
        }

        private static bool TryDispose(IDisposable value)
        {
            if (value == null)
                return false;
            try { value.Dispose(); }
            catch { return false; }
            return true;
        }

        private static string SafeFailureDiagnostic(string phase, Exception exception)
        {
            var diagnostic = new JObject
            {
                ["phase"] = phase ?? "unknown",
                ["exception_type"] = exception == null
                    ? "unknown"
                    : exception.GetType().FullName ?? exception.GetType().Name,
                ["hresult"] = exception == null
                    ? null
                    : "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture)
            };

            if (exception is Autodesk.AutoCAD.Runtime.Exception runtimeException)
                diagnostic["autodesk_runtime_error_status"] = runtimeException.ErrorStatus.ToString();

            return diagnostic.ToString(Formatting.None);
        }

        private sealed class PreflightState
        {
            public PreflightState(JObject evidence, ObjectId textLayerId, ObjectId leaderLayerId,
                ObjectId textStyleId, ObjectId baseTextStyleId, string textStyleName,
                bool usesFallbackTextStyle, ObjectId dimStyleId, ObjectId arrowBlockId,
                JArray blockMembers, string blockGeometrySha256)
            {
                Evidence = evidence;
                TextLayerId = textLayerId;
                LeaderLayerId = leaderLayerId;
                TextStyleId = textStyleId;
                BaseTextStyleId = baseTextStyleId;
                TextStyleName = textStyleName;
                UsesFallbackTextStyle = usesFallbackTextStyle;
                DimStyleId = dimStyleId;
                ArrowBlockId = arrowBlockId;
                BlockMembers = blockMembers;
                BlockGeometrySha256 = blockGeometrySha256;
            }

            public JObject Evidence { get; }
            public ObjectId TextLayerId { get; set; }
            public ObjectId LeaderLayerId { get; set; }
            public ObjectId TextStyleId { get; set; }
            public ObjectId BaseTextStyleId { get; }
            public string TextStyleName { get; set; }
            public bool UsesFallbackTextStyle { get; }
            public ObjectId DimStyleId { get; }
            public ObjectId ArrowBlockId { get; set; }
            public JArray BlockMembers { get; set; }
            public string BlockGeometrySha256 { get; set; }
        }

        private sealed class WriteFailureException : InvalidOperationException
        {
            public WriteFailureException(string code, string message) : base(message)
            {
                Code = code;
            }

            public WriteFailureException(string code, string message, Exception innerException)
                : base(message, innerException)
            {
                Code = code;
            }

            public string Code { get; }
        }
    }
}
