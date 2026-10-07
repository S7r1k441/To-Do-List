using System.Globalization;
using System.Text.Json;

namespace TaskBoard;

/// <summary>
/// Watches due dates in the saved board data and raises Windows notifications.
/// Runs in the host (not the web page) so reminders fire even while the window is hidden in the tray.
/// </summary>
sealed class Reminders
{
    public record Item(string Id, string Title, string Board, string DueRaw, DateTime Due, bool HasTime, int Remind, bool Done)
    {
        public DateTime DueEnd => HasTime ? Due : Due.Date.AddDays(1).AddSeconds(-1);
        public DateTime RemindAt => (HasTime ? Due : Due.Date.AddHours(9)).AddMinutes(-Remind);
    }

    readonly HostPrefs prefs;
    readonly Action<string, string, string> notify;
    string dataJson;

    public Reminders(HostPrefs prefs, Action<string, string, string> notify)
    {
        this.prefs = prefs;
        this.notify = notify;
        dataJson = Storage.LoadData();
    }

    public void UpdateData(string json) => dataJson = json;

    List<Item> Items()
    {
        var items = new List<Item>();
        if (string.IsNullOrEmpty(dataJson)) return items;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (!doc.RootElement.TryGetProperty("boards", out var boards)) return items;
            foreach (var b in boards.EnumerateArray())
            {
                var boardName = Str(b, "name");
                if (!b.TryGetProperty("lists", out var lists)) continue;
                foreach (var l in lists.EnumerateArray())
                {
                    if (!l.TryGetProperty("cards", out var cards)) continue;
                    foreach (var c in cards.EnumerateArray())
                    {
                        var due = Str(c, "due");
                        if (string.IsNullOrEmpty(due)) continue;
                        bool hasTime = due.Contains('T');
                        if (!DateTime.TryParseExact(due, hasTime ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) continue;
                        int remind = c.TryGetProperty("remind", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
                        bool done = c.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
                        items.Add(new Item(Str(c, "id"), Str(c, "title"), boardName, due, dt, hasTime, remind, done));
                    }
                }
            }
        }
        catch { }
        return items;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    public (int overdue, int today) Counts()
    {
        var now = DateTime.Now;
        int overdue = 0, today = 0;
        foreach (var it in Items())
        {
            if (it.Done) continue;
            if (it.DueEnd < now) overdue++;
            else if (it.Due.Date == now.Date) today++;
        }
        return (overdue, today);
    }

    public void Check(bool startup = false)
    {
        var now = DateTime.Now;
        var items = Items();
        var fired = new List<Item>();
        var live = new HashSet<string>();
        bool changed = false;

        foreach (var it in items)
        {
            if (it.Done || it.Remind < 0) continue;
            var key = $"{it.Id}|{it.DueRaw}|{it.Remind}";
            live.Add(key);
            if (now < it.RemindAt || prefs.FiredReminders.Contains(key)) continue;
            prefs.FiredReminders.Add(key);
            changed = true;
            // Don't spam about things that went stale long ago (e.g. PC was off for a week).
            if (now - it.RemindAt < TimeSpan.FromDays(2)) fired.Add(it);
        }

        if (items.Count > 0 || !string.IsNullOrEmpty(dataJson))
            changed |= prefs.FiredReminders.RemoveAll(k => !live.Contains(k)) > 0;

        if (prefs.Notifications && fired.Count > 0)
        {
            if (fired.Count == 1)
            {
                var it = fired[0];
                notify("⏰ " + Trim(it.Title, 60), Describe(it, now), it.Id);
            }
            else
            {
                var lines = fired.OrderBy(f => f.Due).Take(4).Select(f => "• " + Trim(f.Title, 40));
                var more = fired.Count > 4 ? $"\n…and {fired.Count - 4} more" : "";
                notify($"⏰ {fired.Count} reminders", string.Join("\n", lines) + more, null);
            }
        }

        var todayKey = now.ToString("yyyy-MM-dd");
        if (prefs.DailySummary && prefs.LastSummaryDate != todayKey && (startup || now.Hour >= 8))
        {
            prefs.LastSummaryDate = todayKey;
            changed = true;
            var (overdue, today) = Counts();
            if (prefs.Notifications && fired.Count == 0 && overdue + today > 0)
            {
                var parts = new List<string>();
                if (today > 0) parts.Add($"{today} due today");
                if (overdue > 0) parts.Add($"{overdue} overdue");
                var titles = items.Where(i => !i.Done && i.Due.Date <= now.Date).OrderBy(i => i.Due)
                    .Take(3).Select(i => "• " + Trim(i.Title, 40));
                notify("Your day: " + string.Join(", ", parts), string.Join("\n", titles), null);
            }
        }

        if (changed) prefs.Save();
    }

    static string Describe(Item it, DateTime now)
    {
        string when;
        if (it.DueEnd < now) when = "Overdue";
        else if (it.Due.Date == now.Date) when = it.HasTime ? $"Due today at {it.Due:h:mm tt}" : "Due today";
        else if (it.Due.Date == now.Date.AddDays(1)) when = it.HasTime ? $"Due tomorrow at {it.Due:h:mm tt}" : "Due tomorrow";
        else when = it.HasTime ? $"Due {it.Due:ddd, MMM d 'at' h:mm tt}" : $"Due {it.Due:ddd, MMM d}";
        return string.IsNullOrEmpty(it.Board) ? when : $"{when}  ·  {it.Board}";
    }

    static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "(untitled)" : s.Length <= max ? s : s[..(max - 1)] + "…";
}
