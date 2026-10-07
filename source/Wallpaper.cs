using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskBoard;

/// <summary>
/// Tiny localhost-only HTTP server the Wallpaper Engine wallpaper polls for live board data.
/// Every request must carry the secret token that is baked into the installed wallpaper,
/// so other programs or web pages can't read the board.
/// </summary>
sealed class WallpaperServer : IDisposable
{
    public const int Port = 47821;

    readonly HttpListener listener = new();
    readonly string token;
    readonly Action<string> onOpen;
    readonly Action<string> onToggle;
    readonly object gate = new();
    string dataJson;
    long version = 1;

    public bool Running { get; private set; }
    public string Error { get; private set; }

    public WallpaperServer(string token, string initialData, Action<string> onOpen, Action<string> onToggle)
    {
        this.token = token;
        dataJson = initialData;
        this.onOpen = onOpen;
        this.onToggle = onToggle;
    }

    public void Start()
    {
        try
        {
            listener.Prefixes.Add($"http://localhost:{Port}/");
            listener.Start();
            Running = true;
            _ = AcceptLoop();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public void Update(string json)
    {
        lock (gate)
        {
            dataJson = json;
            version++;
        }
    }

    async Task AcceptLoop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; return; }
            if (!TokenMatches(req.QueryString["t"])) { res.StatusCode = 403; return; }

            switch (req.Url!.AbsolutePath)
            {
                case "/state":
                {
                    long v;
                    string data;
                    lock (gate) { v = version; data = dataJson; }
                    long bg = Storage.BackgroundStamp();
                    if (req.QueryString["v"] == v.ToString() && req.QueryString["bg"] == bg.ToString())
                    {
                        res.StatusCode = 204;
                        return;
                    }
                    var body = Encoding.UTF8.GetBytes($"{{\"version\":{v},\"bg\":{bg},\"data\":{data ?? "null"}}}");
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = body.Length;
                    res.OutputStream.Write(body);
                    return;
                }
                case "/background":
                {
                    var file = Storage.BackgroundFile();
                    if (file == null) { res.StatusCode = 404; return; }
                    var bytes = File.ReadAllBytes(file);
                    res.ContentType = Storage.ImageMime(file);
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes);
                    return;
                }
                case "/open":
                    onOpen(req.QueryString["card"]);
                    res.StatusCode = 204;
                    return;
                case "/toggle":
                    if (!string.IsNullOrEmpty(req.QueryString["card"])) onToggle(req.QueryString["card"]);
                    res.StatusCode = 204;
                    return;
                default:
                    res.StatusCode = 404;
                    return;
            }
        }
        catch
        {
            try { res.StatusCode = 500; } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    bool TokenMatches(string given) =>
        given != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public void Dispose()
    {
        try { listener.Close(); } catch { }
    }
}

/// <summary>Finds Wallpaper Engine and writes the TaskBoard web wallpaper project into it.</summary>
static class WallpaperInstaller
{
    const string FolderName = "taskboard";

    public static string FindWallpaperEngine()
    {
        var candidates = new List<string>();
        foreach (var name in new[] { "wallpaper64", "wallpaper32" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { candidates.Add(Path.GetDirectoryName(p.MainModule!.FileName)); } catch { }
            }
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string steam)
            {
                steam = steam.Replace('/', '\\');
                candidates.Add(Path.Combine(steam, @"steamapps\common\wallpaper_engine"));
                var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        candidates.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common\wallpaper_engine"));
                }
            }
        }
        catch { }
        candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine");
        return candidates.FirstOrDefault(c => c != null && Directory.Exists(Path.Combine(c, "projects")));
    }

    public static string InstalledPath(string wePath) =>
        wePath == null ? null : Path.Combine(wePath, "projects", "myprojects", FolderName);

    public static bool IsInstalled(string wePath)
    {
        var dir = InstalledPath(wePath);
        return dir != null && File.Exists(Path.Combine(dir, "project.json"));
    }

    /// <summary>Writes (or refreshes) the wallpaper project into <paramref name="dir"/>.</summary>
    public static void WriteProject(string dir, string token)
    {
        Directory.CreateDirectory(dir);
        WriteIfChanged(Path.Combine(dir, "index.html"), ReadResource("wallpaper.html"));
        WriteIfChanged(Path.Combine(dir, "project.json"), ReadResource("wallpaper.project.json"));
        WriteIfChanged(Path.Combine(dir, "config.js"),
            $"window.TASKBOARD_CONFIG = {{ port: {WallpaperServer.Port}, token: \"{token}\" }};\n");
        var preview = ReadResourceBytes("wallpaper.preview.jpg");
        if (preview != null) File.WriteAllBytes(Path.Combine(dir, "preview.jpg"), preview);
    }

    public static void Apply(string wePath, string dir)
    {
        var exe = new[] { "wallpaper64.exe", "wallpaper32.exe" }
            .Select(e => Path.Combine(wePath, e))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Wallpaper Engine executable not found.");
        foreach (var name in new[] { "wallpaper64", "wallpaper32" })
        {
            var running = Process.GetProcessesByName(name).FirstOrDefault();
            if (running == null) continue;
            try { exe = running.MainModule!.FileName; } catch { }
            break;
        }
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList = { "-control", "openWallpaper", "-file", Path.Combine(dir, "project.json") },
        });
    }

    static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing resource " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static byte[] ReadResourceBytes(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
