#!/usr/bin/env python3
import json
import os
import re
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path

PARSER_VERSION = 4
CLAUDE_PARSER_VERSION = 1
CLAUDE_SESSION = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
CLAUDE_EDIT_TOOLS = ("Edit", "MultiEdit", "Write", "NotebookEdit")
CLAUDE_INTERNAL = re.compile(r"(?:^|/)\.claude/(?!worktrees/)", re.I)
CLAUDE_WORKTREE = re.compile(r"(?:^|/)([^/]+)/\.claude/worktrees/[^/]+(?:/|$)", re.I)
CLAUDE_INITIAL_TAIL = 2 * 1024 * 1024
CLAUDE_READ_BUDGET = 8 * 1024 * 1024
CLAUDE_CACHE_MAX_AGE = 3 * 24 * 3600


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
    if is_internal_context(candidate):
        return None
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


def is_internal_context(value):
    return bool(re.search(r"(?:^|/)\.codex/(?:visualizations|attachments|sessions|tmp)(?:/|$)", str(value).replace("\\", "/"), re.I))


def write_private_cache(cache_dir, cache_path, state):
    cache_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(cache_dir, 0o700)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{cache_path.stem}-", suffix=".tmp", dir=cache_dir)
    try:
        try:
            # fchmod is not available on every supported platform. POSIX hosts
            # keep descriptor hardening; the fallback stays atomic on Windows.
            if hasattr(os, "fchmod"):
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


def cache_directory():
    return Path.home() / ".local" / "state" / "codex-discord-presence"


# ── Claude Code ──────────────────────────────────────────────────────────────

def parse_timestamp(value):
    if not isinstance(value, str) or not value:
        return None
    text = value.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    try:
        moment = datetime.fromisoformat(text)
    except (AttributeError, ValueError):
        try:
            moment = datetime.strptime(value[:23], "%Y-%m-%dT%H:%M:%S.%f").replace(tzinfo=timezone.utc)
        except ValueError:
            return None
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return int(moment.timestamp() * 1000)


def process_alive(pid):
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return False
    if pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def claude_project_directory(cwd):
    return re.sub(r"[^A-Za-z0-9]", "-", cwd or "")


def claude_registry(home):
    output = {}
    try:
        names = [item for item in os.listdir(str(home / "sessions")) if re.fullmatch(r"\d+\.json", item)]
    except OSError:
        return output
    for name in names[:256]:
        try:
            entry = json.loads((home / "sessions" / name).read_text(encoding="utf-8"))
        except Exception:
            continue
        if not isinstance(entry, dict) or not CLAUDE_SESSION.match(str(entry.get("sessionId") or "")):
            continue
        if not process_alive(entry.get("pid")):
            continue
        output[entry["sessionId"]] = {
            "cwd": entry.get("cwd") if isinstance(entry.get("cwd"), str) else None,
            "busy": entry.get("status") == "busy",
            "entrypoint": entry.get("entrypoint") if isinstance(entry.get("entrypoint"), str) else None,
            "title": entry.get("name") if isinstance(entry.get("name"), str) else None,
        }
    return output


def claude_is_human_prompt(record):
    if record.get("type") != "user" or record.get("isMeta") or record.get("isSidechain"):
        return False
    origin = record.get("origin")
    if isinstance(origin, dict) and origin.get("kind") not in (None, "human"):
        return False
    content = (record.get("message") or {}).get("content")
    if isinstance(content, str):
        return bool(content.strip())
    if not isinstance(content, list) or not content:
        return False
    return not any(isinstance(block, dict) and block.get("type") == "tool_result" for block in content)


def claude_edited_file(record):
    if record.get("type") != "assistant":
        return None
    content = (record.get("message") or {}).get("content")
    if not isinstance(content, list):
        return None
    found = None
    for block in content:
        if not isinstance(block, dict) or block.get("type") != "tool_use" or block.get("name") not in CLAUDE_EDIT_TOOLS:
            continue
        data = block.get("input") or {}
        value = data.get("file_path") or data.get("notebook_path")
        if isinstance(value, str) and value.strip() and not CLAUDE_INTERNAL.search(value.replace("\\", "/")):
            found = value.strip()
    return found


