#!/usr/bin/env python3
import json
import os
import re
import sys
import tempfile
from pathlib import Path

PARSER_VERSION = 4


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


def repository_project(cwd, file_path):
    if not cwd or not file_path:
        return None
    candidate = Path(file_path)
    if not candidate.is_absolute():
        candidate = Path(cwd) / candidate
    if not candidate.is_dir():
        candidate = candidate.parent
    for directory in (candidate, *candidate.parents):
        if (directory / ".git").exists():
            return directory.name
    return None


def is_account_root(value):
    if not value:
        return True
    try:
        candidate = Path(value).expanduser().resolve()
        return candidate == Path("/") or candidate == Path.home().resolve()
    except (OSError, RuntimeError):
        return value in ("/", str(Path.home()))


def write_private_cache(cache_dir, cache_path, state):
    cache_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(cache_dir, 0o700)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{cache_path.stem}-", suffix=".tmp", dir=cache_dir)
    try:
        try:
            os.fchmod(descriptor, 0o600)
        except Exception:
            os.close(descriptor)
            raise
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(state, handle, ensure_ascii=False)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary_name, cache_path)
        os.chmod(cache_path, 0o600)
    finally:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass


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
    write_private_cache(cache_dir, cache_path, state)
    cwd = state.get("cwd")
    roots = state.get("roots") or []
    project = repository_project(cwd, state.get("file"))
    if not project:
        project_root = next((item for item in roots if isinstance(item, str) and not is_account_root(item)), None)
        if not project_root and not is_account_root(cwd):
            project_root = cwd
        project = Path(project_root).name if project_root else None
    emit({"ok": True, "threadId": thread_id, "project": project, "cwd": cwd, "file": state.get("file")})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
