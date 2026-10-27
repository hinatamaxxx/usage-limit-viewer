using System.Text.Json;

namespace UsageLimitViewer;

internal record Quota(string Label, double Remaining, DateTimeOffset? Reset);
// NeedsLogin: the service refused the saved sign-in; the app offers to open its sign-in.
internal record Reading(string Provider, List<Quota> Quotas, DateTimeOffset? Updated, string? Error = null, string? Source = null, bool NeedsLogin = false)
{
    public bool Stale => Error != null || Updated == null || DateTimeOffset.UtcNow - Updated > TimeSpan.FromMinutes(10);
    public string Compact => Quotas.Count == 0 ? "—" : $"{Quotas.Min(q => q.Remaining):0.#}%";
}
internal sealed class Preferences
{
    public string Language { get; set; } = "ja";
    public bool Pinned { get; set; } = false;
    public bool OpenDetailsOnHover { get; set; } = false;
    public string TaskbarTop { get; set; } = "Codex";
    public string TaskbarBottom { get; set; } = "Antigravity";
    // true: "5-hour%(weekly%)"; false: "weekly%(5-hour%)".
    public bool FiveHourFirst { get; set; } = true;
    // Install new versions on their own (checked once a day); on by default.
    public bool AutoUpdate { get; set; } = true;
    // Card order in the usage popup after the reader dragged cards; empty keeps taskbar services first.
    public List<string> CardOrder { get; set; } = [];
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = 16;
    // Cache and diagnostics; settings themselves live in AppPaths.SettingsDirectory.
    public static string Folder => AppPaths.DataDirectory;
    private static string SettingsFile => Path.Combine(AppPaths.SettingsDirectory, "settings.json");
    public static Preferences Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsFile)) ?? new();
            if (!KnownProvider(value.TaskbarTop)) value.TaskbarTop = "Codex";
            if (!KnownProvider(value.TaskbarBottom) || value.TaskbarBottom == value.TaskbarTop)
                value.TaskbarBottom = value.TaskbarTop == "Antigravity" ? "Codex" : "Antigravity";
            return value;
        }
        catch { return new(); }
    }
    public static bool KnownProvider(string? provider) => provider is "Codex" or "Antigravity" or "Claude Code" or "Grok";
    public void SelectTaskbarProvider(bool top, string provider, bool persist = true)
    {
        if (!KnownProvider(provider)) return;
        if (top)
        {
            if (TaskbarBottom == provider) TaskbarBottom = TaskbarTop;
            TaskbarTop = provider;
        }
        else
        {
            if (TaskbarTop == provider) TaskbarTop = TaskbarBottom;
            TaskbarBottom = provider;
        }
        if (persist) Save();
    }
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.SettingsDirectory);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this));
    }
}
internal static class QuotaParser
{
    public static Reading Codex(JsonElement result)
    {
        var rows = new List<Quota>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var all) && all.ValueKind == JsonValueKind.Object)
        {
            foreach (var b in all.EnumerateObject()) AddBucket(b.Value, b.Name == "codex" ? "" : b.Name + " · ", rows);
        }
        if (rows.Count == 0 && result.TryGetProperty("rateLimits", out var bucket)) AddBucket(bucket, "", rows);
        return new("Codex", rows, DateTimeOffset.UtcNow, rows.Count == 0 ? L.T("残量が返されませんでした") : null);
    }
    private static void AddBucket(JsonElement b, string prefix, List<Quota> rows)
    {
        if (b.ValueKind != JsonValueKind.Object) return;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!b.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object ||
                !w.TryGetProperty("usedPercent", out var used) || !used.TryGetDouble(out var percent) || !UsageLimitViewer.Compat.IsFinite(percent)) continue;
            double duration = w.TryGetProperty("windowDurationMins", out var d) && d.TryGetDouble(out var minutes) ? minutes : 0;
            var label = duration switch { 300 => "5時間", 10080 => "週間", > 0 => $"{duration / 60:0.#}時間", _ => key };
            DateTimeOffset? reset = null;
            if (w.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var seconds))
                try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
            rows.Add(new(prefix + label, UsageLimitViewer.Compat.Clamp(100 - percent, 0, 100), reset));
        }
    }
    // A headless reply without the "command" result that spent tokens means agy sent "/usage" to the model as a prompt.
    public static bool AgyRanModel(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("command", out _) &&
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty("total_tokens", out var tokens) && tokens.TryGetInt64(out var count) && count > 0;
    public static Reading AntigravityCommand(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "SUCCESS" ||
            !root.TryGetProperty("command", out var command) ||
            !command.TryGetProperty("name", out var name) || name.GetString() != "usage")
            throw new IOException(L.T("agyから残量応答が返されませんでした"));
        var quotas = new Dictionary<string, JsonElement>();
        foreach (var group in command.GetProperty("data").GetProperty("groups").EnumerateArray())
            foreach (var bucket in group.GetProperty("buckets").EnumerateArray())
                quotas[bucket.GetProperty("id").GetString()!] = bucket;
        var data = JsonSerializer.SerializeToElement(new { quota = quotas });
        var reading = Antigravity(data, DateTimeOffset.UtcNow);
        if (reading.Quotas.Count == 0) throw new IOException(L.T("agyの残量応答が空でした"));
        return reading;
    }
    public static Reading Antigravity(JsonElement root, DateTimeOffset? timestamp)
    {
        var rows = new List<Quota>();
        if (root.TryGetProperty("quota", out var q) && q.ValueKind == JsonValueKind.Object)
            foreach (var item in q.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object || !item.Value.TryGetProperty("remaining_fraction", out var f) ||
                    !f.TryGetDouble(out var fraction) || !UsageLimitViewer.Compat.IsFinite(fraction) || fraction < 0 || fraction > 1) continue;
                DateTimeOffset? reset = null;
                if (item.Value.TryGetProperty("reset_time", out var r) && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var dt)) reset = dt;
                rows.Add(new(item.Name, fraction * 100, reset));
            }
        return new("Antigravity", rows, timestamp, rows.Count == 0 ? L.T("agyで /usage を開くと残量が届きます") : null);
    }
}
