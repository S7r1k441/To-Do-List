using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TaskBoard;

public sealed class MainForm : Form
{
    static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem trayAutostart = new("Start with Windows");
    readonly System.Windows.Forms.Timer reminderTimer = new() { Interval = 20_000 };
    readonly HostPrefs prefs;
    readonly Reminders reminders;
    readonly WallpaperServer wallpaper;

    bool allowVisible;
    bool quitting;
    bool webInitStarted;
    string balloonCardId;
    string pendingOpenCard;

    public MainForm(bool fromStartup, EventWaitHandle showSignal)
    {
        prefs = HostPrefs.Load(out bool firstRun);
        try
        {
            if (firstRun || Autostart.IsEnabled()) Autostart.Set(true); // also refreshes the path if the exe moved
        }
        catch { }
        if (firstRun) prefs.Save();

        allowVisible = !(fromStartup && prefs.StartMinimized);

        Text = "TaskBoard";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        MinimumSize = new Size(720, 460);
        BackColor = Color.FromArgb(18, 19, 24);
        RestoreWindowBounds();
        Controls.Add(web);

        SetupTray();
        reminders = new Reminders(prefs, ShowNotification);
        reminderTimer.Tick += (_, _) => { reminders.Check(); UpdateTrayText(); };
        reminderTimer.Start();

        if (string.IsNullOrEmpty(prefs.WallpaperToken))
        {
            prefs.WallpaperToken = WallpaperServer.NewToken();
            prefs.Save();
        }
        wallpaper = new WallpaperServer(prefs.WallpaperToken, Storage.LoadData(),
            cardId => OnUiThread(() =>
            {
                ShowFromTray();
                if (!string.IsNullOrEmpty(cardId)) OpenCardInUi(cardId);
            }),
            cardId => OnUiThread(() => SendEvent("toggleDone", cardId)));
        wallpaper.Start();
        RefreshInstalledWallpaper();

        var startupCheck = new System.Windows.Forms.Timer { Interval = 4000 };
        startupCheck.Tick += (_, _) => { startupCheck.Dispose(); reminders.Check(startup: true); UpdateTrayText(); };
        startupCheck.Start();

        new Thread(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                try { if (IsHandleCreated) BeginInvoke(new Action(ShowFromTray)); } catch { }
            }
        }) { IsBackground = true, Name = "ShowSignal" }.Start();
    }

    // Lets the app start hidden in the tray while still creating the window handle.
    protected override void SetVisibleCore(bool value)
    {
        if (!allowVisible)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetDarkTitleBar(true);
        if (webInitStarted) return;
        webInitStarted = true;
        _ = InitWebAsync();
    }

    // ------------------------------------------------------------------ WebView

    async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Storage.LocalDir, "WebView2"));
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "TaskBoard needs the Microsoft Edge WebView2 Runtime, which could not be started.\n\n" +
                "Download it from https://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message,
                "TaskBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var core = web.CoreWebView2;
        web.DefaultBackgroundColor = BackColor;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("TASKBOARD_DEVTOOLS") == "1";
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                e.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var reader = new StreamReader(stream);
        core.NavigateToString(reader.ReadToEnd());
    }

    async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        await Task.Yield(); // leave the WebView callback before showing any dialogs
        int id = 0;
        object result = null;
        string error = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            var type = root.GetProperty("type").GetString();
            root.TryGetProperty("payload", out var payload);
            result = Dispatch(type, payload);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        try
        {
            web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result, error }));
        }
        catch { }
    }

    void SendEvent(string name, object payload = null)
    {
        try { web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = name, payload })); }
        catch { }
    }

    object Dispatch(string type, JsonElement p)
    {
        switch (type)
        {
            case "init":
            {
                var open = pendingOpenCard;
                pendingOpenCard = null;
                string bg = null;
                try { bg = Storage.LoadBackground(); } catch { }
                return new
                {
                    data = Storage.LoadData(),
                    prefs = PrefsDto(),
                    version = AppVersion,
                    dataPath = Storage.Dir,
                    bgImage = bg,
                    openCard = open,
                };
            }
            case "save":
            {
                var json = p.GetRawText();
                Storage.SaveData(json);
                reminders.UpdateData(json);
                wallpaper.Update(json);
                UpdateTrayText();
                return true;
            }
            case "setPrefs":
                ApplyPrefs(p);
                return PrefsDto();
            case "setTheme":
            {
                bool dark = p.GetProperty("dark").GetBoolean();
                SetDarkTitleBar(dark);
                if (p.TryGetProperty("color", out var c) && c.GetString() is string hex)
                {
                    try
                    {
                        BackColor = ColorTranslator.FromHtml(hex);
                        web.DefaultBackgroundColor = BackColor;
                    }
                    catch { }
                }
                return true;
            }
            case "openDataFolder":
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Storage.Dir}\"") { UseShellExecute = true });
                return true;
            case "exportData":
            {
                using var dlg = new SaveFileDialog
                {
                    Title = "Export TaskBoard backup",
                    Filter = "TaskBoard backup (*.json)|*.json",
                    FileName = $"TaskBoard-backup-{DateTime.Now:yyyy-MM-dd}.json",
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                File.WriteAllText(dlg.FileName, p.GetRawText());
                return dlg.FileName;
            }
            case "importData":
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Import TaskBoard backup",
                    Filter = "TaskBoard backup (*.json)|*.json|All files (*.*)|*.*",
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                return File.ReadAllText(dlg.FileName);
            }
            case "pickImage":
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Choose a background image",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp",
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                return Storage.ImportBackground(dlg.FileName);
            }
            case "testNotification":
                ShowNotification("TaskBoard", "Notifications are working. You'll be reminded here when tasks are due.", null);
                return true;
            case "wallpaperInfo":
            {
                var we = WallpaperInstaller.FindWallpaperEngine();
                return new
                {
                    wallpaperEngine = we,
                    installed = WallpaperInstaller.IsInstalled(we),
                    installPath = WallpaperInstaller.InstalledPath(we),
                    serverRunning = wallpaper.Running,
                    serverError = wallpaper.Error,
                    port = WallpaperServer.Port,
                };
            }
            case "installWallpaper":
            {
                var we = WallpaperInstaller.FindWallpaperEngine()
                    ?? throw new DirectoryNotFoundException("Wallpaper Engine wasn't found. Use \"Save wallpaper folder\" instead.");
                var dir = WallpaperInstaller.InstalledPath(we);
                WallpaperInstaller.WriteProject(dir, prefs.WallpaperToken);
                return dir;
            }
            case "applyWallpaper":
            {
                var we = WallpaperInstaller.FindWallpaperEngine()
                    ?? throw new DirectoryNotFoundException("Wallpaper Engine wasn't found.");
                var dir = WallpaperInstaller.InstalledPath(we);
                WallpaperInstaller.WriteProject(dir, prefs.WallpaperToken);
                WallpaperInstaller.Apply(we, dir);
                return true;
            }
            case "exportWallpaper":
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Choose where to save the TaskBoard wallpaper folder",
                    UseDescriptionForTitle = true,
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                var dir = Path.Combine(dlg.SelectedPath, "TaskBoard Wallpaper");
                WallpaperInstaller.WriteProject(dir, prefs.WallpaperToken);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                return dir;
            }
            case "openUrl":
                OpenExternal(p.GetString());
                return true;
            case "quit":
                BeginInvoke(new Action(Quit));
                return true;
            default:
                throw new InvalidOperationException("Unknown request: " + type);
        }
    }

    object PrefsDto()
    {
        bool autostart = false;
        try { autostart = Autostart.IsEnabled(); } catch { }
        return new
        {
            autostart,
            startMinimized = prefs.StartMinimized,
            closeToTray = prefs.CloseToTray,
            notifications = prefs.Notifications,
            dailySummary = prefs.DailySummary,
        };
    }

    void ApplyPrefs(JsonElement p)
    {
        bool Get(string name, bool fallback) =>
            p.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
                ? v.GetBoolean() : fallback;

        if (p.TryGetProperty("autostart", out _))
        {
            Autostart.Set(Get("autostart", true));
            trayAutostart.Checked = Autostart.IsEnabled();
        }
        prefs.StartMinimized = Get("startMinimized", prefs.StartMinimized);
        prefs.CloseToTray = Get("closeToTray", prefs.CloseToTray);
        prefs.Notifications = Get("notifications", prefs.Notifications);
        prefs.DailySummary = Get("dailySummary", prefs.DailySummary);
        prefs.Save();
    }

    static void OpenExternal(string url)
    {
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("mailto:"))) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void OnUiThread(Action action)
    {
        try { if (IsHandleCreated) BeginInvoke(action); } catch { }
    }

    /// <summary>Keeps an already-installed wallpaper in sync with this version of the app.</summary>
    void RefreshInstalledWallpaper()
    {
        try
        {
            var we = WallpaperInstaller.FindWallpaperEngine();
            if (WallpaperInstaller.IsInstalled(we))
                WallpaperInstaller.WriteProject(WallpaperInstaller.InstalledPath(we), prefs.WallpaperToken);
        }
        catch { }
    }

    // ------------------------------------------------------------------ Tray

    void SetupTray()
    {
        tray.Icon = Icon;
        tray.Text = "TaskBoard";
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open TaskBoard", null, (_, _) => ShowFromTray())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        try { trayAutostart.Checked = Autostart.IsEnabled(); } catch { }
        trayAutostart.Click += (_, _) =>
        {
            try
            {
                Autostart.Set(!Autostart.IsEnabled());
                trayAutostart.Checked = Autostart.IsEnabled();
                SendEvent("prefs", PrefsDto());
            }
            catch { }
        };
        menu.Items.Add(open);
        menu.Items.Add(trayAutostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit TaskBoard", null, (_, _) => Quit()));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        tray.BalloonTipClicked += (_, _) =>
        {
            ShowFromTray();
            if (balloonCardId != null) OpenCardInUi(balloonCardId);
        };
        tray.Visible = true;
    }

    void ShowNotification(string title, string text, string cardId)
    {
        balloonCardId = cardId;
        tray.ShowBalloonTip(10_000, title, string.IsNullOrWhiteSpace(text) ? " " : text, ToolTipIcon.None);
    }

    void OpenCardInUi(string cardId)
    {
        if (web.CoreWebView2 == null) pendingOpenCard = cardId;
        else SendEvent("openCard", cardId);
    }

    void UpdateTrayText()
    {
        var (overdue, today) = reminders.Counts();
        var parts = new List<string>();
        if (today > 0) parts.Add($"{today} due today");
        if (overdue > 0) parts.Add($"{overdue} overdue");
        var text = parts.Count == 0 ? "TaskBoard" : "TaskBoard – " + string.Join(", ", parts);
        tray.Text = text.Length > 63 ? text[..63] : text;
    }

    void ShowFromTray()
    {
        allowVisible = true;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = prefs.WinMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
        TopMost = true;
        TopMost = false;
        SendEvent("shown");
    }

    void HideToTray()
    {
        SaveWindowBounds();
        Hide();
        if (!prefs.TrayHintShown)
        {
            prefs.TrayHintShown = true;
            prefs.Save();
            ShowNotification("TaskBoard is still running",
                "It lives in the system tray so it can remind you. Right-click the tray icon to quit.", null);
        }
    }

    async void Quit()
    {
        if (quitting) return;
        quitting = true;
        try
        {
            if (web.CoreWebView2 != null)
            {
                var json = await web.CoreWebView2.ExecuteScriptAsync("window.__flush ? window.__flush() : null");
                if (!string.IsNullOrEmpty(json) && json != "null") Storage.SaveData(json);
            }
        }
        catch { }
        SaveWindowBounds();
        tray.Visible = false;
        wallpaper.Dispose();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (prefs.CloseToTray) HideToTray();
            else Quit();
            return;
        }
        SaveWindowBounds();
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ Window

    void RestoreWindowBounds()
    {
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 800);
        if (prefs.WinX == int.MinValue || prefs.WinW < 300 || prefs.WinH < 200) return;
        var rect = new Rectangle(prefs.WinX, prefs.WinY, prefs.WinW, prefs.WinH);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = rect;
        if (prefs.WinMaximized) WindowState = FormWindowState.Maximized;
    }

    void SaveWindowBounds()
    {
        if (!IsHandleCreated || !Visible) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (b.Width < 300 || b.Height < 200) return;
        prefs.WinX = b.X;
        prefs.WinY = b.Y;
        prefs.WinW = b.Width;
        prefs.WinH = b.Height;
        prefs.WinMaximized = WindowState == FormWindowState.Maximized;
        prefs.Save();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void SetDarkTitleBar(bool dark)
    {
        try
        {
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int)); // pre-20H1 builds
        }
        catch { }
    }
}
