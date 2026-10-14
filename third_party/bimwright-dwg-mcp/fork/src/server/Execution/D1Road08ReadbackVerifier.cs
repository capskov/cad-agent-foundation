using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server.Execution
{
    public sealed class D1ReadbackResult
    {
        internal D1ReadbackResult(bool passed, IReadOnlyList<string> errors)
        {
            Passed = passed;
            Errors = errors;
        }

        public bool Passed { get; }
        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>
    /// Server-side verifier for the fixed Road08 R01 readback evidence. It
    /// deliberately does not trust the plugin's verification enum or pass flag.
    /// </summary>
    public static class D1Road08ReadbackVerifier
    {
        private static readonly HashSet<string> BlockMemberTypes = new(StringComparer.Ordinal)
        {
            "Autodesk.AutoCAD.DatabaseServices.Polyline",
            "Autodesk.AutoCAD.DatabaseServices.Line"
        };

        public static D1ReadbackResult Verify(JObject pluginJob, string canonicalParametersJson)
        {
            var failures = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                if (pluginJob == null || !D1ParametersMatchContract(canonicalParametersJson))
                    return Failed("d1_contract_mismatch");
                if (!TryReadString(pluginJob, "verification_result_json", out string rawReadback)
                    || !TryReadString(pluginJob, "result_json", out string rawResult))
                    return Failed("d1_readback_missing");

                JObject readback = JObject.Parse(rawReadback);
                JObject result = JObject.Parse(rawResult);
                if (readback.Value<int?>("schema_version") != Road08R01D1WriteContract.EvidenceSchemaVersion
                    || result.Value<int?>("schema_version") != Road08R01D1WriteContract.EvidenceSchemaVersion
                    || readback.Value<string>("plan_id") != Road08R01D1WriteContract.PlanId
                    || result.Value<string>("plan_id") != Road08R01D1WriteContract.PlanId)
                    failures.Add("d1_readback_schema_mismatch");

                JObject preflight = readback["preflight"] as JObject;
                JObject committed = readback["committed"] as JObject;
                JObject resultPreflight = result["preflight"] as JObject;
                if (preflight == null || committed == null || resultPreflight == null)
                    return Failed("d1_readback_sections_missing");
                if (!JToken.DeepEquals(preflight, resultPreflight))
                    failures.Add("d1_preflight_result_mismatch");

                VerifyPreflight(preflight, failures);
                VerifyCommitted(pluginJob, result, preflight, committed, failures);

                if (pluginJob.Value<string>("verification") != "passed")
                    failures.Add("d1_plugin_readback_not_passed");
                if (committed.Value<bool?>("readback_complete") != true)
                    failures.Add("d1_readback_incomplete");
                if (committed.Value<bool?>("exact_readback_passed") != true)
                    failures.Add("d1_handler_exact_readback_not_passed");

                return new D1ReadbackResult(failures.Count == 0, failures.ToArray());
            }
            catch
            {
                failures.Add("d1_readback_malformed");
                return new D1ReadbackResult(false, failures.ToArray());
            }
        }

        private static void VerifyPreflight(JObject preflight, SortedSet<string> failures)
        {
            if (preflight.Value<int?>("schema_version") != Road08R01D1WriteContract.EvidenceSchemaVersion
                || preflight.Value<string>("plan_id") != Road08R01D1WriteContract.PlanId)
                failures.Add("d1_preflight_identity_mismatch");

            JObject targetLinetype = preflight["target_layer_linetype"] as JObject;
            if (!Road08R01D1WriteContract.IsTargetLayerLinetypeEvidenceValid(targetLinetype))
                failures.Add("d1_target_layer_linetype_identity_mismatch");

            if (preflight.Value<int?>("duplicate_count") != 0
                || preflight["duplicate_handles"] is not JArray duplicates || duplicates.Count != 0)
                failures.Add("d1_duplicate_callout_present");

            if (preflight["source_handles"] is not JArray sourceHandles
                || !sourceHandles.Values<string>().SequenceEqual(Road08R01D1WriteContract.SourceHandles, StringComparer.Ordinal))
                failures.Add("d1_source_handle_list_mismatch");

            if (preflight["source_polylines"] is not JArray sources
                || sources.Count != Road08R01D1WriteContract.SourceHandles.Length)
            {
                failures.Add("d1_source_geometry_missing");
            }
            else
            {
                for (int index = 0; index < Road08R01D1WriteContract.SourceHandles.Length; index++)
                {
                    JObject source = sources[index] as JObject;
                    if (source == null
                        || source.Value<string>("handle") != Road08R01D1WriteContract.SourceHandles[index]
                        || source.Value<string>("type") != "Autodesk.AutoCAD.DatabaseServices.Polyline"
                        || !string.Equals(source.Value<string>("layer"), "ROAD", StringComparison.OrdinalIgnoreCase)
                        || source["vertices"] is not JArray vertices || vertices.Count < 2
                        || source["bulges"] is not JArray bulges || bulges.Count != vertices.Count
                        || source["start_widths"] is not JArray startWidths || startWidths.Count != vertices.Count
                        || source["end_widths"] is not JArray endWidths || endWidths.Count != vertices.Count
                        || source["closed"]?.Type != JTokenType.Boolean
                        || !HasFiniteExtents(source["extents"] as JObject))
                        failures.Add("d1_source_geometry_mismatch");
                    else
                    {
                        VerifyFinite(source, failures, "d1_source_geometry_nonfinite");
                        string expectedDigest = Road08R01D1WriteContract.ExpectedSourceGeometrySha256(
                            Road08R01D1WriteContract.SourceHandles[index]);
                        if (!string.Equals(source.Value<string>("source_geometry_sha256"), expectedDigest,
                                StringComparison.Ordinal)
                            || !string.Equals(Road08R01D1WriteContract.ComputeSourceGeometrySha256(source),
                                expectedDigest, StringComparison.Ordinal))
                            failures.Add("d1_source_geometry_baseline_mismatch");

                        JObject geometry = source["geometry"] as JObject;
                        if (geometry == null
                            || !ExpectNumber(geometry, "elevation", 0d)
                            || !ExpectPoint(geometry["normal"] as JObject, new[] { 0d, 0d, 1d })
                            || !ExpectNumber(geometry, "constant_width", 0d)
                            || !ExpectNumber(geometry, "thickness", 0d))
                            failures.Add("d1_source_polyline_plane_or_width_mismatch");
                    }
                }
            }

            JObject required = preflight["required"] as JObject;
            if (required == null
                || !VerifyRequired(required["text_layer"] as JObject, Road08R01D1WriteContract.TextLayer, "exists", failures)
                || !VerifyRequired(required["leader_layer"] as JObject, Road08R01D1WriteContract.LeaderLayer, "exists", failures)
                || !VerifyD1TextStyle(required["text_style"] as JObject,
                    preflight["base_text_style"] as JObject, failures)
                || !VerifyRequired(required["dimstyle"] as JObject, Road08R01D1WriteContract.LeaderStyle, "exists", failures)
                || !VerifyRequired(required["arrow_block"] as JObject, Road08R01D1WriteContract.ArrowBlockName, "fully_inspected", failures))
                failures.Add("d1_required_style_layer_or_block_missing");

            JObject arrowBlock = required?["arrow_block"] as JObject;
            JArray blockMembers = preflight["block_members"] as JArray;
            if (!VerifyBlockGeometry(blockMembers, failures, "d1_preflight_block_geometry")
                || !VerifyArrowBlockDefinition(arrowBlock, Road08R01D1WriteContract.ArrowBlockName, blockMembers,
                    preflight.Value<string>("block_geometry_sha256"),
                    preflight.Value<string>("block_shape_sha256"), failures,
                    "d1_preflight_block_definition"))
                failures.Add("d1_preflight_block_invalid");

            VerifyResourcePlan(preflight, failures);
        }

        private static void VerifyCommitted(JObject pluginJob, JObject result, JObject preflight,
            JObject committed, SortedSet<string> failures)
        {
            string leaderHandle = result.Value<string>("leader_handle");
            string mtextHandle = result.Value<string>("mtext_handle");
            if (!IsHandle(leaderHandle) || !IsHandle(mtextHandle)
                || string.Equals(leaderHandle, mtextHandle, StringComparison.OrdinalIgnoreCase)
                || Road08R01D1WriteContract.SourceHandles.Contains(leaderHandle, StringComparer.OrdinalIgnoreCase)
                || Road08R01D1WriteContract.SourceHandles.Contains(mtextHandle, StringComparer.OrdinalIgnoreCase))
                failures.Add("d1_committed_handles_invalid");

            if (committed.Value<string>("leader_handle") != leaderHandle
                || committed.Value<string>("mtext_handle") != mtextHandle)
                failures.Add("d1_result_handle_mismatch");

            JObject expected = committed["expected"] as JObject;
            JObject leader = committed["leader"] as JObject;
            JObject mtext = committed["mtext"] as JObject;
            JObject required = preflight["required"] as JObject;
            if (expected == null || leader == null || mtext == null || required == null)
            {
                failures.Add("d1_committed_entity_evidence_missing");
                return;
            }

            VerifyExpectedFields(expected, leaderHandle, mtextHandle,
                preflight.Value<string>("block_geometry_sha256"),
                required["text_style"]?.Value<string>("name"), failures);
            VerifyLeader(leader, leaderHandle, mtextHandle, required, failures);
            VerifyMText(mtext, mtextHandle, required, failures);

            JObject selectedStyleAfter = committed["selected_text_style_after"] as JObject;
            JObject baseStyleAfter = committed["base_text_style_after"] as JObject;
            if (committed.Value<bool?>("selected_text_style_matches_preflight") != true
                || !JToken.DeepEquals(required["text_style"], selectedStyleAfter)
                || !VerifyD1TextStyle(selectedStyleAfter, preflight["base_text_style"] as JObject, failures))
                failures.Add("d1_selected_text_style_readback_mismatch");
            if (committed.Value<bool?>("base_text_style_unchanged") != true
                || !JToken.DeepEquals(preflight["base_text_style"], baseStyleAfter))
                failures.Add("d1_shared_text_style_changed");

            JObject postArrowBlock = committed["resource_readback"]?["arrow_block"] as JObject;
            JArray postBlockMembers = postArrowBlock?["members"] as JArray
                ?? committed["post_block_members"] as JArray;
            string postBlockGeometryDigest = postArrowBlock?.Value<string>("geometry_sha256")
                ?? committed.Value<string>("post_block_geometry_sha256");
            string postBlockShapeDigest = postArrowBlock?.Value<string>("shape_sha256")
                ?? committed.Value<string>("post_block_shape_sha256");
            if (!JToken.DeepEquals(preflight["block_members"], postBlockMembers)
                || !string.Equals(preflight.Value<string>("block_geometry_sha256"),
                    postBlockGeometryDigest, StringComparison.Ordinal)
                || committed.Value<bool?>("block_geometry_matches_preflight") != true
                || !VerifyBlockGeometry(postBlockMembers, failures,
                    "d1_committed_block_geometry")
                || !VerifyArrowBlockDefinition(postArrowBlock, Road08R01D1WriteContract.ArrowBlockName,
                    postBlockMembers, postBlockGeometryDigest, postBlockShapeDigest,
                    failures, "d1_committed_block_definition"))
                failures.Add("d1_block_geometry_changed_after_commit");

            VerifyResourceReadback(preflight, committed, failures);

            if (!JToken.DeepEquals(preflight["source_polylines"], committed["source_polylines_after"]))
                failures.Add("d1_source_geometry_changed_after_commit");

            if (!TryReadString(pluginJob, "result_json", out string rawResult)
                || !IsJson(rawResult))
                failures.Add("d1_committed_result_invalid");
        }

        private static void VerifyExpectedFields(JObject expected, string leaderHandle,
            string mtextHandle, string blockDigest, string textStyleName, SortedSet<string> failures)
        {
            ExpectString(expected, "leader_type", "Autodesk.AutoCAD.DatabaseServices.Leader", failures);
            ExpectString(expected, "leader_layer", Road08R01D1WriteContract.LeaderLayer, failures);
            ExpectInteger(expected, "leader_color_index", Road08R01D1WriteContract.LeaderColorIndex, failures);
            ExpectString(expected, "leader_style", Road08R01D1WriteContract.LeaderStyle, failures);
            ExpectString(expected, "leader_arrow_block", Road08R01D1WriteContract.ArrowBlockName, failures);
            ExpectPointsForExpected(expected["leader_vertices_wcs"] as JArray,
                Road08R01D1WriteContract.LeaderVerticesWcs, failures);
            ExpectString(expected, "mtext_type", "Autodesk.AutoCAD.DatabaseServices.MText", failures);
            ExpectString(expected, "mtext_layer", Road08R01D1WriteContract.TextLayer, failures);
            ExpectString(expected, "mtext_contents", Road08R01D1WriteContract.TextContents, failures);
            ExpectPointForExpected(expected["mtext_location_wcs"] as JObject,
                Road08R01D1WriteContract.TextLocationWcs, failures);
            ExpectNumber(expected, "mtext_height", Road08R01D1WriteContract.TextHeight, failures);
            ExpectNumber(expected, "mtext_width", Road08R01D1WriteContract.TextWidth, failures);
            ExpectNumber(expected, "mtext_rotation", Road08R01D1WriteContract.TextRotation, failures);
            ExpectString(expected, "mtext_attachment", Road08R01D1WriteContract.Attachment, failures);
            ExpectInteger(expected, "mtext_color_index", Road08R01D1WriteContract.TextColorIndex, failures);
            ExpectString(expected, "mtext_style", textStyleName, failures);
            if (expected.Value<string>("leader_annotation_handle") != mtextHandle
                || expected.Value<string>("arrow_block_geometry_sha256") != blockDigest
                || expected.Value<string>("arrow_block") != Road08R01D1WriteContract.ArrowBlockName
                || leaderHandle == null)
                failures.Add("d1_expected_relationship_mismatch");
        }

        private static void VerifyResourcePlan(JObject preflight, SortedSet<string> failures)
        {
            if (preflight.Value<string>("resource_creation_policy")
                != Road08R01D1WriteContract.ResourceCreationPolicy)
                failures.Add("d1_resource_creation_policy_mismatch");

            JObject plan = preflight["resource_plan"] as JObject;
            string[] expectedNames = { "text_layer", "leader_layer", "arrow_block" };
            if (plan == null || plan.Properties().Count() != expectedNames.Length
                || !expectedNames.All(name => plan[name] is JObject))
            {
                failures.Add("d1_resource_plan_missing_or_unexpected");
                return;
            }

            JObject required = preflight["required"] as JObject;
            VerifyLayerResourcePlan(plan["text_layer"] as JObject,
                Road08R01D1WriteContract.TextLayer, required?["text_layer"] as JObject, failures);
            VerifyLayerResourcePlan(plan["leader_layer"] as JObject,
                Road08R01D1WriteContract.LeaderLayer, required?["leader_layer"] as JObject, failures);
            VerifyArrowBlockResourcePlan(plan["arrow_block"] as JObject,
                required?["arrow_block"] as JObject,
                preflight["block_members"] as JArray, failures);
        }

        private static void VerifyLayerResourcePlan(JObject plan, string expectedName,
            JObject requiredResource,
            SortedSet<string> failures)
        {
            if (plan == null || plan.Value<string>("name") != expectedName)
            {
                failures.Add("d1_layer_resource_plan_identity_mismatch");
                return;
            }

            if (plan["before_exists"]?.Type != JTokenType.Boolean)
            {
                failures.Add("d1_layer_resource_plan_before_exists_invalid");
                return;
            }
            bool beforeExists = plan.Value<bool>("before_exists");
            string expectedAction = beforeExists ? "existing_verified" : "created";
            if (plan.Value<string>("action") != expectedAction)
                failures.Add("d1_layer_resource_plan_action_mismatch");

            JObject expected = plan["expected"] as JObject;
            if (!IsLayerPolicy(expected, expectedName))
                failures.Add("d1_layer_resource_policy_mismatch");

            JObject before = plan["before"] as JObject;
            if (beforeExists)
            {
                if (!IsLayerResource(before, expectedName)
                    || before.Value<string>("handle") != requiredResource?.Value<string>("handle"))
                    failures.Add("d1_layer_resource_before_mismatch");
            }
            else if (plan["before"]?.Type != JTokenType.Null)
            {
                failures.Add("d1_layer_resource_before_mismatch");
            }
        }

        private static void VerifyArrowBlockResourcePlan(JObject plan, JObject requiredResource,
            JArray requiredMembers, SortedSet<string> failures)
        {
            if (plan == null || plan.Value<string>("name") != Road08R01D1WriteContract.ArrowBlockName)
            {
                failures.Add("d1_arrow_resource_plan_identity_mismatch");
                return;
            }

            if (plan["before_exists"]?.Type != JTokenType.Boolean)
            {
                failures.Add("d1_arrow_resource_plan_before_exists_invalid");
                return;
            }
            bool beforeExists = plan.Value<bool>("before_exists");
            string expectedAction = beforeExists ? "existing_verified" : "created";
            if (plan.Value<string>("action") != expectedAction)
                failures.Add("d1_arrow_resource_plan_action_mismatch");

            JObject expected = plan["expected"] as JObject;
            if (expected == null || expected.Properties().Count() != 1
                || expected.Value<string>("shape_sha256")
                    != Road08R01D1WriteContract.ArrowBlockShapeSha256)
                failures.Add("d1_arrow_resource_shape_policy_mismatch");

            JObject source = plan["source"] as JObject;
            JArray sourceMembers = source?["members"] as JArray;
            if (!VerifyBlockGeometry(sourceMembers, failures, "d1_arrow_resource_source")
                || !VerifyArrowBlockDefinition(source, "_Dot", sourceMembers,
                    source?.Value<string>("geometry_sha256"),
                    source?.Value<string>("shape_sha256"), failures,
                    "d1_arrow_resource_source"))
                failures.Add("d1_arrow_resource_source_mismatch");

            if (beforeExists)
            {
                JObject before = plan["before"] as JObject;
                JArray members = before?["members"] as JArray;
                if (!VerifyBlockGeometry(members, failures, "d1_arrow_resource_before")
                    || !VerifyArrowBlockDefinition(before, Road08R01D1WriteContract.ArrowBlockName,
                        members, before?.Value<string>("geometry_sha256"),
                        before?.Value<string>("shape_sha256"), failures,
                        "d1_arrow_resource_before")
                    || !ArrowBlockMetadataMatches(source, before)
                    || before?.Value<string>("handle") != requiredResource?.Value<string>("handle")
                    || before?.Value<string>("geometry_sha256")
                        != requiredResource?.Value<string>("geometry_sha256")
                    || !JToken.DeepEquals(members, requiredMembers))
                    failures.Add("d1_arrow_resource_before_mismatch");
            }
            else
            {
                if (plan["before"]?.Type != JTokenType.Null
                    || !ArrowBlockMetadataMatches(source, requiredResource))
                    failures.Add("d1_arrow_resource_before_mismatch");
            }
        }

        private static void VerifyResourceReadback(JObject preflight, JObject committed,
            SortedSet<string> failures)
        {
            JObject plan = preflight["resource_plan"] as JObject;
            JObject readback = committed["resource_readback"] as JObject;
            string[] expectedNames = { "text_layer", "leader_layer", "arrow_block" };
            if (plan == null || readback == null || readback.Properties().Count() != expectedNames.Length
                || !expectedNames.All(name => readback[name] is JObject))
            {
                failures.Add("d1_resource_readback_missing_or_unexpected");
                return;
            }

            JObject textLayer = readback["text_layer"] as JObject;
            JObject leaderLayer = readback["leader_layer"] as JObject;
            if (!IsLayerResource(textLayer, Road08R01D1WriteContract.TextLayer)
                || !IsLayerResource(leaderLayer, Road08R01D1WriteContract.LeaderLayer)
                || !IsLayerPolicy(textLayer, Road08R01D1WriteContract.TextLayer)
                || !IsLayerPolicy(leaderLayer, Road08R01D1WriteContract.LeaderLayer))
                failures.Add("d1_layer_resource_readback_mismatch");

            JObject targetLinetype = preflight["target_layer_linetype"] as JObject;
            string targetLinetypeHandle = targetLinetype?.Value<string>("handle");
            if (!IsHandle(targetLinetypeHandle)
                || !SameCanonicalHandle(textLayer?.Value<string>("linetype_handle"), targetLinetypeHandle)
                || !SameCanonicalHandle(leaderLayer?.Value<string>("linetype_handle"), targetLinetypeHandle))
                failures.Add("d1_layer_linetype_object_id_mismatch");

            JObject required = preflight["required"] as JObject;
            if (textLayer?.Value<string>("handle") != required?["text_layer"]?.Value<string>("handle")
                || leaderLayer?.Value<string>("handle") != required?["leader_layer"]?.Value<string>("handle"))
                failures.Add("d1_layer_resource_handle_mismatch");

            JObject resourcePlan = plan;
            foreach (string key in new[] { "text_layer", "leader_layer" })
            {
                JObject itemPlan = resourcePlan[key] as JObject;
                if (itemPlan?.Value<bool?>("before_exists") == true
                    && !SameCanonicalHandle(itemPlan["before"]?.Value<string>("linetype_handle"),
                        targetLinetypeHandle))
                    failures.Add("d1_existing_layer_linetype_object_id_changed");
                if (itemPlan?.Value<bool?>("before_exists") == true
                    && itemPlan["before"]?.Value<string>("handle")
                        != readback[key]?.Value<string>("handle"))
                    failures.Add("d1_existing_layer_resource_changed");
            }

            JObject arrow = readback["arrow_block"] as JObject;
            JArray members = arrow?["members"] as JArray;
            JObject arrowSource = resourcePlan["arrow_block"]?["source"] as JObject;
            if (!VerifyBlockGeometry(members, failures, "d1_arrow_resource_readback")
                || !VerifyArrowBlockDefinition(arrow, Road08R01D1WriteContract.ArrowBlockName,
                    members, arrow?.Value<string>("geometry_sha256"),
                    arrow?.Value<string>("shape_sha256"), failures,
                    "d1_arrow_resource_readback")
                || !ArrowBlockMetadataMatches(arrowSource, arrow))
                failures.Add("d1_arrow_resource_readback_mismatch");

            if (arrow?.Value<string>("handle") != required?["arrow_block"]?.Value<string>("handle")
                || !JToken.DeepEquals(members, preflight["block_members"])
                || !JToken.DeepEquals(required?["arrow_block"], arrow))
                failures.Add("d1_arrow_resource_handle_or_members_changed");

            JObject arrowPlan = resourcePlan["arrow_block"] as JObject;
            if (arrowPlan?.Value<bool?>("before_exists") == true
                && (arrowPlan["before"]?.Value<string>("handle") != arrow?.Value<string>("handle")
                    || arrowPlan["before"]?.Value<string>("geometry_sha256")
                        != arrow?.Value<string>("geometry_sha256")
                    || !JToken.DeepEquals(arrowPlan["before"]?["members"], members)
                    || !ArrowBlockMetadataMatches(arrowPlan["before"] as JObject, arrow)))
                failures.Add("d1_existing_arrow_resource_changed");
        }

        private static bool IsLayerPolicy(JObject value, string expectedName) => value != null
            && value.Value<string>("name") == expectedName
            && value.Value<int?>("color_index") == Road08R01D1WriteContract.TextLayerColorIndex
            && value.Value<string>("color_method") == Road08R01D1WriteContract.TargetLayerColorMethod
            && Road08R01D1WriteContract.LayerLinetypeNameMatches(value.Value<string>("linetype"))
            && value.Value<bool?>("is_off") == Road08R01D1WriteContract.TargetLayerIsOff
            && value.Value<bool?>("is_frozen") == Road08R01D1WriteContract.TargetLayerIsFrozen
            && value.Value<bool?>("is_locked") == Road08R01D1WriteContract.TargetLayerIsLocked
            && value.Value<bool?>("is_plottable") == Road08R01D1WriteContract.TargetLayerIsPlottable;

        private static bool IsLayerResource(JObject value, string expectedName) =>
            IsLayerPolicy(value, expectedName)
            && value.Value<bool?>("exists") == true
            && value.Value<bool?>("fully_inspected") == true
            && IsHandle(value.Value<string>("handle"));

        private static void VerifyLeader(JObject leader, string handle, string mtextHandle,
            JObject required, SortedSet<string> failures)
        {
            if (leader.Value<string>("type") != "Autodesk.AutoCAD.DatabaseServices.Leader"
                || leader.Value<string>("handle") != handle
                || leader.Value<string>("layer") != Road08R01D1WriteContract.LeaderLayer
                || leader.Value<string>("dimension_style") != Road08R01D1WriteContract.LeaderStyle
                || leader.Value<string>("dimension_style_handle") != required["dimstyle"]?.Value<string>("handle")
                || leader.Value<string>("text_style_handle") != required["text_style"]?.Value<string>("handle")
                || leader.Value<string>("arrow_block") != Road08R01D1WriteContract.ArrowBlockName
                || leader.Value<string>("arrow_block_handle") != required["arrow_block"]?.Value<string>("handle")
                || leader.Value<bool?>("has_arrow_head") != true
                || leader.Value<bool?>("is_splined") != false
                || leader.Value<string>("annotation_handle") != mtextHandle
                || !ExpectColor(leader["color"] as JObject, Road08R01D1WriteContract.LeaderColorIndex)
                || !ExpectColor(leader["dimclrd"] as JObject, Road08R01D1WriteContract.LeaderColorIndex)
                || !ExpectNumber(leader, "dimasz", Road08R01D1WriteContract.LeaderArrowSize)
                || !ExpectNumber(leader, "dimscale", Road08R01D1WriteContract.LeaderScale)
                || !ExpectNumber(leader, "dimgap", Road08R01D1WriteContract.LeaderGap)
                || leader.Value<int?>("dimtad") != Road08R01D1WriteContract.LeaderTextAbove
                || !ExpectNumber(leader, "dimtxt", Road08R01D1WriteContract.LeaderTextHeight)
                || !ExpectPoints(leader["vertices"] as JArray,
                    Road08R01D1WriteContract.LeaderVerticesWcs, failures))
                failures.Add("d1_leader_readback_mismatch");
        }

        private static void VerifyMText(JObject mtext, string handle, JObject required,
            SortedSet<string> failures)
        {
            if (mtext.Value<string>("type") != "Autodesk.AutoCAD.DatabaseServices.MText"
                || mtext.Value<string>("handle") != handle
                || mtext.Value<string>("layer") != Road08R01D1WriteContract.TextLayer
                || mtext.Value<string>("contents") != Road08R01D1WriteContract.TextContents
                || !ExpectPoint(mtext["location"] as JObject, Road08R01D1WriteContract.TextLocationWcs)
                || !ExpectNumber(mtext, "text_height", Road08R01D1WriteContract.TextHeight)
                || !ExpectNumber(mtext, "width", Road08R01D1WriteContract.TextWidth)
                || !ExpectNumber(mtext, "rotation", Road08R01D1WriteContract.TextRotation)
                || mtext.Value<string>("attachment") != Road08R01D1WriteContract.Attachment
                || mtext.Value<string>("text_style") != required["text_style"]?.Value<string>("name")
                || mtext.Value<string>("text_style_handle") != required["text_style"]?.Value<string>("handle")
                || !ExpectColor(mtext["color"] as JObject, Road08R01D1WriteContract.TextColorIndex))
                failures.Add("d1_mtext_readback_mismatch");
        }

        private static bool VerifyRequired(JObject item, string name, string flag, SortedSet<string> failures)
        {
            bool valid = item != null && item.Value<string>("name") == name
                && item.Value<bool?>("exists") == true
                && (flag != "fully_inspected" || item.Value<bool?>(flag) == true)
                && IsHandle(item.Value<string>("handle"));
            if (!valid) failures.Add("d1_required_" + name.ToLowerInvariant().Replace(' ', '_') + "_invalid");
            return valid;
        }

        private static bool VerifyD1TextStyle(JObject style, JObject baseStyle,
            SortedSet<string> failures)
        {
            bool valid = style != null && style.Value<bool?>("exists") == true
                && IsHandle(style.Value<string>("handle"))
                && style.Value<bool?>("style_creation_required") == false
                && baseStyle != null
                && baseStyle.Value<string>("name") == Road08R01D1WriteContract.TextStyle
                && IsHandle(baseStyle.Value<string>("handle"))
                && IsSupportedBaseFontPair(baseStyle);
            if (!valid)
            {
                failures.Add("d1_text_style_evidence_invalid");
                return false;
            }

            string name = style.Value<string>("name");
            JObject resolution = style["font_resolution"] as JObject;
            if (name == Road08R01D1WriteContract.TextStyle)
            {
                valid = style.Value<string>("selected_pair") == "project_pair"
                    && MatchesFontFileName(style.Value<string>("font_file"), Road08R01D1WriteContract.TextFontFile)
                    && MatchesFontFileName(style.Value<string>("big_font_file"), Road08R01D1WriteContract.TextBigFontFile)
                    && style.Value<string>("source_style_name") == null
                    && style.Value<string>("handle") == baseStyle.Value<string>("handle")
                    && SameStyleMetrics(style, baseStyle)
                    && resolution?.Value<string>("configured_pair_kind") == "project_pair"
                    && resolution.Value<bool?>("configured_pair_resolved") == true
                    && VerifyResolvedFontPair(resolution, "project_pair",
                        Road08R01D1WriteContract.TextFontFile, null,
                        Road08R01D1WriteContract.TextBigFontFile, null);
            }
            else if (name == Road08R01D1WriteContract.FallbackTextStyle)
            {
                valid = style.Value<string>("selected_pair") == "fallback_pair"
                    && MatchesFontFileName(style.Value<string>("font_file"), Road08R01D1WriteContract.FallbackTextFontFile)
                    && MatchesFontFileName(style.Value<string>("big_font_file"), Road08R01D1WriteContract.FallbackBigFontFile)
                    && style.Value<string>("source_style_name") == Road08R01D1WriteContract.TextStyle
                    && style.Value<string>("source_style_handle") == baseStyle.Value<string>("handle")
                    && SameStyleMetrics(style, baseStyle)
                    && resolution?.Value<string>("configured_pair_kind") == "fallback_pair"
                    && resolution.Value<bool?>("configured_pair_resolved") == true
                    && VerifyResolvedFontPair(resolution, "fallback_pair",
                        Road08R01D1WriteContract.FallbackTextFontFile,
                        Road08R01D1WriteContract.FallbackTextFontSha256,
                        Road08R01D1WriteContract.FallbackBigFontFile,
                        Road08R01D1WriteContract.FallbackBigFontSha256);
            }
            else
            {
                valid = false;
            }

            if (!valid) failures.Add("d1_text_style_font_pair_mismatch");
            return valid;
        }

        private static bool MatchesFontFileName(string actual, string expected) =>
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        private static bool IsSupportedBaseFontPair(JObject style)
        {
            string small = style?.Value<string>("font_file");
            string big = style?.Value<string>("big_font_file");
            return (string.Equals(small, Road08R01D1WriteContract.TextFontFile, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(big, Road08R01D1WriteContract.TextBigFontFile, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(small, Road08R01D1WriteContract.FallbackTextFontFile, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(big, Road08R01D1WriteContract.FallbackBigFontFile, StringComparison.OrdinalIgnoreCase));
        }

        private static bool VerifyResolvedFontPair(JObject resolution, string pairName,
            string smallFile, string smallHash, string bigFile, string bigHash)
        {
            JObject pair = resolution?[pairName] as JObject;
            JObject small = pair?["small_font_resolution"] as JObject;
            JObject big = pair?["big_font_resolution"] as JObject;
            return pair?.Value<bool?>("resolved") == true
                && pair.Value<string>("small_font_file") == smallFile
                && pair.Value<string>("big_font_file") == bigFile
                && VerifyResolvedFont(small, smallFile, smallHash)
                && VerifyResolvedFont(big, bigFile, bigHash);
        }

        private static bool VerifyResolvedFont(JObject font, string fileName, string expectedHash)
        {
            bool valid = font != null && font.Value<bool?>("resolved") == true
                && font.Value<bool?>("basename_matches") == true
                && string.Equals(font.Value<string>("requested_file"), fileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(font.Value<string>("resolved_basename"), fileName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(font.Value<string>("resolved_path"))
                && IsSha256(font.Value<string>("sha256"));
            if (expectedHash != null)
                valid &= string.Equals(font.Value<string>("expected_sha256"), expectedHash, StringComparison.OrdinalIgnoreCase)
                    && font.Value<bool?>("sha256_matches_expected") == true
                    && string.Equals(font.Value<string>("sha256"), expectedHash, StringComparison.OrdinalIgnoreCase);
            return valid;
        }

        private static bool SameStyleMetrics(JObject selected, JObject source) =>
            JToken.DeepEquals(selected["text_size"], source["text_size"])
            && JToken.DeepEquals(selected["x_scale"], source["x_scale"])
            && JToken.DeepEquals(selected["obliquing_angle"], source["obliquing_angle"])
            && JToken.DeepEquals(selected["is_shape_file"], source["is_shape_file"])
            && JToken.DeepEquals(selected["is_vertical"], source["is_vertical"]);

        private static bool VerifyArrowBlockDefinition(JObject definition, string expectedName, JArray members,
            string geometryDigest, string reportedShapeDigest, SortedSet<string> failures,
            string codePrefix)
        {
            bool valid = definition != null
                && definition.Value<string>("name") == expectedName
                && definition.Value<bool?>("exists") == true
                && definition.Value<bool?>("fully_inspected") == true
                && IsHandle(definition.Value<string>("handle"))
                && definition.Value<int?>("member_count") == 2
                && definition.Value<bool?>("is_layout") == false
                && definition.Value<bool?>("is_anonymous") == false
                && definition.Value<bool?>("is_external_reference") == false
                && definition.Value<bool?>("is_overlay_reference") == false
                && definition.Value<bool?>("is_dynamic") == false
                && HasSupportedArrowBlockMetadata(definition);

            string actualGeometryDigest =
                Road08R01D1WriteContract.ComputeArrowBlockGeometrySha256(definition, members);
            string actualShapeDigest =
                Road08R01D1WriteContract.ComputeArrowBlockShapeSha256(definition, members);
            valid &= IsSha256(actualGeometryDigest)
                && string.Equals(actualGeometryDigest, geometryDigest, StringComparison.Ordinal)
                && string.Equals(definition?.Value<string>("geometry_sha256"), actualGeometryDigest,
                    StringComparison.Ordinal)
                && string.Equals(actualShapeDigest, Road08R01D1WriteContract.ArrowBlockShapeSha256,
                    StringComparison.Ordinal)
                && string.Equals(actualShapeDigest, reportedShapeDigest, StringComparison.Ordinal)
                && string.Equals(definition?.Value<string>("shape_sha256"), actualShapeDigest,
                    StringComparison.Ordinal);

            if (!valid)
                failures.Add(codePrefix + "_digest_or_identity_mismatch");
            return valid;
        }

        private static bool HasSupportedArrowBlockMetadata(JObject definition) => definition != null
            && definition["units"]?.Type == JTokenType.String
            && !string.IsNullOrWhiteSpace(definition.Value<string>("units"))
            && definition["block_scaling"]?.Type == JTokenType.String
            && !string.IsNullOrWhiteSpace(definition.Value<string>("block_scaling"))
            && definition["explodable"]?.Type == JTokenType.Boolean
            && definition["comments"]?.Type == JTokenType.String
            && definition["annotative"]?.Type == JTokenType.String
            && !string.IsNullOrWhiteSpace(definition.Value<string>("annotative"))
            && definition["merge_style"]?.Type == JTokenType.String
            && !string.IsNullOrWhiteSpace(definition.Value<string>("merge_style"));

        private static bool ArrowBlockMetadataMatches(JObject source, JObject target)
        {
            if (!HasSupportedArrowBlockMetadata(source) || !HasSupportedArrowBlockMetadata(target))
                return false;
            string[] properties =
            {
                "units", "block_scaling", "explodable", "comments", "annotative", "merge_style"
            };
            return properties.All(name => JToken.DeepEquals(source[name], target[name]));
        }

        private static bool VerifyBlockGeometry(JArray members, SortedSet<string> failures,
            string codePrefix)
        {
            if (members == null || members.Count != 2)
            {
                failures.Add(codePrefix + "_shape_missing");
                return false;
            }

            string[] types = members.OfType<JObject>().Select(member => member.Value<string>("type")).ToArray();
            if (types.Length != 2 || !types.Contains("Autodesk.AutoCAD.DatabaseServices.Polyline", StringComparer.Ordinal)
                || !types.Contains("Autodesk.AutoCAD.DatabaseServices.Line", StringComparer.Ordinal))
                failures.Add(codePrefix + "_members_mismatch");

            string[] handles = members.OfType<JObject>().Select(member => member.Value<string>("handle")).ToArray();
            if (handles.Length != 2 || handles.Any(handle => !IsHandle(handle))
                || !handles.SequenceEqual(handles.OrderBy(handle => handle, StringComparer.Ordinal), StringComparer.Ordinal))
                failures.Add(codePrefix + "_handle_order_invalid");

            foreach (JObject member in members.OfType<JObject>())
            {
                JObject color = member["color"] as JObject;
                string type = member.Value<string>("type");
                JArray vertices = member["vertices"] as JArray;
                JArray bulges = member["bulges"] as JArray;
                bool geometryValid = BlockMemberTypes.Contains(type)
                    && member.Value<string>("layer") == "0"
                    && color?.Value<string>("method") == "ByBlock"
                    && color.Value<int?>("aci") == 0
                    && HasFiniteExtents(member["extents"] as JObject)
                    && member["geometry"] is JObject
                    && vertices != null && vertices.Count >= 2;
                if (type == "Autodesk.AutoCAD.DatabaseServices.Polyline")
                    geometryValid &= bulges != null && bulges.Count == vertices?.Count
                        && member["closed"]?.Type == JTokenType.Boolean;
                else if (type == "Autodesk.AutoCAD.DatabaseServices.Line")
                    geometryValid &= vertices?.Count == 2 && bulges?.Count == 0
                        && member["closed"]?.Type == JTokenType.Null;
                if (!geometryValid)
                    failures.Add(codePrefix + "_member_geometry_invalid");
                VerifyFinite(member, failures, codePrefix + "_nonfinite_geometry");
            }
            return failures.All(code => !code.StartsWith(codePrefix, StringComparison.Ordinal));
        }

        private static bool ExpectColor(JObject color, short aci) => color != null
            && color.Value<string>("method") == "ByAci" && color.Value<int?>("aci") == aci;

        private static bool ExpectPoints(JArray actual, double[][] expected, SortedSet<string> failures)
        {
            bool valid = actual != null && actual.Count == expected.Length;
            if (valid)
            {
                for (int index = 0; index < expected.Length; index++)
                    valid &= ExpectPoint(actual[index] as JObject, expected[index]);
            }
            if (!valid) failures.Add("d1_points_mismatch");
            return valid;
        }

        private static bool ExpectPoint(JObject actual, double[] expected) => actual != null
            && ExpectNumber(actual, "x", expected[0])
            && ExpectNumber(actual, "y", expected[1])
            && ExpectNumber(actual, "z", expected[2]);

        private static bool ExpectPoint(JObject actual, double[] expected, SortedSet<string> failures)
        {
            bool valid = ExpectPoint(actual, expected);
            if (!valid) failures.Add("d1_point_mismatch");
            return valid;
        }

        private static void ExpectPointsForExpected(JArray actual, double[][] expected, SortedSet<string> failures)
        {
            bool valid = actual != null && actual.Count == expected.Length;
            if (valid)
            {
                for (int index = 0; index < expected.Length; index++)
                    valid &= ExpectPoint(actual[index] as JObject, expected[index]);
            }
            if (!valid) failures.Add("d1_expected_points_mismatch");
        }

        private static void ExpectPointForExpected(JObject actual, double[] expected, ICollection<string> failures)
        {
            if (!ExpectPoint(actual, expected)) failures.Add("d1_expected_point_mismatch");
        }

        private static void ExpectNumber(JObject value, string name, double expected, SortedSet<string> failures)
        {
            if (!ExpectNumber(value, name, expected)) failures.Add("d1_expected_" + name + "_mismatch");
        }

        private static bool ExpectNumber(JObject value, string name, double expected) =>
            TryNumber(value?[name], out double actual) && actual.Equals(expected);

        private static void ExpectString(JObject value, string name, string expected, SortedSet<string> failures)
        {
            if (value?.Value<string>(name) != expected) failures.Add("d1_expected_" + name + "_mismatch");
        }

        private static void ExpectInteger(JObject value, string name, int expected, SortedSet<string> failures)
        {
            if (value?.Value<int?>(name) != expected) failures.Add("d1_expected_" + name + "_mismatch");
        }

        private static bool HasFiniteExtents(JObject extents) => extents != null
            && HasFinitePoint(extents["min"] as JObject) && HasFinitePoint(extents["max"] as JObject);

        private static bool HasFinitePoint(JObject point) => point != null
            && TryNumber(point["x"], out _) && TryNumber(point["y"], out _) && TryNumber(point["z"], out _);

        private static void VerifyFinite(JToken value, SortedSet<string> failures, string code)
        {
            if (value is JValue scalar && (scalar.Type == JTokenType.Float || scalar.Type == JTokenType.Integer))
            {
                if (!TryNumber(scalar, out _)) failures.Add(code);
            }
            else if (value is JContainer container)
            {
                foreach (JToken child in container.Children()) VerifyFinite(child, failures, code);
            }
        }

        private static bool TryNumber(JToken token, out double value)
        {
            value = 0;
            if (token == null || token.Type is not (JTokenType.Integer or JTokenType.Float)) return false;
            try
            {
                value = token.Value<double>();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch { return false; }
        }

        private static bool TryReadString(JObject value, string name, out string result)
        {
            result = null;
            if (value?[name]?.Type != JTokenType.String) return false;
            result = value.Value<string>(name);
            return !string.IsNullOrWhiteSpace(result);
        }

        private static bool IsJson(string value)
        {
            try { JToken.Parse(value); return true; }
            catch { return false; }
        }

        private static bool IsHandle(string value) => value is { Length: > 0 and <= 16 }
            && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

        private static bool SameCanonicalHandle(string actual, string expected) =>
            IsHandle(actual) && IsHandle(expected)
            && string.Equals(actual, expected, StringComparison.Ordinal);

        private static bool IsSha256(string value) => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

        private static bool D1ParametersMatchContract(string json)
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

        private static D1ReadbackResult Failed(string code) => new(false, new[] { code });
    }
}
