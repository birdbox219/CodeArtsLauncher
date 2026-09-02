using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core.Abstractions;
using Launcher.Core.Enums;
using Launcher.Core.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.ViewModels;

/// <summary>
/// Drives the launcher window: library list on the left, detail pane on the right.
///
/// Two rules this version holds to, both learned from the previous one:
///   - Every mutation of a bound collection goes through <see cref="IUiDispatcher"/>. The old
///     build called this from Task.Run and WPF threw on the first Games.Clear(), which silently
///     aborted the library, the engine init and the update check in one go.
///   - Nothing is ever swallowed. Failures land in <see cref="LibraryMessage"/> or on the game,
///     so an empty library explains itself on screen instead of just looking broken.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly IItchCatalogService _catalog;
    private readonly IGameInstallEngine _engine;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<MainViewModel>? _logger;

    private LauncherConfig _config = new();
    private CancellationTokenSource? _activeOperation;

    public ObservableCollection<GameItemViewModel> Games { get; } = new();

    /// <summary>The list actually bound to the UI, after the search filter.</summary>
    public ObservableCollection<GameItemViewModel> VisibleGames { get; } = new();

    [ObservableProperty]
    private GameItemViewModel? _selectedGame;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSyncing;

    /// <summary>Why the library looks the way it does: stale cache, bad profile name, offline.</summary>
    [ObservableProperty]
    private string? _libraryMessage;

    [ObservableProperty]
    private string _profileName = string.Empty;

    public bool HasLibraryMessage => !string.IsNullOrWhiteSpace(LibraryMessage);
    public bool IsLibraryEmpty => Games.Count == 0 && !IsSyncing;
    public bool HasSelection => SelectedGame is not null;

    public int PendingUpdateCount => Games.Count(g => g.State == LauncherState.UpdateAvailable);
    public bool HasPendingUpdates => PendingUpdateCount > 0;

    public string LibrarySummary
    {
        get
        {
            if (IsSyncing && Games.Count == 0) return "Syncing library…";
            if (Games.Count == 0) return "No games";

            string games = Games.Count == 1 ? "1 game" : $"{Games.Count} games";
            int updates = PendingUpdateCount;
            return updates == 0 ? games : $"{games}  ·  {updates} update{(updates == 1 ? "" : "s")}";
        }
    }

    public MainViewModel(
        IConfigService configService,
        IItchCatalogService catalog,
        IGameInstallEngine engine,
        IUiDispatcher ui,
        ILogger<MainViewModel>? logger = null)
    {
        _configService = configService;
        _catalog = catalog;
        _engine = engine;
        _ui = ui;
        _logger = logger;

        _engine.GameExited += OnGameExited;
    }

    // ---------------- startup ----------------

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _config = await _configService.LoadConfigAsync(ct);
        ProfileName = _config.ItchProfileUsername;

        await _engine.InitializeAsync(ct);
        await SyncLibraryAsync(ct);
    }

    // ---------------- library ----------------

    [RelayCommand]
    private Task SyncLibraryAsync() => SyncLibraryAsync(CancellationToken.None);

    private async Task SyncLibraryAsync(CancellationToken ct)
    {
        await _ui.InvokeAsync(() =>
        {
            IsSyncing = true;
            LibraryMessage = null;
            OnPropertyChanged(nameof(LibrarySummary));
        });

        try
        {
            var result = await _catalog.FetchProfileGamesAsync(_config.ItchProfileUsername, ct);

            // Manually added games come first so a hand-written entry wins over the scraped one
            // with the same slug — that is the escape hatch for drafts, unlisted games, and
            // anything the profile page gets wrong.
            var combined = _config.ManualGames
                .Concat(result.Games)
                .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var merged = combined.Select(PrepareGame).ToList();

            await _ui.InvokeAsync(() =>
            {
                string? keepSelected = SelectedGame?.Id;

                Games.Clear();
                foreach (var game in Order(merged))
                    Games.Add(new GameItemViewModel(game));

                ApplyFilter();

                SelectedGame = VisibleGames.FirstOrDefault(g => g.Id == keepSelected)
                               ?? VisibleGames.FirstOrDefault();

                LibraryMessage = result.Diagnostic;
                OnPropertyChanged(nameof(IsLibraryEmpty));
                OnPropertyChanged(nameof(LibrarySummary));
            });

            _logger?.LogInformation(
                "Library synced: {Count} games from {User} ({Source}). {Diagnostic}",
                merged.Count, _config.ItchProfileUsername, result.FromCache ? "cache" : "itch.io",
                result.Diagnostic ?? "No warnings.");

            if (_config.AutoCheckUpdates)
                await CheckAllStatesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Window closing; nothing to report.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Library sync failed.");
            await _ui.InvokeAsync(() => LibraryMessage = $"Library sync failed: {ex.Message}");
        }
        finally
        {
            await _ui.InvokeAsync(() =>
            {
                IsSyncing = false;
                OnPropertyChanged(nameof(IsLibraryEmpty));
                OnPropertyChanged(nameof(LibrarySummary));
            });
        }
    }

    /// <summary>
    /// Fills in the install settings the catalog cannot know: where the game goes, which channel
    /// to look at, and whatever this machine already has on disk.
    /// </summary>
    private GameInfo PrepareGame(GameInfo game)
    {
        if (_config.GameOverrides.TryGetValue(game.Id, out var o))
        {
            if (!string.IsNullOrWhiteSpace(o.Channel)) game.Channel = o.Channel;
            if (!string.IsNullOrWhiteSpace(o.ExecutableRelativePath)) game.ExecutableRelativePath = o.ExecutableRelativePath;
            if (!string.IsNullOrWhiteSpace(o.LaunchArguments)) game.LaunchArguments = o.LaunchArguments;
            if (!string.IsNullOrWhiteSpace(o.InstallDirectory)) game.InstallDirectory = o.InstallDirectory;
            if (!string.IsNullOrWhiteSpace(o.PreferredSourceId)) game.PreferredSourceId = o.PreferredSourceId;

            game.InstalledBuildId = o.InstalledBuildId;
            game.InstalledVersion = o.InstalledVersion;
            game.InstalledSizeBytes = o.InstalledSizeBytes;
            game.LastPlayedUtc = o.LastPlayedUtc;
            game.LastUpdatedUtc = o.LastUpdatedUtc;
        }

        if (string.IsNullOrWhiteSpace(game.Channel))
            game.Channel = _config.DefaultChannel;

        if (string.IsNullOrWhiteSpace(game.InstallDirectory))
            game.InstallDirectory = Path.Combine(_config.BaseInstallDirectory, game.Id);

        return game;
    }

    /// <summary>Own games first, then collaborations, alphabetical within each.</summary>
    private static IEnumerable<GameInfo> Order(IEnumerable<GameInfo> games) =>
        games.OrderBy(g => g.IsCollaboration)
             .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var matches = string.IsNullOrWhiteSpace(SearchText)
            ? Games.ToList()
            : Games.Where(g =>
                g.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                || g.Author.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)).ToList();

        VisibleGames.Clear();
        foreach (var m in matches) VisibleGames.Add(m);

        if (SelectedGame is null || !VisibleGames.Contains(SelectedGame))
            SelectedGame = VisibleGames.FirstOrDefault();
    }

    // ---------------- update checks ----------------

    [RelayCommand]
    private Task CheckAllUpdatesAsync() => CheckAllStatesAsync(CancellationToken.None);

    private async Task CheckAllStatesAsync(CancellationToken ct)
    {
        var items = Games.ToList();

        // Each check shells out to butler, so a handful at a time keeps startup responsive
        // without spawning one process per game at once.
        using var gate = new SemaphoreSlim(3);

        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try { await RefreshStateAsync(item, ct); }
            finally { gate.Release(); }
        }));

        await _ui.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(PendingUpdateCount));
            OnPropertyChanged(nameof(HasPendingUpdates));
            OnPropertyChanged(nameof(LibrarySummary));
        });
    }

    private async Task RefreshStateAsync(GameItemViewModel item, CancellationToken ct)
    {
        // A web-only game has no build to compare against, so spawning butler for it would be
        // pure noise. It sits at NotInstalled, where the action becomes "Play in browser".
        if (item.Model.IsBrowserOnly)
        {
            await _ui.InvokeAsync(() => item.State = LauncherState.NotInstalled);
            return;
        }

        await _ui.InvokeAsync(() => item.State = LauncherState.CheckingForUpdates);

        try
        {
            var status = await _engine.CheckStateAsync(item.Model, ct);
            await _ui.InvokeAsync(() =>
            {
                item.Status = status;
                item.State = status.State;
                item.ErrorMessage = status.State == LauncherState.Error ? status.Message : null;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "State check failed for {Game}.", item.Id);
            await _ui.InvokeAsync(() =>
            {
                item.State = item.Model.IsInstalled ? LauncherState.ReadyToPlay : LauncherState.NotInstalled;
                item.ErrorMessage = ex.Message;
            });
        }
    }

    // ---------------- primary action ----------------

    /// <summary>Install, update, play, or open a web build, depending on what the game needs.</summary>
    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (SelectedGame is not { } item || !item.CanAct) return;

        // Web-only games are played on their itch.io page; there is nothing to install.
        if (item.Model.IsBrowserOnly)
        {
            TryShellOpen(item.PageUrl);
            return;
        }

        switch (item.State)
        {
            case LauncherState.ReadyToPlay:
                await PlayAsync(item);
                break;

            case LauncherState.NotInstalled:
            case LauncherState.UpdateAvailable:
            case LauncherState.Error:
                await InstallOrUpdateAsync(item);
                break;
        }
    }

    private async Task InstallOrUpdateAsync(GameItemViewModel item)
    {
        if (_engine.IsBusy)
        {
            await _ui.InvokeAsync(() => item.ErrorMessage = "Another download is already running.");
            return;
        }

        _activeOperation = new CancellationTokenSource();
        var ct = _activeOperation.Token;

        bool updating = item.State == LauncherState.UpdateAvailable;

        await _ui.InvokeAsync(() =>
        {
            item.IsBusy = true;
            item.ErrorMessage = null;
            item.State = LauncherState.Downloading;
            item.Progress = new DownloadProgress(0, 0, item.Status?.DownloadSize ?? 0, 0, null,
                updating ? "Preparing update" : "Preparing download");
        });

        var progress = new Progress<DownloadProgress>(p => _ui.Post(() =>
        {
            item.Progress = p;
            item.State = p.Stage.Contains("patch", StringComparison.OrdinalIgnoreCase)
                ? LauncherState.Patching
                : LauncherState.Downloading;
        }));

        try
        {
            var result = await _engine.InstallOrUpdateAsync(item.Model, progress, ct);

            if (result.Success)
            {
                PersistGameState(item.Model);
                await _configService.SaveConfigAsync(_config, CancellationToken.None);

                _logger?.LogInformation(
                    "{Game} now at build {Build} ({Bytes} transferred, delta: {Delta}).",
                    item.Id, result.BuildId, result.BytesTransferred, result.WasDelta);
            }

            await _ui.InvokeAsync(() =>
            {
                item.IsBusy = false;
                item.ErrorMessage = result.Success ? null : result.Error;
                item.Progress = DownloadProgress.Empty;
                item.RefreshFromModel();
            });

            await RefreshStateAsync(item, CancellationToken.None);
            await _ui.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(PendingUpdateCount));
                OnPropertyChanged(nameof(HasPendingUpdates));
                OnPropertyChanged(nameof(LibrarySummary));
            });
        }
        catch (OperationCanceledException)
        {
            await _ui.InvokeAsync(() =>
            {
                item.IsBusy = false;
                item.Progress = DownloadProgress.Empty;
                item.State = item.Model.IsInstalled ? LauncherState.ReadyToPlay : LauncherState.NotInstalled;
            });
        }
        finally
        {
            _activeOperation?.Dispose();
            _activeOperation = null;
        }
    }

    private async Task PlayAsync(GameItemViewModel item)
    {
        await _ui.InvokeAsync(() => item.ErrorMessage = null);

        bool started = await _engine.LaunchGameAsync(item.Model);

        if (!started)
        {
            await _ui.InvokeAsync(() =>
            {
                item.State = LauncherState.Error;
                item.ErrorMessage =
                    "Could not start the game. No runnable executable was found in " +
                    $"{item.Model.InstallDirectory}. Verify the files or reinstall.";
            });
            return;
        }

        PersistGameState(item.Model);
        await _configService.SaveConfigAsync(_config, CancellationToken.None);

        await _ui.InvokeAsync(() =>
        {
            item.State = LauncherState.GameRunning;
            item.RefreshFromModel();
        });
    }

    private void OnGameExited(string gameId) => _ui.Post(() =>
    {
        var item = Games.FirstOrDefault(g => g.Id == gameId);
        if (item is null) return;

        item.State = item.Model.IsInstalled ? LauncherState.ReadyToPlay : LauncherState.NotInstalled;
        item.RefreshFromModel();
    });

    // ---------------- secondary actions ----------------

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (SelectedGame is not { } item || !item.IsInstalled || _engine.IsBusy) return;

        _activeOperation = new CancellationTokenSource();

        await _ui.InvokeAsync(() =>
        {
            item.IsBusy = true;
            item.ErrorMessage = null;
            item.State = LauncherState.Verifying;
        });

        var progress = new Progress<DownloadProgress>(p => _ui.Post(() => item.Progress = p));

        try
        {
            var result = await _engine.VerifyAndRepairAsync(item.Model, progress, _activeOperation.Token);

            await _ui.InvokeAsync(() =>
            {
                item.IsBusy = false;
                item.Progress = DownloadProgress.Empty;
                item.ErrorMessage = result.Healthy ? null : result.Error;
                item.State = result.Healthy ? LauncherState.ReadyToPlay : LauncherState.Error;
            });
        }
        finally
        {
            _activeOperation?.Dispose();
            _activeOperation = null;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await _engine.CancelOperationAsync();
        if (_activeOperation is { IsCancellationRequested: false } cts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    [RelayCommand]
    private void OpenGamePage()
    {
        if (SelectedGame?.PageUrl is not { Length: > 0 } url) return;
        TryShellOpen(url);
    }

    [RelayCommand]
    private void OpenInstallFolder()
    {
        if (SelectedGame?.Model.InstallDirectory is not { Length: > 0 } dir) return;
        if (!Directory.Exists(dir)) return;
        TryShellOpen(dir);
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var pending = Games.Where(g => g.State == LauncherState.UpdateAvailable).ToList();
        foreach (var item in pending)
        {
            await _ui.InvokeAsync(() => SelectedGame = item);
            await InstallOrUpdateAsync(item);
        }
    }

    // ---------------- persistence ----------------

    private void PersistGameState(GameInfo game)
    {
        if (!_config.GameOverrides.TryGetValue(game.Id, out var o))
        {
            o = new GameOverride();
            _config.GameOverrides[game.Id] = o;
        }

        o.Channel = game.Channel;
        o.ExecutableRelativePath = game.ExecutableRelativePath;
        o.LaunchArguments = game.LaunchArguments;
        o.InstallDirectory = game.InstallDirectory;
        o.PreferredSourceId = game.PreferredSourceId;
        o.InstalledBuildId = game.InstalledBuildId;
        o.InstalledVersion = game.InstalledVersion;
        o.InstalledSizeBytes = game.InstalledSizeBytes;
        o.LastPlayedUtc = game.LastPlayedUtc;
        o.LastUpdatedUtc = game.LastUpdatedUtc;
    }

    private void TryShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not open {Target}.", target);
        }
    }

    partial void OnIsSyncingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(LibrarySummary));
    }

    partial void OnLibraryMessageChanged(string? value) => OnPropertyChanged(nameof(HasLibraryMessage));

    partial void OnSelectedGameChanged(GameItemViewModel? value) => OnPropertyChanged(nameof(HasSelection));
}
