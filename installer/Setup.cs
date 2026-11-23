using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DoubanNowPlayingSetup
{
    /// <summary>
    /// 「豆瓣正在听」安装程序：把内置的运行文件释放到用户目录，
    /// 建开始菜单/桌面快捷方式、注册卸载入口。不包含任何个人数据。
    /// </summary>
    public class SetupForm : Form
    {
        const string AppName = "豆瓣正在听";
        const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DoubanNowPlaying";

        static readonly string DefaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "DoubanNowPlaying");

        TextBox _pathBox;
        CheckBox _desktop;
        CheckBox _runAfter;
        Button _install;
        Button _close;
        ProgressBar _bar;
        Label _status;
        bool _working;

        public SetupForm()
        {
            Text = AppName + " 安装程序";
            ClientSize = new Size(560, 320);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x0B, 0x13, 0x22);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label
            {
                Text = AppName,
                Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0xE4, 0xB8, 0x63),
                Location = new Point(28, 22),
                AutoSize = true
            };
            var sub = new Label
            {
                Text = "实时识别正在播放的专辑 · 展示豆瓣评分 · 可回写短评",
                ForeColor = Color.FromArgb(0x7C, 0x9C, 0xC4),
                Location = new Point(30, 62),
                AutoSize = true
            };

            var pathLabel = new Label
            {
                Text = "安装位置",
                ForeColor = Color.FromArgb(0xA9, 0xC0, 0xDC),
                Location = new Point(30, 100),
                AutoSize = true
            };

            _pathBox = new TextBox
            {
                Text = DefaultDir,
                Location = new Point(30, 124),
                Width = 400,
                BackColor = Color.FromArgb(0x16, 0x23, 0x3A),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var browse = new Button
            {
                Text = "浏览…",
                Location = new Point(440, 122),
                Size = new Size(90, 26)
            };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = _pathBox.Text })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) _pathBox.Text = dlg.SelectedPath;
                }
            };

            _desktop = new CheckBox
            {
                Text = "创建桌面快捷方式",
                Checked = true,
                ForeColor = Color.FromArgb(0xC7, 0xD8, 0xEC),
                Location = new Point(30, 162),
                AutoSize = true
            };

            _runAfter = new CheckBox
            {
                Text = "安装完成后立即启动",
                Checked = true,
                ForeColor = Color.FromArgb(0xC7, 0xD8, 0xEC),
                Location = new Point(30, 188),
                AutoSize = true
            };

            var nodeHint = new Label
            {
                Text = "运行需要 Node.js（本机已" + (HasNode() ? "检测到" : "未检测到，请先安装 LTS 版本") + "）",
                ForeColor = HasNode() ? Color.FromArgb(0x7C, 0x9C, 0xC4) : Color.FromArgb(0xFF, 0x7B, 0x72),
                Location = new Point(30, 214),
                AutoSize = true
            };

            _bar = new ProgressBar
            {
                Location = new Point(30, 246),
                Size = new Size(500, 8),
                Style = ProgressBarStyle.Continuous
            };

            _status = new Label
            {
                Text = "",
                ForeColor = Color.FromArgb(0xA9, 0xC0, 0xDC),
                Location = new Point(30, 260),
                Size = new Size(500, 20)
            };

            _install = new Button
            {
                Text = "开始安装",
                Location = new Point(330, 282),
                Size = new Size(100, 30),
                BackColor = Color.FromArgb(0xE4, 0xB8, 0x63),
                ForeColor = Color.FromArgb(0x06, 0x0A, 0x13),
                FlatStyle = FlatStyle.Flat
            };
            _install.FlatAppearance.BorderSize = 0;
            _install.Click += (s, e) => StartInstall();

            _close = new Button
            {
                Text = "关闭",
                Location = new Point(440, 282),
                Size = new Size(90, 30),
                BackColor = Color.FromArgb(0x16, 0x23, 0x3A),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _close.FlatAppearance.BorderColor = Color.FromArgb(0x33, 0x51, 0x7A);
            _close.Click += (s, e) => { if (!_working) Close(); };

            Controls.AddRange(new Control[] { title, sub, pathLabel, _pathBox, browse,
                _desktop, _runAfter, nodeHint, _bar, _status, _install, _close });
        }

        static bool HasNode()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return File.Exists(Path.Combine(pf, "nodejs", "node.exe"));
        }

        void StartInstall()
        {
            if (_working) return;
            var dir = NormalizeDir(_pathBox.Text.Trim());
            _pathBox.Text = dir;

            _working = true;
            _install.Enabled = false;
            _close.Enabled = false;
            _bar.Style = ProgressBarStyle.Marquee;

            var t = new Thread(() =>
            {
                try
                {
                    DoInstall(dir, _desktop.Checked);
                    BeginInvoke(new Action(() =>
                    {
                        _bar.Style = ProgressBarStyle.Continuous;
                        _bar.Value = 100;
                        _status.Text = "安装完成";
                        MessageBox.Show(this, "安装完成：\n" + dir + "\n\n开始菜单和桌面都有快捷方式。",
                            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        if (_runAfter.Checked)
                        {
                            try { Process.Start(new ProcessStartInfo(Path.Combine(dir, "douban-overlay.exe")) { WorkingDirectory = dir }); }
                            catch { }
                        }
                        Close();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _bar.Style = ProgressBarStyle.Continuous;
                        _status.Text = "安装失败";
                        MessageBox.Show(this, "安装失败：\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _install.Enabled = true;
                        _close.Enabled = true;
                        _working = false;
                    }));
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>盘符根目录（D:\ 这种）自动补一层子目录，避免把文件撒到根上</summary>
        static string NormalizeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return DefaultDir;
            dir = dir.Trim();
            if (dir.Length == 0) return DefaultDir;
            if (dir.Length == 2 && dir[1] == ':') dir += "\\";   // "G:" → "G:\"

            var root = Path.GetPathRoot(dir);
            var trimmed = dir.TrimEnd('\\', '/');
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(root.TrimEnd('\\', '/'), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                dir = Path.Combine(root, "DoubanNowPlaying");      // 盘符根 → 加一层
            }
            return dir;
        }

        static void ProbeWritable(string dir)
        {
            var probe = Path.Combine(dir, ".dnp-write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var fs = File.Create(probe)) { }
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    "目标目录不可写：\n" + dir + "\n\n" + ex.Message +
                    "\n\n可以换一个目录（例如 D:\\DoubanNowPlaying），或者右键安装包选择「以管理员身份运行」。");
            }
        }

        static void DoInstall(string dir, bool createDesktop)
        {
            dir = NormalizeDir(dir);
            Directory.CreateDirectory(dir);
            ProbeWritable(dir);

            // 直接从内嵌的数据流解压，不经过临时文件：
            // 有些机器会把「往 Temp 写 zip」当成可疑行为拦掉。
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (stream == null) throw new Exception("安装包损坏：找不到内置文件");
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
                        if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(entry.Name)) continue;

                        var target = Path.Combine(dir, rel);
                        var parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                        try
                        {
                            using (var es = entry.Open())
                            using (var fs = File.Create(target))
                                es.CopyTo(fs);
                        }
                        catch (IOException)
                        {
                            throw new Exception("文件被占用，装不进去：" + target +
                                                "\n请先退出正在运行的「豆瓣正在听」，再试一次。");
                        }
                    }
                }
            }

            // 空的 data 目录：首次运行会自己写
            Directory.CreateDirectory(Path.Combine(dir, "data"));

            WriteUninstaller(dir);
            CreateShortcuts(dir, createDesktop);
            WriteRegistry(dir);
        }

        static void CopyDirectory(string from, string to)
        {
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(from.Length).TrimStart('\\');
                var target = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        static void WriteUninstaller(string dir)
        {
            // 用占位符替换而不是 string.Format：PowerShell 里的 { } 会被 Format 当成占位符
            var ps1 = @"$ErrorActionPreference = 'SilentlyContinue'
$dir = '__DIR__'
Start-Sleep -Seconds 1
Get-Process douban-overlay -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ""$dir*"" } | Stop-Process -Force
Get-CimInstance Win32_Process -Filter ""Name='node.exe'"" | Where-Object { $_.CommandLine -like ""*$dir*"" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 1
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DoubanNowPlaying' -Recurse -Force
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\__APP__') -Recurse -Force
Remove-Item (Join-Path $env:USERPROFILE 'Desktop\__APP__.lnk') -Force
Set-Location $env:TEMP
Remove-Item $dir -Recurse -Force
";
            ps1 = ps1.Replace("__DIR__", dir.Replace("'", "''")).Replace("__APP__", AppName);
            File.WriteAllText(Path.Combine(dir, "uninstall.ps1"), ps1, new UTF8Encoding(true));

            // 批处理保持纯 ASCII，只负责把脚本复制到 TEMP 再执行（避免边读边删自己）
            var bat = string.Join("\r\n", new[]
            {
                "@echo off",
                "copy /y \"%~dp0uninstall.ps1\" \"%TEMP%\\dnp-uninstall.ps1\" >nul",
                "start \"\" /min powershell -NoProfile -ExecutionPolicy Bypass -File \"%TEMP%\\dnp-uninstall.ps1\"",
                "exit /b",
                ""
            });
            File.WriteAllText(Path.Combine(dir, "卸载.bat"), bat, Encoding.ASCII);
        }

        /// <summary>创建开始菜单与桌面快捷方式</summary>
        static void CreateShortcuts(string dir, bool desktop)
        {
            var exe = Path.Combine(dir, "douban-overlay.exe");
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
            Directory.CreateDirectory(programs);

            MakeShortcut(Path.Combine(programs, AppName + ".lnk"), exe, dir, "");
            MakeShortcut(Path.Combine(programs, "管理.lnk"), exe, dir, "--settings");
            MakeShortcut(Path.Combine(programs, "卸载.lnk"), Path.Combine(dir, "卸载.bat"), dir, "");
            var guide = Path.Combine(dir, "使用教程.html");
            if (File.Exists(guide))
                MakeShortcut(Path.Combine(programs, "使用教程.lnk"), guide, dir, "");

            if (desktop)
            {
                var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                MakeShortcut(Path.Combine(desktopDir, AppName + ".lnk"), exe, dir, "");
            }
        }

        static void MakeShortcut(string lnkPath, string target, string workDir, string args)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var lnkType = lnk.GetType();
                lnkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lnkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                if (!string.IsNullOrEmpty(args))
                    lnkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { args });
                lnkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lnkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch { }
        }

        static void WriteRegistry(string dir)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    if (key == null) return;
                    key.SetValue("DisplayName", AppName);
                    key.SetValue("DisplayVersion", "1.5.5");
                    key.SetValue("Publisher", "DoubanNowPlaying");
                    key.SetValue("InstallLocation", dir);
                    key.SetValue("DisplayIcon", Path.Combine(dir, "douban-overlay.exe"));
                    key.SetValue("UninstallString", "\"" + Path.Combine(dir, "卸载.bat") + "\"");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try
                    {
                        long size = 0;
                        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                            size += new FileInfo(f).Length;
                        key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
                    }
                    catch { }
                }
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false;
            string customDir = null;
            foreach (var a in args)
            {
                if (string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)) silent = true;
                if (a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                    customDir = a.Substring(3).Trim().Trim('"');
            }
            if (silent)
            {
                try
                {
                    DoInstall(string.IsNullOrEmpty(customDir) ? DefaultDir : customDir, true);
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "dnp-setup-error.log"),
                            DateTime.Now + "\n" + ex.ToString(), Encoding.UTF8);
                    }
                    catch { }
                    Environment.Exit(1);
                }
            }

            Application.Run(new SetupForm());
        }
    }
}
