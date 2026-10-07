using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

sealed class FlagshipRouterApp : Form
{
    const int DefaultPort = 20120;
    const string AppName = "FlagshipRouter";
    const string MutexName = @"Global\FlagshipRouter_SingleInstance";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "FlagshipRouter";

    // Auto-update: which GitHub repo publishes FlagshipRouter-windows-x64.zip
    // releases. CI on that repo rebuilds on upstream changes (see
    // .github/workflows/upstream-build.yml on the build branch).
    const string UpdateOwner = "theRizwan";
    const string UpdateRepo = "FlagshipRouter";
    const string UpdateAsset = "FlagshipRouter-windows-x64.zip";
    // Baked at publish time; CI stamps this via /p:AppVersion=<sha>.
    // Defaults to dev-local when built without stamping.
    static string StampedVersion = System.IO.File.Exists(VersionFile) ? System.IO.File.ReadAllText(VersionFile).Trim() : "dev-local";
    static readonly string VersionFile = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "app.version");

    // Per-Monitor DPI Awareness v2 — without this Windows bitmap-scales the
    // WebView2 surface on scaled displays, which renders text blurry ("glare").
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    // GPU flags for WebView2 — allow hardware acceleration everywhere.
    const string WEBVIEW_EXTRA_ARGS = "--enable-features=msWebView2DefaultGPURasterization";

    static int Port = DefaultPort;
    static string ServerDir = "";
    static string ServerEntry = "";
    static string NodeExe = "";
    static string LogFile = "";
    static Mutex SingleMutex;

    Process serverProc;
    NotifyIcon tray;
    WebView2 web;
    System.Windows.Forms.Timer healthTimer;
    ToolStripMenuItem statusItem;
    ToolStripMenuItem updateItem;
    int crashCount;
    DateTime lastStartUtc = DateTime.MinValue;
    bool quitting;
    bool webReady;
    bool serverSeenAlive;
    int navRetryCount;

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        ParseArgs(args);

        bool createdNew;
        SingleMutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            OpenInBrowser();
            return;
        }

        EnsureAutoStart();
        Application.Run(new FlagshipRouterApp());
    }

    static void ParseArgs(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        ServerDir = Path.Combine(baseDir, "server");
        ServerEntry = Path.Combine(ServerDir, "server.js");
        if (!File.Exists(ServerEntry))
            ServerEntry = Path.Combine(ServerDir, "custom-server.js");
        NodeExe = FindNode();
        LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlagshipRouter", "logs", "server.log");

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
            {
                int p;
                if (int.TryParse(args[i + 1], out p) && p > 0 && p < 65536) Port = p;
                i++;
            }
        }
    }

    static string FindNode()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "node", "node.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    static void EnsureAutoStart()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;
                var exe = Path.Combine(AppContext.BaseDirectory, "FlagshipRouter.exe");
                var desired = "\"" + exe + "\"";
                var existing = key.GetValue(RunValueName) as string;
                if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(RunValueName, desired);
            }
        }
        catch { }
    }

    static void RemoveAutoStart()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null && key.GetValue(RunValueName) != null)
                    key.DeleteValue(RunValueName);
            }
        }
        catch { }
    }

    static bool IsAutoStartOn()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(RunValueName) != null;
            }
        }
        catch { return false; }
    }

    static void OpenInBrowser()
    {
        try { Process.Start(new ProcessStartInfo("http://localhost:" + Port + "/dashboard") { UseShellExecute = true }); }
        catch { }
    }

    string DashboardUrl { get { return "http://localhost:" + Port + "/dashboard"; } }

    FlagshipRouterApp()
    {
        Text = AppName;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 800);
        MinimumSize = new Size(900, 600);
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);
        }
        catch { }

        web = new WebView2 { Dock = DockStyle.Fill };
        // CompositeMode = fast path: WebView2 bitmap is presented directly,
        // bypassing extra WinForms composition passes.
        web.DefaultBackgroundColor = Color.White;
        Controls.Add(web);

        tray = new NotifyIcon();
        tray.Icon = Icon ?? SystemIcons.Application;
        tray.Text = AppName + " (starting...)";
        tray.Visible = true;
        tray.DoubleClick += (s, e) => ShowWindow();

        var menu = new ContextMenuStrip();
        statusItem = new ToolStripMenuItem(AppName + " - starting...") { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add("Open " + AppName, null, (s, e) => ShowWindow());
        var autoItem = new ToolStripMenuItem(IsAutoStartOn() ? "✓ Start with Windows" : "Start with Windows");
        autoItem.Click += (s, e) =>
        {
            if (IsAutoStartOn()) { RemoveAutoStart(); autoItem.Text = "Start with Windows"; }
            else { EnsureAutoStart(); autoItem.Text = "✓ Start with Windows"; }
        };
        menu.Items.Add(autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart Server", null, (s, e) => RestartServer());
        updateItem = new ToolStripMenuItem("Check for Updates");
        updateItem.Click += (s, e) => CheckForUpdates(manual: true);
        menu.Items.Add(updateItem);
        menu.Items.Add("Quit", null, (s, e) => QuitApp());
        tray.ContextMenuStrip = menu;

        healthTimer = new System.Windows.Forms.Timer();
        healthTimer.Interval = 2000;
        healthTimer.Tick += (s, e) =>
        {
            if (quitting) return;
            bool alive = IsServerAliveFast();
            statusItem.Text = alive ? AppName + " - running on port " + Port : AppName + " - restarting...";
            tray.Text = alive ? AppName + " (port " + Port + ")" : AppName + " (restarting...)";
            if (!alive) StartServer();
        };

        FormClosing += (s, e) =>
        {
            if (!quitting)
            {
                e.Cancel = true;
                HideToTray();
            }
        };

        Shown += (s, e) => InitWeb();
        StartServer();
        healthTimer.Start();
        Task.Run(() => { Thread.Sleep(20000); if (!quitting) CheckForUpdates(manual: false); });
    }

    void ShowWindow()
    {
        if (Visible && WindowState == FormWindowState.Normal)
        {
            Activate();
            return;
        }
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    void HideToTray()
    {
        Hide();
        tray.ShowBalloonTip(3000, AppName, AppName + " keeps running in the tray. Right-click the tray icon to quit.", ToolTipIcon.Info);
    }

    async void InitWeb()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlagshipRouter", "webview2");
            Directory.CreateDirectory(dataDir);
            var options = new CoreWebView2EnvironmentOptions
            {
                // Warm the GPU cache and keep the compositor snappy under load.
                AdditionalBrowserArguments = WEBVIEW_EXTRA_ARGS
            };
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await web.EnsureCoreWebView2Async(env);
            var s = web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = true;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            // Prefetch pages in the background on hover/link render so
            // dashboard page-to-page navigation starts instantly.
            try { web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "try{document.addEventListener('mouseover',function(e){" +
                "var a=e.target&&e.target.closest?e.target.closest('a[href^=\"/dashboard\"]'):null;" +
                "if(a&&!a.dataset.frPre){a.dataset.frPre='1';fetch(a.href,{method:'HEAD',credentials:'same-origin'}).catch(function(){})}" +
                "},true)}catch(e){}"); } catch { }
            // Keep visual size exact under DPI scaling (no fractional snapping blur).
            web.ZoomFactor = 1.0;
            webReady = true;
            NavigateWhenReady();
        }
        catch (Exception ex) { Log("webview init failed: " + ex.Message); }
    }

    void NavigateWhenReady()
    {
        Task.Run(() =>
        {
            // Fast probe loop: 150ms interval — first paint lands as soon as
            // the port answers instead of waiting out the 1s poll period.
            for (int i = 0; i < 600; i++)
            {
                if (quitting) return;
                if (IsServerAliveFast()) { serverSeenAlive = true; break; }
                Thread.Sleep(150);
            }
            if (quitting || !IsServerAliveFast()) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    navRetryCount = 0;
                    if (webReady && web.CoreWebView2 != null)
                        web.CoreWebView2.Navigate(DashboardUrl);
                }));
            }
            catch { }
        });
    }

    // Warmup: after the server answers, hit the heaviest dashboard routes so
    // Next.js compiles/caches them before the user clicks.
    static readonly string[] WarmupPaths = new string[]
    {
        "/dashboard/cli-tools",
        "/dashboard",
        "/api/cli-tools/all-statuses",
        "/dashboard/usage",
        "/dashboard/models",
        "/dashboard/providers"
    };

    void WarmupServer()
    {
        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(1500);
                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("User-Agent", "FlagshipRouter-Warmup");
                    foreach (var p in WarmupPaths)
                    {
                        if (quitting) return;
                        try { client.DownloadString("http://127.0.0.1:" + Port + p); }
                        catch { }
                    }
                }
                Log("warmup done");
            }
            catch { }
        });
    }

    // Steady-state: keep the server's route-compilation cache warm so the
    // user never pays a cold compile (~2s) for page navigation. Ping the
    // main dashboard every 30s.
    System.Windows.Forms.Timer warmKeeper;
    void StartWarmKeeper()
    {
        warmKeeper = new System.Windows.Forms.Timer();
        warmKeeper.Interval = 30000;
        warmKeeper.Tick += (s, e) =>
        {
            if (quitting) return;
            try
            {
                using (var c = new System.Net.WebClient())
                {
                    c.Headers.Add("User-Agent", "FlagshipRouter-Keeper");
                    c.DownloadString("http://127.0.0.1:" + Port + "/dashboard");
                }
            }
            catch { }
        };
        warmKeeper.Start();
    }

    bool IsServerAliveFast()
    {
        try
        {
            using (var c = new TcpClient())
            {
                var r = c.BeginConnect("127.0.0.1", Port, null, null);
                return r.AsyncWaitHandle.WaitOne(120) && c.Connected;
            }
        }
        catch { return false; }
    }

    bool IsServerAlive()
    {
        if (serverProc == null || serverProc.HasExited) return false;
        return IsServerAliveFast();
    }

    void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
            File.AppendAllText(LogFile, DateTime.Now.ToString("s") + " " + line + Environment.NewLine);
        }
        catch { }
    }

    void StartServer()
    {
        try
        {
            if (serverProc != null && !serverProc.HasExited) return;
            if (NodeExe == null || !File.Exists(ServerEntry))
            {
                Log("cannot start: node=" + (NodeExe ?? "missing") + " entry=" + ServerEntry);
                return;
            }
            var sinceStart = DateTime.UtcNow - lastStartUtc;
            if (sinceStart.TotalSeconds < 30) crashCount++;
            else crashCount = 0;
            if (crashCount >= 5)
            {
                Log("too many rapid crashes, waiting 60s");
                Thread.Sleep(60000);
                crashCount = 0;
            }
            lastStartUtc = DateTime.UtcNow;
            var psi = new ProcessStartInfo(NodeExe, "\"" + ServerEntry + "\"")
            {
                WorkingDirectory = ServerDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.EnvironmentVariables["PORT"] = Port.ToString();
            psi.EnvironmentVariables["HOSTNAME"] = "0.0.0.0";
            serverProc = Process.Start(psi);
            Log("server started pid=" + (serverProc != null ? serverProc.Id.ToString() : "?"));
            WarmupServer();
            StartWarmKeeper();
        }
        catch (Exception ex) { Log("start failed: " + ex.Message); }
    }

    void RestartServer()
    {
        try
        {
            if (serverProc != null && !serverProc.HasExited)
            {
                serverProc.Kill();
                serverProc.WaitForExit(5000);
            }
        }
        catch { }
        serverProc = null;
        crashCount = 0;
        StartServer();
    }

    void QuitApp()
    {
        quitting = true;
        healthTimer.Stop();
        if (warmKeeper != null) warmKeeper.Stop();
        try
        {
            if (serverProc != null && !serverProc.HasExited)
            {
                serverProc.Kill();
                serverProc.WaitForExit(5000);
            }
        }
        catch { }
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    // ---------- Self-update from GitHub releases ----------
    // Compares baked-in AppVersion against the latest release tag
    // (build-<upstream_sha>). Dashboard-only changes swap server/ in place;
    // a changed EXE swaps the whole folder. No rebuild needed by the user.

    bool updating;

    void CheckForUpdates(bool manual)
    {
        if (updating) return;
        updating = true;
        Task.Run(() =>
        {
            try
            {
                string latest = FetchLatestReleaseTag();
                if (string.IsNullOrEmpty(latest))
                {
                    if (manual) ShowUpdateMsg("Could not reach GitHub releases. Try again later.");
                    return;
                }
                string want = latest.StartsWith("build-") ? latest.Substring(6) : latest;
                if (string.Equals(want, StampedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    if (manual) ShowUpdateMsg("Already up to date (" + StampedVersion + ").");
                    return;
                }
                string assetUrl = "https://github.com/" + UpdateOwner + "/" + UpdateRepo +
                    "/releases/download/" + latest + "/" + UpdateAsset;
                BeginInvoke(new Action(() => { if (updateItem != null) updateItem.Text = "Downloading update..."; }));
                string zip = Path.Combine(Path.GetTempPath(), "FlagshipRouter-update.zip");
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "FlagshipRouter-Updater");
                    wc.DownloadFile(assetUrl, zip);
                }
                ApplyUpdate(zip, want);
            }
            catch (Exception ex)
            {
                Log("update failed: " + ex.Message);
                if (manual) ShowUpdateMsg("Update failed: " + ex.Message);
            }
            finally { updating = false; }
        });
    }

    string FetchLatestReleaseTag()
    {
        try
        {
            using (var wc = new System.Net.WebClient())
            {
                wc.Headers.Add("User-Agent", "FlagshipRouter-Updater");
                string json = wc.DownloadString("https://api.github.com/repos/" + UpdateOwner + "/" + UpdateRepo + "/releases/latest");
                int i = json.IndexOf("\"tag_name\"");
                if (i < 0) return null;
                int c = json.IndexOf(':', i) + 1;
                int q1 = json.IndexOf('"', c) + 1;
                int q2 = json.IndexOf('"', q1);
                return json.Substring(q1, q2 - q1);
            }
        }
        catch { return null; }
    }

    void ShowUpdateMsg(string text)
    {
        try { BeginInvoke(new Action(() => tray.ShowBalloonTip(5000, AppName + " Update", text, ToolTipIcon.Info))); }
        catch { }
    }

    void ApplyUpdate(string zipPath, string newVersion)
    {
        try
        {
            string appDir = AppContext.BaseDirectory;
            string stage = Path.Combine(Path.GetTempPath(), "FlagshipRouter-update");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            // Expand-Archive via powershell (available on all Win10/11).
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"Expand-Archive -Force '" + zipPath + "' '" + stage + "'\"")
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            var p = Process.Start(psi);
            p.WaitForExit(120000);

            string stagedServer = Path.Combine(stage, "server");
            string stagedExe = Path.Combine(stage, "FlagshipRouter.exe");
            bool exeChanged = File.Exists(stagedExe) && !FilesEqual(stagedExe, Path.Combine(appDir, "FlagshipRouter.exe"));

            if (!exeChanged && Directory.Exists(stagedServer))
            {
                // Dashboard-only update: swap server/ live, restart node only.
                string live = Path.Combine(appDir, "server");
                string backup = live + ".prev";
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                StopServerOnly();
                Directory.Move(live, backup);
                Directory.Move(stagedServer, live);
                try { Directory.Delete(backup, true); } catch { }
                Directory.Delete(stage, true);
                try { File.Delete(zipPath); } catch { }
                StampedVersion = newVersion;
                Log("dashboard updated to " + newVersion);
                StartServer();
                ShowUpdateMsg("Dashboard updated to " + newVersion + ".");
                BeginInvoke(new Action(() => { if (updateItem != null) updateItem.Text = "Check for Updates"; }));
            }
            else
            {
                // EXE changed: stage a swap script, quit, relaunch.
                string swap = Path.Combine(Path.GetTempPath(), "FlagshipRouter-swap.bat");
                File.WriteAllText(swap,
                    "@echo off\r\n" +
                    "timeout /t 3 /nobreak >nul\r\n" +
                    "xcopy \"" + stage + "\\*\" \"" + appDir + "\" /E /Y /Q\r\n" +
                    "start \"\" \"" + Path.Combine(appDir, "FlagshipRouter.exe") + "\"\r\n" +
                    "del \"%~f0\"\r\n");
                Log("exe update staged to " + newVersion + ", relaunching");
                Process.Start(new ProcessStartInfo(swap) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                QuitApp();
            }
        }
        catch (Exception ex)
        {
            Log("apply update failed: " + ex.Message);
            ShowUpdateMsg("Update failed: " + ex.Message);
        }
    }

    void StopServerOnly()
    {
        try
        {
            if (serverProc != null && !serverProc.HasExited)
            {
                serverProc.Kill();
                serverProc.WaitForExit(8000);
            }
        }
        catch { }
        serverProc = null;
    }

    static bool FilesEqual(string a, string b)
    {
        try
        {
            using (var fa = File.OpenRead(a))
            using (var fb = File.OpenRead(b))
            {
                if (fa.Length != fb.Length) return false;
                int x, y;
                do { x = fa.ReadByte(); y = fb.ReadByte(); }
                while (x == y && x != -1);
                return x == y;
            }
        }
        catch { return false; }
    }
}
