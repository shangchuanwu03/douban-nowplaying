#requires -Version 5.1
<#
  smtc-list.ps1 —— 列出当前所有 SMTC 会话（调试用）

  用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smtc-list.ps1

  输出每个会话的：
    appId / 状态 / 标题 / 艺人 / 专辑 / 是否当前会话
  用来排查「某个播放器认不出来」——先看它到底有没有上报。
#>

$ErrorActionPreference = 'Continue'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
} catch { }

Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

function Await($WinRtTask, $ResultType) {
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

$manager = $null
for ($i = 0; $i -lt 20 -and -not $manager; $i++) {
    try {
        $manager = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
    } catch {
        Start-Sleep -Milliseconds 400
    }
}

if (-not $manager) {
    [Console]::Out.WriteLine('{"error":"smtc_unavailable"}')
    exit 1
}

$current = $null
try { $current = $manager.GetCurrentSession() } catch { }

$sessions = @()
try { $sessions = $manager.GetSessions() } catch { }

$result = @()
foreach ($s in $sessions) {
    $appId = ''
    try { $appId = [string]$s.SourceAppUserModelId } catch { }

    $status = ''
    try { $status = [string]$s.GetPlaybackInfo().PlaybackStatus } catch { }

    $title = ''; $artist = ''; $album = ''
    try {
        $props = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
        if ($props) {
            $title = [string]$props.Title
            $artist = [string]$props.Artist
            $album = [string]$props.AlbumTitle
        }
    } catch { }

    $isCurrent = $false
    try { $isCurrent = ($null -ne $current -and $s.SourceAppUserModelId -eq $current.SourceAppUserModelId) } catch { }

    $result += [ordered]@{
        appId     = $appId
        status    = $status
        title     = $title
        artist    = $artist
        album     = $album
        isCurrent = $isCurrent
    }
}

if ($result.Count -eq 0) {
    [Console]::Out.WriteLine('{"sessions":[],"note":"当前没有任何 SMTC 会话"}')
} else {
    [ordered]@{ sessions = $result } | ConvertTo-Json -Depth 4 -Compress
}
