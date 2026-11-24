#requires -Version 5.1
<#
  Build the WPF overlay with the csc.exe that ships with .NET Framework.
  No third-party SDK required. Output: douban-overlay.exe in the project root.

  Note: this machine has no "Reference Assemblies" folder, so we resolve
  framework assemblies straight out of the GAC.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'overlay\Overlay.cs'
$src2 = Join-Path $root 'overlay\SettingsWindow.cs'
$src3 = Join-Path $root 'overlay\Updater.cs'
$src4 = Join-Path $root 'overlay\Anim.cs'
$out = Join-Path $root 'douban-overlay.exe'

$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found (.NET Framework 4.x required)" }

function Resolve-Gac([string]$name) {
    $fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\$name.dll"
    if (Test-Path $fw) { return $fw }
    $fw32 = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\$name.dll"
    if (Test-Path $fw32) { return $fw32 }

    $roots = @(
        "$env:WINDIR\Microsoft.NET\assembly\GAC_64\$name",
        "$env:WINDIR\Microsoft.NET\assembly\GAC_32\$name",
        "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name",
        "$env:WINDIR\assembly\GAC_64\$name",
        "$env:WINDIR\assembly\GAC_32\$name",
        "$env:WINDIR\assembly\GAC_MSIL\$name"
    )
    $all = @()
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $all += Get-ChildItem -Path $r -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue
    }
    if (-not $all -or $all.Count -eq 0) { return $null }
    # WPF 4.x lives in GAC_64/GAC_32; prefer the 4.0.0.0 build over the legacy 3.0 one
    $v4 = $all | Where-Object { $_.FullName -match '\\4\.0\.0\.0' } | Select-Object -First 1
    if ($v4) { return $v4.FullName }
    return ($all | Sort-Object FullName -Descending | Select-Object -First 1).FullName
}

$names = @(
    'PresentationFramework',
    'PresentationCore',
    'WindowsBase',
    'System.Xaml',
    'System.Web.Extensions',
    'System.Net.Http',
    'System.Net.WebSockets.Client'
)

$refs = @()
foreach ($n in $names) {
    $p = Resolve-Gac $n
    if (-not $p) { throw "cannot resolve assembly: $n" }
    $refs += "/r:$p"
}

$iconPath = Join-Path $root 'ui\icon.ico'

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    '/nowarn:1591',
    "/win32icon:$iconPath",
    "/out:$out"
) + $refs + @($src, $src2, $src3, $src4)

Write-Host "compiling $src"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

$info = Get-Item $out
Write-Host ("ok: " + $info.FullName + "  " + [math]::Round($info.Length / 1KB) + " KB")
