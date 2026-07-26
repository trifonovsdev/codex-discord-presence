[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$InstallDir,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$appDir = Join-Path $InstallDir 'app'
$node = Join-Path $InstallDir 'runtime\node.exe'
$hookScript = Join-Path $appDir 'hook.js'
$daemonScript = Join-Path $appDir 'daemon.js'
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$hooksPath = Join-Path $codexHome 'hooks.json'
$events = @('SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PermissionRequest', 'PostToolUse', 'SubagentStart', 'SubagentStop', 'Stop')
$matcherEvents = @('PreToolUse', 'PermissionRequest', 'PostToolUse')

function Stop-PresenceDaemon {
  Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'node.exe' -and ($_.CommandLine -like "*$daemonScript*" -or $_.CommandLine -like '*\OpenAI\CodexDiscordPresence\daemon.js*')
  } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Remove-PresenceHooks($document) {
  if (-not $document.hooks) { return }
  foreach ($property in @($document.hooks.PSObject.Properties)) {
    $groups = @()
    foreach ($group in @($property.Value)) {
      if ($null -eq $group -or $null -eq $group.hooks) { continue }
      $clean = @($group.hooks | Where-Object {
        $command = "$( $_.command ) $( $_.commandWindows )"
        $command -notlike "*$hookScript*" -and $command -notlike '*\OpenAI\CodexDiscordPresence\hook.js*'
      })
      if ($clean.Count) {
        $group | Add-Member -NotePropertyName hooks -NotePropertyValue $clean -Force
        $groups += $group
      }
    }
    $document.hooks | Add-Member -NotePropertyName $property.Name -NotePropertyValue $groups -Force
  }
}

Stop-PresenceDaemon
if (Test-Path -LiteralPath $hooksPath) {
  $document = Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json
} else {
  New-Item -ItemType Directory -Path $codexHome -Force | Out-Null
  $document = [pscustomobject]@{ hooks = [pscustomobject]@{} }
}
if (-not $document.hooks) { $document | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) -Force }
Remove-PresenceHooks $document

if (-not $Uninstall) {
  $command = '"{0}" "{1}"' -f $node, $hookScript
  foreach ($event in $events) {
    $existing = @($document.hooks.$event | Where-Object { $null -ne $_ })
    $registration = [ordered]@{}
    if ($matcherEvents -contains $event) { $registration.matcher = '*' }
    $registration.hooks = @([ordered]@{ type = 'command'; command = $command; commandWindows = $command; timeout = 5 })
    $document.hooks | Add-Member -NotePropertyName $event -NotePropertyValue (@($existing) + @([pscustomobject]$registration)) -Force
  }

  $legacyConfig = Join-Path $env:LOCALAPPDATA 'OpenAI\CodexDiscordPresence\config.json'
  $legacyDirectory = Split-Path -Parent $legacyConfig
  $newConfig = Join-Path $appDir 'config.json'
  $defaultConfig = Join-Path $appDir 'config.default.json'
  if (-not (Test-Path -LiteralPath $newConfig)) {
    if (Test-Path -LiteralPath $legacyConfig) { Copy-Item -LiteralPath $legacyConfig -Destination $newConfig }
    elseif (Test-Path -LiteralPath $defaultConfig) { Copy-Item -LiteralPath $defaultConfig -Destination $newConfig }
  }
  $localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
  $legacyFull = [IO.Path]::GetFullPath($legacyDirectory)
  if ($legacyFull.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase) -and $legacyFull -ne $appDir -and (Test-Path -LiteralPath $legacyFull)) {
    Remove-Item -LiteralPath $legacyFull -Recurse -Force
  }
}

[IO.File]::WriteAllText($hooksPath, ($document | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
$installedConfig = Join-Path $appDir 'config.json'
if ($Uninstall) {
  foreach ($generatedFile in @($installedConfig, "$installedConfig.tmp", (Join-Path $appDir 'presence.log'), (Join-Path $appDir 'presence.log.1'))) {
    if (Test-Path -LiteralPath $generatedFile) { Remove-Item -LiteralPath $generatedFile -Force }
  }
  $stateDirectory = Join-Path $env:LOCALAPPDATA 'OpenAI\CodexPresence'
  $stateFull = [IO.Path]::GetFullPath($stateDirectory)
  $localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
  if ($stateFull.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $stateFull)) { Remove-Item -LiteralPath $stateFull -Recurse -Force }
}
$legacyStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'CodexDiscordPresence.vbs'
if (Test-Path -LiteralPath $legacyStartup) { Remove-Item -LiteralPath $legacyStartup -Force }
