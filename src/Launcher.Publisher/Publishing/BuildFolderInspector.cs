namespace Launcher.Publisher.Publishing;

/// <summary>What the panel found in a build folder, and whether it is safe to push.</summary>
/// <param name="Path">The resolved absolute path, so the browser shows what butler will be given.</param>
/// <param name="Problems">Reasons the push is refused. Non-empty means the Publish button stays off.</param>
/// <param name="Warnings">Things worth knowing that do not block the push.</param>
public sealed record BuildFolderReport(
    string Path,
    bool CanPush,
    int FileCount,
    long TotalBytes,
    string? Executable,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The folder checks from <c>tools/push-release.ps1</c>, moved somewhere the panel can call them.
/// The point of running them before the upload is that butler will happily push a zip or an empty
/// folder and only the launcher finds out later — a zip cannot be patched at all, and a folder with
/// no executable installs fine and then has nothing to launch.
/// </summary>
public static class BuildFolderInspector
{
    /// <summary>Names that are shipped alongside a game but are never the game.</summary>
    private static readonly string[] NotTheGame =
    {
        "UnityCrashHandler", "crashpad", "vcredist", "unins", "dxwebsetup", "CrashReportClient"
    };

    public static BuildFolderReport Inspect(string? folder)
    {
        var problems = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(folder))
            return new BuildFolderReport("", false, 0, 0, null, new[] { "Pick the build folder first." }, warnings);

        string path;
        try
        {
            path = Path.GetFullPath(folder.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            return new BuildFolderReport(folder, false, 0, 0, null,
                new[] { $"That is not a usable path ({ex.GetType().Name})." }, warnings);
        }

        if (File.Exists(path))
        {
            problems.Add(
                "That is a file, not a folder. Push the unpacked folder: butler diffs file " +
                "contents, and a zip is one opaque blob that changes wholesale on every rebuild, " +
                "so every update would be a full download.");
            return new BuildFolderReport(path, false, 0, 0, null, problems, warnings);
        }

        if (!Directory.Exists(path))
        {
            problems.Add("No folder at that path.");
            return new BuildFolderReport(path, false, 0, 0, null, problems, warnings);
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the folder ({ex.GetType().Name}: {ex.Message}).");
            return new BuildFolderReport(path, false, 0, 0, null, problems, warnings);
        }

        if (files.Length == 0)
        {
            problems.Add("The folder is empty.");
            return new BuildFolderReport(path, false, 0, 0, null, problems, warnings);
        }

        long totalBytes = 0;
        foreach (var f in files)
        {
            try { totalBytes += new FileInfo(f).Length; }
            catch { /* a file that vanished mid-scan is not worth failing the whole report over */ }
        }

        string? executable = PickExecutable(files, path);
        if (executable is null)
        {
            warnings.Add(
                "No .exe found. The launcher auto-detects the executable after installing, so it " +
                "would have nothing to run.");
        }

        // The mistake this catches: exporting to a folder, zipping it, and then pointing the panel
        // at the folder that holds only the zip. butler would push the zip as a single blob.
        var topLevel = Directory.GetFiles(path);
        if (files.Length <= 2 && topLevel.Any(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(
                "This folder is basically just a .zip. Unpack it and push the unpacked files, or " +
                "the whole game counts as one changed block on every push.");
        }

        return new BuildFolderReport(path, true, files.Length, totalBytes, executable, problems, warnings);
    }

    /// <summary>Largest plausible .exe, matching how the launcher guesses at install time.</summary>
    private static string? PickExecutable(IEnumerable<string> files, string root)
    {
        var best = files
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => !NotTheGame.Any(n =>
                Path.GetFileNameWithoutExtension(f).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        return best is null ? null : Path.GetRelativePath(root, best.FullName);
    }
}
