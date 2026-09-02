namespace Launcher.Publisher.Publishing;

public sealed record FolderEntry(string Name, string Path);

public sealed record FolderListing(
    string Path,
    string? Parent,
    IReadOnlyList<FolderEntry> Directories,
    bool HasFiles,
    string? Error);

/// <summary>
/// Server-side directory listing for the Browse dialog.
///
/// A browser cannot hand a real local path to a server — a file input gives you file contents and a
/// bare name, not <c>D:\builds\LogicRift</c> — and butler needs the path. So the picking happens on
/// this side, which is only acceptable because the panel is bound to 127.0.0.1. See the host-header
/// guard in Program.cs.
/// </summary>
public static class FolderBrowser
{
    public static FolderListing List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Drives();

        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) { return new FolderListing(path, null, Array.Empty<FolderEntry>(), false, $"Not a usable path ({ex.GetType().Name})."); }

        if (!Directory.Exists(full))
            return new FolderListing(full, Path.GetDirectoryName(full), Array.Empty<FolderEntry>(), false, "No folder at that path.");

        try
        {
            var dirs = new DirectoryInfo(full)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FolderEntry(d.Name, d.FullName))
                .ToList();

            bool hasFiles = Directory.EnumerateFiles(full).Any();

            return new FolderListing(full, Directory.GetParent(full)?.FullName, dirs, hasFiles, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new FolderListing(full, Directory.GetParent(full)?.FullName,
                Array.Empty<FolderEntry>(), false, "Windows denied access to that folder.");
        }
        catch (Exception ex)
        {
            return new FolderListing(full, Directory.GetParent(full)?.FullName,
                Array.Empty<FolderEntry>(), false, $"Could not read it ({ex.GetType().Name}).");
        }
    }

    /// <summary>Top of the tree: the drives, so the dialog has somewhere to start.</summary>
    private static FolderListing Drives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new FolderEntry(
                string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : $"{d.Name}  {d.VolumeLabel}",
                d.RootDirectory.FullName))
            .ToList();

        return new FolderListing("", null, drives, false, null);
    }
}
