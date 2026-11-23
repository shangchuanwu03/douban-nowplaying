using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;

namespace DoubanNowPlaying
{
    /// <summary>
    /// 收听史 / 手动绑定 / 设置。做成应用内窗口，不再跳浏览器 ——
    /// 浏览器新标签经常抢不到前台，用户会以为「设置没出来」。
    /// </summary>
    public class SettingsWindow
    {
        const string BaseUrl = "http://127.0.0.1:4590";

        static Brush Ink, Dim, Faint, Accent, Base, StarOff, Line, Mint;
        static bool IsGlass;

        static void ApplyPalette(bool glass)
        {
            IsGlass = glass;
            if (glass)
            {
                Ink = NewBrush(0xFF, 0xFF, 0xFF);
                Dim = NewBrush(0xA9, 0xC0, 0xDC);
                Faint = NewBrush(0x6E, 0x8C, 0xB2);
                Accent = NewBrush(0xE4, 0xB8, 0x63);
                Base = NewBrush(0x16, 0x23, 0x3A);
                StarOff = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                Line = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
                Mint = NewBrush(0xE4, 0xB8, 0x63);
            }
            else
            {
                Ink = NewBrush(0x3D, 0x44, 0x51);
                Dim = NewBrush(0x7B, 0x84, 0x94);
                Faint = NewBrush(0x9A, 0xA3, 0xB2);
                Accent = NewBrush(0x6D, 0x5D, 0xFC);
                Base = NewBrush(0xE0, 0xE5, 0xEC);
                StarOff = NewBrush(0xC3, 0xC9, 0xD3);
                Line = NewBrush(0xD3, 0xDA, 0xE4);
                Mint = NewBrush(0x4E, 0xCD, 0xC4);
            }
        }

        static SettingsWindow _instance;

        public static void ShowSingleton()
        {
            ShowSingleton(false, null);
        }

        public static void ShowSingleton(bool startLogin)
        {
            ShowSingleton(startLogin, null);
        }

        public static void ShowSingleton(string initialTab)
        {
            ShowSingleton(false, initialTab);
        }

        public static void ShowSingleton(bool startLogin, string initialTab)
        {
            if (_instance != null && _instance._win != null)
            {
                if (_instance._win.WindowState == WindowState.Minimized)
                    _instance._win.WindowState = WindowState.Normal;
                _instance._win.Activate();
                _instance._win.Topmost = true;
                _instance._win.Topmost = false;
                if (startLogin) _instance.OpenLoginPage();
                if (!string.IsNullOrEmpty(initialTab)) _instance.SwitchTab(initialTab);
                return;
            }
            var w = new SettingsWindow();
            w._initialTab = initialTab;
            w.Open();
            _instance = w;
            if (startLogin) w.OpenLoginPage();
        }

        Window _win;
        HttpClient _http;
        string _currentTab = "history";
        string _initialTab;

        Border _tabHistory, _tabTodo, _tabBind, _tabSettings;
        Border _skinNeo, _skinGlass;
        TextBlock _skinNeoText, _skinGlassText;

        Border _modeAuto, _modeApple, _modeSpotify, _modeNetease, _modeQQ;
        TextBlock _modeAutoText, _modeAppleText, _modeSpotifyText, _modeNeteaseText, _modeQQText, _modeHint;
        string _playerMode = "auto";

        Border _btnLogin, _btnLogout, _btnClip, _btnImport;
        TextBlock _importHint;
        TextBlock _btnLoginText, _doubanStatus;
        bool _loggingIn;
        TextBlock _tabHistoryText, _tabTodoText, _tabBindText, _tabSettingsText;
        ScrollViewer _panelHistory, _panelTodo, _panelSettings;
        StackPanel _panelBind;
        StackPanel _historyList, _todoList, _bindResults;
        TextBox _searchBox, _cookieBox;
        Border _btnSearch, _btnSave, _btnClose;
        TextBlock _statusLine, _saveHint, _btnSaveText;
        string _cookiePlaceholder = "bid=xxxx; dbcl2=xxxx";

        // 版本与更新
        Border _btnCheckUpdate, _btnApplyUpdate;
        TextBlock _btnCheckUpdateText, _btnApplyUpdateText, _updateHint, _updateNotes;
        Updater.ReleaseInfo _pendingUpdate;
        bool _updateBusy;

        void Open()
        {
            _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };

            ApplyPalette(Overlay.Skin == Overlay.SkinKind.Glass);
            var file = IsGlass ? "settings-glass.xaml" : "settings.xaml";
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay", file);
            if (!File.Exists(path))
            {
                MessageBox.Show("缺少界面文件：" + path, "豆瓣正在听");
                return;
            }

            using (var fs = File.OpenRead(path))
            {
                _win = (Window)XamlReader.Load(fs);
            }

            _tabHistory = (Border)_win.FindName("TabHistory");
            _tabTodo = (Border)_win.FindName("TabTodo");
            _tabBind = (Border)_win.FindName("TabBind");
            _tabSettings = (Border)_win.FindName("TabSettings");
            _tabHistoryText = (TextBlock)_win.FindName("TabHistoryText");
            _tabTodoText = (TextBlock)_win.FindName("TabTodoText");
            _tabBindText = (TextBlock)_win.FindName("TabBindText");
            _tabSettingsText = (TextBlock)_win.FindName("TabSettingsText");

            _panelHistory = (ScrollViewer)_win.FindName("PanelHistory");
            _panelTodo = (ScrollViewer)_win.FindName("PanelTodo");
            _panelBind = (StackPanel)_win.FindName("PanelBind");
            _panelSettings = (ScrollViewer)_win.FindName("PanelSettings");

            _historyList = (StackPanel)_win.FindName("HistoryList");
            _todoList = (StackPanel)_win.FindName("TodoList");
            _bindResults = (StackPanel)_win.FindName("BindResults");
            _searchBox = (TextBox)_win.FindName("SearchBox");
            _cookieBox = (TextBox)_win.FindName("CookieBox");

            _btnSearch = (Border)_win.FindName("BtnSearch");
            _btnSave = (Border)_win.FindName("BtnSave");
            _btnClose = (Border)_win.FindName("BtnClose");
            _statusLine = (TextBlock)_win.FindName("StatusLine");
            _saveHint = (TextBlock)_win.FindName("SaveHint");
            _btnSaveText = (TextBlock)_win.FindName("BtnSaveText");

            _skinNeo = (Border)_win.FindName("SkinNeo");
            _skinGlass = (Border)_win.FindName("SkinGlass");
            _skinNeoText = (TextBlock)_win.FindName("SkinNeoText");
            _skinGlassText = (TextBlock)_win.FindName("SkinGlassText");
            if (_skinNeo != null) _skinNeo.MouseLeftButtonUp += (s, e) => PickSkin(false);
            if (_skinGlass != null) _skinGlass.MouseLeftButtonUp += (s, e) => PickSkin(true);
            StyleSkinPills();

