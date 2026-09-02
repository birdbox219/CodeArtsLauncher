# Architecture

Five projects, one dependency rule: **the UI knows about Core, Core knows about nothing**. Core
defines interfaces; the butler engine and the WPF app implement them. That is what lets the delivery
mechanism change from itch.io to a self-hosted CDN without touching the UI.

```text
Launcher.UI  (net9.0-windows, WPF)          ─┐
  App.xaml.cs — DI container, all wiring     │  references Core + Engine.Butler
  MainWindow.xaml — the whole window         │
  Converters, WpfUiDispatcher, FileLogger    │
                                             │
Launcher.Publisher  (net9.0, ASP.NET Core)  ─┤  references Core + Engine.Butler
  Program.cs — minimal API, loopback only    │  YOUR tool, never shipped to players
  Publishing/ butler push, jobs, SSE         │
  wwwroot/    the panel page                 │
                                             │
Launcher.Engine.Butler  (net9.0)            ─┤  references Core
  WharfContentSource : IContentSource        │
  ButlerDaemonManager, ButlerRpcClient       │
                                             │
Launcher.Core  (net9.0, no UI framework)    ─┘  references nothing of ours
  Abstractions/  the interfaces
  Models/        GameInfo, LauncherConfig, DownloadProgress, GameStatus, RemoteVersionInfo
  Services/      catalog, config, install engine
  Sources/       R2ChunkContentSource : IContentSource
  ViewModels/    MainViewModel, GameItemViewModel

tests/Launcher.Core.Tests  — xunit, references Core + Engine.Butler + Publisher
```

`Launcher.Publisher` is the one project that is **not** part of a player's install: it runs butler
with your local itch.io key and browses your filesystem, so it binds to `127.0.0.1` only. It shares
`WharfContentSource.ExtractChannels` with the engine rather than parsing `butler status` a second
time, because two parsers of the same output are free to disagree.

Core has no `System.Windows` reference on purpose: the view models are testable, and a future
Avalonia or console front end is a new project rather than a rewrite.

---

## The four interfaces

`src/Launcher.Core/Abstractions/`

| interface | job | implementation |
|---|---|---|
| `IItchCatalogService` | build the library | `ItchProfileCatalogService` (scrapes the profile page) |
| `IContentSource` | serve and patch one game's content | `WharfContentSource`, `R2ChunkContentSource` |
| `IGameInstallEngine` | pick a source, install, launch, track state | `ContentSourceGameEngine` |
| `IConfigService` | load/save settings | `LocalJsonConfigService` |
| `IUiDispatcher` | marshal to the UI thread | `WpfUiDispatcher` |

`IContentSource` is the seam that matters:

```csharp
Task<bool>               CanServeAsync(game)         // configured, and has content for this game?
Task<RemoteVersionInfo?> GetRemoteVersionAsync(game) // null = can't see this game at all
Task<InstallResult>      InstallOrUpdateAsync(game, progress)
Task<VerifyResult>       VerifyAndRepairAsync(game, progress)
```

`ContentSourceGameEngine` tries the registered sources in order, skipping any whose `CanServeAsync`
returns false, honouring `GameInfo.PreferredSourceId` when set. Registration order is in
`App.BuildServices` — R2 first, wharf second, so the moment the CDN can serve a game it wins.

---

## What happens when you press a button

**Startup** — `App.OnStartup` builds the DI container, shows the window, then awaits
`MainViewModel.InitializeAsync` off the UI thread: load config → `engine.InitializeAsync` →
`SyncLibraryAsync` → `CheckAllStatesAsync` (3 at a time; each one shells out to butler).

**Sync** — `MainViewModel.SyncLibraryAsync` → `IItchCatalogService.FetchProfileGamesAsync` → merge
`config.ManualGames` (they win on slug) → `PrepareGame` applies `GameOverrides`, the default channel
and the install path → rebuild `Games` → `ApplyFilter` fills `VisibleGames`.

**Install / Update / Play** — one command, `PrimaryActionAsync`, dispatching on
`GameItemViewModel.State`:

```
NotInstalled + IsBrowserOnly  →  open PageUrl in the browser
NotInstalled                 →  engine.InstallOrUpdateAsync
UpdateAvailable              →  engine.InstallOrUpdateAsync (butlerd applies the patch)
ReadyToPlay                  →  engine.LaunchGameAsync
Error                        →  retry the check
```

After any install the engine records `InstalledBuildId`, version and size, and
`MainViewModel.PersistGameState` writes them into `GameOverrides[slug]` so the next launch knows
what's on disk without re-scanning.

**Update detection** is a build-id comparison, nothing more:
`game.InstalledBuildId != remote.BuildId`. That is the check the original build never made.

---

## The two install paths inside `WharfContentSource`

This is where "update without re-downloading" actually lives.

