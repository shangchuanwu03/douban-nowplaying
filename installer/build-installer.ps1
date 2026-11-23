#requires -Version 5.1
<#
  build-installer.ps1 —— 打包「豆瓣正在听」安装包

  做法：
    1. 按下面的清单，从项目目录现抓运行文件到 installer\staging
    2. 压成 payload.zip
    3. 用 csc 把 Setup.cs + payload.zip 编成单文件 exe

  这样仓库里不用再存一份 staging 副本，改完代码直接跑这个脚本即可。

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # 项目根目录
$staging = Join-Path $PSScriptRoot 'staging'
$payload = Join-Path $PSScriptRoot 'payload.zip'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist '豆瓣正在听-安装包.exe'

# --- 打进安装包的文件清单（相对项目根目录） --------------------------------
$files = @(
    'douban-overlay.exe',
    'package.json',
    'README.md',
    '使用教程.md',
    '使用教程.html',

    'overlay\overlay.xaml',
    'overlay\overlay-glass.xaml',
    'overlay\settings.xaml',
    'overlay\settings-glass.xaml',

    'server\index.js',
    'server\douban.js',
    'server\store.js',
    'server\smtc.js',
    'server\mb.js',
    'server\netease.js',

    'scripts\smtc.ps1',
    'scripts\smtc-list.ps1',
    'scripts\import-collection.js',
    'scripts\check-players.js',

    'ui\icon.ico',
    'ui\icon.png',
    'ui\manage.html',
    'ui\overlay.html',
    'ui\overlay.css',
    'ui\overlay.js',

    '启动.bat',
    '管理.bat',
    '导入豆瓣收藏.bat',
    '检查播放器.bat',
    '调试-控制台模式.bat',

    'screenshots\02-悬浮窗实拍.png',
    'screenshots\04-管理页.png',
    'screenshots\05-皮肤切换.png',
    'screenshots\06-账号绑定.png',
    'screenshots\09-待评分同步.png',
    'screenshots\10-新界面布局.png',
    'screenshots\11-播放器来源.png',
    'screenshots\15-搜索按人数排序.png'
)

# --- 1. 生成 staging --------------------------------------------------------
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$missing = @()
foreach ($rel in $files) {
    $src = Join-Path $root $rel
    if (-not (Test-Path $src)) { $missing += $rel; continue }
    $dst = Join-Path $staging $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    Copy-Item $src $dst -Force
}
if ($missing.Count -gt 0) {
    throw ("以下文件缺失，打包中止：" + ($missing -join ', '))
}

# 空的 data 目录：首次运行会自己写
New-Item -ItemType Directory -Force -Path (Join-Path $staging 'data') | Out-Null

Write-Host ("staging: " + (Get-ChildItem $staging -Recurse -File | Measure-Object).Count + " 个文件")

# --- 2. 压缩 ---------------------------------------------------------------
$sevenZip = Join-Path $env:APPDATA 'npm\node_modules\7zip-bin\win\x64\7za.exe'
if (-not (Test-Path $sevenZip)) {
    $sevenZip = (Get-Command 7za.exe -ErrorAction SilentlyContinue).Source
}
if (-not (Test-Path $sevenZip)) { throw "找不到 7za.exe，无法压缩" }

if (Test-Path $payload) { Remove-Item $payload -Force }
& $sevenZip a -tzip -mx=9 $payload (Join-Path $staging '*') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "压缩失败" }

# --- 3. 编译安装程序 -------------------------------------------------------
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found" }

function Resolve-Asm([string]$name) {
    $fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\$name.dll"
    if (Test-Path $fw) { return $fw }
    $roots = @(
        "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name",
        "$env:WINDIR\Microsoft.NET\assembly\GAC_64\$name"
    )
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $hit = Get-ChildItem -Path $r -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '4\.0\.0\.0' } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$names = @('System.Windows.Forms','System.Drawing','System.IO.Compression','System.IO.Compression.FileSystem')
$refs = @()
foreach ($n in $names) {
    $p = Resolve-Asm $n
    if (-not $p) { throw "cannot resolve $n" }
    $refs += "/r:$p"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$icon = Join-Path $root 'ui\icon.ico'

$cscArgs = @(
    '/nologo','/target:winexe','/optimize+','/platform:anycpu',
    "/win32icon:$icon",
    "/out:$out",
    "/resource:$payload,payload.zip"
) + $refs + @((Join-Path $PSScriptRoot 'Setup.cs'))

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$info = Get-Item $out
Write-Host ("ok: " + $info.FullName + "  " + [math]::Round($info.Length / 1KB) + " KB")