            _modeAuto = (Border)_win.FindName("ModeAuto");
            _modeApple = (Border)_win.FindName("ModeApple");
            _modeSpotify = (Border)_win.FindName("ModeSpotify");
            _modeNetease = (Border)_win.FindName("ModeNetease");
            _modeQQ = (Border)_win.FindName("ModeQQ");
            _modeAutoText = (TextBlock)_win.FindName("ModeAutoText");
            _modeAppleText = (TextBlock)_win.FindName("ModeAppleText");
            _modeSpotifyText = (TextBlock)_win.FindName("ModeSpotifyText");
            _modeNeteaseText = (TextBlock)_win.FindName("ModeNeteaseText");
            _modeQQText = (TextBlock)_win.FindName("ModeQQText");
            _modeHint = (TextBlock)_win.FindName("ModeHint");
            if (_modeAuto != null) _modeAuto.MouseLeftButtonUp += (s, e) => PickMode("auto");
            if (_modeApple != null) _modeApple.MouseLeftButtonUp += (s, e) => PickMode("applemusic");
            if (_modeSpotify != null) _modeSpotify.MouseLeftButtonUp += (s, e) => PickMode("spotify");
            if (_modeNetease != null) _modeNetease.MouseLeftButtonUp += (s, e) => PickMode("netease");
            if (_modeQQ != null) _modeQQ.MouseLeftButtonUp += (s, e) => PickMode("qqmusic");
            StyleModePills();

            _btnLogin = (Border)_win.FindName("BtnLogin");
            _btnLogout = (Border)_win.FindName("BtnLogout");
            _btnLoginText = (TextBlock)_win.FindName("BtnLoginText");
            _doubanStatus = (TextBlock)_win.FindName("DoubanStatus");
            _btnClip = (Border)_win.FindName("BtnClip");
            if (_btnLogin != null) _btnLogin.MouseLeftButtonUp += (s, e) => OpenLoginPage();
            if (_btnLogout != null) _btnLogout.MouseLeftButtonUp += (s, e) => Unbind();
            Overlay.Trace("settings: btnClip=" + (_btnClip != null) + " btnLogin=" + (_btnLogin != null));
            if (_btnClip != null) _btnClip.MouseLeftButtonUp += (s, e) => ImportFromClipboard();

            _btnImport = (Border)_win.FindName("BtnImport");
            _importHint = (TextBlock)_win.FindName("ImportHint");
            if (_btnImport != null) _btnImport.MouseLeftButtonUp += (s, e) => ImportCollection();

            _btnCheckUpdate = (Border)_win.FindName("BtnCheckUpdate");
            _btnCheckUpdateText = (TextBlock)_win.FindName("BtnCheckUpdateText");
            _btnApplyUpdate = (Border)_win.FindName("BtnApplyUpdate");
            _btnApplyUpdateText = (TextBlock)_win.FindName("BtnApplyUpdateText");
            _updateHint = (TextBlock)_win.FindName("UpdateHint");
            _updateNotes = (TextBlock)_win.FindName("UpdateNotes");

            if (_btnCheckUpdate != null) _btnCheckUpdate.MouseLeftButtonUp += async (s, e) => await CheckUpdateAsync();
            if (_btnApplyUpdate != null) _btnApplyUpdate.MouseLeftButtonUp += async (s, e) => await ApplyUpdateAsync();
            if (_updateHint != null) _updateHint.Text = "当前版本 v" + Updater.LocalVersion();

            _tabHistory.MouseLeftButtonUp += (s, e) => SwitchTab("history");
            if (_tabTodo != null) _tabTodo.MouseLeftButtonUp += (s, e) => SwitchTab("todo");
            _tabBind.MouseLeftButtonUp += (s, e) => SwitchTab("bind");
            _tabSettings.MouseLeftButtonUp += (s, e) => SwitchTab("settings");

