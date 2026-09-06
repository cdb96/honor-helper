# Builds (and optionally runs) the honor-helper app: a C++ background service + a WinUI client.
#   - service          (service/          -> pure-C++ headless host, all hardware access;
#                                          core/honor_core.cpp linked in statically)
#   - common library   (common/           -> shared DTOs / pipe protocol / persistence, UI side)
#   - ui               (ui/               -> WinUI client, talks to service over a named pipe)
param(
    [switch]$Run,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tfm = 'net8.0-windows10.0.26100.0'

# 1) Build the pure-C++ service into service/build/honor-helper-service.exe.
Write-Host "== Building service (C++, $Configuration / $Platform) ==" -ForegroundColor Cyan
& (Join-Path $root 'service\build.ps1') -Configuration $Configuration -Platform $Platform
if ($LASTEXITCODE -ne 0) { throw "Service build failed." }
$svcExe = Join-Path $root 'service\build\honor-helper-service.exe'
if (-not (Test-Path $svcExe)) { throw "Service exe not found: $svcExe" }

# 2) Publish the UI (WinUI client). It talks to the service over a named pipe.
$uiProj = Join-Path $root 'ui\honor-helper.csproj'
Write-Host "== Publishing ui ($Configuration / $Platform) ==" -ForegroundColor Cyan
dotnet publish $uiProj -c $Configuration -p:Platform=$Platform -r win-x64
if ($LASTEXITCODE -ne 0) { throw "UI publish failed." }
$uiOut = Join-Path $root "ui\bin\$Platform\$Configuration\$tfm\win-x64\publish"

# 3) Copy the C++ service exe into the UI publish folder
#    so the single UI app bundle carries both processes.
Copy-Item $svcExe -Destination (Join-Path $uiOut 'honor-helper-service.exe') -Force

# 4) Slim unused language resources in the UI publish (keep zh-CN/zh-TW/en-us).
if ($Configuration -eq 'Release') {
    $keep = 'zh-CN', 'zh-TW', 'en-us'
    Get-ChildItem $uiOut -Directory |
        Where-Object { $_.Name -notin $keep -and $_.Name -notmatch '^(runtimes|Assets|Microsoft\.ui\.xaml)$' } |
        Remove-Item -Recurse -Force
    Remove-Item (Join-Path $uiOut 'runtimes\win-arm64'), (Join-Path $uiOut 'runtimes\win-x86') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "== Slimmed unused languages ==" -ForegroundColor Cyan
}

$exe = Join-Path $uiOut 'honor-helper.exe'
if (-not (Test-Path $exe)) { throw "Exe not found: $exe" }
if (-not (Test-Path (Join-Path $uiOut 'honor-helper-service.exe'))) { throw "Service exe not bundled in UI output." }

Write-Host "Build OK: $exe" -ForegroundColor Green

if ($Run) {
    # The UI does not need elevation (service is elevated). Launch normally.
    Write-Host "Launching UI..." -ForegroundColor Cyan
    Start-Process $exe
} elseif ($Configuration -eq 'Release') {
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist 'honor-helper-win-x64.zip'
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path "$uiOut\*" -DestinationPath $zip
    Write-Host "Packaged: $zip" -ForegroundColor Green
}
