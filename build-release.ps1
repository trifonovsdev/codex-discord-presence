[CmdletBinding()]
param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$staging = Join-Path $env:TEMP "codex-discord-presence-$Version"
$archive = Join-Path $root "codex-discord-presence-$Version.zip"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
foreach ($item in @('src', 'assets', 'install.ps1', 'uninstall.ps1', 'setup-remote.ps1', 'status.ps1', 'config.example.json', 'README.md', 'LICENSE')) {
  Copy-Item -LiteralPath (Join-Path $root $item) -Destination $staging -Recurse
}
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host $archive
