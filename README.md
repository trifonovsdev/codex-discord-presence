<div align="center">

![Codex Presence hero](assets/hero.svg)

# Codex Presence

**The Discord status that follows the task you are actually viewing in Codex Desktop.**

[![Latest release](https://img.shields.io/github/v/release/trifonovsdev/codex-discord-presence?style=flat-square&color=766fff)](https://github.com/trifonovsdev/codex-discord-presence/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/trifonovsdev/codex-discord-presence/.github/workflows/ci.yml?branch=main&style=flat-square)](https://github.com/trifonovsdev/codex-discord-presence/actions/workflows/ci.yml)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows)](https://github.com/trifonovsdev/codex-discord-presence/releases/latest)
[![License](https://img.shields.io/badge/license-MIT-4cd98e?style=flat-square)](LICENSE)

[Download Setup](https://github.com/trifonovsdev/codex-discord-presence/releases/latest/download/CodexPresenceSetup.exe) · [Portable build](https://github.com/trifonovsdev/codex-discord-presence/releases/latest) · [Report a bug](https://github.com/trifonovsdev/codex-discord-presence/issues)

</div>

Codex Presence is a local-first Windows tray application for Discord Rich Presence. It identifies the task selected in ChatGPT/Codex Desktop, shows its project and last edited file, and keeps one stable timer for the entire desktop session. Background tasks cannot silently replace the card.

## Why this one

- **Selected-task accurate** — follows the task visible in Codex Desktop, not simply the latest JSONL file.
- **One-click Windows setup** — the release bundles its own .NET UI and Node runtime; users install no dependencies.
- **Stable timer** — changing a file, project, or task never resets elapsed time.
- **Remote-aware** — maps multiple SSH servers to workspace roots and reads only the selected remote transcript.
- **Private by default** — no telemetry, cloud service, bot token, Discord token, prompt upload, or transcript upload.
- **Product UI** — dashboard, tray controls, settings, privacy presets, diagnostics, and verified updates.

## Install

1. Download [`CodexPresenceSetup.exe`](https://github.com/trifonovsdev/codex-discord-presence/releases/latest/download/CodexPresenceSetup.exe).
2. Run the installer and leave **Start with Windows** enabled.
3. Restart ChatGPT/Codex once so it reloads its hooks.
4. Keep Discord Desktop open and enable **Activity Privacy → Share your detected activities**.

The application ships with the shared Discord Application ID `1526968377048956938`. Friends do not need the Discord Developer Portal or their own application.

> Unsigned community builds can trigger Windows SmartScreen. The release pipeline supports Authenticode signing as soon as a code-signing certificate is configured by the maintainer.

## Dashboard and tray

<div align="center">
  <img src="assets/demo.gif" alt="Codex Presence dashboard, SSH settings, and doctor" width="720">
</div>

Double-click the tray icon to see live state:

```text
● Discord connected

ACTIVE PROJECT
vetements-app
apps/client/src/Customization.tsx
Session 03:42:18 · production

[ Pause presence ] [ Settings ] [ Run doctor ]
```

The tray menu provides pause/resume, settings, diagnostics, update checks, service restart, and clean shutdown. Closing the dashboard keeps the tray service running.

## Privacy presets

| Preset | Project | File | Timer | Recommended for |
|---|:---:|:---:|:---:|---|
| `minimal` | ✓ | hidden | ✓ | Streaming and maximum privacy |
| `standard` | ✓ | relative path | ✓ | Everyday use |
| `detailed` | ✓ | relative path | ✓ | A more descriptive English card |

Every field can be overridden. File display supports filename-only or a project-relative path.

## Multiple SSH workspaces

Open **Settings → Remote workspaces** and add one row per server:

| Name | Host | Workspace roots |
|---|---|---|
| Production | `dev@example.com` | `/srv/store; /srv/api` |
| Homelab | `root@10.0.0.5` | `/root/projects` |

Press **Test SSH**, then **Install helper**. Key-based authentication and Python 3 are required remotely. When multiple entries exist, the longest matching workspace root selects the server.

The helper:

- lives at `~/.local/share/CodexDiscordPresence/remote-monitor.py`;
- reads only the transcript belonging to the selected task;
- stores an incremental byte offset under `~/.local/state`;
- returns project, cwd, and the latest edited file over the existing SSH connection.

## Doctor

**Run doctor** checks:

- configuration validity;
- bundled runtime and daemon files;
- localhost daemon health and version;
- Discord IPC connectivity;
- ChatGPT/Codex process detection;
- Codex hook registration;
- optional Windows startup;
- every configured SSH host and its Python runtime.

The report is copyable, but review project paths and hostnames before posting it publicly.

## Updates and integrity

The tray client checks GitHub Releases using the public GitHub API. It downloads `CodexPresenceSetup.exe`, verifies it against `SHA256SUMS.txt`, and only then starts the silent upgrade. Automatic checks can be disabled in Settings.

Every release contains:

- `CodexPresenceSetup.exe` — one-click Windows installer;
- `CodexPresence-<version>-portable.zip` — no-install bundle;
- `SHA256SUMS.txt` — release integrity manifest.

## Architecture

```text
Codex Desktop route logs ─┐
Codex lifecycle hooks ────┼──> local daemon ──> Discord IPC
Local session JSONL ──────┘         ▲
                                    │ localhost only
Windows tray UI ─ settings/doctor/control/update
                                    │
Selected remote task ───────── system OpenSSH ──> incremental Python helper
```

The local server binds only to `127.0.0.1`. The Discord Application ID is public by design and is not a credential.

## Configuration

Installed configuration:

```text
%LOCALAPPDATA%\Programs\CodexPresence\app\config.json
```

Advanced users can edit it manually and restart the service from the tray menu. The UI manages the same file atomically.

<details>
<summary><strong>Кратко на русском</strong></summary>

Скачай `CodexPresenceSetup.exe`, установи и один раз перезапусти ChatGPT/Codex. Программа появится в трее и сама запустит Discord Presence.

- `minimal` скрывает файл;
- `standard` показывает проект и относительный путь;
- общий таймер не сбрасывается при переключении задач;
- для SSH добавь сервер и корни проектов в Settings, затем нажми `Test SSH` и `Install helper`;
- `Run doctor` проверяет установку и объясняет, что именно не работает.

</details>

## Development

Requirements for contributors: Windows, .NET 8 SDK, Node.js 18+, Python 3, and Inno Setup 6.

```powershell
git clone https://github.com/trifonovsdev/codex-discord-presence.git
cd codex-discord-presence
npm run check
dotnet build .\tray\CodexPresence.Tray.csproj -c Release
.\build-release.ps1 -Version 2.0.1
```

The build script downloads the pinned official Node distribution, publishes a self-contained tray executable, compiles the Inno installer, and emits SHA-256 checksums.

### Release signing

The release workflow signs the tray executable, uninstaller, and setup automatically when these GitHub Actions secrets are configured:

- `CODE_SIGN_PFX_BASE64`
- `CODE_SIGN_PFX_PASSWORD`

The certificate is imported into the ephemeral runner and removed in the final workflow step.

## Security and contributing

Read [SECURITY.md](SECURITY.md) before reporting a vulnerability. Issues and focused pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

This is an unofficial community project and is not affiliated with or endorsed by OpenAI or Discord. Released under the [MIT License](LICENSE).
