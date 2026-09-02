using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Core.Enums;
using Launcher.Core.Models;

namespace Launcher.Core.ViewModels;

/// <summary>
/// One row in the library list and the subject of the detail pane.
///
/// Everything the UI shows is derived here from real state — build ids, byte counts, channel
/// presence — rather than from placeholder strings. When a value is genuinely unknown the text
/// says so instead of inventing a number.
/// </summary>
public partial class GameItemViewModel : ObservableObject
{
    public GameInfo Model { get; }

    public GameItemViewModel(GameInfo model)
    {
        Model = model;
        _state = model.IsInstalled ? LauncherState.ReadyToPlay : LauncherState.NotInstalled;
    }

    // ---- static catalog info ----

    public string Id => Model.Id;
    public string Title => Model.Title;
    public string Author => Model.Author;
    public string CoverImageUrl => Model.CoverImageUrl;
    public string PageUrl => Model.PageUrl;
    public string Description => Model.Description;
    public bool IsCollaboration => Model.IsCollaboration;

    /// <summary>Single letter fallback used when a game has no cover art.</summary>
    public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title[..1].ToUpperInvariant();

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Model.Genre)) parts.Add(Model.Genre);
            if (!string.IsNullOrWhiteSpace(Model.Author)) parts.Add($"by {Model.Author}");
            return parts.Count == 0 ? "itch.io" : string.Join("  ·  ", parts);
        }
    }

    public string PlatformsText =>
        Model.IsBrowserOnly
            ? "Browser only"
            : Model.Platforms.Count > 0
                ? string.Join(", ", Model.Platforms.Select(Capitalize))
                : Model.HasItchDownload
                    ? "Untagged download"
                    : "Platform not listed";

    /// <summary>
    /// A web-only game has no downloadable build, so install and patch do not apply to it. The
    /// launcher opens its page instead of offering an Install button that could never work.
    /// </summary>
    public bool IsBrowserOnly => Model.IsBrowserOnly;

    // ---- live state ----

    [ObservableProperty]
    private LauncherState _state;

    [ObservableProperty]
    private GameStatus? _status;

    [ObservableProperty]
    private DownloadProgress _progress = DownloadProgress.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Set when the last operation failed, so the detail pane can explain it.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    // ---- derived display ----

    public string StatusText => State switch
    {
        LauncherState.CheckingForUpdates => "Checking for updates…",
        LauncherState.NotInstalled when Model.IsBrowserOnly => "Web build  ·  plays in browser",
        // The size is only quoted when it is actually known. A published build whose size could
        // not be read said "0 MB download", which read as a broken build rather than a gap in
        // what `butler status` reports.
        LauncherState.NotInstalled => Status?.Remote is { TotalBytes: > 0 } r
            ? $"Not installed  ·  {DownloadProgress.FormatBytes(r.TotalBytes)} download"
            : "Not installed",
        LauncherState.UpdateAvailable => Status?.Remote is { DeltaAvailable: true, PatchBytes: > 0 } d
            ? $"Update available  ·  {DownloadProgress.FormatBytes(d.PatchBytes)} patch"
            : Status?.Remote is { TotalBytes: > 0 } u
                ? $"Update available  ·  {DownloadProgress.FormatBytes(u.TotalBytes)} download"
                : "Update available",
        LauncherState.Downloading => Progress.Stage,
        LauncherState.Patching => "Applying patch…",
        LauncherState.Verifying => "Verifying files…",
        LauncherState.ReadyToPlay => Model.InstalledVersion is { Length: > 0 } v
            ? $"Up to date  ·  {v}"
            : "Up to date",
        LauncherState.GameRunning => "Running",
        LauncherState.Error => ErrorMessage ?? "Something went wrong",
        _ => ""
    };

    /// <summary>Hex colour for the status dot. Bound through a string-to-brush converter.</summary>
    public string StatusColor => State switch
    {
        LauncherState.ReadyToPlay => "#4ADE80",
        LauncherState.UpdateAvailable => "#FBBF24",
        LauncherState.Downloading or LauncherState.Patching or LauncherState.Verifying => "#60A5FA",
        LauncherState.GameRunning => "#A78BFA",
        LauncherState.Error => "#F87171",
        LauncherState.CheckingForUpdates => "#64748B",
        LauncherState.NotInstalled when Model.IsBrowserOnly => "#38BDF8",
        _ => "#475569"
    };

    public string ActionText => State switch
    {
        LauncherState.NotInstalled when Model.IsBrowserOnly => "Play in browser",
        LauncherState.NotInstalled => "Install",
        LauncherState.UpdateAvailable => "Update",
        LauncherState.ReadyToPlay => "Play",
        LauncherState.GameRunning => "Running",
        LauncherState.Downloading => "Downloading…",
        LauncherState.Patching => "Patching…",
        LauncherState.Verifying => "Verifying…",
        LauncherState.CheckingForUpdates => "Checking…",
        LauncherState.Error => "Retry",
        _ => "…"
    };

    public bool CanAct =>
        !IsBusy && State is LauncherState.NotInstalled
            or LauncherState.UpdateAvailable
            or LauncherState.ReadyToPlay
            or LauncherState.Error;

    public bool IsInstalled => Model.IsInstalled;

    public string VersionText =>
        Model.IsInstalled
            ? string.IsNullOrWhiteSpace(Model.InstalledVersion)
                ? BuildLabel(Model.InstalledBuildId)
                : Model.InstalledVersion
            : Status?.Remote is { } r
                ? string.IsNullOrWhiteSpace(r.Version) ? BuildLabel(r.BuildId) : r.Version
                : "—";

    public string SizeText =>
        Model.InstalledSizeBytes > 0
            ? DownloadProgress.FormatBytes(Model.InstalledSizeBytes)
            : Status?.Remote is { TotalBytes: > 0 } r
                ? DownloadProgress.FormatBytes(r.TotalBytes)
                : "—";

    /// <summary>
    /// Says which size <see cref="SizeText"/> is showing. Before installing it is the compressed
    /// download; afterwards it is the unpacked folder, which is legitimately much larger. Without
    /// the label the jump from 46 MB to 120 MB looks like one of the two numbers being wrong.
    /// </summary>
    public string SizeLabel => Model.InstalledSizeBytes > 0 ? "ON DISK" : "DOWNLOAD";

    /// <summary>
    /// The line that shows the point of the whole launcher: how much smaller a patch is than a
    /// full re-download. Empty when there is no update, so the UI can hide it.
    /// </summary>
    public string SavingsText
    {
        get
        {
            if (State != LauncherState.UpdateAvailable) return "";
            if (Status?.Remote is not { DeltaAvailable: true } r) return "";
            if (r.BytesSaved <= 0) return "";

            return $"Saves {DownloadProgress.FormatBytes(r.BytesSaved)} " +
                   $"({r.PatchRatio:P0} of a full download)";
        }
    }

    public bool HasSavings => SavingsText.Length > 0;

    public string LastPlayedText =>
        Model.LastPlayedUtc is not { } t
            ? "Never played"
            : (DateTime.UtcNow - t) switch
            {
                { TotalMinutes: < 60 } => "Played just now",
                { TotalHours: < 24 } d => $"Played {(int)d.TotalHours}h ago",
                { TotalDays: < 30 } d => $"Played {(int)d.TotalDays}d ago",
                _ => $"Played {t.ToLocalTime():d MMM yyyy}"
            };

    /// <summary>
    /// Shown when a game cannot be installed because it was never pushed to a wharf channel —
    /// the actual reason nothing was ever updatable, spelled out with the command that fixes it.
    /// Suppressed for web-only games, where the button already opens the page and there is no
    /// desktop build to push.
    /// </summary>
    public string? BlockerHint =>
        !Model.IsBrowserOnly
        && State == LauncherState.NotInstalled
        && Status?.Remote is null
        && Status?.Message is { } m ? m : null;

    public bool HasBlockerHint => BlockerHint is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    // Recompute the derived strings whenever the state they read from moves.
    partial void OnStateChanged(LauncherState value) => RaiseDerived();
    partial void OnStatusChanged(GameStatus? value) => RaiseDerived();
    partial void OnProgressChanged(DownloadProgress value) => OnPropertyChanged(nameof(StatusText));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAct));

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasError));
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(SavingsText));
        OnPropertyChanged(nameof(HasSavings));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(BlockerHint));
        OnPropertyChanged(nameof(HasBlockerHint));
    }

    /// <summary>Call after the model's install fields change so the pane reflects disk state.</summary>
    public void RefreshFromModel() => RaiseDerived();

    private static string BuildLabel(string buildId) =>
        string.IsNullOrWhiteSpace(buildId) ? "—" : $"build {buildId}";

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
