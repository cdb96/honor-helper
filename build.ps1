# Builds and (optionally) runs the honor-helper WinUI 3 app.
param(
    [switch]$Run,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'honor-helper.csproj'
# NativeAOT 目标平台版本（vs 10.0.26100.0，见 csproj）。Windows SDK 需已安装对应平台。
$tfm = 'net8.0-windows10.0.26100.0'

if ($Configuration -eq 'Release') {
    # Release 走 publish 产出 NativeAOT 原生可执行文件（csproj 里已设 PublishAot）。
    Write-Host "== Publishing honor-helper (NativeAOT, $Configuration / $Platform) ==" -ForegroundColor Cyan
    dotnet publish $proj -c $Configuration -p:Platform=$Platform -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    $out = Join-Path $PSScriptRoot "bin\$Platform\$Configuration\$tfm\win-x64\publish"
} else {
    Write-Host "== Building honor-helper ($Configuration / $Platform) ==" -ForegroundColor Cyan
    dotnet build $proj -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    # csproj 已全局设置 <RuntimeIdentifier>win-x64</RuntimeIdentifier>，所以 Debug 也输出到 win-x64 子目录。
    $out = Join-Path $PSScriptRoot "bin\$Platform\$Configuration\$tfm\win-x64"
}

# Release 构建后精简：删除未使用的语言资源（保留 zh-CN/zh-TW/en-us）。
# 语言文件夹是 Windows App SDK 复制的 MUI 资源，不受 SatelliteResourceLanguages 控制，
# 只能构建后清理；缺了它们 UI 会回退到中性语言，不影响功能。
if ($Configuration -eq 'Release') {
    $keep = 'zh-CN', 'zh-TW', 'en-us'
    Get-ChildItem $out -Directory |
        Where-Object { $_.Name -notin $keep -and $_.Name -notmatch '^(runtimes|Assets|Microsoft\.ui\.xaml)$' } |
        Remove-Item -Recurse -Force
    Remove-Item (Join-Path $out 'runtimes\win-arm64'), (Join-Path $out 'runtimes\win-x86') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "== Slimmed unused languages ==" -ForegroundColor Cyan
}

$exe = Join-Path $out 'honor-helper.exe'
if (-not (Test-Path $exe)) { throw "Exe not found: $exe" }

Write-Host "Build OK: $exe" -ForegroundColor Green

if ($Run) {
    # Request elevation (UAC) so HONOR WMI can be written.
    Write-Host "Launching as administrator..." -ForegroundColor Cyan
    Start-Process -Verb RunAs $exe
}