def claude_touch(state, at, idle_ms):
    last = state.get("lastAt")
    if last is None or at - last > idle_ms:
        if last is None or at > last:
            state["firstAt"] = at
    if last is None or at > last:
        state["lastAt"] = at


def claude_process_record(state, record, idle_ms):
    kind = record.get("type")
    if kind == "custom-title" and isinstance(record.get("customTitle"), str):
        state["title"] = record["customTitle"]
    elif kind == "summary" and isinstance(record.get("summary"), str) and not state.get("title"):
        state["title"] = record["summary"]
    if isinstance(record.get("cwd"), str) and record["cwd"] and not record.get("isSidechain"):
        state["cwd"] = record["cwd"]
    if isinstance(record.get("entrypoint"), str):
        state["entrypoint"] = record["entrypoint"]
    at = parse_timestamp(record.get("timestamp"))
    if at is None:
        return
    claude_touch(state, at, idle_ms)
    if claude_is_human_prompt(record) and (state.get("promptAt") is None or at > state["promptAt"]):
        state["promptAt"] = at
    edited = claude_edited_file(record)
    if edited:
        state["file"] = edited


def claude_load_state(cache_path, transcript, size):
    try:
        state = json.loads(cache_path.read_text(encoding="utf-8"))
    except Exception:
        state = {}
    if state.get("version") != CLAUDE_PARSER_VERSION or state.get("path") != str(transcript) or int(state.get("offset", 0)) > size:
        offset = max(0, size - CLAUDE_INITIAL_TAIL)
        state = {"version": CLAUDE_PARSER_VERSION, "path": str(transcript), "offset": offset, "skipPartial": offset > 0}
    return state


def claude_read_session(transcript, cache_dir, session_id, idle_ms):
    size = transcript.stat().st_size
    cache_path = cache_dir / "claude-{0}.json".format(session_id)
    state = claude_load_state(cache_path, transcript, size)
    start = int(state.get("offset", 0))
    if start < size:
        with transcript.open("rb") as handle:
            handle.seek(start)
            data = handle.read(CLAUDE_READ_BUDGET)
        # Only whole lines are consumed; a trailing fragment is re-read next time.
        end = data.rfind(b"\n")
        if end >= 0:
            lines = data[:end].split(b"\n")
            if state.pop("skipPartial", False) and lines:
                lines = lines[1:]
            for raw_line in lines:
                try:
                    record = json.loads(raw_line.decode("utf-8"))
                except Exception:
                    continue
                if isinstance(record, dict):
                    claude_process_record(state, record, idle_ms)
            state["offset"] = start + end + 1
        write_private_cache(cache_dir, cache_path, state)
    return state


def claude_worktree_project(value):
    match = CLAUDE_WORKTREE.search((value or "").replace("\\", "/"))
    return match.group(1) if match else None


def claude_repository_root(cwd, file_path):
    for value in (file_path, cwd):
        if not value:
            continue
        candidate = Path(value)
        if not candidate.is_absolute() and cwd:
            candidate = Path(cwd) / candidate
        if not candidate.is_dir():
            candidate = candidate.parent
        for directory in (candidate, *candidate.parents):
            try:
                if (directory / ".git").exists():
                    return directory
            except OSError:
                break
    return None


def claude_summary(state):
    cwd = state.get("cwd")
    file_path = state.get("file")
    root = claude_repository_root(cwd, file_path)
    project = claude_worktree_project(file_path) or claude_worktree_project(cwd)
    if not project and root is not None and not is_account_root(str(root)):
        project = root.name
    if not project and cwd and not is_account_root(cwd):
        project = Path(cwd).name
    shown = None
    if file_path:
        path = Path(file_path)
        for base in (root, Path(cwd) if cwd else None):
            if base is None:
                continue
            try:
                shown = str(path.relative_to(base)).replace("\\", "/")
                break
            except ValueError:
                continue
        if shown is None:
            shown = "/".join(path.parts[-2:])
    return project, shown


