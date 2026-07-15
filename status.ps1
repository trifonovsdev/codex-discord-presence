[CmdletBinding()]
param([string]$InstallDir = "$env:LOCALAPPDATA\OpenAI\CodexDiscordPresence")

$configPath = Join-Path ([IO.Path]::GetFullPath($InstallDir)) 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) { throw 'Presence is not installed.' }
$port = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).port
Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 3 | Format-List
