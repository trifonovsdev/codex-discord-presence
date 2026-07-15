[CmdletBinding()]
param(
  [string]$InstallDir = "$env:LOCALAPPDATA\OpenAI\CodexDiscordPresence",
  [string]$RemoteHost = '',
  [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'
$events = @('SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PermissionRequest', 'PostToolUse', 'SubagentStart', 'SubagentStop', 'Stop')
$matcherEvents = @('PreToolUse', 'PermissionRequest', 'PostToolUse')
$sourceDir = Join-Path $PSScriptRoot 'src'
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source

if (-not $node) {
  throw 'Node.js 18+ is required. Install it from https://nodejs.org and rerun this script.'
}

$nodeMajor = [int]((& $node --version).TrimStart('v').Split('.')[0])
if ($nodeMajor -lt 18) { throw 'Node.js 18 or newer is required.' }
if (-not (Test-Path -LiteralPath (Join-Path $sourceDir 'daemon.js'))) { throw 'Run install.ps1 from the repository root.' }

$InstallDir = [IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceDir 'daemon.js') -Destination $InstallDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir 'hook.js') -Destination $InstallDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir 'remote-monitor.py') -Destination $InstallDir -Force

$configPath = Join-Path $InstallDir 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) {
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.example.json') -Destination $configPath
}
if ($RemoteHost) {
  if ($RemoteHost -notmatch '^[A-Za-z0-9._@:-]+$') { throw 'RemoteHost contains unsupported characters.' }
  $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
  $config.remote.host = $RemoteHost
  [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$hooksPath = Join-Path $codexHome 'hooks.json'
New-Item -ItemType Directory -Path $codexHome -Force | Out-Null
if (Test-Path -LiteralPath $hooksPath) {
  Copy-Item -LiteralPath $hooksPath -Destination "$hooksPath.backup" -Force
  $document = Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json
} else {
  $document = [pscustomobject]@{ hooks = [pscustomobject]@{} }
}
if (-not $document.hooks) { $document | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) -Force }

$hookScript = Join-Path $InstallDir 'hook.js'
$hookCommand = '"{0}" "{1}"' -f $node, $hookScript
foreach ($event in $events) {
  $existing = @($document.hooks.$event)
  $alreadyInstalled = $false
  foreach ($group in $existing) {
    foreach ($hook in @($group.hooks)) {
      if ([string]$hook.commandWindows -eq $hookCommand -or [string]$hook.command -eq $hookCommand) { $alreadyInstalled = $true }
    }
  }
  if ($alreadyInstalled) { continue }

  $registration = [ordered]@{}
  if ($matcherEvents -contains $event) { $registration.matcher = '*' }
  $registration.hooks = @([ordered]@{
    type = 'command'
    command = $hookCommand
    commandWindows = $hookCommand
    timeout = 5
  })
  $document.hooks | Add-Member -NotePropertyName $event -NotePropertyValue (@($existing) + @([pscustomobject]$registration)) -Force
}
[IO.File]::WriteAllText($hooksPath, ($document | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))

if (-not $NoStartup) {
  $startupDir = [Environment]::GetFolderPath('Startup')
  $startupFile = Join-Path $startupDir 'CodexDiscordPresence.vbs'
  $vbs = @"
Set shell = CreateObject(`"WScript.Shell`")
shell.Run `"`"`"$node`"`" `"`"$InstallDir\daemon.js`"`"`", 0, False
"@
  [IO.File]::WriteAllText($startupFile, $vbs, [Text.UTF8Encoding]::new($false))
}

$targetDaemon = Join-Path $InstallDir 'daemon.js'
Get-CimInstance Win32_Process | Where-Object {
  $_.Name -eq 'node.exe' -and $_.CommandLine -like "*$targetDaemon*"
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Process -FilePath $node -ArgumentList @($targetDaemon) -WorkingDirectory $InstallDir -WindowStyle Hidden
Start-Sleep -Seconds 2

try {
  $port = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).port
  $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 3
  Write-Host "Installed successfully. Discord RPC ready: $($health.rpcReady)" -ForegroundColor Green
} catch {
  Write-Warning "Installed, but the health check is not ready yet. Open Discord and ChatGPT, then run .\status.ps1."
}

if ($RemoteHost) {
  & (Join-Path $PSScriptRoot 'setup-remote.ps1') -HostName $RemoteHost -InstallDir $InstallDir
}

Write-Host "Install directory: $InstallDir"
Write-Host "Config: $configPath"
Write-Host 'Restart ChatGPT/Codex once so it reloads hooks.json.'
