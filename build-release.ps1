[CmdletBinding()]
param(
  [string]$Version = '2.1.0',
  [string]$NodeVersion = '24.17.0',
  [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
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
Copy-Item -LiteralPath (Join-Path $root 'src\daemon.js'),(Join-Path $root 'src\hook.js'),(Join-Path $root 'src\remotes.js'),(Join-Path $root 'src\remote-monitor.py') -Destination (Join-Path $stage 'app')
Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination (Join-Path $stage 'app\config.default.json')

$nodeArchive = Join-Path $cache "node-v$NodeVersion-win-x64.zip"
$nodeExtract = Join-Path $cache "node-v$NodeVersion-win-x64"
if (-not (Test-Path -LiteralPath $nodeArchive)) {
  Invoke-WebRequest -Uri "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-x64.zip" -OutFile $nodeArchive
}
if (-not (Test-Path -LiteralPath $nodeExtract)) { Expand-Archive -LiteralPath $nodeArchive -DestinationPath $cache }
Copy-Item -LiteralPath (Join-Path $nodeExtract 'node.exe') -Destination (Join-Path $stage 'runtime\node.exe')
Copy-Item -LiteralPath (Join-Path $nodeExtract 'LICENSE') -Destination (Join-Path $stage 'runtime\NODE_LICENSE')

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
