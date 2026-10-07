using System.Text.Json;
using Microsoft.Win32;

namespace TaskBoard;

static class Storage
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskBoard");
    public static readonly string LocalDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskBoard");

    public static string DataFile => Path.Combine(Dir, "data.json");
    public static string PrefsFile => Path.Combine(Dir, "prefs.json");
    static string BackupDir => Path.Combine(Dir, "backups");

    const int BackupsToKeep = 30;
    static readonly object Gate = new();

    static Storage()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(LocalDir);
    }

    /// <summary>Returns the newest readable, valid JSON copy of the board data, or null.</summary>
    public static string LoadData()
    {
        lock (Gate)
        {
            foreach (var file in Candidates())
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    var text = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) return text;
                }
                catch { }
            }
            return null;
        }
    }

    static IEnumerable<string> Candidates()
    {
        yield return DataFile;
        yield return DataFile + ".bak";
        foreach (var b in Directory.GetFiles(BackupDir, "data-*.json").OrderByDescending(f => f))
            yield return b;
    }

    public static void SaveData(string json)
    {
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("boards", out _))
                throw new InvalidDataException("Refusing to save data without boards.");
        }

        lock (Gate)
        {
            // Keep one snapshot per day of what the file looked like before that day's first change.
            var daily = Path.Combine(BackupDir, $"data-{DateTime.Now:yyyy-MM-dd}.json");
            if (File.Exists(DataFile) && !File.Exists(daily))
            {
                try { File.Copy(DataFile, daily); PruneBackups(); } catch { }
            }

            var tmp = DataFile + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(true);
            }
            if (File.Exists(DataFile)) File.Replace(tmp, DataFile, DataFile + ".bak", true);
            else File.Move(tmp, DataFile);
        }
    }

    static void PruneBackups()
    {
        foreach (var old in Directory.GetFiles(BackupDir, "data-*.json").OrderByDescending(f => f).Skip(BackupsToKeep))
        {
            try { File.Delete(old); } catch { }
        }
    }

    // ---- Custom background image (kept out of data.json so saves stay small) ----

    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
    };

    public static string ImportBackground(string source)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (!ImageTypes.ContainsKey(ext)) throw new InvalidDataException("Unsupported image type.");
        if (new FileInfo(source).Length > 25 * 1024 * 1024) throw new InvalidDataException("Image is larger than 25 MB.");
        foreach (var f in Directory.GetFiles(Dir, "background.*")) File.Delete(f);
        var dest = Path.Combine(Dir, "background" + ext);
        File.Copy(source, dest, true);
        File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
        return LoadBackground();
    }

    public static string LoadBackground()
    {
        var file = BackgroundFile();
        if (file == null) return null;
        return $"data:{ImageMime(file)};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}";
    }

    public static string BackgroundFile() =>
        Directory.GetFiles(Dir, "background.*").FirstOrDefault(f => ImageTypes.ContainsKey(Path.GetExtension(f)));

    public static string ImageMime(string file) => ImageTypes[Path.GetExtension(file)];

    /// <summary>Changes whenever the background image is replaced; 0 when there is none.</summary>
    public static long BackgroundStamp()
    {
        var file = BackgroundFile();
        return file == null ? 0 : File.GetLastWriteTimeUtc(file).Ticks / TimeSpan.TicksPerSecond;
    }
}

public sealed class HostPrefs
{
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool DailySummary { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public string WallpaperToken { get; set; }
    public string LastSummaryDate { get; set; }
    public List<string> FiredReminders { get; set; } = new();
    public int WinX { get; set; } = int.MinValue;
    public int WinY { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public bool WinMaximized { get; set; }

    public static HostPrefs Load(out bool firstRun)
    {
        firstRun = !File.Exists(Storage.PrefsFile);
        try
        {
            if (!firstRun)
                return JsonSerializer.Deserialize<HostPrefs>(File.ReadAllText(Storage.PrefsFile)) ?? new HostPrefs();
        }
        catch { }
        return new HostPrefs();
    }

    public void Save()
    {
        try
        {
            var tmp = Storage.PrefsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Storage.PrefsFile, true);
        }
        catch { }
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "TaskBoard";
    static string Command => $"\"{Environment.ProcessPath}\" --startup";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) k.SetValue(Name, Command);
        else k.DeleteValue(Name, false);
    }
}
