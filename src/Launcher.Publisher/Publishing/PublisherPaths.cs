using System.Diagnostics;

namespace Launcher.Publisher.Publishing;

/// <summary>
/// Finds the vendored butler. Nothing is installed system-wide, so the only copy that exists is
/// the one under the repo's <c>tools\butler\</c> — which means the panel has to locate the repo
/// root from wherever the build output happens to be.
/// </summary>
public static class PublisherPaths
{
    /// <summary>Walks up from the running assembly until it finds the solution file.</summary>
    public static string? FindRepoRoot(string? startAt = null)
    {
        var dir = new DirectoryInfo(startAt ?? AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GameLauncher.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// butler.exe, or null when it is missing. <c>BUTLER_PATH</c> wins so a different butler can
    /// be tested without moving the vendored one.
    /// </summary>
    public static string? ResolveButler(string? repoRoot)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("BUTLER_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        if (repoRoot is null) return null;

        string vendored = Path.Combine(repoRoot, "tools", "butler", "butler.exe");
        return File.Exists(vendored) ? vendored : null;
    }

    /// <summary>
    /// Whether a butler login exists. Deliberately returns a bool and never the key itself — no
    /// endpoint in this panel is allowed to hand the key to the browser.
    /// </summary>
    public static bool HasButlerCredentials() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ITCH_API_KEY"))
        || File.Exists(ButlerCredentialsPath);

    public static string ButlerCredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "itch", "butler_creds");

    /// <summary>Opens the panel in the default browser. Best-effort; failing is not an error.</summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Headless or no default browser — the URL is printed to the console anyway.
        }
    }
}
