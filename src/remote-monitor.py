#!/usr/bin/env python3
import json
import os
import re
import sys
from pathlib import Path

PARSER_VERSION = 3


def emit(payload):
    print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))


def find_session(codex_home, thread_id):
    matches = list((codex_home / "sessions").glob(f"**/*{thread_id}*.jsonl"))
    return max(matches, key=lambda item: item.stat().st_mtime, default=None)


def load_cache(cache_path, session_path, size):
    try:
        state = json.loads(cache_path.read_text(encoding="utf-8"))
    except Exception:
        state = {}
    if state.get("version") != PARSER_VERSION or state.get("session") != str(session_path) or int(state.get("offset", 0)) > size:
        state = {"version": PARSER_VERSION, "session": str(session_path), "offset": 0, "cwd": None, "roots": [], "file": None}
    return state


def parse_input(value):
    if isinstance(value, str):
        try:
            return json.loads(value)
        except Exception:
            return value
    return value


def collect_strings(value, output, depth=0):
    if depth > 6 or value is None:
        return
    if isinstance(value, str):
        output.append(value)
    elif isinstance(value, list):
        for item in value:
            collect_strings(item, output, depth + 1)
    elif isinstance(value, dict):
        for item in value.values():
            collect_strings(item, output, depth + 1)


def edited_file(record, cwd):
    payload = record.get("payload") or {}
    if record.get("type") != "response_item" or payload.get("type") not in ("function_call", "custom_tool_call"):
        return None
    value = parse_input(payload.get("arguments", payload.get("input")))
    strings = []
    collect_strings(value, strings)
    serialized = value if isinstance(value, str) else json.dumps(value or {}, ensure_ascii=False)
    name = str(payload.get("name") or "").lower()
    if not re.search(r"apply_patch|edit|write", name) and "*** Update File:" not in serialized and "*** Add File:" not in serialized:
        return None
    candidates = []
    for text in strings:
        # Tool inputs occur both as decoded multiline strings and as JSON-escaped
        # strings. Normalising both forms keeps remote transcript parsing stable.
        normalized = text.replace("\\r\\n", "\n").replace("\\n", "\n")
        candidates.extend(
            match.group(1).strip().rstrip("\\")
            for match in re.finditer(
                r"\*\*\*\s+(?:Add|Update|Delete) File:\s*([^\r\n]+)",
                normalized,
                re.I,
            )
        )
    if not candidates and isinstance(value, dict):
        for key in ("file", "file_path", "filepath", "filename", "path", "target", "destination"):
            item = value.get(key)
            if isinstance(item, str):
                candidates.append(item)
    if not candidates:
        return None
    result = candidates[-1].strip().strip("\"'").replace("\\", "/")
    if cwd and result.startswith(cwd.rstrip("/") + "/"):
        result = result[len(cwd.rstrip("/")) + 1:]
    return result[-120:]


def process_record(state, record):
    payload = record.get("payload") or {}
    if record.get("type") == "session_meta" and isinstance(payload.get("cwd"), str):
        state["cwd"] = payload["cwd"]
    elif record.get("type") == "turn_context":
        if isinstance(payload.get("cwd"), str):
            state["cwd"] = payload["cwd"]
        if isinstance(payload.get("workspace_roots"), list):
            state["roots"] = payload["workspace_roots"]
    file_path = edited_file(record, state.get("cwd"))
    if file_path:
        state["file"] = file_path


def main():
    if len(sys.argv) != 2 or not re.fullmatch(r"[0-9a-fA-F-]{20,64}", sys.argv[1]):
        emit({"ok": False, "error": "invalid-thread-id"})
        return 2
    thread_id = sys.argv[1]
    codex_home = Path(os.environ.get("CODEX_HOME", Path.home() / ".codex"))
    session_path = find_session(codex_home, thread_id)
    if session_path is None:
        emit({"ok": False, "threadId": thread_id, "error": "session-not-found"})
        return 0
    size = session_path.stat().st_size
    cache_dir = Path.home() / ".local" / "state" / "codex-discord-presence"
    cache_dir.mkdir(parents=True, exist_ok=True)
    cache_path = cache_dir / f"{thread_id}.json"
    state = load_cache(cache_path, session_path, size)
    with session_path.open("rb") as handle:
        handle.seek(int(state.get("offset", 0)))
        for raw_line in handle:
            try:
                process_record(state, json.loads(raw_line.decode("utf-8")))
            except Exception:
                continue
        state["offset"] = handle.tell()
    cache_path.write_text(json.dumps(state, ensure_ascii=False), encoding="utf-8")
    cwd = state.get("cwd")
    roots = state.get("roots") or []
    project_root = next((item for item in roots if isinstance(item, str) and item not in ("/", "/root")), cwd)
    project = Path(project_root).name if project_root else None
    emit({"ok": True, "threadId": thread_id, "project": project, "cwd": cwd, "file": state.get("file")})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
