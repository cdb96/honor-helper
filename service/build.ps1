# Builds the pure-C++ background service into service/build/.
# Output: honor-helper-service.exe (GUI subsystem, requireAdministrator,
# app.ico embedded). Links core/honor_core.cpp statically — no honor_core.dll.
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$coreSrc = Join-Path $root '..\core\honor_core.cpp'
$coreIncl = Join-Path $root '..\core\include'
$out = Join-Path $root 'build'

foreach ($p in @($srcDir, $coreSrc, $coreIncl)) {
    if (-not (Test-Path $p)) { throw "Missing: $p" }
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

# ---- locate MSVC ----
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsDir = $null
if (Test-Path $vswhere) {
    $vsDir = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vsDir) {
    foreach ($cand in @('E:\vs\visual studio', "$env:ProgramFiles\Microsoft Visual Studio\2022\Community")) {
        # NOTE: Join-Path throws on a non-existent drive (e.g. E: on CI
        # runners), so guard with a plain Test-Path first.
        if ((Test-Path $cand) -and (Test-Path (Join-Path $cand 'VC\Auxiliary\Build\vcvars64.bat'))) { $vsDir = $cand; break }
    }
}
if (-not $vsDir) { throw "Visual Studio C++ build tools not found." }

# ---- locate Windows SDK ----
$sdkDir = $null
$sdkVer = $null
foreach ($r in @('E:\Windows Kits\10', "$env:ProgramFiles(x86)\Windows Kits\10")) {
    # NOTE: same non-existent-drive guard as above (E: is local-only).
    if (-not (Test-Path $r)) { continue }
    if (Test-Path (Join-Path $r 'Include')) {
        $sdkDir = $r
        $sdkVer = Get-ChildItem (Join-Path $r 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
        break
    }
}
if (-not $sdkDir) { throw "Windows SDK not found." }

$vcvars = Join-Path $vsDir 'VC\Auxiliary\Build\vcvars64.bat'
$flag = if ($Configuration -eq 'Release') { '/O2 /GL' } else { '/Od /Zi' }
$defs = '/DUNICODE /D_UNICODE /DNDEBUG /DHONOR_CORE_EXPORTS /EHsc /std:c++17 /W3 /utf-8 /DWIN32_LEAN_AND_MEAN /D_NOMINMAX'
$lib = if ($Configuration -eq 'Release') { '/LTCG' } else { '/DEBUG' }
$exe = Join-Path $out 'honor-helper-service.exe'
$sdkLib = "$sdkDir\Lib\$sdkVer\um\x64"

$bat = @"
@echo off
call "$vcvars" >nul 2>&1
if errorlevel 1 ( echo vcvars failed & exit /b 1 )
set "WindowsSdkDir=$sdkDir\"
set "WindowsSDKVersion=$sdkVer\"
cl /nologo $defs $flag /I"$srcDir" /I"$coreIncl" "$srcDir\json.cpp" "$srcDir\hardware.cpp" "$srcDir\stores.cpp" "$srcDir\pipe_server.cpp" "$srcDir\trigger_engine.cpp" "$srcDir\tray.cpp" "$srcDir\ui_coordinator.cpp" "$srcDir\autostart.cpp" "$srcDir\main.cpp" "$coreSrc" /Fo:"$out\\" /Fe:"$exe" /Fd:"$out\vc.pdb"
if errorlevel 1 ( echo CL compile failed & exit /b 1 )
"@

# NOTE: link via cl single-step (above) already links; append manifest + icon here.
$batPath = Join-Path $out 'build_service.bat'
[System.IO.File]::WriteAllText($batPath, $bat, [System.Text.Encoding]::Default)

try {
    & cmd.exe /c "`"$batPath`""
    if ($LASTEXITCODE -ne 0) { throw "Service compile failed with exit code $LASTEXITCODE." }
} finally {
    Remove-Item $batPath -ErrorAction SilentlyContinue
}

# ---- link extras: subsystem WINDOWS + manifest + icon ----
# Re-link objects with the GUI subsystem and the requireAdministrator manifest.
$objs = Get-ChildItem $out -Filter *.obj | ForEach-Object { "`"$($_.FullName)`"" }
$objsLine = $objs -join ' '
$rcExe = Join-Path $sdkDir "bin\$sdkVer\x64\rc.exe"
$mtExe = Join-Path $sdkDir "bin\$sdkVer\x64\mt.exe"
$manifest = Join-Path $root 'app.manifest'
$ico = Join-Path $root 'Assets\app.ico'
$rcFile = Join-Path $out 'svc.rc'
$rcObj = Join-Path $out 'svc.res'
$rcText = '1 ICON "Assets\\app.ico"' + "`r`n" + '1 24 "app.manifest"' + "`r`n"
[System.IO.File]::WriteAllText($rcFile, $rcText, [System.Text.Encoding]::Default)

$linkBat = @"
@echo off
call "$vcvars" >nul 2>&1
if errorlevel 1 ( echo vcvars failed & exit /b 1 )
set "WindowsSdkDir=$sdkDir\"
set "WindowsSDKVersion=$sdkVer\"
cd /d "$root"
"$rcExe" /nologo /fo "$rcObj" "$rcFile"
if errorlevel 1 ( echo RC failed & exit /b 1 )
link /nologo $lib /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup /OUT:"$exe" $objsLine "$rcObj" "$sdkLib\wbemuuid.lib" "$sdkLib\ole32.lib" "$sdkLib\oleaut32.lib" "$sdkLib\uuid.lib" "$sdkLib\advapi32.lib" "$sdkLib\user32.lib" "$sdkLib\shell32.lib" "$sdkLib\shlwapi.lib" "$sdkLib\psapi.lib" "$sdkLib\ole32.lib"
if errorlevel 1 ( echo LINK failed & exit /b 1 )
"@
$linkPath = Join-Path $out 'link_service.bat'
[System.IO.File]::WriteAllText($linkPath, $linkBat, [System.Text.Encoding]::Default)
try {
    & cmd.exe /c "`"$linkPath`""
    if ($LASTEXITCODE -ne 0) { throw "Service link failed with exit code $LASTEXITCODE." }
} finally {
    Remove-Item $linkPath -ErrorAction SilentlyContinue
}

if (-not (Test-Path $exe)) { throw "Service exe not produced: $exe" }
Write-Host "Service build OK: $exe" -ForegroundColor Green

# ---- smoke-test client (pipe_smoke.exe): links only the test TU ----
$smokeSrc = Join-Path $root 'tests\pipe_smoke.cpp'
$smokeExe = Join-Path $out 'pipe_smoke.exe'
$smokeBat = @"
@echo off
call "$vcvars" >nul 2>&1
if errorlevel 1 ( echo vcvars failed & exit /b 1 )
set "WindowsSdkDir=$sdkDir\"
set "WindowsSDKVersion=$sdkVer\"
cl /nologo $defs $flag "$smokeSrc" /Fo:"$out\\" /Fe:"$smokeExe" /Fd:"$out\smoke.pdb"
if errorlevel 1 ( echo SMOKE compile failed & exit /b 1 )
"@
$smokePath = Join-Path $out 'build_smoke.bat'
[System.IO.File]::WriteAllText($smokePath, $smokeBat, [System.Text.Encoding]::Default)
try {
    & cmd.exe /c "`"$smokePath`""
    if ($LASTEXITCODE -ne 0) { throw "Smoke client build failed with exit code $LASTEXITCODE." }
} finally {
    Remove-Item $smokePath -ErrorAction SilentlyContinue
}
Write-Host "Smoke client OK: $smokeExe" -ForegroundColor Green
