using System.Diagnostics;

namespace UsageLimitViewer;

// Opens the Claude Code CLI sign-in (claude auth login) in its own console window; the CLI opens the browser.
internal static class ClaudeLogin
{
    private const string SetupPage = "https://code.claude.com/docs/en/setup";

    // The native installer puts claude.exe in ~/.local/bin; npm installs put claude.cmd on PATH.
    internal static string? FindCli()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                if (File.Exists(Path.Combine(folder, name))) return Path.Combine(folder, name);
        return null;
    }

    // The started console, or null when the CLI is not installed (the setup page opens instead).
    internal static Process? Start()
    {
        var cli = FindCli();
        try
        {
            if (cli == null)
            {
                Process.Start(new ProcessStartInfo(SetupPage) { UseShellExecute = true })?.Dispose();
                return null;
            }
            return Process.Start(new ProcessStartInfo(cli, "auth login") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return null; }
    }
}