def claude_prune_cache(cache_dir, now):
    try:
        for item in cache_dir.glob("claude-*.json"):
            try:
                if now - item.stat().st_mtime > CLAUDE_CACHE_MAX_AGE:
                    item.unlink()
            except OSError:
                continue
    except OSError:
        pass


def claude_main(idle_seconds):
    home = Path(os.environ.get("CLAUDE_CONFIG_DIR") or (Path.home() / ".claude"))
    now_ms = int(time.time() * 1000)
    idle_ms = idle_seconds * 1000
    registry = claude_registry(home)
    projects = home / "projects"
    paths = {}
    for session_id, entry in registry.items():
        if entry.get("cwd"):
            paths[session_id] = projects / claude_project_directory(entry["cwd"]) / "{0}.jsonl".format(session_id)
    try:
        for transcript in projects.glob("*/*.jsonl"):
            session_id = transcript.stem
            if session_id in paths or not CLAUDE_SESSION.match(session_id):
                continue
            try:
                if now_ms - transcript.stat().st_mtime * 1000 <= idle_ms:
                    paths[session_id] = transcript
            except OSError:
                continue
    except OSError:
        pass

    cache_dir = cache_directory()
    candidates = []
    for session_id, transcript in paths.items():
        entry = registry.get(session_id) or {}
        try:
            state = claude_read_session(transcript, cache_dir, session_id, idle_ms)
        except OSError:
            continue
        busy = entry.get("busy") is True
        if busy:
            claude_touch(state, now_ms, idle_ms)
        last = state.get("lastAt")
        if not busy and (last is None or now_ms - last > idle_ms):
            continue
        state.setdefault("cwd", entry.get("cwd"))
        candidates.append((session_id, state, entry, busy))

    claude_prune_cache(cache_dir, time.time())
    if not candidates:
        emit({"ok": True, "agent": "claude", "now": now_ms, "active": False, "sessions": 0})
        return 0

    candidates.sort(key=lambda item: (item[1].get("promptAt") or 0, item[1].get("lastAt") or 0), reverse=True)
    session_id, state, entry, busy = candidates[0]
    project, shown = claude_summary(state)
    starts = [item[1].get("firstAt") for item in candidates if item[1].get("firstAt")]
    title = state.get("title") or entry.get("title")
    emit({
        "ok": True,
        "agent": "claude",
        "now": now_ms,
        "active": True,
        "sessions": len(candidates),
        "sessionId": session_id,
        "cwd": state.get("cwd"),
        "project": project,
        "file": shown[-120:] if shown else None,
        "title": title[:200] if isinstance(title, str) else None,
        "busy": busy,
        "entrypoint": state.get("entrypoint") or entry.get("entrypoint"),
        "startedAt": min(starts) if starts else now_ms,
        "focusAt": state.get("promptAt") or state.get("lastAt"),
        "activityAt": state.get("lastAt"),
    })
    return 0


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--claude":
        try:
            idle_seconds = max(60, min(4 * 3600, int(sys.argv[2])))
        except ValueError:
            emit({"ok": False, "error": "invalid-idle-window"})
            return 2
        return claude_main(idle_seconds)
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
    cache_dir = cache_directory()
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
        project_root = next((item for item in roots if isinstance(item, str) and not is_account_root(item) and not is_internal_context(item)), None)
        if not project_root and not is_account_root(cwd) and not is_internal_context(cwd):
            project_root = cwd
        project = Path(project_root).name if project_root else None
    emit({"ok": True, "threadId": thread_id, "project": project, "cwd": cwd, "file": state.get("file")})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
