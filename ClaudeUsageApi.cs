using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageLimitViewer;

// The Claude Code CLI sign-in is missing, expired or refused: only signing in again (claude auth login) fixes it.
internal sealed class ClaudeLoginRequiredException : IOException
{
    internal ClaudeLoginRequiredException() : base(L.T("Claude Code のログインが切れています")) { }
}

// Reads the account-wide Claude quota with the Claude Code CLI sign-in, so it also reflects Claude Desktop usage.
// The token is only sent to api.anthropic.com and is never stored or logged by this app.
internal static class ClaudeUsageApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static string CredentialsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    // Same endpoint and client as the Claude Code CLI (TOKEN_URL / CLIENT_ID in its OAuth config).
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static DateTimeOffset retryAfter = DateTimeOffset.MinValue;

    // Just under the 60-second poll, so every poll asks; a 429 backs off and the last values stay on screen meanwhile.
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(55);
    private static string CachePath => Path.Combine(Preferences.Folder, "claude-usage-cache.json");

    // Last successful reading (quota numbers only, never credentials), so a restart or a 429 shows grayed values instead of "—".
    internal static Reading? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var captured = doc.RootElement.TryGetProperty("captured_at", out var t) && t.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return null;
            var reading = Parse(usage, captured);
            return reading.Quotas.Count > 0 ? reading : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException) { return null; }
    }

    private static void SaveCache(JsonElement usage, DateTimeOffset captured)
    {
        try
        {
            Directory.CreateDirectory(Preferences.Folder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new { captured_at = captured.ToUnixTimeMilliseconds(), usage }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A manual refresh may ask sooner, but never more than once every 30 seconds.
    private static readonly TimeSpan ManualInterval = TimeSpan.FromSeconds(30);

    internal static async Task<Reading> ReadLive(CancellationToken stop, bool force = false)
    {
        if (LoadCache() is { } cached && DateTimeOffset.UtcNow - cached.Updated < (force ? ManualInterval : MinimumInterval)) return cached;
        if (DateTimeOffset.UtcNow < retryAfter) throw new IOException(L.T("Claudeの使用状況の取得を一時的に控えています"));
        var token = ReadToken() ?? throw new ClaudeLoginRequiredException();
        // The CLI only renews its sign-in when it calls the model, so an idle CLI leaves the token expired.
        if (token.Expires <= DateTimeOffset.UtcNow.AddMinutes(5)) token = await Renew(token, stop);
        var response = await SendUsage(token.Access, stop);
        // A sign-in that looks valid can still be refused when the CLI rotated it meanwhile: renew once, then ask again.
        if (response.StatusCode == HttpStatusCode.Unauthorized && token.Refresh != null)
        {
            response.Dispose();
            var latest = ReadToken();
            token = latest is { } fresh && fresh.Access != token.Access && fresh.Expires > DateTimeOffset.UtcNow.AddMinutes(1)
                ? fresh : await Renew(latest ?? token, stop);
            response = await SendUsage(token.Access, stop);
        }
        using var _ = response;
        if (response.StatusCode == (HttpStatusCode)429)
        {
            retryAfter = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(2));
            throw new IOException(L.T("Claudeの使用状況の取得を一時的に控えています"));
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ClaudeLoginRequiredException();
        if (!response.IsSuccessStatusCode) throw new IOException(L.T("Claudeの使用状況を取得できません"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var now = DateTimeOffset.UtcNow;
        var reading = Parse(doc.RootElement, now);
        if (reading.Quotas.Count > 0) SaveCache(doc.RootElement, now);
        return reading;
    }

    private static async Task<HttpResponseMessage> SendUsage(string access, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("UsageLimitViewer/" + typeof(ClaudeUsageApi).Assembly.GetName().Version?.ToString(3));
        return await Http.SendAsync(request, stop);
    }

    internal readonly record struct Token(string Access, string? Refresh, DateTimeOffset Expires, string[] Scopes);

    private static Token? ReadToken()
    {
        try { return File.Exists(CredentialsPath) ? ParseToken(File.ReadAllText(CredentialsPath)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static Token? ParseToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var access) || access.ValueKind != JsonValueKind.String) return null;
            var expires = oauth.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MaxValue;
            var refresh = oauth.TryGetProperty("refreshToken", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var scopes = oauth.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array
                ? sc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
            return new Token(access.GetString()!, refresh, expires, scopes);
        }
        catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException) { return null; }
    }

    // Renews the CLI sign-in the way the CLI does and writes it back, so the CLI keeps working with the rotated refresh token.
    private static async Task<Token> Renew(Token token, CancellationToken stop)
    {
        if (token.Refresh == null) throw new ClaudeLoginRequiredException();
        var body = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = token.Refresh, ["client_id"] = ClientId };
        if (token.Scopes.Length > 0) body["scope"] = string.Join(" ", token.Scopes);
        using var response = await Http.PostAsync(TokenUrl, new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), stop);
        if (!response.IsSuccessStatusCode)
        {
            // Another process (the CLI) may have rotated the refresh token first; use its fresh sign-in if so.
            if (ReadToken() is { } latest && latest.Expires > DateTimeOffset.UtcNow.AddMinutes(1)) return latest;
            throw new ClaudeLoginRequiredException();
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var now = DateTimeOffset.UtcNow;
        var json = File.ReadAllText(CredentialsPath);
        if (ParseToken(json) is { } current && current.Refresh != token.Refresh && current.Expires > now.AddMinutes(1)) return current;
        var updated = ApplyRenewal(json, doc.RootElement, now);
        var temp = CredentialsPath + ".usage-limit-viewer.tmp";
        File.WriteAllText(temp, updated, new System.Text.UTF8Encoding(false));
        UsageLimitViewer.Compat.MoveOver(temp, CredentialsPath);
        return ParseToken(updated) ?? throw new ClaudeLoginRequiredException();
    }

    // Updates only the renewed fields and keeps everything else the CLI stored.
    internal static string ApplyRenewal(string credentials, JsonElement renewal, DateTimeOffset now)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(credentials)!.AsObject();
        var oauth = root["claudeAiOauth"]!.AsObject();
        oauth["accessToken"] = renewal.GetProperty("access_token").GetString();
        if (renewal.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String)
            oauth["refreshToken"] = refresh.GetString();
        oauth["expiresAt"] = (now + TimeSpan.FromSeconds(renewal.GetProperty("expires_in").GetDouble())).ToUnixTimeMilliseconds();
        if (renewal.TryGetProperty("refresh_token_expires_in", out var life) && life.TryGetDouble(out var seconds))
            oauth["refreshTokenExpiresAt"] = (now + TimeSpan.FromSeconds(seconds)).ToUnixTimeMilliseconds();
        if (renewal.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(scope.GetString()))
        {
            var scopes = new System.Text.Json.Nodes.JsonArray();
            foreach (var item in scope.GetString()!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) scopes.Add(item);
            oauth["scopes"] = scopes;
        }
        return root.ToJsonString();
    }

    internal static Reading Parse(JsonElement root, DateTimeOffset captured)
    {
        var rows = new List<Quota>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            AddWindow(root, "five_hour", "5時間", rows);
            AddWindow(root, "seven_day", "週間", rows);
            AddWindow(root, "seven_day_opus", "週間 · Opus", rows);
            AddWindow(root, "seven_day_sonnet", "週間 · Sonnet", rows);
        }
        return new Reading("Claude Code", rows, captured,
            rows.Count == 0 ? L.T("Claude Codeの残量は利用できません") : null,
            L.T("Claude Code CLI経由"));
    }

    private static void AddWindow(JsonElement root, string key, string label, List<Quota> rows)
    {
        if (!root.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var used) || !used.TryGetDouble(out var percent) ||
            !UsageLimitViewer.Compat.IsFinite(percent) || percent < 0) return;
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            if (at <= DateTimeOffset.UtcNow) return;
            reset = at;
        }
        // Model-specific weekly windows without a reset time are placeholders for unused models, not real limits.
        if (reset == null && key.StartsWith("seven_day_")) return;
        rows.Add(new(label, UsageLimitViewer.Compat.Clamp(100 - percent, 0, 100), reset));
    }
}