            _btnSearch.MouseLeftButtonUp += async (s, e) => await SearchAsync();
            _searchBox.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter) await SearchAsync();
            };

            _btnSave.MouseLeftButtonUp += async (s, e) => await SaveCookieAsync();
            _btnClose.MouseLeftButtonUp += (s, e) => _win.Close();

            _win.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) _win.Close();
            };
            // 只有标题栏区域可拖动。挂在整窗上会把标签/按钮的点击吞掉。
            var header = _win.FindName("Header") as FrameworkElement;
            if (header != null)
            {
                header.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ButtonState == MouseButtonState.Pressed)
                    {
                        try { _win.DragMove(); } catch { }
                    }
                };
            }

            _win.Closed += (s, e) => _instance = null;

            _win.Show();
            // 从命令行/后台拉起时，单纯 Activate 可能抢不到前台，用 Topmost 抬一下再取消
            _win.Topmost = true;
            _win.Activate();
            _win.Topmost = false;
            _win.Focus();

            LoadHistory();
            LoadSettings();

            if (!string.IsNullOrEmpty(_initialTab))
            {
                SwitchTab(_initialTab);
                _initialTab = null;
            }
            else
            {
                _searchBox.Focus();
            }
        }

        /* -------------------------------- 账号 -------------------------------- */

        void ApplyAccountState(bool configured, string name)
        {
            if (_doubanStatus == null || _btnLogout == null || _btnLoginText == null) return;
            if (configured)
            {
                _doubanStatus.Text = string.IsNullOrEmpty(name) ? "已绑定（未取到昵称）" : "已绑定：" + name;
                _btnLogout.Visibility = Visibility.Visible;
                _btnLoginText.Text = "打开豆瓣";
            }
            else
            {
                _doubanStatus.Text = "未绑定";
                _btnLogout.Visibility = Visibility.Collapsed;
                _btnLoginText.Text = "打开豆瓣";
            }
        }

        /// <summary>从注册表取默认浏览器的 exe</summary>
        static string GetDefaultBrowserExe()
        {
            try
            {
                string progId;
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                {
                    progId = key == null ? null : key.GetValue("ProgId") as string;
                }
                if (string.IsNullOrEmpty(progId)) return null;

                string command;
                using (var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                {
                    command = cmd == null ? null : cmd.GetValue(null) as string;
                }
                if (string.IsNullOrEmpty(command)) return null;

                command = command.Trim();
                if (command.StartsWith("\""))
                {
                    int endQ = command.IndexOf('"', 1);
                    return endQ > 1 ? command.Substring(1, endQ - 1) : null;
                }
                int sp = command.IndexOf(' ');
                return sp > 0 ? command.Substring(0, sp) : command;
            }
            catch { return null; }
        }

        /// <summary>点一下就用默认浏览器打开豆瓣</summary>
        void OpenLoginPage()
        {
            const string url = "https://www.douban.com/";
            try
            {
                var exe = GetDefaultBrowserExe();
                Overlay.Trace("open douban exe=" + (exe ?? "null"));
                if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                {
                    // 直接跑浏览器本体。Chromium 系（ego/chrome/edge）用 --new-window，
                    // 否则浏览器会起一个进程然后立刻退出。
                    var name = System.IO.Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                    bool chromium = name.Contains("ego") || name.Contains("chrome") ||
                                    name.Contains("msedge") || name.Contains("brave") ||
                                    name.Contains("opera") || name.Contains("vivaldi");
                    var argLine = chromium ? "--new-window \"" + url + "\"" : "\"" + url + "\"";
                    Process.Start(new ProcessStartInfo(exe, argLine) { UseShellExecute = false });
                }
                else
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                Overlay.Trace("open douban ok");
                if (_doubanStatus != null) _doubanStatus.Text = "已在浏览器打开豆瓣";
            }
            catch (Exception ex)
            {
                Overlay.Trace("open douban failed: " + ex.Message);
                if (_doubanStatus != null) _doubanStatus.Text = "打开浏览器失败：" + ex.Message;
            }
        }

        /// <summary>从剪贴板里认 bid / dbcl2，拼成 Cookie 头</summary>
        static string ParseCookieFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var bid = ExtractCookieValue(text, "bid");
            var dbcl2 = ExtractCookieValue(text, "dbcl2");
            if (bid == null && dbcl2 == null) return null;
            var parts = new System.Collections.Generic.List<string>();
            if (bid != null) parts.Add("bid=" + bid);
            // 豆瓣下发的 dbcl2 本身就带引号，这里按原样写回，避免校验失败
            if (dbcl2 != null) parts.Add("dbcl2=\"" + dbcl2.Trim('"') + "\"");
            return string.Join("; ", parts.ToArray());
        }

        static string ExtractCookieValue(string text, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text,
                "(?:^|[;\\s\"'])" + name + "=\"?([^;\"\\s]+)\"?");
            return m.Success ? m.Groups[1].Value : null;
        }

        async void ImportFromClipboard()
        {
            string text = "";
            try { text = Clipboard.GetText() ?? ""; }
            catch (Exception ex) { Overlay.Trace("clipboard read failed: " + ex.Message); }
            Overlay.Trace("clipboard len=" + text.Length);

            var cookie = ParseCookieFromText(text);
            if (cookie == null)
            {
                if (_doubanStatus != null) _doubanStatus.Text = "剪贴板里没有 bid / dbcl2，请复制完整 Cookie";
                return;
            }
            if (cookie.IndexOf("dbcl2=") < 0)
            {
                if (_doubanStatus != null) _doubanStatus.Text = "缺少 dbcl2，可能还没登录";
                return;
            }

            if (_cookieBox != null) _cookieBox.Text = cookie;
            if (_doubanStatus != null) _doubanStatus.Text = "正在验证…";
            await SaveAndVerifyAsync(cookie);
        }

        /// <summary>把豆瓣上「我听过的音乐」拉进本地，避免重复打分/误覆盖</summary>
        async void ImportCollection()
        {
            if (_importing) return;
            _importing = true;

            int start = 0;
            int rounds = 0;
            int total = 0;
            try
            {
                // 分批拉取：每批最多 6 页，避免单次请求过长
                using (var http = new HttpClient
                {
                    BaseAddress = new Uri(BaseUrl),
                    Timeout = TimeSpan.FromMinutes(3)
                })
                {
                    while (rounds < 300)
                    {
                        if (_importHint != null)
                        {
                            _importHint.Text = rounds == 0
                                ? "正在导入…"
                                : "正在导入… 已处理 " + total + " 条（第 " + rounds + " 批）";
                            _importHint.Foreground = Dim;
                        }

                        var body = "{\"maxPages\":6}";
                        var resp = await http.PostAsync("/api/douban/import",
                            new StringContent(body, Encoding.UTF8, "application/json"));
                        var text = await resp.Content.ReadAsStringAsync();
                        var ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                        var r = ser.Deserialize<Dictionary<string, object>>(text);

                        bool ok = r != null && r.ContainsKey("ok") && Convert.ToBoolean(r["ok"]);
                        if (!ok)
                        {
                            string err = r != null && r.ContainsKey("error") ? Convert.ToString(r["error"]) : "导入失败";
                            if (_importHint != null) { _importHint.Text = err; _importHint.Foreground = NewBrush(0xFF, 0x6B, 0x6B); }
                            return;
                        }

                        total += (int)Num(r, "imported");
                        total += (int)Num(r, "updated");
                        bool hasMore = r.ContainsKey("hasMore") && Convert.ToBoolean(r["hasMore"]);
                        rounds += 1;

                        if (!hasMore) break;
                    }
                }

                if (_importHint != null)
                {
                    _importHint.Text = "本次同步 " + total + " 条";
                    _importHint.Foreground = Mint;
                }
                _statusLine.Text = "已同步豆瓣收听记录：" + total + " 条";
            }
            catch (Exception ex)
            {
                if (_importHint != null) { _importHint.Text = "导入失败：" + ex.Message; _importHint.Foreground = NewBrush(0xFF, 0x6B, 0x6B); }
            }
            finally
            {
                _importing = false;
            }
        }

        bool _importing;

        /// <summary>已有 Cookie 但没有昵称时，补一次验证并记住昵称</summary>
        async void EnsureProfileAsync()
        {
            try
            {
                var resp = await _http.PostAsync("/api/douban/verify",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();
                var ser = new JavaScriptSerializer();
                var prof = ser.Deserialize<Dictionary<string, object>>(text);
                if (prof == null || !prof.ContainsKey("ok") || !Convert.ToBoolean(prof["ok"])) return;

                string uid = Convert.ToString(prof["uid"]);
                string name = prof.ContainsKey("name") ? Convert.ToString(prof["name"]) : "";
                var body = ser.Serialize(new Dictionary<string, object>
                {
                    { "doubanUser", new Dictionary<string, object> { { "uid", uid }, { "name", name } } }
                });
                await _http.PostAsync("/api/settings",
                    new StringContent(body, Encoding.UTF8, "application/json"));

                ApplyAccountState(true, name);
            }
            catch { }
        }

        /// <summary>保存 Cookie 并向服务端验证身份</summary>
        async System.Threading.Tasks.Task SaveAndVerifyAsync(string cookie)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var verifyBody = ser.Serialize(new Dictionary<string, object> { { "doubanCookie", cookie } });
                var resp = await _http.PostAsync("/api/douban/verify",
                    new StringContent(verifyBody, Encoding.UTF8, "application/json"));
                var verifyText = await resp.Content.ReadAsStringAsync();
                var prof = ser.Deserialize<Dictionary<string, object>>(verifyText);

                bool ok = prof != null && prof.ContainsKey("ok") && Convert.ToBoolean(prof["ok"]);
                if (!ok)
                {
                    string reason = prof != null && prof.ContainsKey("error") ? Convert.ToString(prof["error"]) : "";
                    string msg;
                    if (reason == "no_dbcl2") msg = "还差 dbcl2：这个浏览器没登录豆瓣，请先登录";
                    else if (reason == "expired") msg = "Cookie 已过期，请重新登录";
                    else if (reason.StartsWith("http_")) msg = "豆瓣返回 " + reason.Substring(5) + "，请稍后重试";
                    else msg = "Cookie 无效：" + (string.IsNullOrEmpty(reason) ? "请检查是否复制完整" : reason);
                    if (_doubanStatus != null) _doubanStatus.Text = msg;
                    return;
                }

                string uid = Convert.ToString(prof["uid"]);
                string name = prof.ContainsKey("name") ? Convert.ToString(prof["name"]) : "";

                var saveBody = ser.Serialize(new Dictionary<string, object>
                {
                    { "doubanCookie", cookie },
                    { "doubanUser", new Dictionary<string, object> { { "uid", uid }, { "name", name } } }
                });
                await _http.PostAsync("/api/settings",
                    new StringContent(saveBody, Encoding.UTF8, "application/json"));

                ApplyAccountState(true, name);
                _statusLine.Text = "已配置豆瓣登录";
                if (_cookieBox != null) _cookieBox.ToolTip = "已设置，留空表示不修改";
                _saveHint.Text = "账号已绑定";
                _saveHint.Foreground = Mint;
            }
            catch (Exception ex)
            {
                if (_doubanStatus != null) _doubanStatus.Text = "验证失败：" + ex.Message;
            }
        }

        async void Unbind()
        {
            try
            {
                var body = "{\"unbind\":true}";
                await _http.PostAsync("/api/settings",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                ApplyAccountState(false, "");
                _statusLine.Text = "未配置豆瓣登录（只读评分，无法回写）";
                _saveHint.Text = "已解除绑定";
                _saveHint.Foreground = Faint;
            }
            catch (Exception ex)
            {
                _saveHint.Text = "解绑失败：" + ex.Message;
            }
        }

        /* -------------------------------- 更新 -------------------------------- */

        /// <summary>点「检查更新」：走 GitHub 的公开 release 接口，只读，不发任何本机信息。</summary>
        async System.Threading.Tasks.Task CheckUpdateAsync()
        {
            if (_updateBusy || _updateHint == null) return;
            _updateBusy = true;
            _updateHint.Text = "正在检查…";
            if (_btnApplyUpdate != null) _btnApplyUpdate.Visibility = Visibility.Collapsed;
            if (_updateNotes != null) _updateNotes.Visibility = Visibility.Collapsed;
            SetUpdateButtonBusy(true);

            try
            {
                var rel = await Updater.CheckAsync();
                _pendingUpdate = rel.HasUpdate ? rel : null;

                if (rel.HasUpdate)
                {
                    _updateHint.Text = "发现新版本 v" + rel.Latest + "（当前 v" + rel.Current + "）"
                                       + (string.IsNullOrEmpty(rel.SizeText) ? "" : " · " + rel.SizeText);
                    if (_updateNotes != null && !string.IsNullOrEmpty(rel.Notes))
                    {
                        _updateNotes.Text = rel.Notes;
                        _updateNotes.Visibility = Visibility.Visible;
                    }
                    if (_btnApplyUpdate != null) _btnApplyUpdate.Visibility = Visibility.Visible;
                }
                else
                {
                    _updateHint.Text = "已是最新版本（v" + rel.Current + "）";
                }
            }
            catch (Exception ex)
            {
                _pendingUpdate = null;
                _updateHint.Text = "检查失败：" + ex.Message;
            }
            finally
            {
                SetUpdateButtonBusy(false);
                _updateBusy = false;
            }
        }

        /// <summary>
        /// 点「立即更新」：下载安装包 → 交给独立进程等本程序退出后静默覆盖安装 → 自己退出。
        /// 覆盖装回同一目录，data/ 里的评分和账号不会动。
        /// </summary>
        async System.Threading.Tasks.Task ApplyUpdateAsync()
        {
            if (_updateBusy || _pendingUpdate == null) return;
            _updateBusy = true;
            SetUpdateButtonBusy(true, "下载中…");
            if (_btnApplyUpdate != null) _btnApplyUpdate.IsEnabled = false;

            var dest = Path.Combine(Path.GetTempPath(),
                "DoubanNowPlaying-" + _pendingUpdate.Latest + "-Setup.exe");

            try
            {
                _updateHint.Text = "正在下载 v" + _pendingUpdate.Latest + "…";
                int lastShown = -1;
                await Updater.DownloadAsync(_pendingUpdate, dest, (done, total) =>
                {
                    // 回调本身就在 UI 线程上，直接改文案即可；只在百分比变化时刷新
                    if (total <= 0 || _updateHint == null) return;
                    int pct = (int)(done * 100 / total);
                    if (pct == lastShown) return;
                    lastShown = pct;
                    _updateHint.Text = "正在下载 v" + _pendingUpdate.Latest + "… " + pct + "%";
                });

                _updateHint.Text = "下载完成，正在退出并覆盖安装…";
                Updater.ScheduleInstall(dest, Process.GetCurrentProcess().Id);

                await System.Threading.Tasks.Task.Delay(700);

                if (_win != null) _win.Close();
                if (Overlay.Current != null) Overlay.Current.ExitForUpdate();
                else Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _updateHint.Text = "更新失败：" + ex.Message;
                if (_btnApplyUpdate != null) _btnApplyUpdate.IsEnabled = true;
                SetUpdateButtonBusy(false);
                _updateBusy = false;
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                }
                catch { }
            }
        }

        void SetUpdateButtonBusy(bool busy, string label = null)
        {
            if (_btnCheckUpdate != null) _btnCheckUpdate.IsEnabled = !busy;
            if (_btnCheckUpdateText != null)
                _btnCheckUpdateText.Text = busy ? (label ?? "检查中…") : "检查更新";
        }

        /* -------------------------------- 皮肤 -------------------------------- */

        void StyleSkinPills()
        {
            if (_skinNeo == null || _skinGlass == null) return;
            StyleOneSkinPill(_skinNeo, _skinNeoText, !IsGlass);
            StyleOneSkinPill(_skinGlass, _skinGlassText, IsGlass);
        }

        void StyleOneSkinPill(Border border, TextBlock text, bool active)
        {
            if (IsGlass)
            {
                border.BorderThickness = new Thickness(1);
                border.BorderBrush = active
                    ? new SolidColorBrush(Color.FromArgb(0x88, 0xE4, 0xB8, 0x63))
                    : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                border.Background = active
                    ? new SolidColorBrush(Color.FromArgb(0x24, 0xE4, 0xB8, 0x63))
                    : Brushes.Transparent;
                text.Foreground = active ? Accent : Dim;
                border.Effect = null;
            }
            else
            {
                border.BorderThickness = new Thickness(0);
                border.Background = active ? Base : Brushes.Transparent;
                text.Foreground = active ? Ink : Dim;
                border.Effect = active
                    ? (System.Windows.Media.Effects.Effect)new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Color.FromRgb(0xB8, 0xBC, 0xC2),
                        BlurRadius = 8,
                        ShadowDepth = 3,
                        Direction = 315,
                        Opacity = 0.9
                    }
                    : null;
            }
        }

        void PickSkin(bool glass)
        {
            if (glass == IsGlass) return;
            var target = glass ? Overlay.SkinKind.Glass : Overlay.SkinKind.Neumorphic;
            PostSkinAsync(target);

            // 主窗口先换，再重建自己
            if (Overlay.Current != null) Overlay.Current.SetSkin(target, false);

            var old = _win;
            _win = null;
            try { old.Close(); } catch { }
            ShowSingleton();
        }

        async System.Threading.Tasks.Task PostSkinAsync(Overlay.SkinKind skin)
        {
            try
            {
                var body = "{\"skin\":\"" + (skin == Overlay.SkinKind.Glass ? "glass" : "neumorphic") + "\"}";
                await _http.PostAsync("/api/settings",
                    new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        /* ------------------------------ 识别来源 ------------------------------ */

        static string ModeLabel(string mode)
        {
            switch (mode)
            {
                case "applemusic": return "Apple Music";
                case "spotify": return "Spotify";
                case "netease": return "网易云音乐";
                case "qqmusic": return "QQ 音乐";
                default: return "自动识别";
            }
        }

        void StyleModePills()
        {
            if (_modeAuto == null) return;
            StyleOneSkinPill(_modeAuto, _modeAutoText, _playerMode == "auto");
            StyleOneSkinPill(_modeApple, _modeAppleText, _playerMode == "applemusic");
            StyleOneSkinPill(_modeSpotify, _modeSpotifyText, _playerMode == "spotify");
            StyleOneSkinPill(_modeNetease, _modeNeteaseText, _playerMode == "netease");
            StyleOneSkinPill(_modeQQ, _modeQQText, _playerMode == "qqmusic");
        }

        void PickMode(string mode)
        {
            if (mode == _playerMode) return;
            _playerMode = mode;
            StyleModePills();
            PostModeAsync(mode);
            if (_statusLine != null) _statusLine.Text = "识别来源：" + ModeLabel(mode);
            if (_modeHint != null)
            {
                _modeHint.Text = mode == "auto"
                    ? "自动 = 谁在播就认谁；指定来源后只认这个播放器。"
                    : "只认 " + ModeLabel(mode) + "，其他播放器会被忽略。";
            }
        }

        async System.Threading.Tasks.Task PostModeAsync(string mode)
        {
            try
            {
                var body = "{\"playerMode\":\"" + mode + "\"}";
                await _http.PostAsync("/api/settings",
                    new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        /* -------------------------------- 标签 -------------------------------- */

        void SwitchTab(string tab)
        {
            _currentTab = tab;

            StyleTab(_tabHistory, _tabHistoryText, tab == "history");
            if (_tabTodo != null) StyleTab(_tabTodo, _tabTodoText, tab == "todo");
            StyleTab(_tabBind, _tabBindText, tab == "bind");
            StyleTab(_tabSettings, _tabSettingsText, tab == "settings");

            _panelHistory.Visibility = tab == "history" ? Visibility.Visible : Visibility.Collapsed;
            if (_panelTodo != null) _panelTodo.Visibility = tab == "todo" ? Visibility.Visible : Visibility.Collapsed;
            _panelBind.Visibility = tab == "bind" ? Visibility.Visible : Visibility.Collapsed;
            _panelSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;

            if (tab == "history") LoadHistory();
            else if (tab == "todo") LoadTodos();
        }

        static void StyleTab(Border border, TextBlock text, bool active)
        {
            if (IsGlass)
            {
                border.Background = active
                    ? new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF))
                    : Brushes.Transparent;
                text.Foreground = active ? Ink : Dim;
                border.Effect = null;
                return;
            }
            border.Background = active ? Base : Brushes.Transparent;
            text.Foreground = active ? Ink : Dim;
            if (active)
            {
                border.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0xB8, 0xBC, 0xC2),
                    BlurRadius = 8,
                    ShadowDepth = 3,
                    Direction = 315,
                    Opacity = 0.9
                };
            }
            else
            {
                border.Effect = null;
            }
        }

        /* -------------------------------- 数据 -------------------------------- */

        async void LoadHistory()
        {
            try
            {
                var historyJson = await _http.GetStringAsync("/api/history?limit=100");
                var ratingsJson = await _http.GetStringAsync("/api/ratings");

                var ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var history = ser.Deserialize<Dictionary<string, object>>(historyJson);
                var ratings = ser.Deserialize<Dictionary<string, object>>(ratingsJson);

                var ratingMap = new Dictionary<string, int>();
                if (ratings != null && ratings.ContainsKey("items"))
                {
                    foreach (var d in DictList(ratings["items"]))
                        ratingMap[Str(d, "subjectId")] = (int)Num(d, "rating");
                }

                _historyList.Children.Clear();
                var list = history != null && history.ContainsKey("items")
                    ? DictList(history["items"])
                    : new List<Dictionary<string, object>>();
                if (list.Count == 0)
                {
                    _historyList.Children.Add(Empty("还没有收听记录"));
                    return;
                }

                bool first = true;
                foreach (var d in list)
                {
                    if (!first) _historyList.Children.Add(Separator());
                    first = false;

                    int rating = 0;
                    ratingMap.TryGetValue(Str(d, "subjectId"), out rating);
                    _historyList.Children.Add(HistoryRow(d, rating));
                }
            }
            catch (Exception ex)
            {
                _historyList.Children.Clear();
                _historyList.Children.Add(Empty("读取失败：" + ex.Message));
            }
        }

        FrameworkElement HistoryRow(Dictionary<string, object> d, int rating)
        {
            var grid = new Grid { Margin = new Thickness(4, 10, 4, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cover = CoverBox(Str(d, "cover"), 46);
            Grid.SetColumn(cover, 0);
            grid.Children.Add(cover);

            var mid = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = Str(d, "title"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var sub = Str(d, "artist");
            var date = Str(d, "date");
            if (!string.IsNullOrEmpty(date) && date.Length >= 4) sub += " · " + date.Substring(0, 4);
            sub += " · " + Ago(Num(d, "startedAt"));
            mid.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (rating > 0) right.Children.Add(Stars(rating, 12));
            else right.Children.Add(new TextBlock { Text = "未评分", FontSize = 12, Foreground = Faint });
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            // 识别错了的条目可以删掉：先点一下变「确认删除」，再点才真删
            string rowId = Str(d, "id");
            var del = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(10, 0, 0, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            var delText = new TextBlock { Text = "删除", FontSize = 12, Foreground = Faint };
            del.Child = delText;
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);

            bool armed = false;
            DispatcherTimer revert = null;
            del.MouseLeftButtonUp += async (s, e) =>
            {
                if (!armed)
                {
                    armed = true;
                    delText.Text = "确认删除";
                    delText.Foreground = NewBrush(0xFF, 0x6B, 0x6B);
                    if (revert == null)
                    {
                        revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        revert.Tick += (s2, e2) =>
                        {
                            revert.Stop();
                            armed = false;
                            delText.Text = "删除";
                            delText.Foreground = Faint;
                        };
                    }
                    revert.Stop();
                    revert.Start();
                    return;
                }
                if (revert != null) revert.Stop();
                delText.Text = "删除中…";
                del.IsEnabled = false;
                try
                {
                    var body = new JavaScriptSerializer().Serialize(
                        new Dictionary<string, object> { { "id", rowId } });
                    var resp = await _http.PostAsync("/api/history/delete",
                        new StringContent(body, Encoding.UTF8, "application/json"));
                    var text = await resp.Content.ReadAsStringAsync();
                    if (text.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0)
                    {
                        LoadHistory();
                    }
                    else
                    {
                        del.IsEnabled = true;
                        delText.Text = "删除失败";
                        delText.Foreground = NewBrush(0xFF, 0x6B, 0x6B);
                    }
                }
                catch (Exception ex)
                {
                    del.IsEnabled = true;
                    delText.Text = "删除失败";
                    delText.Foreground = NewBrush(0xFF, 0x6B, 0x6B);
                    if (_statusLine != null) _statusLine.Text = "删除失败：" + ex.Message;
                }
            };

            return grid;
        }

        /* ------------------------------ 待评分 ------------------------------ */

        async void LoadTodos()
        {
            if (_todoList == null) return;
            try
            {
                var json = await _http.GetStringAsync("/api/todo");
                var ser = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                var d = ser.Deserialize<Dictionary<string, object>>(json);
                var list = d != null && d.ContainsKey("items")
                    ? DictList(d["items"])
                    : new List<Dictionary<string, object>>();

                _todoList.Children.Clear();
                if (list.Count == 0)
                {
                    _todoList.Children.Add(Empty("还没有待评分的专辑\n在浮窗上点「稍后再评」，这张就会出现在这里"));
                    return;
                }

                bool first = true;
                foreach (var it in list)
                {
                    if (!first) _todoList.Children.Add(Separator());
                    first = false;
                    _todoList.Children.Add(TodoRow(it));
                }
            }
            catch (Exception ex)
            {
                _todoList.Children.Clear();
                _todoList.Children.Add(Empty("读取失败：" + ex.Message));
            }
        }

        FrameworkElement TodoRow(Dictionary<string, object> d)
        {
            int subjectId = (int)Num(d, "subjectId");
            string todoId = Str(d, "id");
            string title = Str(d, "title");
            if (string.IsNullOrEmpty(title)) title = Str(d, "album");
            int chosen = (int)Num(d, "rating");

            var grid = new Grid { Margin = new Thickness(4, 10, 4, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cover = CoverBox(Str(d, "cover"), 46);
            Grid.SetColumn(cover, 0);
            Grid.SetRowSpan(cover, 2);
            grid.Children.Add(cover);

            var mid = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(title) ? "未知专辑" : title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var sub = Str(d, "artist");
            var app = Str(d, "app");
            if (!string.IsNullOrEmpty(app) && app.IndexOf("AppleMusicWin", StringComparison.OrdinalIgnoreCase) < 0)
                sub += (sub.Length > 0 ? " · " : "") + app;
            if (string.IsNullOrEmpty(sub)) sub = "待评分";
            sub += " · " + Ago(Num(d, "createdAt")) + "加入";
            if (subjectId <= 0) sub += " · 未绑定豆瓣";
            mid.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            var stars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(stars, 2);
            grid.Children.Add(stars);

            var commentBox = new TextBox
            {
                Text = Str(d, "comment"),
                FontSize = 12,
                MaxLength = 140,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Ink,
                CaretBrush = Accent,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 0, 12, 0),
                IsEnabled = subjectId > 0
            };

            var commentShell = new Border
            {
                CornerRadius = new CornerRadius(14),
                Height = 34,
                Margin = new Thickness(14, 8, 8, 0),
                Background = Base,
                Child = commentBox
            };
            Grid.SetColumn(commentShell, 1);
            Grid.SetRow(commentShell, 1);
            grid.Children.Add(commentShell);

            if (subjectId > 0)
            {
                var starFont = new FontFamily("Segoe UI Symbol");
                var starViews = new List<TextBlock>();
                Action<int> paint = (value) =>
                {
                    for (int k = 0; k < starViews.Count; k++)
                        starViews[k].Foreground = k < value ? Accent : StarOff;
                };

                for (int i = 1; i <= 5; i++)
                {
                    int value = i;
                    var star = new TextBlock
                    {
                        Text = "★",
                        FontFamily = starFont,
                        FontSize = 16,
                        Foreground = StarOff,
                        Margin = new Thickness(1, 0, 1, 0),
                        Cursor = Cursors.Hand
                    };
                    starViews.Add(star);
                    star.MouseEnter += (s2, e) => paint(value);
                    star.MouseLeave += (s2, e) => paint(chosen);
                    star.MouseLeftButtonUp += async (s2, e) =>
                    {
                        chosen = value;
                        paint(chosen);
                        await SaveTodoRatingAsync(subjectId, chosen, commentBox.Text);
                    };
                    stars.Children.Add(star);
                }
                paint(chosen);
            }
            else
            {
                var bind = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 5, 12, 5),
                    Background = Base,
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var bindText = new TextBlock { Text = "去绑定", FontSize = 12, Foreground = Dim };
                bind.Child = bindText;
                bind.MouseLeftButtonUp += (s2, e) =>
                {
                    SwitchTab("bind");
                    if (_searchBox != null) _searchBox.Text = title;
                    SearchAsync();
                };
                stars.Children.Add(bind);
            }

            // 移除（不评也可以直接丢掉）
            var del = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(10, 0, 0, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            var delText = new TextBlock { Text = "移除", FontSize = 12, Foreground = Faint };
            del.Child = delText;
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);
            del.MouseLeftButtonUp += async (s2, e) =>
            {
                del.IsEnabled = false;
                try
                {
                    var body = new JavaScriptSerializer().Serialize(
                        new Dictionary<string, object> { { "id", todoId } });
                    await _http.PostAsync("/api/todo/remove",
                        new StringContent(body, Encoding.UTF8, "application/json"));
                    LoadTodos();
                }
                catch (Exception ex)
                {
                    del.IsEnabled = true;
                    if (_statusLine != null) _statusLine.Text = "移除失败：" + ex.Message;
                }
            };

            // 同步到豆瓣（先在本地存星与评语，再提交）
            var syncBorder = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 0, 16, 0),
                Height = 34,
                Margin = new Thickness(10, 8, 0, 0),
                Background = Accent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            var syncText = new TextBlock
            {
                Text = "同步",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            syncBorder.Child = syncText;
            Grid.SetColumn(syncBorder, 3);
            Grid.SetRow(syncBorder, 1);
            grid.Children.Add(syncBorder);

            if (subjectId <= 0)
            {
                syncBorder.IsEnabled = false;
                syncBorder.Opacity = 0.4;
            }
            else
            {
                syncBorder.MouseLeftButtonUp += async (s2, e) =>
                {
                    if (chosen <= 0)
                    {
                        if (_statusLine != null) _statusLine.Text = "先点星打个分再同步";
                        return;
                    }
                    syncBorder.IsEnabled = false;
                    syncText.Text = "同步中…";
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var save = ser.Serialize(new Dictionary<string, object>
                        {
                            { "subjectId", subjectId },
                            { "rating", chosen },
                            { "comment", commentBox.Text ?? "" }
                        });
                        await _http.PostAsync("/api/rating",
                            new StringContent(save, Encoding.UTF8, "application/json"));

                        // 连评语一起带上，服务端不必再读一遍本地记录
                        var sync = ser.Serialize(new Dictionary<string, object>
                        {
                            { "subjectId", subjectId },
                            { "rating", chosen },
                            { "comment", commentBox.Text ?? "" }
                        });
                        var resp = await _http.PostAsync("/api/sync",
                            new StringContent(sync, Encoding.UTF8, "application/json"));
                        var text = await resp.Content.ReadAsStringAsync();

                        if (text.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0)
                        {
                            if (_statusLine != null) _statusLine.Text = "已同步到豆瓣「听过」：" + title;
                            LoadTodos();
                        }
                        else
                        {
                            syncText.Text = "同步";
                            syncBorder.IsEnabled = true;
                            if (_statusLine != null) _statusLine.Text = "同步失败：" + (ErrorText(text) ?? "豆瓣没有返回成功");
                        }
                    }
                    catch (Exception ex)
                    {
                        syncText.Text = "同步";
                        syncBorder.IsEnabled = true;
                        if (_statusLine != null) _statusLine.Text = "同步失败：" + ex.Message;
                    }
                };
            }

            return grid;
        }

        /// <summary>从服务端返回里取 error 字段</summary>
        static string ErrorText(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = json.IndexOf("\"error\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        /// <summary>在待评分里改星/评语：只写本地，同步靠旁边的「同步」按钮</summary>
        async System.Threading.Tasks.Task SaveTodoRatingAsync(int subjectId, int rating, string comment)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var body = ser.Serialize(new Dictionary<string, object>
                {
                    { "subjectId", subjectId },
                    { "rating", rating },
                    { "comment", comment ?? "" }
                });
                var resp = await _http.PostAsync("/api/rating",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();
                if (_statusLine != null)
                    _statusLine.Text = text.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0
                        ? "已记下 " + rating + " 星，点「同步」写入豆瓣"
                        : "保存失败";
            }
            catch (Exception ex)
            {
                if (_statusLine != null) _statusLine.Text = "保存失败：" + ex.Message;
            }
        }

        async void LoadSettings()
        {
            try
            {
                var json = await _http.GetStringAsync("/api/settings");
                var ser = new JavaScriptSerializer();
                var d = ser.Deserialize<Dictionary<string, object>>(json);
                bool configured = Str(d, "doubanCookie") == "已设置";
                _cookieBox.Text = "";
                _cookieBox.ToolTip = configured ? "已设置，留空表示不修改" : _cookiePlaceholder;
                _statusLine.Text = configured ? "已配置豆瓣登录" : "未配置豆瓣登录（只读评分，无法回写）";

                string mode = Str(d, "playerMode");
                _playerMode = string.IsNullOrEmpty(mode) ? "auto" : mode;
                StyleModePills();

                var user = d != null && d.ContainsKey("doubanUser")
                    ? d["doubanUser"] as Dictionary<string, object>
                    : null;
                string uname = user != null ? Str(user, "name") : "";
                ApplyAccountState(configured, uname);

                // 有 Cookie 但没昵称（比如手动粘贴保存的）：补一次身份验证
                if (configured && string.IsNullOrEmpty(uname)) EnsureProfileAsync();

                UpdateModeHintAsync();
            }
            catch
            {
                _statusLine.Text = "无法读取配置：本地服务未启动";
                if (_doubanStatus != null) _doubanStatus.Text = "本地服务未连接";
            }
        }

        /// <summary>把「现在识别到的播放器」写进识别来源那一行</summary>
        async void UpdateModeHintAsync()
        {
            if (_modeHint == null) return;
            try
            {
                var json = await _http.GetStringAsync("/api/state");
                var ser = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                var d = ser.Deserialize<Dictionary<string, object>>(json);
                var player = d != null && d.ContainsKey("player") ? d["player"] as Dictionary<string, object> : null;
                string appName = player != null ? Str(player, "appName") : "";
                string prefix = _playerMode == "auto"
                    ? "自动 = 谁在播就认谁"
                    : "只认 " + ModeLabel(_playerMode);
                _modeHint.Text = string.IsNullOrEmpty(appName)
                    ? prefix + "；当前没有检测到播放器。"
                    : prefix + "；当前识别：" + appName;
            }
            catch
            {
                // 服务没起来时保持默认提示
            }
        }

        async System.Threading.Tasks.Task SearchAsync()
        {
            var q = (_searchBox.Text ?? "").Trim();
            if (q.Length == 0) return;

            _bindResults.Children.Clear();
            _bindResults.Children.Add(Empty("搜索中…"));

            try
            {
                var json = await _http.GetStringAsync("/api/search?q=" + Uri.EscapeDataString(q));
                var ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var d = ser.Deserialize<Dictionary<string, object>>(json);
                var items = d != null && d.ContainsKey("items")
                    ? DictList(d["items"])
                    : new List<Dictionary<string, object>>();

                _bindResults.Children.Clear();
                if (items.Count == 0)
                {
                    _bindResults.Children.Add(Empty("没有结果"));
                    return;
                }

                bool first = true;
                foreach (var it in items)
                {
                    if (!first) _bindResults.Children.Add(Separator());
                    first = false;
                    _bindResults.Children.Add(BindRow(it));
                }
            }
            catch (Exception ex)
            {
                _bindResults.Children.Clear();
                _bindResults.Children.Add(Empty("搜索失败：" + ex.Message));
            }
        }

        FrameworkElement BindRow(Dictionary<string, object> it)
        {
            var grid = new Grid { Margin = new Thickness(4, 8, 4, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cover = CoverBox(Str(it, "cover"), 40);
            Grid.SetColumn(cover, 0);
            grid.Children.Add(cover);

            var mid = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = Str(it, "title"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            mid.Children.Add(new TextBlock
            {
                Text = Str(it, "raw"),
                FontSize = 11,
                Foreground = Dim,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            double score = Num(it, "score");
            double votes = Num(it, "votes");
            var scoreBox = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            scoreBox.Children.Add(new TextBlock
            {
                Text = score > 0 ? score.ToString("0.0") : "—",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                HorizontalAlignment = HorizontalAlignment.Right
            });
            var votesLabel = VotesLabel(votes);
            if (votesLabel.Length > 0)
            {
                scoreBox.Children.Add(new TextBlock
                {
                    Text = votesLabel,
                    FontSize = 10,
                    Foreground = Faint,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            Grid.SetColumn(scoreBox, 2);
            grid.Children.Add(scoreBox);

            var btn = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = Base,
                Padding = new Thickness(16, 7, 16, 7),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xB8, 0xBC, 0xC2),
                BlurRadius = 6,
                ShadowDepth = 2,
                Direction = 315,
                Opacity = 0.8
            };
            var btnText = new TextBlock { Text = "绑定", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Dim };
            btn.Child = btnText;
            Grid.SetColumn(btn, 3);
            grid.Children.Add(btn);

            int subjectId = (int)Num(it, "id");
            btn.MouseLeftButtonUp += async (s, e) =>
            {
                btnText.Text = "绑定中";
                btn.IsEnabled = false;
                try
                {
                    var body = "{\"subjectId\":" + subjectId + "}";
                    var resp = await _http.PostAsync("/api/pick",
                        new StringContent(body, Encoding.UTF8, "application/json"));
                    var text = await resp.Content.ReadAsStringAsync();
                    if (text.Contains("\"ok\":true"))
                    {
                        btnText.Text = "已绑定";
                        btn.Background = Base;
                        btnText.Foreground = Mint;
                    }
                    else
                    {
                        btnText.Text = "绑定";
                        btn.IsEnabled = true;
                        _statusLine.Text = "当前没有正在播放的曲目，无法绑定";
                    }
                }
                catch (Exception ex)
                {
                    btnText.Text = "绑定";
                    btn.IsEnabled = true;
                    _statusLine.Text = "绑定失败：" + ex.Message;
                }
            };

            return grid;
        }

        async System.Threading.Tasks.Task SaveCookieAsync()
        {
            var value = (_cookieBox.Text ?? "").Trim();
            if (value.Length == 0)
            {
                _saveHint.Text = "请先粘贴 Cookie";
                _saveHint.Foreground = Faint;
                return;
            }

            _btnSave.IsEnabled = false;
            _btnSaveText.Text = "保存中…";
            try
            {
                var ser = new JavaScriptSerializer();
                var body = ser.Serialize(new Dictionary<string, object> { { "doubanCookie", value } });
                var resp = await _http.PostAsync("/api/settings",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();

                if (text.Contains("\"ok\":true"))
                {
                    _saveHint.Text = "已保存到本机";
                    _saveHint.Foreground = Mint;
                    _cookieBox.Text = "";
                    _cookieBox.ToolTip = "已设置，留空表示不修改";
                    _statusLine.Text = "已配置豆瓣登录";
                    EnsureProfileAsync();
                }
                else
                {
                    _saveHint.Text = "保存失败";
                    _saveHint.Foreground = NewBrush(0xFF, 0x6B, 0x6B);
                }
            }
            catch (Exception ex)
            {
                _saveHint.Text = "保存失败：" + ex.Message;
                _saveHint.Foreground = NewBrush(0xFF, 0x6B, 0x6B);
            }
            _btnSaveText.Text = "保存";
            _btnSave.IsEnabled = true;
        }

        /* -------------------------------- 零件 -------------------------------- */

        static Border CoverBox(string coverUrl, double size)
        {
            var bd = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(12),
                Background = Base,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!IsGlass)
            {
                bd.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0xB8, 0xBC, 0xC2),
                    BlurRadius = 6,
                    ShadowDepth = 2,
                    Direction = 315,
                    Opacity = 0.7
                };
            }
            if (!string.IsNullOrEmpty(coverUrl))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(BaseUrl + "/api/cover?u=" + Uri.EscapeDataString(coverUrl));
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.EndInit();
                    bd.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
            return bd;
        }

        static StackPanel Stars(int rating, double size)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            // 指定符号字体：否则 ★ 会落到彩色 emoji 字体上，Foreground 不起作用
            var font = new FontFamily("Segoe UI Symbol");
            for (int i = 1; i <= 5; i++)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "★",
                    FontFamily = font,
                    FontSize = size,
                    Foreground = i <= rating ? Accent : StarOff,
                    Margin = new Thickness(0, 0, 1, 0)
                });
            }
            return sp;
        }

        static Border Separator()
        {
            return new Border { Height = 1, Background = Line, Margin = new Thickness(0, 0, 0, 0) };
        }

        static TextBlock Empty(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Faint,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 520,
                Margin = new Thickness(0, 40, 0, 40)
            };
        }

        /// <summary>评分人数：过万显示「x.x 万」</summary>
        static string VotesLabel(double votes)
        {
            if (votes <= 0) return "";
            if (votes >= 10000) return (votes / 10000.0).ToString("0.#") + " 万人";
            return ((int)votes) + " 人";
        }

        static string Ago(double ts)
        {
            if (ts <= 0) return "";
            var d = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts;
            var m = Math.Max(0, (long)(d / 60000));
            if (m < 1) return "刚刚";
            if (m < 60) return m + " 分钟前";
            var h = m / 60;
            if (h < 24) return h + " 小时前";
            return (h / 24) + " 天前";
        }

        /// <summary>JavaScriptSerializer 的数组不一定是 object[]，统一按 IEnumerable 解析</summary>
        static List<Dictionary<string, object>> DictList(object raw)
        {
            var list = new List<Dictionary<string, object>>();
            var seq = raw as System.Collections.IEnumerable;
            if (seq == null || raw is string) return list;
            foreach (var o in seq)
            {
                var d = o as Dictionary<string, object>;
                if (d != null) list.Add(d);
            }
            return list;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return "";
            return Convert.ToString(d[key]);
        }

        static double Num(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return 0;
            var v = d[key];
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            double outv;
            double.TryParse(Convert.ToString(v), NumberStyles.Any, CultureInfo.InvariantCulture, out outv);
            return outv;
        }

        static Brush NewBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
