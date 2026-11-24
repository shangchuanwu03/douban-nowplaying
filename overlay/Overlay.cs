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
                     OnAccent, ScoreInk, DbStarOn, DistBase, DistTrack, Subtle, QuietFill,
                     Primary, OnPrimary, Done, OnDone;

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
                Primary = NewBrush(0xE9, 0xEF, 0xF9);   // 主按钮：近白胶囊 + 墨字
                OnPrimary = NewBrush(0x0B, 0x12, 0x20);
                Done = Accent;                          // 已同步：香槟金 + 深字
                OnDone = NewBrush(0x2A, 0x1D, 0x08);
                ScoreInk = NewBrush(0xE4, 0xB8, 0x63);
                DbStarOn = NewBrush(0xE4, 0xB8, 0x63);
                DistBase = NewBrush(0xE4, 0xB8, 0x63);
                DistTrack = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
                Subtle = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
                QuietFill = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                Ink = NewBrush(0x3D, 0x44, 0x51);
                Dim = NewBrush(0x5B, 0x64, 0x73);
                Faint = NewBrush(0x66, 0x70, 0x7F);
                Accent = NewBrush(0x8A, 0x5A, 0x0A);   // 香槟金（深一档，浅底上才够对比）
                Mint = NewBrush(0x8A, 0x5A, 0x0A);
                Coral = NewBrush(0xC0, 0x39, 0x2B);
                Base = NewBrush(0xE0, 0xE5, 0xEC);
                StarOff = NewBrush(0xC3, 0xC9, 0xD3);
                PulseIdle = NewBrush(0xB8, 0xBC, 0xC2);
                OnAccent = Brushes.White;
                Primary = NewBrush(0x26, 0x2C, 0x45);   // 新拟物：主按钮用墨蓝，金色只留给评分
                OnPrimary = Brushes.White;
                Done = Primary;
                OnDone = OnPrimary;
                ScoreInk = NewBrush(0x8A, 0x5A, 0x0A);
                DbStarOn = NewBrush(0x8A, 0x5A, 0x0A);
                DistTrack = new SolidColorBrush(Color.FromArgb(0x0D, 0x23, 0x29, 0x41));
                DistBase = Accent;
                Subtle = NewBrush(0xE0, 0xE5, 0xEC);
                QuietFill = new LinearGradientBrush(
                    Color.FromRgb(0xD3, 0xDA, 0xE3), Color.FromRgb(0xED, 0xF1, 0xF6), 45);
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
        Border _statusChip;
        TextBlock _trackText, _commentPh;
        Image _cover, _coverPrev;
        Grid _dbStars, _dist;
        StackPanel _myStars;
        TextBox _comment;
        System.Windows.Shapes.Ellipse _pulse, _pulseRing, _syncSpinner;
        System.Windows.Shapes.Path _syncCheck;
        System.Windows.Shapes.Rectangle _glowA, _glowB;
        FrameworkElement _metaPanel, _syncIconHost, _scoreLine, _vinyl, _coverGlow, _coverSheen;
        ScaleTransform _progressScale;
        DispatcherTimer _hintTimer;
        object _hintToken;
        object _coverToken;
        double _shownScore = -1;
        double _dbFrac = -1;
        bool _progressInited;
        bool _vinylOn;
        TranslateTransform _sheenShift;
        bool _myStarsBuilt;
        int _paintedRating = -1;
        int _pulseState = -1;
        string _trackSig;
        string _distSig;
        Border _dbClip;

        int? _subjectId;
        int _myRating;
        bool _commentDirty;
        int? _commentSubjectId;   // 评语框里的文字属于哪张专辑
        bool _syncing;
        bool _synced;
        bool _wasSyncing;
        bool _wasSynced;
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
            _statusChip = _win.FindName("StatusChip") as Border;
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
            _coverPrev = (Image)_win.FindName("CoverImagePrev");
            _dbStars = (Grid)_win.FindName("DbStars");
            _dist = (Grid)_win.FindName("DistBar");
            _myStars = (StackPanel)_win.FindName("MyStars");
            _comment = (TextBox)_win.FindName("CommentBox");
            _pulse = (System.Windows.Shapes.Ellipse)_win.FindName("Pulse");
            _pulseRing = (System.Windows.Shapes.Ellipse)_win.FindName("PulseRing");
            _metaPanel = _win.FindName("MetaPanel") as FrameworkElement;
            _scoreLine = _win.FindName("ScoreLine") as FrameworkElement;
            _commentPh = _win.FindName("CommentPlaceholder") as TextBlock;
            _syncIconHost = _win.FindName("SyncIconHost") as FrameworkElement;
            _syncSpinner = _win.FindName("SyncSpinner") as System.Windows.Shapes.Ellipse;
            _syncCheck = _win.FindName("SyncCheck") as System.Windows.Shapes.Path;
            _glowA = _win.FindName("GlowA") as System.Windows.Shapes.Rectangle;
            _glowB = _win.FindName("GlowB") as System.Windows.Shapes.Rectangle;
            _vinyl = _win.FindName("Vinyl") as FrameworkElement;
            _coverGlow = _win.FindName("CoverGlow") as FrameworkElement;
            _coverSheen = _win.FindName("CoverSheen") as FrameworkElement;

            // 进度条：宽度铺满轨道，用 ScaleX 平滑推进（不反复触发布局）
            _progressFill.Width = 0;
            _progressScale = new ScaleTransform(0, 1);
            _progressFill.RenderTransform = _progressScale;
            _progressFill.RenderTransformOrigin = new Point(0, 0.5);
            _progressTrack.SizeChanged += (s, e) =>
            {
                if (_progressFill == null) return;
                _progressFill.Width = Math.Max(0, _progressTrack.ActualWidth);
                if (!_progressInited && _progressScale != null) _progressScale.ScaleX = 0;
            };

            // 换皮肤会重建窗口：所有动画缓存都要归零，否则新控件里是空的/旧值
            _shownScore = -1;
            _dbFrac = -1;
            _dbClip = null;
            _distSig = null;
            _trackSig = null;
            _myStarsBuilt = false;
            _paintedRating = -1;
            _pulseState = -1;
            _progressInited = false;
            _wasSyncing = false;
            _wasSynced = false;
            _vinylOn = false;
            _hintToken = null;
            if (_hintTimer != null) { _hintTimer.Stop(); _hintTimer = null; }

            BuildMyStars(0);
            _coverUrl = null; // 新窗口需要重新加载封面
            _coverToken = null;
            UpdateCommentPlaceholder();

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
            Anim.Pressable(_btnManage, 1.08);
            Anim.Pressable(_btnSync, 1.04);
            if (_btnLater != null) Anim.Pressable(_btnLater, 1.04);
            if (_btnSync != null) _btnSync.ToolTip = "把评分和短评写到豆瓣";
            if (_btnLater != null) _btnLater.ToolTip = "先放进待评分，回头再补";
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
                UpdateCommentPlaceholder();
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

            // 入场：淡入 + 轻轻上浮；玻璃皮肤的两团柔光开始缓慢漂移
            _win.Opacity = 0;
            _win.Loaded += (s, e) =>
            {
                if (Anim.Enabled && _root != null)
                {
                    _root.RenderTransformOrigin = new Point(0.5, 0.5);
                    var sc = Anim.Scale(_root);
                    Anim.Animate(sc, ScaleTransform.ScaleXProperty, 0.97, 1, 420, 0, Anim.EaseOut());
                    Anim.Animate(sc, ScaleTransform.ScaleYProperty, 0.97, 1, 420, 0, Anim.EaseOut());
                    var tr = Anim.Translate(_root);
                    Anim.Animate(tr, TranslateTransform.YProperty, 16, 0, 420, 0, Anim.EaseOut());
                }
                Anim.Animate(_win, Window.OpacityProperty, 0, 1, 280, 0, Anim.EaseOut());
                if (Skin == SkinKind.Glass)
                {
                    if (_glowA != null) Anim.DriftGlow(_glowA, -0.16, 0.14, 11000);
                    if (_glowB != null) Anim.DriftGlow(_glowB, 0.14, -0.10, 13000);
                }
                StartCoverAmbient();
                if (_pulseState < 0) SetPulse(false);
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
        const double BaseWidth = 580;   // 和介绍动画里的浮窗同宽

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
                SetStatus("");
                _album.Text = "还没有在放歌";
                _artist.Text = "在 Apple Music 里放一张专辑即可";
                _scoreNum.Text = "—";
                _scoreNum.Foreground = Faint;
                _votes.Text = "豆瓣评分";
                _scoreUnit.Visibility = Visibility.Collapsed;
                if (_scoreLine != null) _scoreLine.Visibility = Visibility.Collapsed;
                if (_dist != null) _dist.Visibility = Visibility.Collapsed;
                if (_metaPanel != null) _metaPanel.VerticalAlignment = VerticalAlignment.Center;
                SetMineVisible(false);
                SetDbStars(0);
                ClearDist();
                if (_hintTimer != null) { _hintTimer.Stop(); _hintTimer = null; }
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
                SetPulse(false);
                if (_trackSig != null)
                {
                    _trackSig = null;
                    _shownScore = -1;
                    if (_metaPanel != null) Anim.SlideIn(_metaPanel, 16, 340, 0);
                }
                _hintToken = null;
                SetCover(null);
                if (_coverLetter != null)
                {
                    _coverLetter.Text = "♪";
                    _coverLetter.Visibility = Visibility.Collapsed;
                }
                SetVinyl(true);
                UpdateCommentPlaceholder();
                return;
            }

            _scoreUnit.Visibility = Visibility.Visible;
            if (_scoreLine != null) _scoreLine.Visibility = Visibility.Visible;
            if (_metaPanel != null) _metaPanel.VerticalAlignment = VerticalAlignment.Top;
            SetMineVisible(true);
            SetLaterEnabled(true);
            _appName.Text = GetStr(player, "appName") ?? "未知来源";
            var st = GetStr(player, "status");
            string statusText = st == "Playing" ? "正在播放" : st == "Paused" ? "已暂停" : "";
            if (_status.Text != statusText) SetStatus(statusText);
            SetPulse(st == "Playing");

            var subject = match != null ? GetDict(match, "subject") : null;

            // 标题 / 艺人
            string albumText = subject != null ? GetStr(subject, "title") : null;
            if (string.IsNullOrEmpty(albumText))
                albumText = GetStr(player, "album");
            if (string.IsNullOrEmpty(albumText))
                albumText = GetStr(player, "title");
            string newAlbum = string.IsNullOrEmpty(albumText) ? "未知曲目" : albumText;

            string artistText = subject != null ? GetStr(subject, "artistText") : null;
            if (string.IsNullOrEmpty(artistText)) artistText = GetStr(player, "artist");
            _artistName = artistText ?? "";
            string date = subject != null ? GetStr(subject, "date") : null;
            string genre = subject != null ? GetStr(subject, "genre") : null;
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(artistText)) bits.Add(artistText);
            if (!string.IsNullOrEmpty(date) && date.Length >= 4) bits.Add(date.Substring(0, 4));
            if (!string.IsNullOrEmpty(genre)) bits.Add(genre);
            string newArtist = string.Join(" · ", bits);

            var newSubjectId = subject != null ? (int?)GetDbl(subject, "id") : null;
            string sig = newAlbum + "\u0001" + newArtist + "\u0001" +
                         (newSubjectId.HasValue ? newSubjectId.Value.ToString(CultureInfo.InvariantCulture) : "-");
            bool trackChanged = sig != _trackSig;
            if (trackChanged)
            {
                _trackSig = sig;
                _shownScore = -1;     // 新专辑：分数从 0 滚上去
                _distSig = null;
            }

            if (_album.Text != newAlbum) _album.Text = newAlbum;
            if (_artist.Text != newArtist) _artist.Text = newArtist;
            if (trackChanged && _metaPanel != null) Anim.SlideIn(_metaPanel, 16, 340, 10);

            // 顶栏「播放器 · 当前曲目」：曲目和专辑同名时不重复显示
            if (_trackText != null)
            {
                var cur = GetStr(player, "title");
                bool sameAsAlbum = !string.IsNullOrEmpty(cur) && !string.IsNullOrEmpty(albumText) &&
                                   SameName(cur, albumText);
                string line = string.IsNullOrEmpty(cur) || sameAsAlbum ? "" : "· " + cur;
                if (_trackText.Text != line) _trackText.Text = line;
            }

            // 封面：换专辑时交叉淡入
            var coverUrl = subject != null ? GetStr(subject, "cover") : null;
            SetCover(coverUrl);

            // 豆瓣评分：数字滚动 + 星星/分布条生长
            double score = subject != null ? GetDbl(subject, "score") : 0;
            double votes = subject != null ? GetDbl(subject, "votes") : 0;
            if (score > 0)
            {
                _scoreNum.Foreground = ScoreInk;
                _scoreNum.ToolTip = "豆瓣评分 " + score.ToString("0.0", CultureInfo.InvariantCulture) +
                                    " · " + FormatVotes(votes);
                if (Math.Abs(score - _shownScore) > 0.001)
                {
                    double from = _shownScore > 0 ? _shownScore : 0;
                    Anim.CountTo(_scoreNum, from, score, "0.0", 520);
                    Anim.Pop(_scoreNum, 1.0, 1.06, 320);
                    _shownScore = score;
                }
                string vt = FormatVotes(votes);
                if (_votes.Text != vt) _votes.Text = vt;
                SetDbStars(score);
                RenderDist(subject);
            }
            else
            {
                _shownScore = -1;
                _scoreNum.Text = match != null ? "--" : "···";
                _scoreNum.Foreground = Faint;
                _scoreNum.ToolTip = null;
                bool resolvingNow = GetBool(root, "resolving");
                string vt = match != null ? "豆瓣暂无评分" : (resolvingNow ? "正在查豆瓣…" : "豆瓣没有收录");
                if (_votes.Text != vt) _votes.Text = vt;
                SetDbStars(0);
                ClearDist();
            }

            // 我的评分
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
                string comment = my != null ? (GetStr(my, "comment") ?? "") : "";
                if (_comment.Text != comment) _comment.Text = comment;
            }
            UpdateCommentPlaceholder();

            bool synced = my != null && GetBool(my, "synced");
            if (!_syncing) SetSyncState(synced, false);
            SetMineEnabled(_subjectId.HasValue && _myRating > 0);

            // 待评分状态：同一张已经「稍后再评」过就显示成已加入
            if (!_laterBusy) SetLaterState(GetBool(root, "currentTodo"), false);

            // 提示
            bool resolving = GetBool(root, "resolving");
            if (match == null && !resolving && !string.IsNullOrEmpty(GetStr(player, "title")))
            {
                string msg = "豆瓣没找到这张 · 右键「手动绑定」";
                if (_hint.Text != msg || _hint.Visibility != Visibility.Visible)
                {
                    _hintToken = null;
                    _hint.Text = msg;
                    _hint.Foreground = Dim;
                    _hint.Visibility = Visibility.Visible;
                    Anim.SlideIn(_hint, 6, 200, 0);
                }
            }
            else if (_hintTimer == null)
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
            SetStatus("");
            _album.Text = "等待服务…";
            _artist.Text = "正在尝试重新连接 127.0.0.1:4590";
        }

        /* -------------------------------- 视图 -------------------------------- */

        /// <summary>封面光晕呼吸 + 每隔几秒一道高光扫过封面</summary>
        void StartCoverAmbient()
        {
            if (_coverGlow != null)
            {
                _coverGlow.Opacity = 0.18;
                if (Anim.Enabled)
                {
                    var breathe = new DoubleAnimation(0.16, 0.38,
                        new Duration(TimeSpan.FromMilliseconds(3800)))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    _coverGlow.BeginAnimation(UIElement.OpacityProperty, breathe);
                }
                else
                {
                    _coverGlow.Opacity = 0.22;
                }
            }

            if (_coverSheen != null)
            {
                _sheenShift = Anim.Translate(_coverSheen);
                if (!Anim.Enabled)
                {
                    _coverSheen.Opacity = 0;
                    return;
                }
                var sweep = new DoubleAnimationUsingKeyFrames
                {
                    Duration = new Duration(TimeSpan.FromMilliseconds(5200)),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                sweep.KeyFrames.Add(new LinearDoubleKeyFrame(-170, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                sweep.KeyFrames.Add(new EasingDoubleKeyFrame(170,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), Anim.EaseOut()));
                sweep.KeyFrames.Add(new LinearDoubleKeyFrame(170,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(5200))));
                _sheenShift.BeginAnimation(TranslateTransform.XProperty, sweep);
            }
        }

        void SetVinyl(bool show)
        {
            if (_vinyl == null) return;
            if (show == _vinylOn) return;
            _vinylOn = show;
            if (show)
            {
                _vinyl.Visibility = Visibility.Visible;
                _vinyl.Opacity = 0;
                Anim.FadeIn(_vinyl, 320, 0, 0);
                Anim.RotateForever(_vinyl, 14000);
                if (_coverLetter != null) _coverLetter.Visibility = Visibility.Collapsed;
            }
            else
            {
                Anim.FadeOut(_vinyl, 220, () =>
                {
                    if (_vinylOn) return;   // 淡出途中又开始放封面，别把新的唱片收掉
                    _vinyl.Visibility = Visibility.Collapsed;
                    Anim.StopRotate(_vinyl);
                });
            }
        }

        void SetCover(string url)
        {
            if (url == _coverUrl)
            {
                if (string.IsNullOrEmpty(url))
                {
                    if (_coverLetter != null)
                        _coverLetter.Text = string.IsNullOrEmpty(_album.Text) ? "♪" : _album.Text.Substring(0, 1);
                    SetVinyl(true);
                }
                return;
            }
            _coverUrl = url;
            var token = new object();
            _coverToken = token;

            if (string.IsNullOrEmpty(url))
            {
                if (_cover.Source != null)
                {
                    _coverPrev.Source = _cover.Source;
                    _coverPrev.Opacity = 1;
                    _cover.Source = null;
                    _cover.Opacity = 0;
                    Anim.FadeOut(_coverPrev, 240, () => { _coverPrev.Source = null; });
                }
                else
                {
                    _coverPrev.Source = null;
                    _coverPrev.Opacity = 0;
                }
                _coverLetter.Text = string.IsNullOrEmpty(_album.Text) ? "♪" : _album.Text.Substring(0, 1);
                _coverLetter.Visibility = Visibility.Collapsed;
                SetVinyl(true);
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

                bool applied = false;
                Action apply = () =>
                {
                    if (applied) return;
                    applied = true;
                    if (!ReferenceEquals(_coverToken, token)) return;
                    _coverLetter.Visibility = Visibility.Collapsed;
                            Anim.StopAnim(_coverLetter);
                    SetVinyl(false);
                    Anim.FadeIn(_cover, 280, 0, 0);
                    Anim.ScaleTo(_cover, 1.035, 1.0, 520, 0, false);
                    if (_coverPrev.Source != null)
                    {
                        Anim.FadeOut(_coverPrev, 300, () => { _coverPrev.Source = null; });
                    }
                };

                bmp.DownloadCompleted += (s2, e2) => apply();
                bmp.DownloadFailed += (s2, e2) =>
                {
                    if (!ReferenceEquals(_coverToken, token)) return;
                    _cover.Source = null;
                    _coverLetter.Text = string.IsNullOrEmpty(_album.Text) ? "♪" : _album.Text.Substring(0, 1);
                    _coverLetter.Visibility = Visibility.Collapsed;
                    SetVinyl(true);
                };

                // 旧封面留在下一层，新封面加载好再交叉淡入，避免中间闪白
                var oldSource = _cover.Source;
                if (oldSource != null)
                {
                    _coverPrev.Source = oldSource;
                    _coverPrev.Opacity = 1;
                }
                _cover.Source = bmp;
                _cover.Opacity = 0;

                // 本地缓存命中时 DownloadCompleted 可能已经在 EndInit 里走完，
                // 这里补一次检查，避免封面永远停在 0 透明度。
                if (!bmp.IsDownloading && bmp.PixelWidth > 0) apply();
            }
            catch
            {
                _cover.Source = null;
                _coverLetter.Visibility = Visibility.Collapsed;
                SetVinyl(true);
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
            const double slot = 18;
            const double size = 15;
            double total = slot * 5;

            if (_dbClip == null || _dbStars.Children.Count != 2)
            {
                _dbStars.Children.Clear();
                _dbStars.Width = total;
                _dbStars.Height = size;
                _dbStars.Children.Add(StarStrip(5, slot, size, StarOff));

                _dbClip = new Border
                {
                    Width = 0,
                    Height = size,
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = StarStrip(5, slot, size, DbStarOn)
                };
                _dbStars.Children.Add(_dbClip);
                _dbFrac = 0;
            }

            double target = total * frac;
            if (Math.Abs(target - _dbFrac) < 0.3) return;
            double from = _dbClip.Width;
            if (double.IsNaN(from)) from = 0;
            _dbFrac = target;
            if (!Anim.Enabled) { _dbClip.Width = target; return; }
            Anim.Animate(_dbClip, FrameworkElement.WidthProperty, from, target, 460, 30, Anim.EaseOut());
        }

        void PaintMyStars(int n, bool lift, bool pop)
        {
            for (int k = 0; k < _myStars.Children.Count; k++)
            {
                var host = _myStars.Children[k] as Grid;
                if (host == null || host.Children.Count == 0) continue;
                var star = host.Children[0] as System.Windows.Shapes.Path;
                if (star != null)
                {
                    var fill = star.Fill as SolidColorBrush;
                    if (fill != null)
                    {
                        Anim.ColorTo(fill, k < n ? ColorOf(Accent) : ColorOf(StarOff), 130);
                    }
                }
                var sc = Anim.Scale(host);
                double to = lift && k < n ? 1.14 : 1.0;
                if (pop && k == n - 1)
                {
                    Anim.Pop(host, 1.32, to, 340);
                }
                else
                {
                    Anim.Animate(sc, ScaleTransform.ScaleXProperty, sc.ScaleX, to, 140, 0, Anim.EaseOut());
                    Anim.Animate(sc, ScaleTransform.ScaleYProperty, sc.ScaleY, to, 140, 0, Anim.EaseOut());
                }
            }
        }

        void BuildMyStars(int rating)
        {
            if (!_myStarsBuilt)
            {
                _myStars.Children.Clear();
                const double slot = 20;
                const double size = 16;
                _myStars.Width = slot * 5;
                _myStars.Height = 20;

                string[] tips = { "1 星 · 很差", "2 星 · 较差", "3 星 · 还行", "4 星 · 推荐", "5 星 · 力荐" };
                for (int i = 1; i <= 5; i++)
                {
                    var host = new Grid
                    {
                        Width = slot,
                        Height = 20,
                        Background = Brushes.Transparent,
                        Cursor = Cursors.Hand,
                        ToolTip = tips[i - 1]
                    };
                    var fill = new SolidColorBrush(ColorOf(StarOff));
                    host.Children.Add(MakeStar(size, fill));

                    int value = i;
                    host.MouseEnter += (s, e) =>
                    {
                        _lastInteraction = DateTime.Now;
                        PaintMyStars(value, true, false);
                    };
                    host.MouseLeave += (s, e) => PaintMyStars(_myRating, false, false);
                    host.MouseLeftButtonUp += async (s, e) => await RateAsync(value);
                    _myStars.Children.Add(host);
                }
                _myStarsBuilt = true;
                _paintedRating = -1;
            }

            if (_paintedRating != rating)
            {
                _paintedRating = rating;
                PaintMyStars(rating, false, false);
            }
        }

        static Color ColorOf(Brush b)
        {
            var s = b as SolidColorBrush;
            return s != null ? s.Color : Colors.White;
        }

        void RenderDist(Dictionary<string, object> subject)
        {
            var raw = subject != null && subject.ContainsKey("distribution") ? subject["distribution"] : null;
            var vals = new List<double>();
            var seq = raw as System.Collections.IEnumerable;
            if (seq != null && !(raw is string))
            {
                foreach (var o in seq) vals.Add(ToDouble(o));
            }
            if (vals.Count != 5)
            {
                ClearDist();
                return;
            }

            var sig = new StringBuilder();
            for (int i = 0; i < 5; i++) sig.Append(vals[i].ToString("0.##", CultureInfo.InvariantCulture)).Append('|');
            string sigText = sig.ToString();
            if (sigText == _distSig)
            {
                _dist.Visibility = Visibility.Visible;
                return;
            }
            _distSig = sigText;

            _dist.Visibility = Visibility.Visible;
            _dist.Children.Clear();
            _dist.ColumnDefinitions.Clear();
            _dist.RowDefinitions.Clear();
            _dist.Background = null;

            // 视频里的五星分布：五行通栏轨道，长度按比例，从 5 星到 1 星往下排
            for (int i = 0; i < 5; i++)
            {
                double pct = Math.Max(vals[i], 0.4);
                _dist.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var track = new Border
                {
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = null,
                    Margin = new Thickness(0, 0, 0, i < 4 ? 5 : 0),
                    ToolTip = (i + 1) + " 星 · " + pct.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                };
                var fill = new Border
                {
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = DistBarBrush(),
                    RenderTransformOrigin = new Point(0, 0.5)
                };
                fill.RenderTransform = new ScaleTransform(0, 1);
                track.Child = fill;
                Grid.SetRow(track, i);
                _dist.Children.Add(track);
                // 保留一点最小长度：0.2% 这种真实数据也要能看见一小截
                Anim.Animate((ScaleTransform)fill.RenderTransform, ScaleTransform.ScaleXProperty,
                    0, Math.Max(pct, 1.2) / 100.0, 520, 40 + i * 55, Anim.EaseOut());
            }
        }

        static Brush DistBarBrush()
        {
            var c = ((SolidColorBrush)DistBase).Color;
            var g = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, c.R, c.G, c.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, c.R, c.G, c.B), 1));
            g.Freeze();
            return g;
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
            _distSig = null;
            _dist.Visibility = Visibility.Collapsed;
            _dist.Children.Clear();
            _dist.ColumnDefinitions.Clear();
            _dist.RowDefinitions.Clear();
        }

        void SetProgress(double ratio)
        {
            if (_progressFill == null || _progressScale == null) return;
            ratio = Math.Max(0, Math.Min(1, ratio));
            double w = _progressTrack.ActualWidth;
            if (w <= 0) w = 400;
            if (Math.Abs(_progressFill.Width - w) > 0.5) _progressFill.Width = w;

            double from = _progressInited ? _progressScale.ScaleX : 0;
            if (!_progressInited)
            {
                _progressInited = true;
                _progressScale.ScaleX = ratio;
                return;
            }
            if (Math.Abs(ratio - from) < 0.004) return;
            // 跳曲/拖动进度时直接落位，正常播放时用 900ms 匀速推进
            if (ratio < from - 0.06 || Math.Abs(ratio - from) > 0.4)
            {
                Anim.AnimateLinear(_progressScale, ScaleTransform.ScaleXProperty, ratio, ratio, 1);
                return;
            }
            Anim.AnimateLinear(_progressScale, ScaleTransform.ScaleXProperty, from, ratio, 900);
        }

        void SetMineVisible(bool visible)
        {
            if (_mineRow == null) return;
            var target = visible ? Visibility.Visible : Visibility.Collapsed;
            if (_mineRow.Visibility == target) return;
            _mineRow.Visibility = target;
            if (visible) Anim.SlideIn(_mineRow, 7, 280, 40);
        }

        void UpdateCommentPlaceholder()
        {
            if (_commentPh == null) return;
            bool empty = string.IsNullOrEmpty(_comment.Text);
            if (empty)
            {
                if (_commentPh.Visibility != Visibility.Visible)
                {
                    _commentPh.Visibility = Visibility.Visible;
                    Anim.FadeIn(_commentPh, 140, 0, 0);
                }
            }
            else
            {
                _commentPh.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>「正在播放」的小圆点：呼吸 + 向外扩散的一圈波纹</summary>
        void SetPulse(bool playing)
        {
            int state = playing ? 1 : 0;
            if (state == _pulseState) return;
            _pulseState = state;

            if (_pulse != null) _pulse.Fill = playing ? Mint : PulseIdle;
            if (_pulseRing != null) _pulseRing.Stroke = playing ? Mint : PulseIdle;

            if (playing)
            {
                if (_pulseRing != null) Anim.RippleForever(_pulseRing, 2.6, 1700);
                if (_pulse != null) Anim.PulseForever(_pulse, 1.22, 0.72, 1200);
            }
            else
            {
                if (_pulseRing != null)
                {
                    Anim.StopAnim(_pulseRing);
                    _pulseRing.Opacity = 0;
                }
                if (_pulse != null) Anim.StopAnim(_pulse);
            }
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

        /// <summary>顶栏状态文字：有内容时显示成小胶囊，空字符串时整块收起</summary>
        void SetStatus(string text)
        {
            if (_status == null) return;
            _status.Text = text;
            if (_statusChip != null)
                _statusChip.Visibility = string.IsNullOrEmpty(text)
                    ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>同步按钮的四种状态：不可用 / 可提交 / 提交中 / 已同步</summary>
        void RefreshSyncVisual()
        {
            if (_syncText == null || _btnSync == null) return;
            bool wasSyncing = _wasSyncing;
            bool wasSynced = _wasSynced;

            if (_syncing)
            {
                _syncText.Text = "同步中";
                _btnSync.Background = QuietFill;
                _syncText.Foreground = Dim;
                _btnSync.IsEnabled = false;
                if (_syncCheck != null) _syncCheck.Visibility = Visibility.Collapsed;
                if (_syncSpinner != null)
                {
                    _syncSpinner.Visibility = Visibility.Visible;
                    if (!wasSyncing) Anim.RotateForever(_syncSpinner, 900);
                }
            }
            else
            {
                if (_syncSpinner != null)
                {
                    if (wasSyncing) Anim.StopRotate(_syncSpinner);
                    _syncSpinner.Visibility = Visibility.Collapsed;
                }

                if (!_syncEnabled)
                {
                    _syncText.Text = "同步";
                    _btnSync.Background = QuietFill;
                    _syncText.Foreground = Faint;
                    _btnSync.IsEnabled = false;
                    if (_syncCheck != null) _syncCheck.Visibility = Visibility.Collapsed;
                }
                else if (_synced)
                {
                    _syncText.Text = "已同步";
                    _btnSync.Background = Done;
                    _syncText.Foreground = OnDone;
                    _btnSync.IsEnabled = true;
                    if (_syncCheck != null)
                    {
                        _syncCheck.Visibility = Visibility.Visible;
                        _syncCheck.Stroke = OnDone;
                        if (!wasSynced) Anim.Pop(_syncCheck, 0.35, 1.0, 340);
                    }
                }
                else
                {
                    _syncText.Text = "同步";
                    _btnSync.Background = Primary;
                    _syncText.Foreground = OnPrimary;
                    _btnSync.IsEnabled = true;
                    if (_syncCheck != null) _syncCheck.Visibility = Visibility.Collapsed;
                }
            }

            if (_syncIconHost != null)
            {
                _syncIconHost.Visibility = (_syncing || (!_syncing && _syncEnabled && _synced))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            _wasSyncing = _syncing;
            _wasSynced = _synced;
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
            bool becameTodo = inTodo && !_inTodo;
            _inTodo = inTodo;
            _laterBusy = busy;
            if (_btnLater == null || _laterText == null) return;

            _btnLater.Background = QuietFill;
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
                if (becameTodo) Anim.Pop(_btnLater, 0.96, 1.0, 280);
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
            _paintedRating = value;
            PaintMyStars(value, false, true);
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
            if (_hintTimer != null) { _hintTimer.Stop(); _hintTimer = null; }
            var token = new object();
            _hintToken = token;
            _hint.Text = text;
            _hint.Foreground = color;
            _hint.Visibility = Visibility.Visible;
            Anim.SlideIn(_hint, 8, 220, 0);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            _hintTimer = timer;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (!ReferenceEquals(_hintTimer, timer)) return;
                _hintTimer = null;
                Anim.FadeOut(_hint, 180, () =>
                {
                    if (!ReferenceEquals(_hintToken, token)) return;
                    _hint.Visibility = Visibility.Collapsed;
                    _hint.Foreground = Dim;
                });
            };
            timer.Start();
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
      <Border x:Name='MenuRoot' Background='{{BG}}' BorderBrush='{{BORDER}}' BorderThickness='1'
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
                var root = menu.Template.FindName("MenuRoot", menu) as FrameworkElement;
                if (root != null) Anim.SlideIn(root, -6, 180, 0);
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
            el.MouseEnter += (s, e) =>
            {
                Anim.Animate(eff, DropShadowEffect.BlurRadiusProperty,
                    eff.BlurRadius, Math.Max(2, baseBlur * 0.5), 150, 0, Anim.EaseOut());
            };
            el.MouseLeave += (s, e) =>
            {
                Anim.Animate(eff, DropShadowEffect.BlurRadiusProperty,
                    eff.BlurRadius, baseBlur, 180, 0, Anim.EaseOut());
            };
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
