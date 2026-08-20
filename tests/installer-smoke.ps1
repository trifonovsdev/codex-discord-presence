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
$trayProcess = $null
$smokeStartedAt = Get-Date
$uiSmokeLog = Join-Path ([IO.Path]::GetTempPath()) 'codex-presence-ui-smoke.log'

function Invoke-UiSmoke {
  param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Label
  )

  Remove-Item -LiteralPath $uiSmokeLog -Force -ErrorAction SilentlyContinue
  $process = Start-Process $Executable -ArgumentList '--ui-smoke' -PassThru -WindowStyle Hidden
  if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $failure = 'timed out after 30 seconds'
  } elseif ($process.ExitCode -ne 0) {
    $failure = "returned $($process.ExitCode)"
  } else {
    return
  }

  Write-Host "--- $Label diagnostics ---"
  if (Test-Path -LiteralPath $uiSmokeLog) {
    Get-Content -LiteralPath $uiSmokeLog | ForEach-Object { Write-Host $_ }
  } else {
    Write-Host "UI smoke log was not created at $uiSmokeLog"
  }
  throw "$Label $failure."
}

function Write-SmokeDiagnostics {
  param(
    [Diagnostics.Process]$TrayProcess,
    [string]$InstallDir,
    [int]$Port,
    [Management.Automation.ErrorRecord]$LastHealthError
  )

  Write-Host '--- installer smoke diagnostics ---'
  if ($TrayProcess) {
    try {
      $status = if ($TrayProcess.HasExited) { "exited with code $($TrayProcess.ExitCode)" } else { 'still running' }
      Write-Host "Tray PID $($TrayProcess.Id): $status"
    } catch {
      Write-Host "Tray status unavailable: $($_.Exception.Message)"
    }
  } else {
    Write-Host 'Tray process was not created.'
  }
  if ($LastHealthError) { Write-Host "Last health error: $($LastHealthError.Exception.Message)" }

  $presenceLog = Join-Path $InstallDir 'app\presence.log'
  if (Test-Path -LiteralPath $presenceLog) {
    Write-Host "presence.log ($presenceLog):"
    Get-Content -LiteralPath $presenceLog -Tail 120
  } else {
    Write-Host "presence.log was not created at $presenceLog"
  }

  try {
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $smokeStartedAt } -ErrorAction Stop |
      Where-Object { $_.Message -match 'CodexPresence|Codex Presence|node\.exe' } |
      Select-Object -First 10 TimeCreated,ProviderName,Id,LevelDisplayName,Message
    if ($events) {
      Write-Host 'Relevant Windows Application events:'
      $events | Format-List | Out-String | Write-Host
    } else {
      Write-Host 'No matching Windows Application events were recorded.'
    }
  } catch {
    Write-Host "Windows Application events unavailable: $($_.Exception.Message)"
  }

  $nodePath = Join-Path $InstallDir 'runtime\node.exe'
  $daemonPath = Join-Path $InstallDir 'app\daemon.js'
  if (-not (Test-Path -LiteralPath $nodePath) -or -not (Test-Path -LiteralPath $daemonPath)) { return }

  $stdout = Join-Path $env:RUNNER_TEMP 'codex-presence-daemon.stdout.log'
  $stderr = Join-Path $env:RUNNER_TEMP 'codex-presence-daemon.stderr.log'
  Remove-Item -LiteralPath $stdout,$stderr -Force -ErrorAction SilentlyContinue
  $directDaemon = $null
  try {
    $env:CODEX_PRESENCE_TEST = '1'
    $directDaemon = Start-Process $nodePath -ArgumentList @($daemonPath) -WorkingDirectory (Split-Path $daemonPath) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Start-Sleep -Seconds 2
    try {
      $directHealth = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2
      Write-Host "Direct bundled Node health: v$($directHealth.version)"
    } catch {
      Write-Host "Direct bundled Node health failed: $($_.Exception.Message)"
    }
    try {
      $directStatus = if ($directDaemon.HasExited) { "exited with code $($directDaemon.ExitCode)" } else { 'still running' }
      Write-Host "Direct bundled Node PID $($directDaemon.Id): $directStatus"
    } catch {}
    foreach ($capture in @($stdout,$stderr)) {
      if (Test-Path -LiteralPath $capture) {
        Write-Host "$([IO.Path]::GetFileName($capture)):"
        Get-Content -LiteralPath $capture -Tail 120
      }
    }
  } finally {
    Remove-Item Env:CODEX_PRESENCE_TEST -ErrorAction SilentlyContinue
    try { Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/control" -ContentType 'application/json' -Body '{"action":"shutdown"}' -TimeoutSec 1 | Out-Null } catch {}
    if ($directDaemon -and -not $directDaemon.HasExited) { Stop-Process -Id $directDaemon.Id -Force -ErrorAction SilentlyContinue }
  }
}

