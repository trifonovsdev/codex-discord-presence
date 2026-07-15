[CmdletBinding()]
param(
  [string]$InstallerPath = (Join-Path $PSScriptRoot '..\artifacts\CodexPresenceSetup.exe'),
  [int]$Port = 37645
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = [IO.Path]::GetFullPath((Join-Path $repository '.installer-smoke'))
if (-not $testRoot.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe smoke-test path.' }
if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }

$originalLocalAppData = $env:LOCALAPPDATA
$originalCodexHome = $env:CODEX_HOME
$env:LOCALAPPDATA = Join-Path $testRoot 'LocalAppData'
$env:CODEX_HOME = Join-Path $testRoot 'CodexHome'
$installDir = Join-Path $testRoot 'Program'
$foreignCommand = 'foreign-tool --keep-me'
$setupExit = $null
$uninstallExit = $null

try {
  New-Item -ItemType Directory -Path $env:LOCALAPPDATA,$env:CODEX_HOME -Force | Out-Null
  $legacyDir = Join-Path $env:LOCALAPPDATA 'OpenAI\CodexDiscordPresence'
  New-Item -ItemType Directory -Path $legacyDir -Force | Out-Null
  $legacy = [ordered]@{
    clientId = '1526968377048956938'; port = $Port; largeImageKey = 'codex'; largeImageText = 'OpenAI Codex'; appProcess = 'ChatGPT'; presenceEnabled = $true
    privacy = [ordered]@{ preset = 'minimal'; showProject = $true; showFile = $false; showTimer = $true; fileMode = 'name' }
    remote = [ordered]@{ host = ''; monitorPath = '~/.local/share/CodexDiscordPresence/remote-monitor.py'; pollIntervalMs = 7000 }
  }
  [IO.File]::WriteAllText((Join-Path $legacyDir 'config.json'), ($legacy | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $legacyDir 'daemon.js'), 'legacy', [Text.UTF8Encoding]::new($false))
  $oldHook = '"node" "{0}"' -f (Join-Path $legacyDir 'hook.js')
  $hooks = [ordered]@{ hooks = [ordered]@{ PostToolUse = @(
    [ordered]@{ matcher = 'foreign'; hooks = @([ordered]@{ type = 'command'; command = $foreignCommand; timeout = 9 }) },
    [ordered]@{ matcher = '*'; hooks = @([ordered]@{ type = 'command'; command = $oldHook; commandWindows = $oldHook; timeout = 5 }) }
  ) } }
  [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'hooks.json'), ($hooks | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))

  $setup = Start-Process ([IO.Path]::GetFullPath($InstallerPath)) -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=$installDir",'/NOICONS','/TASKS=""') -Wait -PassThru -WindowStyle Hidden
  $setupExit = $setup.ExitCode
  if ($setupExit -ne 0) { throw "Setup returned $setupExit." }
  $config = Get-Content -LiteralPath (Join-Path $installDir 'app\config.json') -Raw | ConvertFrom-Json
  if ($config.port -ne $Port -or $config.privacy.preset -ne 'minimal') { throw 'Legacy configuration was not migrated.' }
  if (Test-Path -LiteralPath $legacyDir) { throw 'Legacy installation directory was not removed.' }

  $newHookPath = Join-Path $installDir 'app\hook.js'
  $hookText = Get-Content -LiteralPath (Join-Path $env:CODEX_HOME 'hooks.json') -Raw
  $normalizedHooks = $hookText.Replace('\\', '\')
  if (([regex]::Matches($hookText, [regex]::Escape($foreignCommand))).Count -ne 1) { throw 'Foreign hook was not preserved.' }
  if (([regex]::Matches($normalizedHooks, [regex]::Escape($newHookPath))).Count -ne 16) { throw 'Expected eight dual-platform hook registrations.' }
  if ($normalizedHooks.IndexOf((Join-Path $legacyDir 'hook.js'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'Legacy hook registration remains.' }

  Start-Process (Join-Path $installDir 'CodexPresence.exe') -ArgumentList '--background'
  $health = $null
  for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 250
    try { $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 1; if ($health.ok) { break } } catch {}
  }
  if ($health.version -ne '2.0.0') { throw 'Installed daemon did not become healthy.' }
  $pause = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/control" -ContentType 'application/json' -Body '{"action":"pause"}'
  $resume = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/control" -ContentType 'application/json' -Body '{"action":"resume"}'
  if ($pause.presenceEnabled -ne $false -or $resume.presenceEnabled -ne $true) { throw 'Pause/resume control failed.' }

  $uninstall = Start-Process (Join-Path $installDir 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru -WindowStyle Hidden
  $uninstallExit = $uninstall.ExitCode
  if ($uninstallExit -ne 0) { throw "Uninstall returned $uninstallExit." }
  Start-Sleep -Seconds 2
  $finalHooks = Get-Content -LiteralPath (Join-Path $env:CODEX_HOME 'hooks.json') -Raw
  if (-not $finalHooks.Contains($foreignCommand) -or $finalHooks.Replace('\\', '\').IndexOf($newHookPath, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'Uninstall hook cleanup failed.' }
  if (Test-Path -LiteralPath $installDir) { throw 'Install directory remains after uninstall.' }
  if (@(Get-Process CodexPresence -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installDir*" }).Count) { throw 'Tray process survived uninstall.' }
  try { $null = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 1; throw 'Daemon survived uninstall.' } catch { if ($_.Exception.Message -eq 'Daemon survived uninstall.') { throw } }

  [pscustomobject]@{ Setup = 'PASS'; Migration = 'PASS'; ForeignHook = 'PRESERVED'; Daemon = $health.version; Control = 'PASS'; Uninstall = 'PASS'; Removal = 'CLEAN' } | Format-List
}
finally {
  Get-Process CodexPresence -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installDir*" } | Stop-Process -Force -ErrorAction SilentlyContinue
  try { Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/control" -ContentType 'application/json' -Body '{"action":"shutdown"}' -TimeoutSec 1 | Out-Null } catch {}
  $uninstaller = Join-Path $installDir 'unins000.exe'
  if (Test-Path -LiteralPath $uninstaller) { Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -WindowStyle Hidden }
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
  $env:LOCALAPPDATA = $originalLocalAppData
  $env:CODEX_HOME = $originalCodexHome
}