| | butlerd (`Install.Queue` → `Install.Perform`) | butler CLI (`fetch`) |
|---|---|---|
| applies wharf **patches** | yes — this is the delta path | no, full download every time |
| needs | a **full-scope** API key | any key, including butler's own |
| used when | a full-scope key is available | fallback |

`TryGetLoggedInRpcAsync` returns null when the key is wharf-scoped (butlerd answers
`api key does not permit profile:me`), and the source degrades to the CLI rather than failing. So a
missing key costs you deltas, not installs. `TryPlanUpgradeAsync` uses `Install.Plan` to get the real
patch size, which is what the UI's "Saves 42.9 MB" line reports.

Either path requires the game to have been pushed to a channel first — see
[`PUBLISHING.md`](PUBLISHING.md).

---

## Threading

WPF throws if a bound `ObservableCollection` changes off the UI thread, and every long operation
here runs on a background thread. The rule:

> **Every mutation of `Games`, `VisibleGames`, or any bound property goes through `IUiDispatcher`.**

`await _ui.InvokeAsync(...)` when you need it to have happened, `_ui.Post(...)` for fire-and-forget
progress. Progress callbacks are already wrapped:
`new Progress<DownloadProgress>(p => _ui.Post(() => item.Progress = p))`.

---

## Where to make a given change

| you want to | edit |
|---|---|
| change what the library rail or detail pane looks like | `src/Launcher.UI/MainWindow.xaml` |
| change colours, fonts, button styles | `src/Launcher.UI/App.xaml` (all resources live there) |
| change a status string, badge, or derived label | `ViewModels/GameItemViewModel.cs` — not the XAML |
| add a command or change what a button does | `ViewModels/MainViewModel.cs` (`[RelayCommand]`) |
| fix the itch.io scraper after a markup change | `Services/ItchProfileCatalogService.cs` |
| add a field the UI shows about a game | `Models/GameInfo.cs`, then expose it on `GameItemViewModel` |
| add a setting | `Models/LauncherConfig.cs` (JSON round-trips automatically) |
| change how a source is picked | `Services/ContentSourceGameEngine.ResolveSourceAsync` |
| change butler invocation or parsing | `Launcher.Engine.Butler/WharfContentSource.cs` |
| add a whole new delivery mechanism | new `IContentSource`, register it in `App.BuildServices` |
| change DI, logging, or the API key lookup | `src/Launcher.UI/App.xaml.cs` |
| change the publishing panel's page, layout or wording | `src/Launcher.Publisher/wwwroot/` (`index.html`, `app.js`, `styles.css`) |
| add or change a panel endpoint | `src/Launcher.Publisher/Program.cs` |
| change how a push is run, or parse a new butler line | `Publishing/ButlerPublishService.cs`, `Publishing/ButlerCli.cs` |
| change what is checked before an upload | `Publishing/BuildFolderInspector.cs` |
| change what the panel's game list shows | `Publishing/PublisherCatalog.cs` |

---

## Files on disk

| path | what |
|---|---|
| `%LOCALAPPDATA%\MyGameLauncher\config.json` | settings, overrides, install state |
| `%LOCALAPPDATA%\MyGameLauncher\cache\catalog.json` | last good catalog; the offline fallback |
| `%LOCALAPPDATA%\MyGameLauncher\butler.db` | butler's install database |
| `%LOCALAPPDATA%\MyGameLauncher\Games\<slug>\` | default install root |
| `<install>\.launcher\manifest.json` | local chunk manifest, for the R2 source |
| next to `Launcher.UI.exe`: `launcher.log` | rolling log — first place to look |
| next to `Launcher.UI.exe`: `tools\butler\butler.exe` | vendored butler; nothing is installed system-wide |

---

## Build and test

```powershell
dotnet build GameLauncher.sln
dotnet test  --filter "FullyQualifiedName!~ButlerDaemonIntegration"   # 66 tests, no network
dotnet run   --project src\Launcher.UI
.\tools\publish-panel.ps1                                            # the publishing panel
```

The filter excludes tests that need a real butler login. Three things that have cost time before:

- **`dotnet test` does not refresh `Launcher.UI\bin`.** After changing Core, `dotnet build` before
  launching the app, or you will debug a stale DLL. This has already produced one round of "the fix
  didn't work" when it had.
- **A running panel locks its own DLL.** `dotnet build` fails with MSB3021/MSB3027 while
  `Launcher.Publisher.exe` is up. Stop it (Ctrl+C in its window, or
  `taskkill /F /IM Launcher.Publisher.exe`) and build again.
- **XML comments cannot contain `--`.** A `<!-- ---- divider ---- -->` in XAML is `error MC3000`.
  Use `<!-- === divider === -->`.

Test fixtures for the scraper must be **copied from a real page**, not written from memory. A
hand-written fixture once passed every parser test while the launcher found zero games.