try {
  New-Item -ItemType Directory -Path $env:LOCALAPPDATA,$env:CODEX_HOME -Force | Out-Null

  $portableArchives = @(Get-ChildItem -LiteralPath (Join-Path $repository 'artifacts') -Filter 'CodexPresence-*-portable.zip' -File)
  if ($portableArchives.Count -ne 1) { throw "Expected one portable archive; found $($portableArchives.Count)." }
  $portableRoot = Join-Path $testRoot 'Portable'
  Expand-Archive -LiteralPath $portableArchives[0].FullName -DestinationPath $portableRoot
  foreach ($relativePath in @('CodexPresence.exe','codex-presence.ico','runtime\node.exe','app\daemon.js','app\config.default.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $portableRoot $relativePath))) { throw "Portable bundle is missing $relativePath." }
  }
  Invoke-UiSmoke -Executable (Join-Path $portableRoot 'CodexPresence.exe') -Label 'Portable UI smoke test'

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

  $trayExecutable = Join-Path $installDir 'CodexPresence.exe'
  Invoke-UiSmoke -Executable $trayExecutable -Label 'Desktop UI smoke test'

  $trayProcess = Start-Process $trayExecutable -ArgumentList '--background' -PassThru
  $health = $null
  $lastHealthError = $null
  for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 250
    try {
      $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 1
      if ($health.ok) { break }
    } catch {
      $lastHealthError = $_
      if ($trayProcess.HasExited) { break }
    }
  }
  $expectedVersion = (Get-Content -LiteralPath (Join-Path $repository 'package.json') -Raw | ConvertFrom-Json).version
  if ($health.version -ne $expectedVersion) {
    Write-SmokeDiagnostics -TrayProcess $trayProcess -InstallDir $installDir -Port $Port -LastHealthError $lastHealthError
    throw "Installed daemon did not become healthy (expected v$expectedVersion, got '$($health.version)')."
  }

  $activationProbe = Start-Process $trayExecutable -ArgumentList '--background' -PassThru -WindowStyle Hidden
  if (-not $activationProbe.WaitForExit(5000)) {
    Stop-Process -Id $activationProbe.Id -Force -ErrorAction SilentlyContinue
    throw 'Activation probe did not hand off to the running tray within 5 seconds.'
  }
  if ($activationProbe.ExitCode -ne 0) { throw "Activation probe returned $($activationProbe.ExitCode)." }
  Start-Sleep -Milliseconds 500
  $ownedTrayProcesses = @(Get-Process CodexPresence -ErrorAction SilentlyContinue | Where-Object {
    try { [string]::Equals($_.Path, $trayExecutable, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
  })
  if ($ownedTrayProcesses.Count -ne 1 -or $ownedTrayProcesses[0].Id -ne $trayProcess.Id) {
    throw "Expected a single tray host after activation; found $($ownedTrayProcesses.Count)."
  }

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

  [pscustomobject]@{ Portable = 'PASS'; Setup = 'PASS'; Migration = 'PASS'; ForeignHook = 'PRESERVED'; UI = 'PASS'; Daemon = $health.version; Control = 'PASS'; Uninstall = 'PASS'; Removal = 'CLEAN' } | Format-List
}
finally {
  Get-Process CodexPresence -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installDir*" } | Stop-Process -Force -ErrorAction SilentlyContinue
  try { Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/control" -ContentType 'application/json' -Body '{"action":"shutdown"}' -TimeoutSec 1 | Out-Null } catch {}
  $uninstaller = Join-Path $installDir 'unins000.exe'
  if (Test-Path -LiteralPath $uninstaller) { Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -WindowStyle Hidden }
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
  Remove-Item -LiteralPath $uiSmokeLog -Force -ErrorAction SilentlyContinue
  $env:LOCALAPPDATA = $originalLocalAppData
  $env:CODEX_HOME = $originalCodexHome
}
