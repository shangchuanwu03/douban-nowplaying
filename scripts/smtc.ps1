#requires -Version 5.1
<#
  smtc.ps1 —— Windows 系统媒体控制（SMTC）常驻采集器

  作用：读取系统「正在播放」信息（歌名/艺人/专辑/进度），
        状态有变化就往 stdout 打一行 JSON，供 Node 端解析。

  为什么要常驻：每次调用 PowerShell 启动约 200-400ms，
  切歌时轮询会导致明显延迟和 CPU 抖动，所以这里是一个长循环。

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File smtc.ps1
    powershell ... -File smtc.ps1 -IntervalMs 800          # 自定义轮询间隔
    powershell ... -File smtc.ps1 -Once                    # 只输出一次（调试用）
#>
param(
    [int]$IntervalMs = 900,
    [switch]$Once
)

$ErrorActionPreference = 'Continue'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

# --- WinRT async 帮助函数 ---------------------------------------------------
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
[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

# --- 获取管理器（失败就重试，不退出） ---------------------------------------
$manager = $null
for ($i = 0; $i -lt 30 -and -not $manager; $i++) {
    try {
        $manager = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
if (-not $manager) {
    [Console]::Out.WriteLine('{"error":"smtc_unavailable"}')
    exit 1
}

function Get-BestSession {
    param($mgr)
    try { $sessions = $mgr.GetSessions() } catch { return $null }
    if (-not $sessions -or $sessions.Count -eq 0) { return $null }

    $fallback = $null
    foreach ($s in $sessions) {
        try { $info = $s.GetPlaybackInfo() } catch { continue }
        if (-not $fallback) { $fallback = $s }
        if ($info.PlaybackStatus -eq 'Playing') { return $s }
    }
    return $fallback
}

function Get-WindowFallback {
    <#
      有些播放器（实测网易云 3.1.41）即使配置里 SMTCEnable=true 也不往系统 SMTC 注册，
      但窗口标题里带着「歌名 - 艺人」。这里退一步读窗口标题。
    #>
    $names = @('cloudmusic', 'QQMusic', 'Spotify', 'KuGou', 'MusicBee', 'foobar2000')
    foreach ($n in $names) {
        $procs = @()
        try { $procs = Get-Process -Name $n -ErrorAction SilentlyContinue } catch { }
        foreach ($p in $procs) {
            $t = ''
            try { $t = [string]$p.MainWindowTitle } catch { }
            if (-not $t) { continue }
            # 去掉常见的后缀
            $t = $t -replace '\s*[-–—]\s*(网易云音乐|QQ音乐|QQ 音乐|Spotify|酷狗音乐)\s*$', ''
            $song = $t
            $artist = ''
            $m = [regex]::Match($t, '^(.+?)\s+[-–—]\s+(.+)$')
            if ($m.Success) {
                $song = $m.Groups[1].Value.Trim()
                $artist = $m.Groups[2].Value.Trim()
            }
            if (-not $song) { continue }
            return [ordered]@{
                app         = $p.ProcessName + '.exe'
                status      = 'Playing'
                title       = $song
                artist      = $artist
                album       = ''
                albumArtist = $artist
                trackNo     = 0
                position    = 0
                duration    = 0
                via         = 'window'
                ts          = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            }
        }
    }
    return $null
}

function Emit([string]$json) {
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

$lastSig = $null

while ($true) {
    $payload = $null
    $sig = ''

    try {
        $session = Get-BestSession -mgr $manager
        if (-not $session) {
            $fb = Get-WindowFallback
            if ($fb) {
                $payload = $fb
                $sig = 'WIN|' + $fb.app + '|' + $fb.title + '|' + $fb.artist
                if ($sig -ne $lastSig) {
                    $lastSig = $sig
                    Emit ($payload | ConvertTo-Json -Compress -Depth 4)
                }
                if ($Once) { break }
                Start-Sleep -Milliseconds $IntervalMs
                continue
            }
        }
        if ($session) {
            $props = $null
            try {
                $props = Await ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
            } catch { }

            $info = $null
            try { $info = $session.GetPlaybackInfo() } catch { }

            $pos = 0.0; $dur = 0.0
            try {
                $tl = $session.GetTimelineProperties()
                $pos = [math]::Round($tl.Position.TotalSeconds, 1)
                $dur = [math]::Round($tl.EndTime.TotalSeconds, 1)
            } catch { }

            $appId = ''
            try { $appId = [string]$session.SourceAppUserModelId } catch { }

            $obj = [ordered]@{
                app        = $appId
                status     = if ($info) { [string]$info.PlaybackStatus } else { 'Unknown' }
                title      = if ($props) { [string]$props.Title } else { '' }
                artist     = if ($props) { [string]$props.Artist } else { '' }
                album      = if ($props) { [string]$props.AlbumTitle } else { '' }
                albumArtist= if ($props) { [string]$props.AlbumArtist } else { '' }
                trackNo    = if ($props) { [int]$props.TrackNumber } else { 0 }
                position   = $pos
                duration   = $dur
                ts         = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            }
            $payload = $obj
            $sig = "$appId|$($obj.status)|$($obj.title)|$($obj.artist)|$($obj.album)|$($obj.trackNo)"
        }
        else {
            $payload = [ordered]@{ app = $null; status = 'None'; title = ''; artist = ''; album = ''; albumArtist = ''; trackNo = 0; position = 0; duration = 0; ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
            $sig = 'NONE'
        }
    }
    catch {
        $payload = [ordered]@{ app = $null; status = 'Error'; error = $_.Exception.Message; ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
        $sig = 'ERR|' + $_.Exception.Message
    }

    # 只在「切歌/播放状态变化」时输出；进度变化不触发（前端按时间自己插值）
    if ($sig -ne $lastSig) {
        $lastSig = $sig
        Emit ($payload | ConvertTo-Json -Compress -Depth 4)
    }

    if ($Once) { break }
    Start-Sleep -Milliseconds $IntervalMs
}
