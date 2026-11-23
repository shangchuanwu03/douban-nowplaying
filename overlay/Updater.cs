using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DoubanNowPlaying
{
    /// <summary>
    /// 检查更新与覆盖安装。
    ///
    /// 只读 GitHub 的公开 release 接口，不发送任何本机或账号信息。
    /// 放在 C# 里而不是本地 Node 服务里，是因为 .NET 走 Windows 证书库和系统代理，
    /// Node 自带的 CA 库在装了抓包代理 / 企业根证书的机器上会直接报
    /// UNABLE_TO_GET_ISSUER_CERT_LOCALLY —— 那正是「检查更新」最容易失效的地方。
    /// </summary>
    internal static class Updater
    {
        public const string Repo = "shangchuanwu03/douban-nowplaying";
        public static readonly string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
        static readonly string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string UserAgent = "DoubanNowPlaying-Updater";

        /* ------------------------------ 版本 ------------------------------ */

        /// <summary>本地版本：优先读安装目录里的 package.json，其次读卸载项里的 DisplayVersion。</summary>
        public static string LocalVersion()
        {
            try
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "package.json");
                if (File.Exists(file))
                {
                    var m = Regex.Match(File.ReadAllText(file), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
            catch { }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DoubanNowPlaying"))
                {
                    var v = key == null ? null : Convert.ToString(key.GetValue("DisplayVersion"));
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            catch { }

            return "0.0.0";
        }

        static int[] ParseVersion(string v)
        {
            var parts = (v ?? "").Trim().TrimStart('v', 'V').Split('.');
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                int.TryParse(parts[i].Trim(), out n);
                nums[i] = n;
            }
            return nums;
        }

        /// <summary>a 比 b 新返回 &gt;0，相同返回 0</summary>
        public static int CompareVersion(string a, string b)
        {
            var pa = ParseVersion(a);
            var pb = ParseVersion(b);
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < pa.Length ? pa[i] : 0;
                int y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        /* ------------------------------ 检查 ------------------------------ */

        public class ReleaseInfo
        {
            public string Latest = "";
            public string PageUrl = ReleasesPage;
            public string Notes = "";
            public string AssetName = "";
            public string AssetUrl = "";
            public long AssetSize;
            public string Current = "";
            public bool HasUpdate;

            public string SizeText
            {
                get
                {
                    if (AssetSize <= 0) return "";
                    return (AssetSize / 1024.0 / 1024.0).ToString("0.0") + " MB";
                }
            }
        }

        static HttpClient NewClient(TimeSpan timeout)
        {
            // .NET Framework 默认可能只协商 TLS 1.0，显式补上 1.2
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }

            var c = new HttpClient { Timeout = timeout };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            return c;
        }

        static string SummarizeNotes(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var s = body;
            s = Regex.Replace(s, "^#+\\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, "`([^`]*)`", "$1");
            s = Regex.Replace(s, "\\[([^\\]]*)\\]\\([^)]*\\)", "$1");
            s = Regex.Replace(s, "\\*\\*([^*]*)\\*\\*", "$1");
            s = Regex.Replace(s, "^[-*]\\s+", "· ", RegexOptions.Multiline);
            s = Regex.Replace(s, "\n{3,}", "\n\n").Trim();
            return s.Length > 240 ? s.Substring(0, 240) + "…" : s;
        }

        public static async Task<ReleaseInfo> CheckAsync()
        {
            using (var http = NewClient(TimeSpan.FromSeconds(20)))
            {
                var resp = await http.GetAsync(ApiUrl);
                int code = (int)resp.StatusCode;
                if (code == 403 || code == 429)
                    throw new Exception("GitHub 接口请求太频繁，过几分钟再试");
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("GitHub 返回 " + code);

                var text = await resp.Content.ReadAsStringAsync();
                var ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var root = ser.Deserialize<Dictionary<string, object>>(text);

                var info = new ReleaseInfo();
                info.Current = LocalVersion();
                info.Latest = Convert.ToString(Get(root, "tag_name")).Trim().TrimStart('v', 'V');
                var page = Convert.ToString(Get(root, "html_url"));
                if (!string.IsNullOrEmpty(page)) info.PageUrl = page;
                info.Notes = SummarizeNotes(Convert.ToString(Get(root, "body")));

                // 注意：JavaScriptSerializer 把 JSON 数组反序列化成 ArrayList，不是 object[]
                var raw = Get(root, "assets") as System.Collections.IEnumerable;
                var assets = new List<Dictionary<string, object>>();
                if (raw != null)
                {
                    foreach (var item in raw)
                    {
                        var a = item as Dictionary<string, object>;
                        if (a != null) assets.Add(a);
                    }
                }

                // 优先挑名字带 Setup 的那个
                foreach (var a in assets)
                {
                    var name = Convert.ToString(Get(a, "name"));
                    if (name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        FillAsset(info, a);
                        break;
                    }
                }
                if (string.IsNullOrEmpty(info.AssetUrl))
                {
                    foreach (var a in assets)
                    {
                        var name = Convert.ToString(Get(a, "name"));
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            FillAsset(info, a);
                            break;
                        }
                    }
                }

                info.HasUpdate = CompareVersion(info.Latest, info.Current) > 0;
                return info;
            }
        }

        static void FillAsset(ReleaseInfo info, Dictionary<string, object> a)
        {
            info.AssetName = Convert.ToString(Get(a, "name"));
            info.AssetUrl = Convert.ToString(Get(a, "browser_download_url"));
            var size = Get(a, "size");
            info.AssetSize = size == null ? 0 : Convert.ToInt64(size);
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v;
            return null;
        }

        /* ------------------------------ 下载 ------------------------------ */

        public static async Task<long> DownloadAsync(ReleaseInfo rel, string dest,
            Action<long, long> progress = null)
        {
            if (string.IsNullOrEmpty(rel.AssetUrl)) throw new Exception("这个版本没有提供安装包");

            var uri = new Uri(rel.AssetUrl);
            if (uri.Scheme != "https") throw new Exception("下载地址不是 HTTPS，已拒绝");
            var host = uri.Host.ToLowerInvariant();
            bool trusted = host == "github.com" || host.EndsWith(".github.com")
                        || host.EndsWith(".githubusercontent.com");
            if (!trusted) throw new Exception("下载地址不在 GitHub 域名下，已拒绝");

            using (var http = NewClient(TimeSpan.FromMinutes(5)))
            using (var resp = await http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("下载失败：HTTP " + (int)resp.StatusCode);

                long total = resp.Content.Headers.ContentLength ?? rel.AssetSize;
                long done = 0;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var fs = File.Create(dest))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await fs.WriteAsync(buf, 0, n);
                        done += n;
                        if (progress != null) progress(done, total);
                    }
                }

                if (done < 50 * 1024) throw new Exception("下载的安装包不完整");
                return done;
            }
        }

        /* --------------------------- 覆盖安装 --------------------------- */

        /// <summary>PowerShell 单引号字符串转义</summary>
        static string PsQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "''") + "'";
        }

        public static string AppDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        /// <summary>
        /// 装一个独立进程：等浮窗退出后，静默把安装包覆盖到当前目录（保留 data/），
        /// 成功就把程序重新拉起来，失败就打开下载页让人手动处理。
        /// </summary>
        /// <summary>拼出覆盖安装脚本（单独抽出来，方便离线校验语法）</summary>
        public static string BuildInstallScript(string setupPath, int overlayPid)
        {
            var appDir = AppDir;
            var lines = new[]
            {
                "$ErrorActionPreference = 'SilentlyContinue'",
                "$overlayPid = " + overlayPid,
                "$setup = " + PsQuote(setupPath),
                "$appDir = " + PsQuote(appDir),
                "$releases = " + PsQuote(ReleasesPage),
                "",
                "# 等浮窗退出，最多 30 秒",
                "for ($i = 0; $i -lt 60; $i++) {",
                "    if (-not (Get-Process -Id $overlayPid -ErrorAction SilentlyContinue)) { break }",
                "    Start-Sleep -Milliseconds 500",
                "}",
                "if (Get-Process -Id $overlayPid -ErrorAction SilentlyContinue) { Stop-Process -Id $overlayPid -Force }",
                "Start-Sleep -Milliseconds 700",
                "",
                "# 兜底：清掉可能没退干净的本地服务",
                "Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" |",
                "    Where-Object { $_.CommandLine -like \"*$appDir*\" } |",
                "    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }",
                "Start-Sleep -Milliseconds 500",
                "",
                "# 静默覆盖安装到原目录",
                "$argLine = " + PsQuote("/S /D=\"" + appDir + "\""),
                "$p = Start-Process -FilePath $setup -ArgumentList $argLine -Wait -PassThru",
                "Start-Sleep -Milliseconds 800",
                "",
                "$exe = Join-Path $appDir 'douban-overlay.exe'",
                "if ($p -and $p.ExitCode -eq 0 -and (Test-Path $exe)) {",
                "    Start-Process -FilePath $exe",
                "} else {",
                "    Start-Process $releases",
                "}",
                "Remove-Item $setup -Force",
                "",
            };

            return string.Join("\r\n", lines);
        }

        public static void ScheduleInstall(string setupPath, int overlayPid)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "dnp-update-" + DateTime.Now.Ticks + ".ps1");
            // Windows PowerShell 5.1 只有看到 BOM 才按 UTF-8 读脚本
            File.WriteAllText(scriptPath, BuildInstallScript(setupPath, overlayPid), new UTF8Encoding(true));

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }
    }
}
