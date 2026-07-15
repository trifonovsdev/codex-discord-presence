[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$HostName,
  [string]$InstallDir = "$env:LOCALAPPDATA\OpenAI\CodexDiscordPresence"
)

$ErrorActionPreference = 'Stop'
if ($HostName -notmatch '^[A-Za-z0-9._@:-]+$') { throw 'HostName contains unsupported characters.' }
$ssh = (Get-Command ssh.exe -ErrorAction SilentlyContinue).Source
$scp = (Get-Command scp.exe -ErrorAction SilentlyContinue).Source
if (-not $ssh -or -not $scp) { throw 'Windows OpenSSH Client is required.' }

$remoteDir = '~/.local/share/CodexDiscordPresence'
$remoteFile = "$remoteDir/remote-monitor.py"
& $ssh -T -o BatchMode=yes -o ConnectTimeout=8 $HostName "mkdir -p $remoteDir"
if ($LASTEXITCODE -ne 0) { throw 'SSH connection failed. Configure SSH keys, then retry.' }
& $scp -q (Join-Path $PSScriptRoot 'src\remote-monitor.py') "${HostName}:$remoteFile"
if ($LASTEXITCODE -ne 0) { throw 'Could not upload remote-monitor.py.' }
& $ssh -T -o BatchMode=yes -o ConnectTimeout=8 $HostName "chmod 700 $remoteFile && python3 -m py_compile $remoteFile"
if ($LASTEXITCODE -ne 0) { throw 'Remote Python validation failed.' }

$configPath = Join-Path ([IO.Path]::GetFullPath($InstallDir)) 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) { throw 'Install the presence first.' }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.remote.host = $HostName
$config.remote.monitorPath = $remoteFile
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Host "Remote support configured for $HostName." -ForegroundColor Green
Write-Host 'Restart the presence daemon or rerun install.ps1 to apply the config.'
