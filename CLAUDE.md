# Birdbox Launcher — project brief

A game launcher the user (**birdbox774** on itch.io) hands to players so they can install, play,
and **delta-update** their games without re-downloading the whole build. Steam-style chunked
updates is the entire point of the project — if a change makes updates full-download again, it
has defeated the purpose.

C# / .NET 9, **WPF** (`net9.0-windows`, `UseWPF`) with CommunityToolkit.Mvvm. Not Avalonia.

## Docs

| file | what it answers |
|---|---|
| [`docs/PUBLISHING.md`](docs/PUBLISHING.md) | How to publish a game so the launcher can install and patch it — from the panel (`tools/publish-panel.ps1`) or the CLI. Start here. |
| [`docs/ADDING-GAMES.md`](docs/ADDING-GAMES.md) | How a game gets into the library, and what to edit for a new one. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | What each project and file does, and where to make a given change. |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | What is implemented, what is not, and what to build next in order. |

## Hard facts — verified, do not re-derive

- **itch.io account is `birdbox774`.** Not the Windows user name (`badri`). An earlier version
  hardcoded the machine account and the library came back empty.
- **6 games span 3 accounts**, so `Owner` is per-game, never a single global username:
  `birdbox774` (4), `ahmed-bahaa66/last-champion`, `falcon-eye/what-can-possibly-go-pong`.
  The last two are collaborations and **must** appear in the library — the user was explicit.
- **The library is scraped from `https://itch.io/profile/{user}`**, not read from the API.
  `/profile/games` omits collaborations hosted under other accounts, and the available API key
  is not scoped for it either.
- **5 of the 6 games have a downloadable zip on itch; only `defintel` is web-only.** itch shows
  platform icons only for uploads whose platform traits were ticked, and these uploads have none,
  so the profile grid shows nothing. Never infer "browser only" from the grid — read the game page
  (`ItchProfileCatalogService.ParseGamePage`). Getting this wrong marked three real games unplayable.
- **`logic-rift` is pushed; the other five are not.** As of 2026-09-02 `birdbox774/logic-rift:windows`
  has build `1939422` (`1.0.0`, 45.6 MB archive) and installs from the launcher. It is the **only**
  channel that exists, and it has **one** build — so there is still no patch chain. A *second* push
  to the same channel is what first exercises the delta path.
- **An itch zip upload is not a wharf build.** Zips have no channel, no build id and no block
  signature, so they cannot be diffed. Publishing must go through `butler push` of an *unpacked
  folder*.
- **API key scopes:** the key written by `butler login` (`%USERPROFILE%\.config\itch\butler_creds`)
  is wharf-scoped — `push`/`status`/`fetch` work, but `profile:me`, `profile:games` and
  `game:view:uploads` are denied and `Profile.LoginWithAPIKey` fails 403 against butlerd. butlerd's
  patch pipeline needs a **full-scope key** from <https://itch.io/user/settings/api-keys>.

## Non-negotiables

- **Never embed an itch.io API key in the build.** A key that can fetch builds must not ship in a
  launcher given to players. This is why the R2 chunk CDN exists as a second content source, and it
  is a requirement, not a nice-to-have. Read keys from `ITCH_API_KEY` or the local butler creds.
- **Never fake progress, sizes, versions or news.** A previous version invented a 100 MB total,
  wrote a text file named `.exe`, and shipped a hardcoded "news feed". All of it was deleted. If a
  value is unknown, show `—` and say why.
- **Every mutation of a bound collection goes through `IUiDispatcher`.** WPF's `CollectionView`
  throws `NotSupportedException` on cross-thread changes, and that single exception previously
  aborted the whole library load into a swallowed `catch`.
- **Do not swallow exceptions.** Surface them: `launcher.log` next to the exe, plus the diagnostic
  banner in the library rail.
- **`Launcher.Publisher` is the user's tool, never the player's.** The publishing panel runs butler
  with the local itch.io key and browses the local filesystem, so it binds to `IPAddress.Loopback`
  only, pins the `Host` header, and requires an `X-Publisher-Panel` header on every POST. It must
  never be exposed on a network interface and never shipped in the launcher build. No endpoint in it
  may return the key to the browser — `PublisherPaths.HasButlerCredentials()` returns a bool.
- No system-wide installs. Every dependency is vendored under the project directory — butler lives
  in `tools/butler/`.

## Gotchas that have already cost time

- `dotnet test` does not refresh another project's `bin`. Run `dotnet build` before launching the
  app, or the smoke test exercises a stale DLL.
- XML comments cannot contain `--`, so XAML section dividers use `===`, never `-----`.
- `butler` verbs: `--json` is a **global pre-verb** flag (`butler --json status <target>`).
  There is no `butler install`. `butler verify` takes `verify <signature> <dir>`, not a bare dir.
- **`butler status` never reports a build's size** — its `head` object has no `files` array, with or
  without `--show-all-files`, so there is nothing to sum and it comes back 0. The download size
  comes from `https://itch.io/api/1/{key}/wharf/channels?target={owner}/{slug}` →
  `channels.<name>.upload.size` (the wharf-scoped key is enough). Fixed in
  `WharfContentSource.TryFetchUploadSizeAsync`; showing `0 MB` for a real 45.6 MB download was the
  visible symptom.
- **Download size and on-disk size are different numbers, both correct.** The archive is
  compressed; the installed folder is not. `GameItemViewModel.SizeLabel` switches between
  `DOWNLOAD` and `ON DISK` so the jump does not read as a bug.
- `GameInfo.FullExecutablePath` returns `string.Empty`, never null — test with
  `string.IsNullOrEmpty`.
- Scraper fixtures in tests must be copied verbatim from a live `curl`. A hand-written fixture
  once passed every test while the launcher found zero games.

## Build and check

```powershell
dotnet build
dotnet test --filter "FullyQualifiedName!~ButlerDaemonIntegration"   # 66 tests, no network
```

The excluded suite starts a real butlerd daemon; run it deliberately, not by default.

A running panel locks its own DLL: stop `Launcher.Publisher.exe` before `dotnet build`, or MSBuild
fails with MSB3021/MSB3027.
