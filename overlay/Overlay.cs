using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DoubanNowPlaying
{
    /// <summary>
    /// 桌面悬浮窗：置顶、半透明、无边框。
    /// 数据全部来自本地 Node 服务（http://127.0.0.1:4590），
    /// 这里只负责显示与交互，不做任何豆瓣请求。
    /// </summary>
    public class Overlay
    {
        const string BaseUrl = "http://127.0.0.1:4590";

        // 新拟物派：同色底 + 双阴影，只用紫罗兰做强调
        public enum SkinKind { Neumorphic, Glass }

        public static SkinKind Skin = SkinKind.Neumorphic;
        public static Overlay Current;

        // 两套皮肤共用这些槽位，切换时整体重设
        static Brush Ink, Dim, Faint, Accent, Mint, Coral, Base, StarOff, PulseIdle,
                     OnAccent, ScoreInk, DbStarOn, DistBase, Subtle;

        static void ApplyPalette(SkinKind skin)
        {
            if (skin == SkinKind.Glass)
            {
                Ink = NewBrush(0xFF, 0xFF, 0xFF);
                Dim = NewBrush(0xA9, 0xC0, 0xDC);
                Faint = NewBrush(0x6E, 0x8C, 0xB2);
                Accent = NewBrush(0xE4, 0xB8, 0x63);
                Mint = NewBrush(0xE4, 0xB8, 0x63);
                Coral = NewBrush(0xFF, 0x7B, 0x72);
                Base = NewBrush(0x16, 0x23, 0x3A);
                StarOff = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                PulseIdle = NewBrush(0x55, 0x70, 0x8F);
                OnAccent = NewBrush(0x06, 0x0A, 0x13);
                ScoreInk = NewBrush(0xE4, 0xB8, 0x63);
                DbStarOn = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                DistBase = NewBrush(0xE4, 0xB8, 0x63);
                Subtle = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                Ink = NewBrush(0x3D, 0x44, 0x51);
                Dim = NewBrush(0x7B, 0x84, 0x94);
                Faint = NewBrush(0x9A, 0xA3, 0xB2);
                Accent = NewBrush(0x6D, 0x5D, 0xFC);
                Mint = NewBrush(0x4E, 0xCD, 0xC4);
                Coral = NewBrush(0xFF, 0x6B, 0x6B);
                Base = NewBrush(0xE0, 0xE5, 0xEC);
                StarOff = NewBrush(0xC3, 0xC9, 0xD3);
                PulseIdle = NewBrush(0xB8, 0xBC, 0xC2);
                OnAccent = Brushes.White;
                ScoreInk = NewBrush(0x3D, 0x44, 0x51);
                DbStarOn = NewBrush(0x3D, 0x44, 0x51);
                DistBase = NewBrush(0x3D, 0x44, 0x51);
                Subtle = NewBrush(0xE0, 0xE5, 0xEC);
            }
        }

        Window _win;
        HttpClient _http;
        DispatcherTimer _timer;
        Process _serverProc;
        StreamWriter _serverLog;
        int _pollFailures;
        DateTime _lastRecover = DateTime.MinValue;
        bool _recovering;

        // 控件
        Border _card, _coverShell, _btnSync, _progressTrack, _progressFill, _btnManage, _mineRow, _btnLater;
        TextBlock _appName, _status, _album, _artist, _scoreNum, _scoreUnit, _votes, _hint, _coverLetter, _syncText, _laterText;
        TextBlock _trackText;
        Image _cover;
        Grid _dbStars, _dist;
        StackPanel _myStars;
        TextBox _comment;
        System.Windows.Shapes.Ellipse _pulse;

        int? _subjectId;
        int _myRating;
        bool _commentDirty;
        int? _commentSubjectId;   // 评语框里的文字属于哪张专辑
        bool _syncing;
        bool _synced;
        bool _syncEnabled;
        bool _busy;
        string _coverUrl;
        bool _anchorBottom = true;
        DateTime _lastInteraction = DateTime.Now;
        string _artistName = "";

        // 待评分 / 缩放
        bool _inTodo;
        bool _laterBusy;
        double _scale = 1.0;
        FrameworkElement _root;
        DispatcherTimer _scaleSaveTimer;

        public static bool Run()
        {
            var overlay = new Overlay();
            return overlay.Start();
        }

        public static void Trace(string msg)
        {
            try
            {
                var dir = Path.Combine(AppDir, "data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "app.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        static System.Threading.Mutex _mutex;
        static System.Threading.EventWaitHandle _settingsSignal =
            new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset,
                "DoubanNowPlaying_OpenSettings_v1");
        static System.Threading.EventWaitHandle _loginSignal =
            new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset,
                "DoubanNowPlaying_Login_v1");
        static System.Threading.EventWaitHandle _todoSignal =
            new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset,
                "DoubanNowPlaying_Todo_v1");

        bool Start()
        {
            bool createdNew;
            _mutex = new System.Threading.Mutex(true, "DoubanNowPlayingOverlay_v1", out createdNew);
            Trace("start createdNew=" + createdNew + " settings=" + Program.OpenSettingsRequested + " login=" + Program.OpenLoginRequested);
            if (!createdNew)
            {
                // 已经有一个在跑：只把「打开设置」的请求转给它
                if (Program.OpenTodoRequested)
                {
                    try { _todoSignal.Set(); } catch { }
                }
                else if (Program.OpenLoginRequested)
                {
                    try { _loginSignal.Set(); Trace("signalled login"); } catch (Exception ex) { Trace("login signal failed: " + ex.Message); }
                }
                else if (Program.OpenSettingsRequested)
                {
                    try { _settingsSignal.Set(); } catch { }
                }
                return false;
            }

            StartSettingsListener();
            Current = this;

            _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(8) };

            string err;
            if (!EnsureServer(out err))
            {
                MessageBox.Show("本地服务启动失败：\n" + err, "豆瓣正在听", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var cfg = ReadSettingsFromServer();
            Skin = cfg != null && Convert.ToString(cfg.ContainsKey("skin") ? cfg["skin"] : "") == "glass"
                ? SkinKind.Glass
                : SkinKind.Neumorphic;
            if (cfg != null && cfg.ContainsKey("overlayScale"))
            {
                _scale = NormalizeScale(ToDouble(cfg["overlayScale"]));
            }
            ApplyPalette(Skin);
            Trace("settings: skin=" + Skin + " scale=" + _scale + " cfg=" + (cfg == null ? "null" : "ok"));

            LoadUi();
            if (_win == null) return false;

            if (Program.OpenSettingsRequested)
            {
                // 窗口在 LoadUi() 里就已经 Show 了，Loaded 可能早就过去；
                // 直接挂 Loaded 会时灵时不灵（管理.bat 打不开设置就是这么来的）。
                // 排到消息队列里，等窗口真正显示出来再开设置页。
                _win.Dispatcher.BeginInvoke(
                    new Action(() => SettingsWindow.ShowSingleton()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _timer.Tick += async (s, e) => await PollAsync();
            _timer.Start();

            PollAsync();
            return true;
        }

        static Dictionary<string, object> ReadSettingsFromServer()
        {
            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    wc.Proxy = null;
                    // 必须自己按 UTF-8 解：WebClient.DownloadString 在这台机器上
                    // 会用系统 ANSI 代码页解码，中文「已设置」会把后面的引号吃掉，JSON 直接坏掉
                    var bytes = wc.DownloadData(BaseUrl + "/api/settings");
                    var json = Encoding.UTF8.GetString(bytes);
                    var ser = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                    return ser.Deserialize<Dictionary<string, object>>(json);
                }
            }
            catch (Exception ex)
            {
                Trace("read settings failed: " + ex.Message);
            }
            return null;
        }

        static double NormalizeScale(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return 1.0;
            if (v < 0.85) return 0.85;
            if (v > 1.4) return 1.4;
            return v;
        }

        /// <summary>切换皮肤：换 XAML 重建窗口，位置保持</summary>
        public void SetSkin(SkinKind skin, bool persist)
        {
            if (persist) SaveSkinAsync(skin);
            if (skin == Skin)
            {
                if (_win != null) _win.Activate();
                return;
            }

            Skin = skin;
            ApplyPalette(skin);
            if (_win == null) return;

            double right = _win.Left + _win.ActualWidth;
            double bottom = _win.Top + _win.ActualHeight;
            bool visible = _win.IsVisible;

            _reloading = true;
            try { _win.Close(); } catch { }
            _reloading = false;
            _win = null;

            _restoreLeft = right;
            _restoreBottom = bottom;
            LoadUi();
            if (_win == null) return;
            _win.Show();
            if (!visible) _win.Hide();
            RefreshNow();
        }

        async void SaveSkinAsync(SkinKind skin)
        {
            try
            {
                var body = "{\"skin\":\"" + (skin == SkinKind.Glass ? "glass" : "neumorphic") + "\"}";
                await _http.PostAsync("/api/settings",
                    new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        double _restoreLeft = double.NaN;
        double _restoreBottom = double.NaN;
        bool _reloading;

        void LoadUi()
        {
            var file = Skin == SkinKind.Glass ? "overlay-glass.xaml" : "overlay.xaml";
            var xamlPath = Path.Combine(AppDir, "overlay", file);
            if (!File.Exists(xamlPath))
            {
                MessageBox.Show("缺少界面文件：" + xamlPath, "豆瓣正在听");
                return;
            }

            using (var fs = File.OpenRead(xamlPath))
            {
                _win = (Window)XamlReader.Load(fs);
            }

            _card = (Border)_win.FindName("Card");
            _coverShell = (Border)_win.FindName("CoverShell");
            _btnSync = (Border)_win.FindName("BtnSync");
            _progressTrack = (Border)_win.FindName("ProgressTrack");
            _progressFill = (Border)_win.FindName("ProgressFill");
            _btnManage = (Border)_win.FindName("BtnManage");
            _mineRow = (Border)_win.FindName("MineRow");
            _btnLater = (Border)_win.FindName("BtnLater");

            _appName = (TextBlock)_win.FindName("AppName");
            _trackText = (TextBlock)_win.FindName("TrackText");
            _status = (TextBlock)_win.FindName("StatusText");
            _album = (TextBlock)_win.FindName("AlbumTitle");
            _artist = (TextBlock)_win.FindName("ArtistLine");
            _scoreNum = (TextBlock)_win.FindName("ScoreNum");
            _scoreUnit = (TextBlock)_win.FindName("ScoreUnit");
            _votes = (TextBlock)_win.FindName("VotesLine");
            _hint = (TextBlock)_win.FindName("HintText");
            _coverLetter = (TextBlock)_win.FindName("CoverLetter");
            _syncText = (TextBlock)_win.FindName("BtnSyncText");
            _laterText = (TextBlock)_win.FindName("BtnLaterText");

            _cover = (Image)_win.FindName("CoverImage");
            _dbStars = (Grid)_win.FindName("DbStars");
            _dist = (Grid)_win.FindName("DistBar");
            _myStars = (StackPanel)_win.FindName("MyStars");
            _comment = (TextBox)_win.FindName("CommentBox");
            _pulse = (System.Windows.Shapes.Ellipse)_win.FindName("Pulse");

            _myStars.Width = 5 * 17;
            BuildMyStars(0);
            _coverUrl = null; // 新窗口需要重新加载封面

            // 缩放：内容整体 LayoutTransform，窗口宽度跟着变；Ctrl+滚轮 / 右键菜单可调
            _root = _win.Content as FrameworkElement;
            ApplyScale();

            // 拖动
            AttachDrag(_win.FindName("Header") as UIElement);
            AttachDrag(_coverShell);
            AttachDrag(_album);
            AttachDrag(_artist);

            // 交互
            // 「⋯」统一弹菜单（原来直接开设置窗，导致点了永远看不到菜单）
            AttachHover(_btnManage, 8);
            AttachHover(_btnSync, 8);
            AttachHover(_btnLater, 8);
            _btnSync.MouseLeftButtonUp += async (s, e) => await SyncAsync();
            if (_btnLater != null) _btnLater.MouseLeftButtonUp += async (s, e) => await SaveLaterAsync();

            // Ctrl + 滚轮缩放
            _win.PreviewMouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                ChangeScale(e.Delta > 0 ? 0.05 : -0.05);
                e.Handled = true;
            };

            _comment.TextChanged += (s, e) =>
            {
                if (_comment.IsFocused) _commentDirty = true;
            };

            _comment.LostFocus += async (s, e) => { if (_commentDirty) await SaveCommentAsync(); };
            _comment.PreviewKeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter) { _commentDirty = true; await SaveCommentAsync(); _win.Focus(); }
            };

            _win.ContextMenu = BuildMenu();

            // 右上角「⋯」也能打开菜单（此前只绑了窗口右键，按钮点了没反应）
            if (_btnManage != null)
            {
                _btnManage.MouseLeftButtonUp += (s2, e2) =>
                {
                    e2.Handled = true;
                    // 延后一拍再弹：直接在 MouseUp 里弹的话，菜单会出现在鼠标下方，
                    // 抬起事件还没走完就落到第一项上（曾把「历史与设置」直接点开）。
                    // 用定时器延后 150ms：必须等这次鼠标点击（按下+抬起）彻底走完，
                    // 否则菜单一出现就会被同一次点击当成「点了第一项」。
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    timer.Tick += (s3, e3) =>
                    {
                        timer.Stop();
                        var menu = _win.ContextMenu;
                        if (menu == null) return;
                        menu.PlacementTarget = _btnManage;
                        menu.Placement = PlacementMode.Bottom;
                        menu.VerticalOffset = 6;
                        menu.HorizontalOffset = -200;   // 靠左展开，别跑出屏幕
                        menu.IsOpen = true;
                    };
                    timer.Start();
                };
            }
            _win.MouseEnter += (s, e) => _lastInteraction = DateTime.Now;

            _win.Loaded += (s, e) =>
            {
                var wa = SystemParameters.WorkArea;
                if (!double.IsNaN(_restoreLeft))
                {
                    // 换皮肤重建窗口：贴回原来的右下角
                    _homeRight = _restoreLeft;
                    _homeBottom = _restoreBottom;
                    _homeLeft = double.NaN;
                    _restoreLeft = double.NaN;
                    _restoreBottom = double.NaN;
                }
                else if (double.IsNaN(_homeRight) && double.IsNaN(_homeLeft))
                {
                    _homeRight = wa.Right - 6;
                    _homeBottom = wa.Bottom - 6;
                }
                ApplyHome();
            };

            _win.SizeChanged += (s, e) => ApplyHome();

            _win.Closed += (s, e) =>
            {
                if (_reloading) return;
                Shutdown();
            };

            // 入场动画
            _win.Opacity = 0;
            _win.Loaded += (s, e) =>
            {
                var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                _win.BeginAnimation(Window.OpacityProperty, anim);
            };
       
            _win.Show();
        }

        void StartSettingsListener()
        {
            var th = new System.Threading.Thread(() =>
            {
                var handles = new System.Threading.WaitHandle[] { _settingsSignal, _loginSignal, _todoSignal };
                while (true)
                {
                    int idx;
                    try { idx = System.Threading.WaitHandle.WaitAny(handles); } catch (Exception ex) { Trace("WaitAny failed: " + ex.Message); return; }
                    Trace("signal idx=" + idx);
                    if (_win == null) continue;
                    bool login = idx == 1;
                    bool todo = idx == 2;
                    _win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (todo) SettingsWindow.ShowSingleton("todo");
                        else SettingsWindow.ShowSingleton(login);
                    }));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        double _homeLeft = double.NaN;
        double _homeRight = double.NaN;
        double _homeBottom = double.NaN;

        /// <summary>贴右下角。窗口变宽/放大时保持右边缘不动，不会跑出屏幕。</summary>
        void ApplyHome()
        {
            if (_win == null) return;
            if (!double.IsNaN(_homeBottom)) _win.Top = _homeBottom - _win.ActualHeight;
            if (!double.IsNaN(_homeRight)) _win.Left = _homeRight - _win.ActualWidth;
            else if (!double.IsNaN(_homeLeft)) _win.Left = _homeLeft;
        }

        /* ------------------------------- 缩放 ------------------------------- */

        // 设计稿宽度：460（含 18 的外边距）
        const double BaseWidth = 460;

        void ApplyScale()
        {
            if (_root != null)
            {
                _root.LayoutTransform = new ScaleTransform(_scale, _scale);
            }
            if (_win != null)
            {
                _win.Width = Math.Round(BaseWidth * _scale);
            }
            ApplyHome();
        }

        void ChangeScale(double delta)
        {
            double next = NormalizeScale(Math.Round((_scale + delta) * 100) / 100.0);
            if (Math.Abs(next - _scale) < 0.001) return;
            _scale = next;
            ApplyScale();
            ScheduleScaleSave();
            ShowHint("窗口大小 " + Math.Round(_scale * 100) + "%", Faint);
        }

        void ScheduleScaleSave()
        {
            if (_scaleSaveTimer == null)
            {
                _scaleSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _scaleSaveTimer.Tick += async (s, e) =>
                {
                    _scaleSaveTimer.Stop();
                    await SaveScaleAsync(_scale);
                };
            }
            _scaleSaveTimer.Stop();
            _scaleSaveTimer.Start();
        }

        async System.Threading.Tasks.Task SaveScaleAsync(double scale)
        {
            try
            {
                var body = "{\"overlayScale\":" + scale.ToString("0.##", CultureInfo.InvariantCulture) + "}";
                await _http.PostAsync("/api/settings",
                    new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        void RefreshNow()
        {
            PollAsync();
        }

        static string AppDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        /* -------------------------------- 服务 -------------------------------- */

        bool EnsureServer(out string error)
        {
            error = null;
            if (Ping()) return true;

            string node = FindNode();
            if (node == null)
            {
                error = "没有找到 node.exe，请先安装 Node.js";
                return false;
            }

            var script = Path.Combine(AppDir, "server", "index.js");
            if (!File.Exists(script))
            {
                error = "找不到服务端文件：" + script;
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = node,
                    Arguments = "\"" + script + "\"",
                    WorkingDirectory = AppDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                _serverProc = Process.Start(psi);

                // 必须把子进程输出读走：管道写满后 node 会阻塞在 stdout 上，
                // 表现为「服务莫名其妙不响应了」。这里转存到 data/server.log。
                try
                {
                    Directory.CreateDirectory(Path.Combine(AppDir, "data"));
                    _serverLog = new StreamWriter(Path.Combine(AppDir, "data", "server.log"), true)
                    {
                        AutoFlush = true
                    };
                    _serverProc.OutputDataReceived += (s2, e2) => WriteServerLog(e2.Data);
                    _serverProc.ErrorDataReceived += (s2, e2) => WriteServerLog(e2.Data);
                    _serverProc.BeginOutputReadLine();
                    _serverProc.BeginErrorReadLine();
                }
                catch { }
            }
            catch (Exception ex)
            {
                error = "启动 node 失败：" + ex.Message;
                return false;
            }

            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(250);
                if (Ping()) return true;
            }

            error = "服务在 10 秒内没有就绪（端口 4590 可能被占用）";
            return false;
        }

        void WriteServerLog(string line)
        {
            if (string.IsNullOrEmpty(line) || _serverLog == null) return;
            try { _serverLog.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + line); }
            catch { }
        }

        bool Ping()
        {
            try
            {
                using (var c = new System.Net.WebClient())
                {
                    c.Proxy = null;
                    c.DownloadString(BaseUrl + "/api/state");
                    return true;
                }
            }
            catch { return false; }
        }

        static string FindNode()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;

            try
            {
                var psi = new ProcessStartInfo("where", "node")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (var line in outp.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(t)) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        /* -------------------------------- 轮询 -------------------------------- */

        async Task PollAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var json = await _http.GetStringAsync("/api/state");
                var ser = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                var root = ser.Deserialize<Dictionary<string, object>>(json);
                _pollFailures = 0;
                Apply(root);
            }
            catch (Exception)
            {
                _pollFailures++;
                SetOffline();
                TryRecoverServer();
            }
            finally
            {
                _busy = false;
            }
        }

        void Apply(Dictionary<string, object> root)
        {
            var player = GetDict(root, "player");
            var match = GetDict(root, "match");
            var my = GetDict(root, "myRating");

            if (player == null)
            {
                _appName.Text = "等待播放";
                if (_trackText != null) _trackText.Text = "";
                _status.Text = "";
                _album.Text = "还没有在放歌";
                _artist.Text = "在 Apple Music 里播放一张专辑即可";
                _scoreNum.Text = "—";
                _scoreNum.Foreground = Faint;
                _votes.Text = "豆瓣评分";
                _scoreUnit.Visibility = Visibility.Collapsed;
                _mineRow.Visibility = Visibility.Collapsed;
                SetDbStars(0);
                ClearDist();
                _hint.Visibility = Visibility.Collapsed;
                SetProgress(0);
                FlushPendingComment();
                _subjectId = null;
                _commentSubjectId = null;
                _myRating = 0;
                BuildMyStars(0);
                SetMineEnabled(false);
                SetLaterState(false, false);
                SetLaterEnabled(false);
                return;
            }

            _mineRow.Visibility = Visibility.Visible;
            _scoreUnit.Visibility = Visibility.Visible;
            SetLaterEnabled(true);
            _appName.Text = GetStr(player, "appName") ?? "未知来源";
            var st = GetStr(player, "status");
            _status.Text = st == "Playing" ? "正在播放" : st == "Paused" ? "已暂停" : "";
            _pulse.Fill = st == "Playing" ? Mint : PulseIdle;

            var subject = match != null ? GetDict(match, "subject") : null;

            // 标题 / 艺人
            string albumText = subject != null ? GetStr(subject, "title") : null;
            if (string.IsNullOrEmpty(albumText))
                albumText = GetStr(player, "album");
            if (string.IsNullOrEmpty(albumText))
                albumText = GetStr(player, "title");
            _album.Text = string.IsNullOrEmpty(albumText) ? "未知曲目" : albumText;

            // 顶栏「播放器 · 当前曲目」：曲目和专辑同名时不重复显示
            if (_trackText != null)
            {
                var cur = GetStr(player, "title");
                bool sameAsAlbum = !string.IsNullOrEmpty(cur) && !string.IsNullOrEmpty(albumText) &&
                                   SameName(cur, albumText);
                _trackText.Text = string.IsNullOrEmpty(cur) || sameAsAlbum ? "" : "· " + cur;
            }

            string artistText = subject != null ? GetStr(subject, "artistText") : null;
            if (string.IsNullOrEmpty(artistText)) artistText = GetStr(player, "artist");
            _artistName = artistText ?? "";
            string date = subject != null ? GetStr(subject, "date") : null;
            string genre = subject != null ? GetStr(subject, "genre") : null;
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(artistText)) bits.Add(artistText);
            if (!string.IsNullOrEmpty(date) && date.Length >= 4) bits.Add(date.Substring(0, 4));
            if (!string.IsNullOrEmpty(genre)) bits.Add(genre);
            _artist.Text = string.Join(" · ", bits);

            // 封面
            var coverUrl = subject != null ? GetStr(subject, "cover") : null;
            SetCover(coverUrl);

            // 豆瓣评分
            double score = subject != null ? GetDbl(subject, "score") : 0;
            double votes = subject != null ? GetDbl(subject, "votes") : 0;
            if (score > 0)
            {
                _scoreNum.Text = score.ToString("0.0", CultureInfo.InvariantCulture);
                _scoreNum.Foreground = ScoreInk;
                _votes.Text = FormatVotes(votes);
                SetDbStars(score);
                RenderDist(subject);
            }
            else
            {
                _scoreNum.Text = match != null ? "--" : "···";
                _scoreNum.Foreground = Faint;
                bool resolvingNow = GetBool(root, "resolving");
                _votes.Text = match != null ? "豆瓣暂无评分" : (resolvingNow ? "正在查豆瓣…" : "豆瓣没有收录");
                SetDbStars(0);
                ClearDist();
            }

            // 我的评分
            var newSubjectId = subject != null ? (int?)GetDbl(subject, "id") : null;
            bool switching = newSubjectId != _commentSubjectId;

            // 换专辑时，先把还没保存的评语写回它真正所属的那张，
            // 否则失焦保存会把上一张的评语写到新专辑上（曾经就出过这个错）。
            if (switching) FlushPendingComment();

            _subjectId = newSubjectId;
            _myRating = my != null ? (int)GetDbl(my, "rating") : 0;
            BuildMyStars(_myRating);

            if (switching)
            {
                _comment.Text = my != null ? (GetStr(my, "comment") ?? "") : "";
                _commentDirty = false;
                _commentSubjectId = newSubjectId;
            }
            else if (!_commentDirty && !_comment.IsFocused)
            {
                _comment.Text = my != null ? (GetStr(my, "comment") ?? "") : "";
            }
            bool synced = my != null && GetBool(my, "synced");
            SetSyncState(synced, false);
            SetMineEnabled(_subjectId.HasValue && _myRating > 0);

            // 待评分状态：同一张已经「稍后再评」过就显示成已加入
            SetLaterState(GetBool(root, "currentTodo"), false);

            // 提示
            bool resolving = GetBool(root, "resolving");
            if (match == null && !resolving && !string.IsNullOrEmpty(GetStr(player, "title")))
            {
                _hint.Text = "豆瓣没找到这张 · 右键「手动绑定」";
                _hint.Visibility = Visibility.Visible;
            }
            else
            {
                _hint.Visibility = Visibility.Collapsed;
            }

            // 进度
            double pos = GetDbl(player, "position");
            double dur = GetDbl(player, "duration");
            double ts = GetDbl(player, "ts");
            if (st == "Playing" && ts > 0)
            {
                pos += (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts) / 1000.0;
            }
            SetProgress(dur > 0 ? Math.Max(0, Math.Min(1, pos / dur)) : 0);
        }

        /// <summary>本地服务掉线时自动重启（最多每 12 秒试一次）</summary>
        void TryRecoverServer()
        {
            if (_recovering) return;
            if (_pollFailures < 3) return;
            if ((DateTime.UtcNow - _lastRecover).TotalSeconds < 12) return;

            _recovering = true;
            _lastRecover = DateTime.UtcNow;
            Trace("server offline, trying to restart");
            Task.Run(() =>
            {
                try
                {
                    string err;
                    EnsureServer(out err);
                }
                catch { }
                finally
                {
                    _recovering = false;
                    _pollFailures = 0;
                }
            });
        }

        void SetOffline()
        {
            _appName.Text = "本地服务未连接";
            _status.Text = "";
            _album.Text = "等待服务…";
            _artist.Text = "正在尝试重新连接 127.0.0.1:4590";
        }

        /* -------------------------------- 视图 -------------------------------- */

        void SetCover(string url)
        {
            if (url == _coverUrl) return;
            _coverUrl = url;

            if (string.IsNullOrEmpty(url))
            {
                _cover.Source = null;
                _coverLetter.Text = string.IsNullOrEmpty(_album.Text) ? "♪" : _album.Text.Substring(0, 1);
                _coverLetter.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(BaseUrl + "/api/cover?u=" + Uri.EscapeDataString(url));
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.DownloadCompleted += (s, e) =>
                {
                    _coverLetter.Visibility = Visibility.Collapsed;
                };
                bmp.DownloadFailed += (s, e) =>
                {
                    _coverLetter.Visibility = Visibility.Visible;
                };
                _cover.Source = bmp;
            }
            catch
            {
                _cover.Source = null;
                _coverLetter.Visibility = Visibility.Visible;
            }
        }

        const string StarGeometry =
            "M12,2 L14.9,8.6 L22,9.3 L16.6,14 L18.2,21 L12,17.3 L5.8,21 L7.4,14 L2,9.3 L9.1,8.6 Z";

        static System.Windows.Shapes.Path MakeStar(double size, Brush fill)
        {
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(StarGeometry),
                Fill = fill,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        static StackPanel StarStrip(int count, double slot, double size, Brush brush)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Width = slot * count };
            for (int i = 0; i < count; i++)
            {
                var host = new Grid { Width = slot, Height = size };
                host.Children.Add(MakeStar(size, brush));
                sp.Children.Add(host);
            }
            return sp;
        }

        void SetDbStars(double score10)
        {
            double frac = Math.Max(0, Math.Min(5, score10 / 2.0)) / 5.0;
            const double slot = 15;
            const double size = 12;
            double total = slot * 5;

            _dbStars.Children.Clear();
            _dbStars.Width = total;
            _dbStars.Height = size;

            _dbStars.Children.Add(StarStrip(5, slot, size, StarOff));
            if (frac <= 0) return;

            var clip = new Border
            {
                Width = total * frac,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            clip.Child = StarStrip(5, slot, size, DbStarOn);
            _dbStars.Children.Add(clip);
        }

        void PaintMyStars(int n)
        {
            for (int k = 0; k < _myStars.Children.Count; k++)
            {
                var host = _myStars.Children[k] as Grid;
                if (host == null) continue;
                var star = host.Children.Count > 0 ? host.Children[0] as System.Windows.Shapes.Path : null;
                if (star != null) star.Fill = k < n ? Accent : StarOff;
            }
        }

        void BuildMyStars(int rating)
        {
            _myStars.Children.Clear();
            const double slot = 18;
            const double size = 15;
            _myStars.Width = slot * 5;
            _myStars.Height = size;

            for (int i = 1; i <= 5; i++)
            {
                var host = new Grid
                {
                    Width = slot,
                    Height = size,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                host.Children.Add(MakeStar(size, i <= rating ? Accent : StarOff));

                int value = i;
                host.MouseEnter += (s, e) =>
                {
                    _lastInteraction = DateTime.Now;
                    PaintMyStars(value);
                };
                host.MouseLeave += (s, e) => PaintMyStars(_myRating);
                host.MouseLeftButtonUp += async (s, e) => await RateAsync(value);
                _myStars.Children.Add(host);
            }
        }

        void RenderDist(Dictionary<string, object> subject)
        {
            _dist.Children.Clear();
            _dist.ColumnDefinitions.Clear();
            _dist.Background = null;

            var raw = subject != null && subject.ContainsKey("distribution") ? subject["distribution"] : null;
            var vals = new List<double>();
            var seq = raw as System.Collections.IEnumerable;
            if (seq != null && !(raw is string))
            {
                foreach (var o in seq) vals.Add(ToDouble(o));
            }
            if (vals.Count != 5) return;

            for (int i = 0; i < 5; i++)
            {
                double pct = vals[i];
                _dist.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max(pct, 0.4), GridUnitType.Star)
                });

                var bd = new Border
                {
                    Background = DistBrush(i),
                    Margin = new Thickness(0, 0, i < 4 ? 3 : 0, 0)
                };
                Grid.SetColumn(bd, i);
                _dist.Children.Add(bd);
            }
        }

        static Brush DistBrush(int index)
        {
            byte alpha;
            switch (index)
            {
                case 0: alpha = 0xE6; break;
                case 1: alpha = 0x8C; break;
                case 2: alpha = 0x4D; break;
                case 3: alpha = 0x26; break;
                default: alpha = 0x14; break;
            }
            var c = ((SolidColorBrush)DistBase).Color;
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }

        void ClearDist()
        {
            _dist.Children.Clear();
            _dist.ColumnDefinitions.Clear();
        }

        void SetProgress(double ratio)
        {
            double w = _progressTrack.ActualWidth;
            if (w <= 0) w = 400;
            _progressFill.Width = Math.Max(0, Math.Min(1, ratio)) * w;
        }

        void SetMineEnabled(bool enabled)
        {
            _syncEnabled = enabled;
            _myStars.Opacity = _subjectId.HasValue ? 1 : 0.55;
            RefreshSyncVisual();
        }

        void SetSyncState(bool synced, bool syncing)
        {
            _synced = synced;
            _syncing = syncing;
            RefreshSyncVisual();
        }

        /// <summary>同步按钮的四种状态：不可用 / 可提交 / 提交中 / 已同步</summary>
        void RefreshSyncVisual()
        {
            if (_syncing)
            {
                _syncText.Text = "同步中…";
                _btnSync.Background = Base;
                _syncText.Foreground = Dim;
                _btnSync.IsEnabled = false;
                return;
            }
            if (!_syncEnabled)
            {
                _syncText.Text = "同步";
                _btnSync.Background = Base;
                _syncText.Foreground = Faint;
                _btnSync.IsEnabled = false;
                return;
            }
            if (_synced)
            {
                _syncText.Text = "已同步";
                _btnSync.Background = Base;
                _syncText.Foreground = Mint;
            }
            else
            {
                _syncText.Text = "同步";
                _btnSync.Background = Accent;
                _syncText.Foreground = OnAccent;
            }
            _btnSync.IsEnabled = true;
        }

        /* ------------------------------ 稍后再评 ------------------------------ */

        void SetLaterEnabled(bool enabled)
        {
            if (_btnLater == null) return;
            _btnLater.IsEnabled = enabled;
            _btnLater.Opacity = enabled ? 1 : 0.55;
        }

        /// <summary>稍后再评按钮的三种状态：可保存 / 保存中 / 已在待评分里</summary>
        void SetLaterState(bool inTodo, bool busy)
        {
            _inTodo = inTodo;
            _laterBusy = busy;
            if (_btnLater == null || _laterText == null) return;

            _btnLater.Background = Subtle;
            if (busy)
            {
                _laterText.Text = "保存中…";
                _laterText.Foreground = Dim;
                _btnLater.IsEnabled = false;
                return;
            }
            _btnLater.IsEnabled = true;
            if (inTodo)
            {
                _laterText.Text = "已在待评";
                _laterText.Foreground = Mint;
            }
            else
            {
                _laterText.Text = "稍后再评";
                _laterText.Foreground = Dim;
            }
        }

        async Task SaveLaterAsync()
        {
            if (_laterBusy || _inTodo) return;
            if (_album == null || string.IsNullOrEmpty(_album.Text)) return;

            SetLaterState(_inTodo, true);
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "title", _album.Text ?? "" },
                    { "artist", _artistName }
                };
                if (_subjectId.HasValue) payload["subjectId"] = _subjectId.Value;

                var json = new JavaScriptSerializer().Serialize(payload);
                var resp = await _http.PostAsync("/api/todo",
                    new StringContent(json, Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();
                if (text.Contains("\"ok\":true"))
                {
                    SetLaterState(true, false);
                    ShowHint("已放进「待评分」，随时去管理里补星", Mint);
                }
                else
                {
                    SetLaterState(false, false);
                    ShowHint(ExtractError(text) ?? "保存失败", Coral);
                }
            }
            catch (Exception ex)
            {
                SetLaterState(false, false);
                ShowHint("保存失败：" + ex.Message, Coral);
            }
        }

        /* -------------------------------- 动作 -------------------------------- */

        async Task RateAsync(int value)
        {
            if (!_subjectId.HasValue) return;
            _myRating = value;
            PaintMyStars(value);
            _synced = false; // 改了分就不再是「已同步」
            SetMineEnabled(true);
            await PostJsonAsync("/api/rating", new Dictionary<string, object>
            {
                { "subjectId", _subjectId.Value },
                { "rating", value },
                { "comment", _comment.Text ?? "" }
            });
        }

        /// <summary>把「已改但还没保存」的评语写回它所属的那张专辑</summary>
        void FlushPendingComment()
        {
            if (!_commentDirty || !_commentSubjectId.HasValue) return;
            var id = _commentSubjectId.Value;
            var text = _comment.Text ?? "";
            var rating = _myRating;
            _commentDirty = false;
            var task = PostJsonAsync("/api/rating", new Dictionary<string, object>
            {
                { "subjectId", id },
                { "rating", rating },
                { "comment", text }
            });
        }

        async Task SaveCommentAsync()
        {
            var id = _commentSubjectId ?? _subjectId;
            if (!id.HasValue) return;
            _commentDirty = false;
            await PostJsonAsync("/api/rating", new Dictionary<string, object>
            {
                { "subjectId", id.Value },
                { "rating", _myRating },
                { "comment", _comment.Text ?? "" }
            });
        }

        async Task SyncAsync()
        {
            if (!_subjectId.HasValue || _syncing) return;
            var subjectId = _subjectId.Value;
            var rating = _myRating;
            var comment = _comment.Text ?? "";

            if (rating <= 0)
            {
                ShowHint("先点星打个分再同步", Coral);
                return;
            }

            SetSyncState(false, true);
            try
            {
                // 关键：先把「星 + 评语」明确写进本地，再带着它们同步。
                // 只发 subjectId 的话，服务端读到的可能是还没保存的旧记录
                //（点同步会让评论框失焦，保存请求和同步请求会抢跑，评语就丢了）。
                var save = new Dictionary<string, object>
                {
                    { "subjectId", subjectId },
                    { "rating", rating },
                    { "comment", comment }
                };
                await PostJsonAsync("/api/rating", save);
                _commentDirty = false;
                _commentSubjectId = subjectId;

                var payload = new Dictionary<string, object>
                {
                    { "subjectId", subjectId },
                    { "rating", rating },
                    { "comment", comment }
                };
                var json = new JavaScriptSerializer().Serialize(payload);
                var resp = await _http.PostAsync("/api/sync",
                    new StringContent(json, Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();
                bool ok = text.Contains("\"ok\":true");
                if (ok)
                {
                    SetSyncState(true, false);
                    ShowHint("已写入豆瓣", Mint);
                }
                else
                {
                    SetSyncState(false, false);
                    ShowHint(ExtractError(text) ?? "同步失败（检查豆瓣登录 Cookie）", Coral);
                }
            }
            catch (Exception ex)
            {
                SetSyncState(false, false);
                ShowHint("同步失败：" + ex.Message, Coral);
            }
        }

        async Task PostJsonAsync(string path, Dictionary<string, object> payload)
        {
            try
            {
                var json = new JavaScriptSerializer().Serialize(payload);
                await _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        void ShowHint(string text, Brush color)
        {
            _hint.Text = text;
            _hint.Foreground = color;
            _hint.Visibility = Visibility.Visible;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                _hint.Visibility = Visibility.Collapsed;
                _hint.Foreground = Faint;
            };
            t.Start();
        }

        void OpenManage()
        {
            SettingsWindow.ShowSingleton();
        }

        const string MenuXaml = @"
<ContextMenu xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
             xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
             Background='Transparent' BorderThickness='0' HasDropShadow='False'>
  <ContextMenu.Resources>
    <Style TargetType='MenuItem'>
      <Setter Property='Foreground' Value='{{TEXT}}' />
      <Setter Property='FontSize' Value='12' />
      <Setter Property='Padding' Value='12,7' />
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='MenuItem'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='8' Padding='{TemplateBinding Padding}'>
              <ContentPresenter ContentSource='Header' VerticalAlignment='Center' />
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsHighlighted' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='{{HOVER}}' />
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType='Separator'>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Separator'>
            <Border Height='1' Background='{{LINE}}' Margin='10,5' />
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </ContextMenu.Resources>
  <ContextMenu.Template>
    <ControlTemplate TargetType='ContextMenu'>
      <Border Background='{{BG}}' BorderBrush='{{BORDER}}' BorderThickness='1'
              CornerRadius='16' Padding='6' Margin='12'>
        <Border.Effect>
          <DropShadowEffect Color='{{SHADOW}}' BlurRadius='22' ShadowDepth='7' Direction='315' Opacity='0.85' />
        </Border.Effect>
        <StackPanel IsItemsHost='True' />
      </Border>
    </ControlTemplate>
  </ContextMenu.Template>
  <MenuItem x:Name='MiManage' Header='历史与设置…' />
  <MenuItem x:Name='MiTodo' Header='待评分…' />
  <MenuItem x:Name='MiSync' Header='把评分写入豆瓣' />
  <Separator />
  <MenuItem x:Name='MiTop' Header='窗口置顶' />
  <MenuItem x:Name='MiZoomIn' Header='放大' />
  <MenuItem x:Name='MiZoomOut' Header='缩小' />
  <MenuItem x:Name='MiZoomReset' Header='恢复默认大小' />
  <Separator />
  <MenuItem x:Name='MiExit' Header='退出' />
</ContextMenu>";

        ContextMenu BuildMenu()
        {
            bool glass = Skin == SkinKind.Glass;
            var xaml = MenuXaml
                .Replace("{{BG}}", glass ? "#101C30" : "#E0E5EC")
                .Replace("{{TEXT}}", glass ? "#FFFFFF" : "#3D4451")
                .Replace("{{HOVER}}", glass ? "#1E3450" : "#D3DAE4")
                .Replace("{{LINE}}", glass ? "#26FFFFFF" : "#CBD2DC")
                .Replace("{{BORDER}}", glass ? "#26FFFFFF" : "#00FFFFFF")
                .Replace("{{SHADOW}}", glass ? "#000000" : "#B8BCC2");
            var menu = (ContextMenu)XamlReader.Parse(xaml);

            var miManage = (MenuItem)menu.FindName("MiManage");
            var miTodo = (MenuItem)menu.FindName("MiTodo");
            var miSync = (MenuItem)menu.FindName("MiSync");
            var miTop = (MenuItem)menu.FindName("MiTop");
            var miZoomIn = (MenuItem)menu.FindName("MiZoomIn");
            var miZoomOut = (MenuItem)menu.FindName("MiZoomOut");
            var miZoomReset = (MenuItem)menu.FindName("MiZoomReset");
            var miExit = (MenuItem)menu.FindName("MiExit");

            miManage.Click += (s, e) => OpenManage();
            miTodo.Click += (s, e) => SettingsWindow.ShowSingleton("todo");
            miSync.Click += async (s, e) => await SyncAsync();
            miTop.Click += (s, e) =>
            {
                _win.Topmost = !_win.Topmost;
                miTop.Header = _win.Topmost ? "窗口置顶 ✓" : "窗口置顶";
            };
            miZoomIn.Click += (s, e) => ChangeScale(0.05);
            miZoomOut.Click += (s, e) => ChangeScale(-0.05);
            menu.Opened += (s, e) =>
            {
                miTop.Header = _win.Topmost ? "窗口置顶 ✓" : "窗口置顶";
            };
            miZoomReset.Click += (s, e) =>
            {
                _scale = 1.0;
                ApplyScale();
                ScheduleScaleSave();
            };
            miExit.Click += (s, e) => _win.Close();

            return menu;
        }

        /// <summary>hover 时把阴影模糊减半，模拟手靠近遮光（新拟物的交互语言）</summary>
        void AttachHover(UIElement el, double baseBlur)
        {
            var fe = el as FrameworkElement;
            if (fe == null) return;
            var eff = fe.Effect as DropShadowEffect;
            if (eff == null) return;
            el.MouseEnter += (s, e) => eff.BlurRadius = Math.Max(2, baseBlur * 0.5);
            el.MouseLeave += (s, e) => eff.BlurRadius = baseBlur;
        }

        /// <summary>child 是否在 parent 里面（用来判断点的是不是某个按钮）</summary>
        /// <summary>去掉标点与分隔符后比较，判断曲名与专辑名是不是同一张</summary>
        static bool SameName(string a, string b)
        {
            var na = OnlyWordChars(a);
            var nb = OnlyWordChars(b);
            if (na.Length == 0 || nb.Length == 0) return false;
            if (na == nb) return true;
            // 专辑名常写成「中文 / English」，包含其中之一也算
            return na.IndexOf(nb, StringComparison.Ordinal) >= 0 ||
                   nb.IndexOf(na, StringComparison.Ordinal) >= 0;
        }

        static string OnlyWordChars(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        static bool IsInside(DependencyObject child, DependencyObject parent)
        {
            if (child == null || parent == null) return false;
            var node = child;
            while (node != null)
            {
                if (node == parent) return true;
                try { node = VisualTreeHelper.GetParent(node); } catch { return false; }
            }
            return false;
        }

        void AttachDrag(UIElement el)
        {
            if (el == null) return;
            el.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;

                // 顶栏里的「⋯」是个按钮，不能被窗口拖动吃掉点击
                var src = e.OriginalSource as DependencyObject;
                if (_btnManage != null && IsInside(src, _btnManage)) return;

                try { _win.DragMove(); } catch { }
                _homeRight = _win.Left + _win.ActualWidth;
                _homeLeft = double.NaN;
                _homeBottom = _win.Top + _win.ActualHeight;
            };
        }

        /// <summary>检查更新时用：关掉本地服务并退出，好让安装包覆盖 exe</summary>
        public void ExitForUpdate()
        {
            Shutdown();
        }

        void Shutdown()
        {
            if (_timer != null) _timer.Stop();
            if (_serverProc != null && !_serverProc.HasExited)
            {
                try { _serverProc.Kill(); } catch { }
            }
            Application.Current.Shutdown();
        }

        /* ------------------------------ JSON 辅助 ------------------------------ */

        static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return null;
            return d[key] as Dictionary<string, object>;
        }

        static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return null;
            var v = d[key];
            return v == null ? null : Convert.ToString(v);
        }

        static double GetDbl(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return 0;
            return ToDouble(d[key]);
        }

        static bool GetBool(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return false;
            var v = d[key];
            if (v is bool) return (bool)v;
            return Convert.ToString(v) == "True" || Convert.ToString(v) == "true";
        }

        static double ToDouble(object v)
        {
            if (v == null) return 0;
            double d;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is decimal) return (double)(decimal)v;
            double.TryParse(Convert.ToString(v), NumberStyles.Any, CultureInfo.InvariantCulture, out d);
            return d;
        }

        static string FormatVotes(double v)
        {
            if (v >= 10000)
            {
                double w = v / 10000.0;
                return (w >= 100 ? Math.Round(w).ToString("0") : w.ToString("0.#")) + " 万人评价";
            }
            return ((int)v) + " 人评价";
        }

        static string ExtractError(string json)
        {
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (d != null && d.ContainsKey("error")) return Convert.ToString(d["error"]);
            }
            catch { }
            return null;
        }

        static Brush NewBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }

    public static class Program
    {
        public static bool OpenSettingsRequested;
        public static bool OpenLoginRequested;
        public static bool OpenTodoRequested;

        [STAThread]
        public static void Main(string[] args)
        {
            foreach (var a in args)
            {
                if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase))
                    OpenSettingsRequested = true;
                if (string.Equals(a, "--login", StringComparison.OrdinalIgnoreCase))
                    OpenLoginRequested = true;
                if (string.Equals(a, "--todo", StringComparison.OrdinalIgnoreCase))
                    OpenTodoRequested = true;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (!Overlay.Run()) return;
            app.Run();
        }
    }
}
