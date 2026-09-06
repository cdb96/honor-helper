# Builds the native honor-core DLL (C++) into core/build/honor_core.dll.
# Locates MSVC (cl.exe) and the Windows SDK automatically via vswhere, falling
# back to the common install paths. The C# app P/Invokes this DLL.
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'honor_core.cpp'
$incl = Join-Path $root 'include'
$out  = Join-Path $root 'build'

if (-not (Test-Path $src)) { throw "Source not found: $src" }
if (-not (Test-Path $incl)) { throw "Include dir not found: $incl" }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# ---- locate MSVC & Windows SDK ----
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsDir = $null
if (Test-Path $vswhere) {
    $vsDir = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vsDir) {
    foreach ($cand in @('E:\vs\visual studio', "$env:ProgramFiles\Microsoft Visual Studio\2022\Community")) {
        if (Test-Path (Join-Path $cand 'VC\Auxiliary\Build\vcvars64.bat')) { $vsDir = $cand; break }
    }
}
if (-not $vsDir) { throw "Visual Studio C++ build tools not found." }

# ---- locate Windows SDK ----
$sdkDir = $null
$sdkVer = $null
foreach ($root in @('E:\Windows Kits\10', "$env:ProgramFiles(x86)\Windows Kits\10")) {
    if (Test-Path (Join-Path $root 'Include')) {
        $inc = Join-Path $root 'Include'
        $sdkDir = $root
        $sdkVer = Get-ChildItem $inc -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
        break
    }
}
if (-not $sdkDir) { throw "Windows SDK not found." }

# ---- invoke the MSVC environment ----
$vcvars = Join-Path $vsDir 'VC\Auxiliary\Build\vcvars64.bat'
$flag = if ($Configuration -eq 'Release') { '/O2' } else { '/Od /Zi' }
$defs = '/DUNICODE /D_UNICODE /DNDEBUG /DHONOR_CORE_EXPORTS'

$bat = @"
@echo off
call "$vcvars" >nul 2>&1
if errorlevel 1 ( echo vcvars failed & exit /b 1 )
set "WindowsSdkDir=$sdkDir\"
set "WindowsSDKVersion=$sdkVer\"
cl /nologo /EHsc /std:c++17 /W3 $flag /utf-8 /c $defs /I"$incl" "$src" /Fo:"$out\honor_core.obj"
if errorlevel 1 ( echo CL compile failed & exit /b 1 )
link /nologo /DLL /OUT:"$out\honor_core.dll" "$out\honor_core.obj" ^
     "$sdkDir\Lib\$sdkVer\um\x64\wbemuuid.lib" ^
     "$sdkDir\Lib\$sdkVer\um\x64\ole32.lib" ^
     "$sdkDir\Lib\$sdkVer\um\x64\oleaut32.lib" ^
     "$sdkDir\Lib\$sdkVer\um\x64\uuid.lib" ^
     "$sdkDir\Lib\$sdkVer\um\x64\advapi32.lib"
if errorlevel 1 ( echo LINK failed & exit /b 1 )
echo BUILD OK
"@
$batPath = Join-Path $out 'build_core.bat'
[System.IO.File]::WriteAllText($batPath, $bat, [System.Text.Encoding]::Default)

try {
    & cmd.exe /c "`"$batPath`""
    if ($LASTEXITCODE -ne 0) { throw "Core build failed with exit code $LASTEXITCODE." }
} finally {
    Remove-Item $batPath -ErrorAction SilentlyContinue
}

$dll = Join-Path $out 'honor_core.dll'
if (-not (Test-Path $dll)) { throw "Core DLL not produced: $dll" }
Write-Host "Core build OK: $dll" -ForegroundColor Green
