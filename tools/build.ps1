<#
    WaterCalcMod 构建脚本（Windows PowerShell / PowerShell 7 均可）

      .\tools\build.ps1              # 发布版（Release）→ dist\WaterCalcMod\
      .\tools\build.ps1 -Dev         # 开发版（DEV，带 ctl 调试通道与 log.txt）
      .\tools\build.ps1 -GameDir "D:\steam\steamapps\common\Probably Stolen"

    依赖：游戏本体自带的 Managed 程序集（不随仓库分发）。
#>
[CmdletBinding()]
param(
    [switch]$Dev,
    [string]$GameDir = $env:GAME_DIR
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Src = Join-Path $Root 'src'
$Out = Join-Path $Root 'dist\WaterCalcMod'
$Lib = Join-Path $Root 'lib'

# ---- 找游戏目录 ----
if (-not $GameDir) {
    $guess = @(
        'D:\steam\steamapps\common\Probably Stolen',
        'C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen',
        'C:\Program Files\Steam\steamapps\common\Probably Stolen',
        'E:\steam\steamapps\common\Probably Stolen'
    )
    foreach ($g in $guess) {
        if (Test-Path (Join-Path $g 'Probably Stolen_Data\Managed')) { $GameDir = $g; break }
    }
}
if (-not $GameDir) {
    throw "找不到游戏目录：请传 -GameDir 或设置 `$env:GAME_DIR"
}
$Managed = Join-Path $GameDir 'Probably Stolen_Data\Managed'
if (-not (Test-Path $Managed)) { throw "缺少 Managed 目录：$Managed" }

# ---- 找 csc ----
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }

New-Item -ItemType Directory -Force -Path $Out | Out-Null

$args = @('/nologo', '/target:library', '/platform:x64', '/langversion:5',
          ('/out:' + (Join-Path $Out 'WaterCalcMod.dll')))
if ($Dev) { $args += '/define:DEV' }

$refs = @(
    '0Harmony.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll',
    'UnityEngine.InputLegacyModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.UIModule.dll', 'UnityEngine.UI.dll', 'UnityEngine.ImageConversionModule.dll',
    'Unity.TextMeshPro.dll', 'Assembly-CSharp.dll', 'netstandard.dll'
)
foreach ($r in $refs) {
    $p = Join-Path $Managed $r
    if (-not (Test-Path $p)) { $p = Join-Path $Lib $r }
    if (Test-Path $p) { $args += ('/r:' + $p) }
    else { Write-Warning "引用缺失：$r" }
}

$files = Get-ChildItem -Path $Src -Filter *.cs | ForEach-Object { $_.FullName }

Write-Host "=== 构建 $(if ($Dev) { 'DEV' } else { 'Release' })（$($files.Count) 个源文件） ==="
Write-Host "    游戏目录：$GameDir"

$log = & $csc @args $files 2>&1 | Out-String
$log | Write-Host
if ($log -match 'error CS') {
    throw '编译报错，产物不可信（不要拿去发布）'
}

if ($Dev) {
    Copy-Item (Join-Path $Root 'manifest.dev.xml') (Join-Path $Out 'manifest.xml') -Force
    Copy-Item (Join-Path $Out 'WaterCalcMod.dll') (Join-Path $Root 'dist\WaterCalcMod.dev.dll') -Force
} else {
    Copy-Item (Join-Path $Root 'manifest.release.xml') (Join-Path $Out 'manifest.xml') -Force
}

Write-Host "=== 完成：$Out ==="
Get-ChildItem $Out | Format-Table Name, Length
Write-Host '（把 dist\WaterCalcMod 整个文件夹放进 游戏目录\Mods\ 即可安装）'
