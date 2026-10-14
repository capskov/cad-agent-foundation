#!/usr/bin/env python3
"""Narrow MCP stdio client for one explicitly bounded, read-only DWG query."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import ntpath
import os
import queue
import re
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path
from typing import Any


REPORT_SCHEMA = "bimwright-readonly-entry-report-v1"
SNAPSHOT_SCHEMA = "bimwright-binding-snapshot-v1"
CATALOG_ALLOWLIST = frozenset(
    {
        "dwg_list_layers",
        "dwg_get_entity_properties",
        "dwg_query_entities",
        "dwg_inspect_lisp",
        "dwg_bind_active_document",
        "dwg_job_get",
        "dwg_job_get_historical",
        "dwg_job_cancel",
    }
)
REQUIRED_CATALOG = frozenset(
    {"dwg_bind_active_document", "dwg_get_entity_properties", "dwg_job_get"}
)
CALLABLE_TOOLS = frozenset(
    {"dwg_bind_active_document", "dwg_get_entity_properties", "dwg_job_get"}
)
IDENTITY_FIELDS = (
    "owner_generation",
    "document_session_id",
    "database_session_id",
    "database_fingerprint",
)
SNAPSHOT_FIELDS = (
    "schema",
    "observation",
    "reported_at_utc",
    "identity_status",
    "owner_pid",
    "owner_started_at_utc",
    "owner_generation",
    "document_session_id",
    "database_session_id",
    "database_fingerprint",
    "drawing_path",
    "host_year",
    "transport",
    "current_layout",
    "current_space",
    "document_read_only",
    "plugin_build",
    "unknown_fields",
)
UNAVAILABLE_SNAPSHOT_FIELDS = (
    "current_layout",
    "current_space",
    "document_read_only",
    "plugin_build",
)
FORBIDDEN_STATE_FILES = frozenset(
    {
        "request-ledger.sqlite",
        "request-ledger.sqlite-wal",
        "request-ledger.sqlite-shm",
        "bound-plugin-ownership.sqlite",
        "bound-plugin-ownership.sqlite-wal",
        "bound-plugin-ownership.sqlite-shm",
    }
)
HANDLE_PATTERN = re.compile(r"^[0-9A-Fa-f]+$")
SENSITIVE_KEY_PARTS = ("token", "secret", "password", "authorization", "credential")
BEARER_PATTERN = re.compile(r"(?i)(\bbearer\s+)[A-Za-z0-9._~+/-]+=*")
TOKEN_ASSIGNMENT_PATTERN = re.compile(
    r"(?i)(\b(?:access[_ -]?token|refresh[_ -]?token|auth[_ -]?token|token)\b\s*[:=]\s*[\"']?)[^\s,;\"']+"
)
UTC_TIMESTAMP_PATTERN = re.compile(
    r"^(?P<base>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?P<fraction>\d{1,7}))?(?P<offset>Z|[+-]\d{2}:\d{2})$"
)


class SafetyError(ValueError):
    """Input or runtime-root validation rejected the run before MCP startup."""


class NotConnectedError(SafetyError):
    """The isolated runtime root has not been prepared with plugin discovery."""


class ProtocolError(RuntimeError):
    """The MCP response did not match the expected JSON-RPC shape."""


class TransportTimeout(TimeoutError):
    """A bounded stdio RPC wait expired; execution state may be unknown."""


class TransportClosed(ConnectionError):
    """The MCP process closed stdio before answering."""


class RemoteRpcError(RuntimeError):
    """The MCP server returned a JSON-RPC error."""


class ApplicationFailure(RuntimeError):
    """A tool returned an explicit application-level failure envelope."""


def redact_text(value: str) -> str:
    value = BEARER_PATTERN.sub(r"\1<redacted>", value)
    return TOKEN_ASSIGNMENT_PATTERN.sub(r"\1<redacted>", value)


def redact(value: Any, *, key: str = "") -> Any:
    if any(part in key.casefold() for part in SENSITIVE_KEY_PARTS):
        return "<redacted>"
    if isinstance(value, dict):
        return {str(k): redact(v, key=str(k)) for k, v in value.items()}
    if isinstance(value, list):
        return [redact(item) for item in value]
    if isinstance(value, tuple):
        return [redact(item) for item in value]
    if isinstance(value, str):
        return redact_text(value)
    return value


def parse_handles(value: str | list[str] | tuple[str, ...]) -> list[str]:
    if isinstance(value, str):
        pieces = value.split(",")
    elif isinstance(value, (list, tuple)):
        pieces = list(value)
    else:
        raise SafetyError("Handles must be supplied as a comma-separated list.")
    handles = [str(item).strip() for item in pieces]
    if not handles or any(not item for item in handles):
        raise SafetyError("Supply 1 to 64 explicit hexadecimal handles.")
    if len(handles) > 64:
        raise SafetyError("At most 64 handles may be read in one request.")
    if any(not HANDLE_PATTERN.fullmatch(item) for item in handles):
        raise SafetyError("Every handle must contain hexadecimal characters only.")
    folded = [format(int(item, 16), "X") for item in handles]
    if len(set(folded)) != len(folded):
        raise SafetyError("Handles must be unique, ignoring letter case.")
    return folded


def parse_utc_timestamp(value: str, field_name: str) -> dt.datetime:
    if not isinstance(value, str) or not value.strip():
        raise SafetyError(f"{field_name} must be an ISO 8601 UTC timestamp.")
    raw = value.strip()
    match = UTC_TIMESTAMP_PATTERN.fullmatch(raw)
    if not match:
        raise SafetyError(f"{field_name} must be an ISO 8601 UTC timestamp with at most 7 fractional digits.")
    offset = match.group("offset")
    if offset.casefold() == "z":
        offset = "+00:00"
    # Parse whole seconds and the offset separately. datetime supports only
    # microseconds; the tick key below preserves .NET's seventh digit too.
    raw_base = match.group("base") + offset
    fraction = match.group("fraction") or ""
    fractional_base = match.group("base")
    if fraction:
        fractional_base += "." + fraction[:6]
    try:
        parsed = dt.datetime.fromisoformat(fractional_base + offset)
    except ValueError as ex:
        raise SafetyError(f"{field_name} must be an ISO 8601 UTC timestamp.") from ex
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise SafetyError(f"{field_name} must include a UTC offset.")
    return parsed.astimezone(dt.timezone.utc)


def _utc_timestamp_ticks(value: str, field_name: str) -> int:
    raw = value.strip()
    match = UTC_TIMESTAMP_PATTERN.fullmatch(raw)
    if not match:
        raise SafetyError(f"{field_name} must be an ISO 8601 UTC timestamp with at most 7 fractional digits.")
    offset = match.group("offset")
    if offset.casefold() == "z":
        offset = "+00:00"
    try:
        whole_seconds = dt.datetime.fromisoformat(match.group("base") + offset).astimezone(dt.timezone.utc)
    except ValueError as ex:
        raise SafetyError(f"{field_name} must be an ISO 8601 UTC timestamp.") from ex
    epoch = dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc)
    delta = whole_seconds - epoch
    seconds = delta.days * 86400 + delta.seconds
    fraction_ticks = int((match.group("fraction") or "").ljust(7, "0"))
    return seconds * 10_000_000 + fraction_ticks


def timestamp_text(value: dt.datetime) -> str:
    return value.astimezone(dt.timezone.utc).isoformat(timespec="microseconds").replace(
        "+00:00", "Z"
    )


def normalize_absolute_dwg_path(value: str, field_name: str = "drawing path") -> str:
    if not isinstance(value, str) or not value.strip():
        raise SafetyError(f"{field_name} must be an absolute path.")
    raw = value.strip()
    if not ntpath.isabs(raw):
        raise SafetyError(f"{field_name} must be an absolute path.")
    if raw.startswith(("\\\\?\\", "\\\\.\\")):
        raise SafetyError(f"{field_name} cannot use a Windows device namespace.")
    return ntpath.normcase(ntpath.normpath(raw))


def validate_input_drawing(value: str) -> Path:
    normalized = normalize_absolute_dwg_path(value, "--drawing")
    drawing = Path(value).resolve(strict=True)
    if not drawing.is_file() or drawing.suffix.casefold() != ".dwg":
        raise SafetyError("--drawing must name an existing DWG file.")
    if ntpath.normcase(ntpath.normpath(str(drawing))) != normalized:
        # Resolving a path through a link can change the file actually named by
        # the argument. Require the expected and opened path to be identical.
        raise SafetyError("--drawing resolves to a different path than the supplied path.")
    return drawing


def _default_bimwright_root() -> str | None:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        return ntpath.normcase(ntpath.normpath(ntpath.join(local_app_data, "Bimwright")))
    if os.name == "nt":
        return ntpath.normcase(
            ntpath.normpath(ntpath.join(str(Path.home()), "AppData", "Local", "Bimwright"))
        )
    return None


def _paths_overlap(left: str, right: str) -> bool:
    try:
        common = ntpath.commonpath((left, right))
    except ValueError:
        return False
    common = ntpath.normcase(ntpath.normpath(common))
    return common in {
        ntpath.normcase(ntpath.normpath(left)),
        ntpath.normcase(ntpath.normpath(right)),
    }


def _path_is_under(child: str, parent: str) -> bool:
    try:
        common = ntpath.normcase(ntpath.normpath(ntpath.commonpath((child, parent))))
    except ValueError:
        return False
    return common == ntpath.normcase(ntpath.normpath(parent))


def _is_directory_link(path: Path) -> bool:
    is_junction = getattr(path, "is_junction", None)
    return path.is_symlink() or (callable(is_junction) and bool(is_junction()))


def validate_runtime_root(value: str) -> Path:
    """Accept a bootstrap-prepared run root without opening its discovery file."""
    root_text = str(value).strip()
    if not ntpath.isabs(root_text):
        raise SafetyError("--runtime-root must be an absolute path.")
    if root_text.startswith(("\\\\?\\", "\\\\.\\")):
        raise SafetyError("--runtime-root cannot use a Windows device namespace.")
    root = Path(root_text).resolve(strict=True)
    if not root.is_dir():
        raise SafetyError("--runtime-root must be an existing directory prepared for this run.")
    root_norm = ntpath.normcase(ntpath.normpath(str(root)))
    default_root = _default_bimwright_root()
    if default_root and _paths_overlap(root_norm, default_root):
        raise SafetyError(
            "--runtime-root overlaps the shared LocalAppData Bimwright root; use a run-specific isolated root."
        )

    dwg_dir = root / "Dwg"
    if not dwg_dir.is_dir():
        raise NotConnectedError("The isolated runtime root has no Dwg state directory yet.")
    resolved_dwg = dwg_dir.resolve(strict=True)
    expected_dwg = root / "Dwg"
    if ntpath.normcase(ntpath.normpath(str(resolved_dwg))) != ntpath.normcase(
        ntpath.normpath(str(expected_dwg))
    ):
        raise SafetyError("The Dwg state directory must remain inside the isolated runtime root.")

    discovery = dwg_dir / "acad-2026.json"
    if not discovery.is_file():
        raise NotConnectedError(
            "The prepared runtime root must already contain Dwg\\acad-2026.json from CAD bootstrap."
        )
    if not _path_is_under(str(discovery.resolve(strict=True)), str(root)):
        raise SafetyError("The isolated discovery file must be inside the runtime root.")

    found = []
    for current, directories, files in os.walk(dwg_dir, followlinks=False):
        linked_directories = [item for item in directories if _is_directory_link(Path(current) / item)]
        if linked_directories:
            raise SafetyError("The Dwg state tree cannot contain linked directories.")
        for name in files:
            if name.casefold() in FORBIDDEN_STATE_FILES:
                found.append(str(Path(current) / name))
    if found:
        raise SafetyError(
            "Refusing an old or nonempty Server state root containing request-ledger.sqlite or "
            "bound-plugin-ownership.sqlite. Start with a fresh run-specific root."
        )
    return root


def validate_server_path(value: str) -> Path:
    if not ntpath.isabs(value):
        raise SafetyError("--server must be an absolute path to the Server executable.")
    server = Path(value).resolve(strict=True)
    if not server.is_file() or server.suffix.casefold() != ".exe":
        raise SafetyError("--server must name an existing Bimwright.Dwg.Server.exe file.")
    return server


def validate_output_path(value: str, runtime_root: Path) -> Path:
    if not ntpath.isabs(value):
        raise SafetyError("--output must be an absolute path to a new JSON report file.")
    output = Path(value)
    parent = output.parent.resolve(strict=True)
    output = parent / output.name
    if output.suffix.casefold() != ".json":
        raise SafetyError("--output must use a .json extension.")
    if output.exists():
        raise SafetyError("--output already exists; refusing to overwrite it.")
    if _paths_overlap(
        ntpath.normcase(ntpath.normpath(str(output))),
        ntpath.normcase(ntpath.normpath(str(runtime_root))),
    ):
        raise SafetyError("--output must be outside the isolated runtime root.")
    return output


def build_server_command(server: Path) -> list[str]:
    return [
        str(server),
        "--target",
        "2026",
        "--toolsets",
        "query",
        "--read-only",
        "--disable-toolbaker",
        "--disable-d1-write-pilot",
        "--disable-d1-preflight-pilot",
    ]


def build_server_environment(runtime_root: Path, source: dict[str, str] | None = None) -> dict[str, str]:
    base = dict(os.environ if source is None else source)
    for key in list(base):
        upper_key = key.upper()
        if upper_key.startswith("BIMWRIGHT_DWG_") or upper_key.startswith("BIMWRIGHT_D1_"):
            del base[key]
    base["BIMWRIGHT_DWG_ROOT"] = str(runtime_root)
    return base


def _parse_json_text(value: str) -> Any:
    try:
        return json.loads(value)
    except (json.JSONDecodeError, TypeError):
        return None


def _candidate_tool_payloads(raw: dict[str, Any]) -> list[Any]:
    candidates: list[Any] = []
    structured = raw.get("structuredContent")
    if structured is not None:
        candidates.append(structured)
    for block in raw.get("content", []) if isinstance(raw.get("content"), list) else []:
        if isinstance(block, dict) and block.get("type") == "text":
            parsed = _parse_json_text(block.get("text", ""))
            if parsed is not None:
                candidates.append(parsed)
    if not candidates and any(key in raw for key in ("ok", "result", "found", "job", "entities")):
        candidates.append(raw)
    return candidates


def parse_mcp_tool_result(raw: Any) -> dict[str, Any]:
    """Parse either SDK-style structuredContent or text-wrapped nested JSON."""
    if not isinstance(raw, dict):
        raise ProtocolError("MCP tools/call did not return an object.")
    if raw.get("isError") is True:
        raise ApplicationFailure(redact_text(_tool_error_text(raw)))
    candidates = _candidate_tool_payloads(raw)
    decoded: list[dict[str, Any]] = []
    for candidate in candidates:
        current = candidate
        # MCP servers sometimes wrap structured output as a JSON string in a
        # result field. Decode only JSON objects and stop after a small bound.
        for _ in range(4):
            if isinstance(current, str):
                next_value = _parse_json_text(current)
                if next_value is None:
                    break
                current = next_value
                continue
            if isinstance(current, dict) and isinstance(current.get("result"), str):
                next_value = _parse_json_text(current["result"])
                if isinstance(next_value, dict):
                    current = {**current, "result": next_value}
            break
        if isinstance(current, dict):
            decoded.append(current)
    if not decoded:
        raise ProtocolError("MCP tools/call did not contain a JSON object result.")
    # structuredContent and text can both expose the same result. If they
    # disagree, fail closed rather than guessing which representation to use.
    if any(item != decoded[0] for item in decoded[1:]):
        raise ProtocolError("MCP result representations disagree.")
    return redact(decoded[0])


def _tool_error_text(raw: dict[str, Any]) -> str:
    chunks = []
    for block in raw.get("content", []) if isinstance(raw.get("content"), list) else []:
        if isinstance(block, dict) and isinstance(block.get("text"), str):
            chunks.append(block["text"])
    return "; ".join(chunks) or "MCP tool returned isError=true."


def unwrap_application_result(value: dict[str, Any]) -> dict[str, Any]:
    current = value
    for _ in range(4):
        if current.get("ok") is False:
            raise ApplicationFailure(
                redact_text(str(current.get("error") or current.get("message") or "Tool returned ok=false."))
            )
        result = current.get("result")
        if current.get("ok") is True and isinstance(result, dict):
            current = result
            continue
        return current
    raise ProtocolError("Application result nesting exceeded the supported bound.")


def project_binding_snapshot(snapshot: Any) -> dict[str, Any]:
    if not isinstance(snapshot, dict):
        return {}
    return {key: redact(snapshot[key], key=key) for key in SNAPSHOT_FIELDS if key in snapshot}


def check_binding_identity(
    snapshot: Any,
    expected_pid: int,
    expected_started_at_utc: str | dt.datetime,
    expected_drawing: str,
) -> tuple[str, list[str]]:
    """Return (gate status, issues) without using a bind snapshot as live state."""
    if isinstance(expected_started_at_utc, str):
        expected_started = parse_utc_timestamp(expected_started_at_utc, "expected start time")
    else:
        expected_started = expected_started_at_utc.astimezone(dt.timezone.utc)
    expected_path = normalize_absolute_dwg_path(expected_drawing, "expected drawing")
    if not isinstance(snapshot, dict):
        return "blocked_identity_unknown", ["binding_snapshot_missing"]
    if snapshot.get("schema") != SNAPSHOT_SCHEMA or snapshot.get("observation") != "bind_time":
        return "blocked_identity_unknown", ["binding_snapshot_schema_or_observation_invalid"]
    try:
        parse_utc_timestamp(str(snapshot.get("reported_at_utc") or ""), "reported_at_utc")
    except SafetyError:
        return "blocked_identity_unknown", ["reported_at_utc_missing_or_invalid"]
    if snapshot.get("identity_status") != "known":
        return "blocked_identity_unknown", ["identity_status_not_known"]

    owner_pid = snapshot.get("owner_pid")
    started_raw = snapshot.get("owner_started_at_utc")
    path_raw = snapshot.get("drawing_path")
    if isinstance(owner_pid, bool) or not isinstance(owner_pid, int) or owner_pid <= 0:
        return "blocked_identity_unknown", ["owner_pid_missing_or_invalid"]
    if any(not isinstance(snapshot.get(field), str) or not snapshot[field].strip() for field in IDENTITY_FIELDS):
        return "blocked_identity_unknown", ["known_identity_fields_incomplete"]
    if not isinstance(started_raw, str) or not isinstance(path_raw, str):
        return "blocked_identity_unknown", ["owner_start_or_drawing_path_missing"]
    try:
        owner_started = parse_utc_timestamp(started_raw, "owner_started_at_utc")
        bound_path = normalize_absolute_dwg_path(path_raw, "binding drawing path")
    except SafetyError:
        return "blocked_identity_unknown", ["owner_start_or_drawing_path_invalid"]

    unknown_fields = snapshot.get("unknown_fields")
    if not isinstance(unknown_fields, list) or any(not isinstance(item, str) for item in unknown_fields):
        return "blocked_identity_unknown", ["unknown_fields_missing_or_invalid"]
    for field in UNAVAILABLE_SNAPSHOT_FIELDS:
        if snapshot.get(field) is not None or field not in unknown_fields:
            return "blocked_identity_unknown", [f"{field}_unknown_contract_invalid"]

    mismatches = []
    if owner_pid != expected_pid:
        mismatches.append("owner_pid_mismatch")
    try:
        owner_ticks = _utc_timestamp_ticks(started_raw, "owner_started_at_utc")
        expected_ticks = _utc_timestamp_ticks(
            timestamp_text(expected_started), "expected start time"
        ) if isinstance(expected_started_at_utc, dt.datetime) else _utc_timestamp_ticks(
            expected_started_at_utc, "expected start time"
        )
    except SafetyError:
        return "blocked_identity_unknown", ["owner_start_or_expected_time_invalid"]
    if owner_ticks != expected_ticks:
        mismatches.append("owner_started_at_utc_mismatch")
    if bound_path != expected_path:
        mismatches.append("drawing_path_mismatch")
    if mismatches:
        return "blocked_identity_mismatch", mismatches
    return "identity_matched", []


def _rpc_event(report: dict[str, Any], method: str, outcome: str, elapsed_ms: float, error: str | None = None) -> None:
    event = {
        "method": method,
        "outcome": outcome,
        "elapsed_ms": round(elapsed_ms, 1),
    }
    if error:
        event["error"] = redact_text(error)
    report["calls"].append(event)


def _safe_call_arguments(tool_name: str, arguments: dict[str, Any]) -> dict[str, Any]:
    safe = dict(arguments)
    if "context_id" in safe:
        safe["context_id"] = "<redacted>"
    if "request_id" in safe and tool_name != "dwg_get_entity_properties":
        # The generated request ID is recorded once at report level for
        # correlation; duplicating it in every poll row adds no value.
        safe["request_id"] = "<request-id>"
    return redact(safe)


def _call_tool_logged(
    client: Any,
    report: dict[str, Any],
    tool_name: str,
    arguments: dict[str, Any],
    timeout_s: float,
) -> Any:
    if tool_name not in CALLABLE_TOOLS:
        raise SafetyError(f"The local client refuses tool {tool_name!r}.")
    start = time.monotonic()
    try:
        result = client.call_tool(tool_name, arguments, timeout_s)
    except Exception as ex:
        _rpc_event(report, f"tools/call:{tool_name}", "no_response", (time.monotonic() - start) * 1000, str(ex))
        raise
    _rpc_event(report, f"tools/call:{tool_name}", "response_received", (time.monotonic() - start) * 1000)
    report["calls"][-1]["arguments"] = _safe_call_arguments(tool_name, arguments)
    return result


def _extract_job(value: dict[str, Any]) -> tuple[bool | None, dict[str, Any] | None]:
    try:
        body = unwrap_application_result(value)
    except ApplicationFailure:
        raise
    found = body.get("found")
    if found is False:
        return False, None
    if found is not True:
        return None, None
    job = body.get("job")
    if not isinstance(job, dict):
        return None, None
    return True, job


def _validate_found_job_identity(
    job: dict[str, Any], context_id: str, request_id: str
) -> list[str]:
    issues = []
    if job.get("request_id") != request_id:
        issues.append("request_id_mismatch")
    if job.get("context_id") != context_id:
        issues.append("context_id_mismatch")
    if job.get("tool_name") != "get_entity_properties":
        issues.append("tool_name_mismatch")
    if job.get("effects") != "none":
        issues.append("effects_not_none_or_missing")
    if "is_write" in job and job.get("is_write") is not False:
        issues.append("is_write_true_or_unknown")
    return issues


def _parse_property_result(job: dict[str, Any], requested: list[str]) -> tuple[dict[str, Any] | None, dict[str, Any]]:
    raw = job.get("result_json")
    if not isinstance(raw, str) or not raw:
        return None, {
            "requested_handles": requested,
            "returned_handles": [],
            "failures": [],
            "missing_handles": requested,
            "truncated_paths": [],
            "all_ok": False,
            "complete": False,
            "result_error": "completed job did not contain result_json",
        }
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError:
        return None, {
            "requested_handles": requested,
            "returned_handles": [],
            "failures": [],
            "missing_handles": requested,
            "truncated_paths": [],
            "all_ok": False,
            "complete": False,
            "result_error": "result_json was not valid JSON",
        }
    if not isinstance(payload, dict):
        return None, {
            "requested_handles": requested,
            "returned_handles": [],
            "failures": [],
            "missing_handles": requested,
            "truncated_paths": [],
            "all_ok": False,
            "complete": False,
            "result_error": "result_json did not contain an object",
        }

    entities = payload.get("entities")
    if not isinstance(entities, list):
        entities = []
    returned = []
    failures = []
    for index, entity in enumerate(entities):
        if not isinstance(entity, dict):
            failures.append({"index": index, "reason": "entity_result_not_an_object"})
            continue
        handle = entity.get("handle")
        canonical_handle = None
        if isinstance(handle, str) and HANDLE_PATTERN.fullmatch(handle):
            canonical_handle = format(int(handle, 16), "X")
            returned.append(canonical_handle)
        elif handle is not None:
            failures.append({"index": index, "reason": "invalid_returned_handle"})
        if entity.get("ok") is not True:
            failure = {"index": index}
            if canonical_handle is not None:
                failure["handle"] = canonical_handle
            failure["error"] = redact(entity.get("error"))
            failures.append(failure)

    returned_unique = list(dict.fromkeys(returned))
    returned_set = set(returned_unique)
    missing = [handle for handle in requested if handle not in returned_set]
    requested_set = set(requested)
    unexpected = [handle for handle in returned_unique if handle not in requested_set]
    duplicates = sorted({handle for handle in returned if returned.count(handle) > 1})
    truncated_paths = []

    def collect_truncation(value: Any, prefix: str = "$", depth: int = 0) -> None:
        if depth > 20:
            return
        if isinstance(value, dict):
            for key, child in value.items():
                path = f"{prefix}.{key}"
                if key.casefold().endswith("_truncated") and child is True:
                    truncated_paths.append(path)
                elif key.casefold() == "truncated" and child is True:
                    truncated_paths.append(path)
                else:
                    collect_truncation(child, path, depth + 1)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                collect_truncation(child, f"{prefix}[{index}]", depth + 1)

    collect_truncation(payload)
    summary = {
        "requested_handles": requested,
        "returned_handles": returned_unique,
        "failures": failures,
        "missing_handles": missing,
        "unexpected_handles": unexpected,
        "duplicate_handles": duplicates,
        "truncated_paths": truncated_paths,
        "all_ok": not failures and not missing and not unexpected and not duplicates,
        "complete": not missing and not unexpected and not duplicates and not truncated_paths,
    }
    return redact(payload), redact(summary)


def run_readonly_entry(
    client: Any,
    *,
    expected_pid: int,
    expected_started_at_utc: str,
    drawing_path: str,
    handles: list[str] | tuple[str, ...] | str,
    transport_timeout_s: float = 15.0,
    job_timeout_s: float = 30.0,
    poll_interval_ms: int = 250,
) -> dict[str, Any]:
    """Perform initialize, one catalog check, one bind, one submit, then same-ID polls."""
    requested = parse_handles(handles)
    parse_utc_timestamp(expected_started_at_utc, "--expected-started-at-utc")
    expected_drawing = normalize_absolute_dwg_path(drawing_path, "--drawing")
    report: dict[str, Any] = {
        "schema": REPORT_SCHEMA,
        "started_at_utc": timestamp_text(dt.datetime.now(dt.timezone.utc)),
        "completed_at_utc": None,
        "status": "unknown",
        "stage": "initialize",
        "execution_state": "not_submitted",
        "transport": "stdio",
        "snapshot_observation": "bind_time_only",
        "server_pid": None,
        "server_shutdown": None,
        "catalog_names": [],
        "catalog_verified": False,
        "binding_snapshot": None,
        "context_id_present": False,
        "request_id": None,
        "submit_attempts": 0,
        "submission_outcome": None,
        "job": None,
        "properties": None,
        "payload": None,
        "calls": [],
        "error": None,
    }

    def invoke(method: str, function: Any, timeout: float) -> Any:
        started = time.monotonic()
        try:
            result = function(timeout)
        except Exception as ex:
            _rpc_event(report, method, "no_response", (time.monotonic() - started) * 1000, str(ex))
            raise
        _rpc_event(report, method, "response_received", (time.monotonic() - started) * 1000)
        return result

    try:
        invoke("initialize", client.initialize, transport_timeout_s)
        report["stage"] = "tools/list"
        catalog = invoke("tools/list", client.tools_list, transport_timeout_s)
        tools = catalog.get("tools") if isinstance(catalog, dict) else None
        if not isinstance(tools, list):
            report.update(status="unsafe_catalog", error="tools/list did not return a tools array")
            return report
        names = []
        for item in tools:
            if not isinstance(item, dict) or not isinstance(item.get("name"), str):
                report.update(status="unsafe_catalog", error="tools/list contained an invalid descriptor")
                return report
            names.append(item["name"])
        if len(set(names)) != len(names):
            report.update(status="unsafe_catalog", error="tools/list contained duplicate tool names")
            return report
        report["catalog_names"] = sorted(names)
        extras = sorted(set(names) - CATALOG_ALLOWLIST)
        missing = sorted(REQUIRED_CATALOG - set(names))
        if extras or missing:
            report.update(
                status="unsafe_catalog" if extras else "unsupported_capability",
                error={"unapproved_tools": extras, "required_tools_missing": missing},
            )
            return report
        report["catalog_verified"] = True

        report["stage"] = "bind"
        bind_raw = _call_tool_logged(
            client, report, "dwg_bind_active_document", {}, transport_timeout_s
        )
        bind_payload = parse_mcp_tool_result(bind_raw)
        bind_value = unwrap_application_result(bind_payload)
        context_id = bind_value.get("context_id")
        snapshot = bind_value.get("binding_snapshot")
        report["context_id_present"] = isinstance(context_id, str) and bool(context_id)
        report["binding_snapshot"] = project_binding_snapshot(snapshot)
        if not report["context_id_present"]:
            report.update(status="bind_failed", error="bind response did not contain context_id")
            return report
        gate_status, issues = check_binding_identity(
            snapshot, expected_pid, expected_started_at_utc, drawing_path
        )
        if gate_status != "identity_matched":
            report.update(status=gate_status, error={"identity_issues": issues})
            return report

        request_id = "readonly-entry-" + uuid.uuid4().hex
        report["request_id"] = request_id
        read_arguments = {
            "context_id": context_id,
            "request_id": request_id,
            "handles": json.dumps(requested, separators=(",", ":")),
            "include_geometry": True,
        }
        report["stage"] = "get_entity_properties_submit"
        report["execution_state"] = "unknown_after_submit_attempt"
        report["submit_attempts"] = 1
        try:
            submit_raw = _call_tool_logged(
                client,
                report,
                "dwg_get_entity_properties",
                read_arguments,
                transport_timeout_s,
            )
            submit_payload = parse_mcp_tool_result(submit_raw)
            try:
                unwrap_application_result(submit_payload)
                report["submission_outcome"] = "response_received"
            except ApplicationFailure as ex:
                # A refusal or ambiguous plugin response still gets reconciled
                # through job_get for this exact request ID. It is never retried.
                report["submission_outcome"] = "application_error_reconcile_same_id"
                report["submission_error"] = redact_text(str(ex))
        except Exception as ex:
            # The call may have reached the Server or plugin before stdio was
            # lost. Reconcile only with job_get and never resubmit.
            report["submission_outcome"] = "response_unknown_reconcile_same_id"
            report["submission_error"] = redact_text(str(ex))

        report["stage"] = "job_get"
        deadline = time.monotonic() + job_timeout_s
        first_poll = True
        terminal_job = None
        while first_poll or time.monotonic() < deadline:
            first_poll = False
            remaining = max(0.05, deadline - time.monotonic())
            per_call_timeout = min(transport_timeout_s, remaining)
            arguments = {"context_id": context_id, "request_id": request_id, "wait_ms": 0}
            try:
                raw_job = _call_tool_logged(
                    client, report, "dwg_job_get", arguments, per_call_timeout
                )
                parsed_job = parse_mcp_tool_result(raw_job)
                found, job = _extract_job(parsed_job)
                if found is True and job is not None:
                    identity_issues = _validate_found_job_identity(job, context_id, request_id)
                    if identity_issues:
                        report.update(
                            status="job_snapshot_mismatch",
                            execution_state="unknown",
                            error={"job_identity_issues": identity_issues},
                        )
                        return report
                    phase = job.get("phase")
                    report["job"] = redact(
                        {key: job.get(key) for key in ("phase", "effects", "verification", "observation", "error_code", "error_message") if key in job}
                    )
                    if phase in {"completed", "failed", "cancelled"}:
                        terminal_job = job
                        break
                elif found is None:
                    report["last_job_observation"] = "response_missing_found_or_job"
            except Exception as ex:
                report["last_job_observation"] = "query_response_unknown"
                report["last_job_error"] = redact_text(str(ex))

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            time.sleep(min(poll_interval_ms / 1000.0, remaining))

        if terminal_job is None:
            report.update(status="job_unknown", execution_state="unknown")
            report["error"] = "No terminal job snapshot arrived before the bounded wait expired."
            return report

        phase = terminal_job.get("phase")
        report["execution_state"] = phase
        if phase == "failed":
            report.update(status="job_failed", error=terminal_job.get("error_message"))
            return report
        if phase == "cancelled":
            report.update(status="job_cancelled")
            return report

        payload, summary = _parse_property_result(terminal_job, requested)
        report["payload"] = payload
        report["properties"] = summary
        if payload is None:
            report.update(status="completed_result_unavailable", error=summary.get("result_error"))
        elif (
            summary.get("failures")
            or summary.get("missing_handles")
            or summary.get("unexpected_handles")
            or summary.get("duplicate_handles")
        ):
            report["status"] = "completed_with_property_failures"
        elif summary.get("truncated_paths"):
            report["status"] = "completed_with_truncation"
        else:
            report["status"] = "completed"
        return report
    except ApplicationFailure as ex:
        report.update(status="failed", error=redact_text(str(ex)))
        return report
    except (SafetyError, ProtocolError) as ex:
        report.update(status="failed", error=redact_text(str(ex)))
        return report
    except Exception as ex:
        # A transport error before any entity submit is a connection unknown;
        # after submit, execution state remains unknown until job_get resolves.
        report["status"] = "unknown"
        report["error"] = redact_text(str(ex))
        if report["submit_attempts"]:
            report["execution_state"] = "unknown"
        return report
    finally:
        report["completed_at_utc"] = timestamp_text(dt.datetime.now(dt.timezone.utc))


class McpStdioClient:
    """Small line-oriented MCP stdio transport adapted from the R0 smoke path."""

    def __init__(self, process: subprocess.Popen[str]):
        self.process = process
        self._lines: queue.Queue[str | None] = queue.Queue()
        self._pending: dict[Any, dict[str, Any]] = {}
        self._lock = threading.Lock()
        self._closed = False
        self._reader = threading.Thread(target=self._pump_stdout, daemon=True)
        self._reader.start()
        self._initialized = False
        self._tools_listed = False
        self._shutdown_report: dict[str, Any] | None = None

    def _pump_stdout(self) -> None:
        try:
            assert self.process.stdout is not None
            for line in self.process.stdout:
                self._lines.put(line)
        finally:
            self._lines.put(None)

    def _write(self, message: dict[str, Any]) -> None:
        if self._closed or self.process.poll() is not None:
            raise TransportClosed("MCP Server process is not running.")
        if self.process.stdin is None:
            raise TransportClosed("MCP Server stdin is unavailable.")
        raw = json.dumps(message, separators=(",", ":"), ensure_ascii=False)
        try:
            self.process.stdin.write(raw + "\n")
            self.process.stdin.flush()
        except (BrokenPipeError, OSError) as ex:
            raise TransportClosed("MCP Server stdin closed before the request was sent.") from ex

    def rpc(self, method: str, params: dict[str, Any], timeout_s: float) -> dict[str, Any]:
        if not 0 < timeout_s <= 120:
            raise SafetyError("Transport waits must be between 0 and 120 seconds.")
        request_id = uuid.uuid4().hex
        with self._lock:
            self._write(
                {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "method": method,
                    "params": params,
                }
            )
            deadline = time.monotonic() + timeout_s
            if request_id in self._pending:
                response = self._pending.pop(request_id)
            else:
                response = None
            while response is None:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TransportTimeout(f"MCP {method} response timed out.")
                try:
                    line = self._lines.get(timeout=remaining)
                except queue.Empty as ex:
                    raise TransportTimeout(f"MCP {method} response timed out.") from ex
                if line is None:
                    raise TransportClosed(f"MCP Server closed stdout while waiting for {method}.")
                try:
                    item = json.loads(line)
                except json.JSONDecodeError as ex:
                    raise ProtocolError("MCP Server wrote a non-JSON line to stdout.") from ex
                if not isinstance(item, dict):
                    continue
                if item.get("id") == request_id:
                    response = item
                elif "id" in item and len(self._pending) < 128:
                    self._pending[item["id"]] = item
                # Notifications and late responses are consumed without
                # changing the request ID or resubmitting anything.
            if "error" in response:
                raise RemoteRpcError(redact_text(str(response["error"])))
            result = response.get("result")
            if not isinstance(result, dict):
                raise ProtocolError(f"MCP {method} response did not contain a result object.")
            return result

    def notify(self, method: str, params: dict[str, Any]) -> None:
        with self._lock:
            self._write(
                {"jsonrpc": "2.0", "method": method, "params": params}
            )

    def initialize(self, timeout_s: float) -> dict[str, Any]:
        if self._initialized:
            raise SafetyError("MCP initialize may be sent only once.")
        result = self.rpc(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "bimwright-readonly-entry", "version": "1"},
            },
            timeout_s,
        )
        self.notify("notifications/initialized", {})
        self._initialized = True
        return result

    def tools_list(self, timeout_s: float) -> dict[str, Any]:
        if not self._initialized:
            raise SafetyError("MCP initialize must complete before tools/list.")
        if self._tools_listed:
            raise SafetyError("This client sends tools/list only once.")
        result = self.rpc("tools/list", {}, timeout_s)
        self._tools_listed = True
        return result

    def call_tool(self, name: str, arguments: dict[str, Any], timeout_s: float) -> dict[str, Any]:
        if name not in CALLABLE_TOOLS:
            raise SafetyError(f"The local client refuses tool {name!r}.")
        if not self._tools_listed:
            raise SafetyError("MCP tools/list must complete before any tool call.")
        return self.rpc(
            "tools/call",
            {"name": name, "arguments": arguments},
            timeout_s,
        )

    def close(self) -> dict[str, Any]:
        if self._closed:
            return dict(self._shutdown_report or {})
        self._closed = True
        summary: dict[str, Any] = {
            "pid": self.process.pid,
            "exit_code": None,
            "stopped": False,
            "forced_terminate": False,
            "forced_kill": False,
            "termination_action": "none",
        }
        errors = []
        try:
            if self.process.stdin is not None:
                self.process.stdin.close()
        except OSError as ex:
            errors.append(f"stdin_close: {ex}")
        try:
            summary["exit_code"] = self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            summary["termination_action"] = "terminate"
            try:
                self.process.terminate()
                summary["forced_terminate"] = True
                summary["exit_code"] = self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                summary["termination_action"] = "kill"
                try:
                    self.process.kill()
                    summary["forced_kill"] = True
                    summary["exit_code"] = self.process.wait(timeout=2)
                except Exception as ex:
                    errors.append(f"kill_wait: {ex}")
            except Exception as ex:
                # The process may have exited between timeout and terminate;
                # poll below records whether this exact child has stopped.
                errors.append(f"terminate_wait: {ex}")
        except Exception as ex:
            errors.append(f"graceful_wait: {ex}")
        try:
            self._reader.join(timeout=1)
        except Exception as ex:
            errors.append(f"stdout_reader_join: {ex}")
        observed_exit_code = self.process.poll()
        if observed_exit_code is not None:
            summary["exit_code"] = observed_exit_code
        summary["stopped"] = observed_exit_code is not None
        if errors:
            summary["error"] = redact_text("; ".join(errors))
        self._shutdown_report = summary
        return dict(summary)


def launch_client(server: Path, runtime_root: Path) -> McpStdioClient:
    process = subprocess.Popen(
        build_server_command(server),
        env=build_server_environment(runtime_root),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    return McpStdioClient(process)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Bind one AutoCAD document and read explicit entity handles through Bimwright MCP."
    )
    parser.add_argument("--runtime-root", required=True, help="Prepared, run-specific BIMWRIGHT_DWG_ROOT.")
    parser.add_argument("--server", required=True, help="Absolute path to Bimwright.Dwg.Server.exe.")
    parser.add_argument("--expected-pid", required=True, type=int, help="Expected AutoCAD process ID.")
    parser.add_argument(
        "--expected-started-at-utc",
        required=True,
        help="Expected AutoCAD process start timestamp, including UTC offset (PID reuse guard).",
    )
    parser.add_argument("--drawing", required=True, help="Absolute path to the isolated DWG copy.")
    parser.add_argument("--handles", required=True, help="Comma-separated explicit hexadecimal handles (1-64).")
    parser.add_argument("--output", required=True, help="Absolute path to a new .json report file.")
    parser.add_argument(
        "--transport-timeout-sec",
        type=float,
        default=15.0,
        help="Per MCP request timeout, 1-60 seconds (default: 15).",
    )
    parser.add_argument(
        "--job-timeout-sec",
        type=float,
        default=30.0,
        help="Maximum time to poll the same request ID, 1-300 seconds (default: 30).",
    )
    parser.add_argument(
        "--poll-interval-ms",
        type=int,
        default=250,
        help="Delay between job_get snapshots, 100-5000 ms (default: 250).",
    )
    return parser


def _validate_waits(parser: argparse.ArgumentParser, args: argparse.Namespace) -> None:
    if not 1 <= args.transport_timeout_sec <= 60:
        parser.error("--transport-timeout-sec must be between 1 and 60.")
    if not 1 <= args.job_timeout_sec <= 300:
        parser.error("--job-timeout-sec must be between 1 and 300.")
    if not 100 <= args.poll_interval_ms <= 5000:
        parser.error("--poll-interval-ms must be between 100 and 5000.")
    if args.expected_pid <= 0:
        parser.error("--expected-pid must be a positive process ID.")


def _reserve_output(path: Path) -> None:
    with path.open("x", encoding="utf-8"):
        pass


def _write_reserved_report(path: Path, report: dict[str, Any]) -> None:
    with path.open("r+", encoding="utf-8") as stream:
        stream.seek(0)
        stream.truncate()
        json.dump(redact(report), stream, ensure_ascii=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def main(argv: list[str] | None = None) -> int:
    # Windows can otherwise emit CP936 when stdout is piped, while callers
    # consume UTF-8 JSON reports containing Chinese workspace paths.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = _build_parser()
    args = parser.parse_args(argv)
    _validate_waits(parser, args)
    try:
        handles = parse_handles(args.handles)
        parse_utc_timestamp(args.expected_started_at_utc, "--expected-started-at-utc")
        if not ntpath.isabs(args.runtime_root):
            raise SafetyError("--runtime-root must be an absolute path.")
        root_candidate = Path(args.runtime_root).resolve(strict=True)
        if not root_candidate.is_dir():
            raise SafetyError("--runtime-root must be an existing directory.")
        server = validate_server_path(args.server)
        drawing = validate_input_drawing(args.drawing)
        output = validate_output_path(args.output, root_candidate)
    except (OSError, SafetyError) as ex:
        parser.error(str(ex))

    try:
        runtime_root = validate_runtime_root(args.runtime_root)
    except NotConnectedError as ex:
        try:
            _reserve_output(output)
        except OSError as reserve_error:
            parser.error(f"Could not reserve --output without overwrite: {reserve_error}")
        now = timestamp_text(dt.datetime.now(dt.timezone.utc))
        preflight_report = {
            "schema": REPORT_SCHEMA,
            "started_at_utc": now,
            "completed_at_utc": now,
            "status": "not_connected",
            "stage": "discovery_preflight",
            "execution_state": "not_submitted",
            "transport": "stdio",
            "snapshot_observation": "bind_time_only",
            "server_pid": None,
            "server_shutdown": None,
            "catalog_names": [],
            "catalog_verified": False,
            "binding_snapshot": None,
            "context_id_present": False,
            "request_id": None,
            "submit_attempts": 0,
            "submission_outcome": None,
            "job": None,
            "properties": None,
            "payload": None,
            "calls": [],
            "error": str(ex),
        }
        _write_reserved_report(output, preflight_report)
        print(json.dumps({"status": "not_connected", "output": str(output)}, ensure_ascii=False))
        return 3
    except (OSError, SafetyError) as ex:
        parser.error(str(ex))

    report: dict[str, Any]
    client: McpStdioClient | None = None
    try:
        # Reserve the path with exclusive create before launching the Server.
        # The final report replaces only this file's contents, never an older
        # user's report.
        _reserve_output(output)
    except OSError as ex:
        parser.error(f"Could not reserve --output without overwrite: {ex}")

    try:
        client = launch_client(server, runtime_root)
        server_pid = client.process.pid
        report = run_readonly_entry(
            client,
            expected_pid=args.expected_pid,
            expected_started_at_utc=args.expected_started_at_utc,
            drawing_path=str(drawing),
            handles=handles,
            transport_timeout_s=args.transport_timeout_sec,
            job_timeout_s=args.job_timeout_sec,
            poll_interval_ms=args.poll_interval_ms,
        )
        report["server_pid"] = server_pid
    except Exception as ex:
        server_pid = client.process.pid if client is not None else None
        report = {
            "schema": REPORT_SCHEMA,
            "started_at_utc": timestamp_text(dt.datetime.now(dt.timezone.utc)),
            "completed_at_utc": timestamp_text(dt.datetime.now(dt.timezone.utc)),
            "status": "failed" if client is None else "unknown",
            "stage": "server_start" if client is None else "client_run",
            "execution_state": "not_submitted",
            "transport": "stdio",
            "snapshot_observation": "bind_time_only",
            "server_pid": server_pid,
            "server_shutdown": None,
            "catalog_names": [],
            "catalog_verified": False,
            "binding_snapshot": None,
            "context_id_present": False,
            "request_id": None,
            "submit_attempts": 0,
            "submission_outcome": None,
            "job": None,
            "properties": None,
            "payload": None,
            "calls": [],
            "error": redact_text(str(ex)),
        }
    finally:
        if client is not None:
            try:
                shutdown = client.close()
            except Exception as ex:
                shutdown = {
                    "pid": client.process.pid,
                    "exit_code": client.process.poll(),
                    "stopped": client.process.poll() is not None,
                    "forced_terminate": False,
                    "forced_kill": False,
                    "termination_action": "unknown",
                    "error": redact_text(str(ex)),
                }
            report["server_pid"] = client.process.pid
            report["server_shutdown"] = shutdown

    # Replace the exclusively reserved empty file; no pre-existing report is
    # ever truncated.
    _write_reserved_report(output, report)
    print(json.dumps({"status": report.get("status"), "output": str(output)}, ensure_ascii=False))
    return 0 if report.get("status") == "completed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
