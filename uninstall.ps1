[CmdletBinding(SupportsShouldProcess)]
param([string]$InstallDir = "$env:LOCALAPPDATA\OpenAI\CodexDiscordPresence")

$ErrorActionPreference = 'Stop'
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$targetDaemon = Join-Path $InstallDir 'daemon.js'
$targetHook = Join-Path $InstallDir 'hook.js'

Get-CimInstance Win32_Process | Where-Object {
  $_.Name -eq 'node.exe' -and $_.CommandLine -like "*$targetDaemon*"
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$hooksPath = Join-Path $codexHome 'hooks.json'
if (Test-Path -LiteralPath $hooksPath) {
  $document = Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json
  foreach ($property in @($document.hooks.PSObject.Properties)) {
    $cleanGroups = @()
    foreach ($group in @($property.Value)) {
      if ($null -eq $group -or $null -eq $group.hooks) { continue }
      $cleanHooks = @($group.hooks | Where-Object {
        [string]$_.command -notlike "*$targetHook*" -and [string]$_.commandWindows -notlike "*$targetHook*"
      })
      if ($cleanHooks.Count) {
        $group | Add-Member -NotePropertyName hooks -NotePropertyValue $cleanHooks -Force
        $cleanGroups += $group
      }
    }
    $document.hooks | Add-Member -NotePropertyName $property.Name -NotePropertyValue $cleanGroups -Force
  }
  [IO.File]::WriteAllText($hooksPath, ($document | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

$startupFile = Join-Path ([Environment]::GetFolderPath('Startup')) 'CodexDiscordPresence.vbs'
if (Test-Path -LiteralPath $startupFile) { Remove-Item -LiteralPath $startupFile -Force }

$localAppData = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
if (-not $InstallDir.StartsWith($localAppData, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to remove a directory outside LOCALAPPDATA: $InstallDir"
}
if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
Write-Host 'Codex Discord Presence was removed.' -ForegroundColor Green
