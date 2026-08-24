[CmdletBinding()]
param(
  [string]$Version = '2.4.1',
  [string]$NodeVersion = '24.17.0',
  [string]$NodeSha256 = 'f2aa33b35b75aca5f3f7b85675a6f6423201053e9381911e64961f3bda2528ab',
  [string]$DiscordSdkVersion = '1.9.16441',
  [string]$DiscordSdkCommit = '79b03948f0299931ef5477d033758fb4c3761c33',
  [string]$DiscordSdkSha256 = '46170463bf263045972fde1ccaa51b380eb1443541036a809d24f4e6a9f9c388',
  [string]$DiscordSdkNoticesSha256 = 'e8afa66340c225431e69768543cc34a7240f3494a9d759189fe118620ea8eebf',
  [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid release version: $Version" }
if ($NodeVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid Node version: $NodeVersion" }
if ($NodeSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'NodeSha256 must be a 64-character SHA-256 digest.' }
if ($DiscordSdkVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid Discord SDK version: $DiscordSdkVersion" }
if ($DiscordSdkCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'DiscordSdkCommit must be a 40-character Git commit.' }
if ($DiscordSdkSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'DiscordSdkSha256 must be a 64-character SHA-256 digest.' }
if ($DiscordSdkNoticesSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'DiscordSdkNoticesSha256 must be a 64-character SHA-256 digest.' }
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$packageVersion = (Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version
if ($Version -ne $packageVersion) { throw "Release version $Version must match package.json version $packageVersion." }
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts 'stage'
$cache = Join-Path $root '.build-cache'
foreach ($path in @($artifacts, $cache)) {
  $full = [IO.Path]::GetFullPath($path)
  if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe build path: $full" }
}
if (Test-Path -LiteralPath $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage 'app'),(Join-Path $stage 'runtime'),$cache -Force | Out-Null

dotnet publish (Join-Path $root 'tray\CodexPresence.Tray.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=true -p:PublishReadyToRun=true -o (Join-Path $artifacts 'tray-publish')
if ($LASTEXITCODE -ne 0) { throw 'Tray publish failed.' }
Copy-Item -LiteralPath (Join-Path $artifacts 'tray-publish\CodexPresence.exe') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'assets\codex-presence.ico') -Destination $stage
# Wildcards keep the payload in sync with src/ instead of a list that has to be edited by hand.
Copy-Item -Path (Join-Path $root 'src\*.js'),(Join-Path $root 'src\remote-monitor.py') -Destination (Join-Path $stage 'app')
Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination (Join-Path $stage 'app\config.default.json')

function Save-CachedDownload {
  param([Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$Destination)
  if (Test-Path -LiteralPath $Destination) { return }
  $partial = "$Destination.download"
  try {
    Invoke-WebRequest -Uri $Uri -OutFile $partial
    Move-Item -LiteralPath $partial -Destination $Destination -Force
  } finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
  }
}

$nodeArchiveName = "node-v$NodeVersion-win-x64.zip"
$nodeArchive = Join-Path $cache $nodeArchiveName
$nodeExtract = Join-Path $cache "node-v$NodeVersion-win-x64"
$nodeChecksums = Join-Path $cache "node-v$NodeVersion-SHASUMS256.txt"
Save-CachedDownload -Uri "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt" -Destination $nodeChecksums
Save-CachedDownload -Uri "https://nodejs.org/dist/v$NodeVersion/$nodeArchiveName" -Destination $nodeArchive

$escapedNodeArchiveName = [regex]::Escape($nodeArchiveName)
$nodeChecksumPattern = '^[0-9a-fA-F]{64}\s+\*?' + $escapedNodeArchiveName + '$'
$checksumLine = Get-Content -LiteralPath $nodeChecksums | Where-Object {
  $_.Trim() -match $nodeChecksumPattern
} | Select-Object -First 1
if (-not $checksumLine) { throw "Official Node SHASUMS256.txt has no entry for $nodeArchiveName." }
$manifestNodeHash = (($checksumLine.Trim() -split '\s+')[0]).ToLowerInvariant()
$expectedNodeHash = $NodeSha256.ToLowerInvariant()
if ($manifestNodeHash -ne $expectedNodeHash) { throw "Official manifest checksum $manifestNodeHash does not match pinned checksum $expectedNodeHash." }
$actualNodeHash = (Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualNodeHash -ne $expectedNodeHash) { throw "Node archive checksum mismatch for $nodeArchiveName." }

$nodeExtractMarker = Join-Path $nodeExtract '.archive-sha256'
$cachedExtractHash = if (Test-Path -LiteralPath $nodeExtractMarker) {
  (Get-Content -LiteralPath $nodeExtractMarker -Raw).Trim().ToLowerInvariant()
} else {
  ''
}
if (-not (Test-Path -LiteralPath $nodeExtract) -or $cachedExtractHash -ne $actualNodeHash) {
  if (Test-Path -LiteralPath $nodeExtract) { Remove-Item -LiteralPath $nodeExtract -Recurse -Force }
  Expand-Archive -LiteralPath $nodeArchive -DestinationPath $cache
  [IO.File]::WriteAllText($nodeExtractMarker, $actualNodeHash, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath (Join-Path $nodeExtract 'node.exe') -Destination (Join-Path $stage 'runtime\node.exe')
Copy-Item -LiteralPath (Join-Path $nodeExtract 'LICENSE') -Destination (Join-Path $stage 'runtime\NODE_LICENSE')

# Discord distributes the Social SDK from its authenticated Developer Portal.
# This pinned vendor mirror makes release builds reproducible without storing a
# native binary in Git; both the artifact and its notices are hash-verified.
$discordSdkBaseUri = "https://media.githubusercontent.com/media/blazium-games/discord-social-sdk/$DiscordSdkCommit"
$discordSdkBinary = Join-Path $cache "discord_partner_sdk-$DiscordSdkVersion.dll"
$discordSdkNotices = Join-Path $cache "discord-social-sdk-$DiscordSdkVersion-notices.txt"
Save-CachedDownload -Uri "$discordSdkBaseUri/bin/release/discord_partner_sdk.dll" -Destination $discordSdkBinary
Save-CachedDownload -Uri "https://raw.githubusercontent.com/blazium-games/discord-social-sdk/$DiscordSdkCommit/License-Notices.txt" -Destination $discordSdkNotices

$actualDiscordSdkHash = (Get-FileHash -LiteralPath $discordSdkBinary -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualDiscordSdkHash -ne $DiscordSdkSha256.ToLowerInvariant()) { throw 'Discord Social SDK binary checksum mismatch.' }
$actualDiscordNoticesHash = (Get-FileHash -LiteralPath $discordSdkNotices -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualDiscordNoticesHash -ne $DiscordSdkNoticesSha256.ToLowerInvariant()) { throw 'Discord Social SDK notices checksum mismatch.' }
Copy-Item -LiteralPath $discordSdkBinary -Destination (Join-Path $stage 'discord_partner_sdk.dll')
Copy-Item -LiteralPath $discordSdkNotices -Destination (Join-Path $stage 'DISCORD_SOCIAL_SDK_NOTICES.txt')

if (-not $SkipInstaller) {
  $registeredInno = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } | Select-Object -First 1 -ExpandProperty InstallLocation
  $iscc = @(
    $(if ($registeredInno) { Join-Path $registeredInno 'ISCC.exe' }),
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
  ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
  if (-not $iscc) { throw 'Inno Setup 6 compiler was not found.' }
  $compilerArguments = @()
  $compilerArguments += "/DMyAppVersion=$Version"
  if ($env:CODE_SIGN_THUMBPRINT) {
    $signTool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signTool) { throw 'signtool.exe was not found.' }
    $signCommand = '"{0}" sign /sha1 {1} /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f' -f $signTool.FullName, $env:CODE_SIGN_THUMBPRINT
    $compilerArguments += '/DSIGN_BUILD=1'
    $compilerArguments += "/Scodexsign=$signCommand"
  }
  $compilerArguments += (Join-Path $root 'installer\CodexPresence.iss')
  & $iscc @compilerArguments
  if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}

$portable = Join-Path $artifacts "CodexPresence-$Version-portable.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -CompressionLevel Optimal
$files = Get-ChildItem -LiteralPath $artifacts -File | Where-Object { $_.Extension -in '.exe','.zip' }
$lines = foreach ($file in $files) { "{0}  {1}" -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name }
[IO.File]::WriteAllLines((Join-Path $artifacts 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
$files | Select-Object Name,Length | Format-Table -AutoSize
