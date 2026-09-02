# Adding games to the library

Short answer: **you usually don't have to edit anything.** The library is built from your itch.io
profile page every time the launcher starts or you press **Sync**. Publish a game on itch.io under
`birdbox774` — or get credited on someone else's — and it appears by itself.

This document covers the cases where that isn't enough.

---

## How a game gets into the library

```text
https://itch.io/profile/birdbox774
        │
        │  ItchProfileCatalogService.FetchProfileGamesAsync
        ▼
  scrape the game grid  ──►  ParseProfilePage: slug, itch id, title, owner, author,
        │                    cover, description, genre, platform icons, web flag
        │
        │  then, per game (3 at a time)
        ▼
  fetch the game's own page ──►  ParseGamePage: is there a download, its name and size
        │
        ▼
  cached to %LOCALAPPDATA%\MyGameLauncher\cache\catalog.json
        │
        │  MainViewModel.SyncLibraryAsync merges config.ManualGames (they win on slug)
        │  MainViewModel.PrepareGame applies config.GameOverrides + defaults
        ▼
  library rail, own games first then collaborations, alphabetical within each
```

Two things worth knowing:

- **Collaborations are included on purpose.** A game hosted under another account (`falcon-eye`,
  `ahmed-bahaa66`) still shows on your profile page, so it is in the library, badged `COLLAB`. This
  is why the launcher scrapes the profile page instead of calling the itch API — `/profile/games`
  returns only games you own.
- **The `Owner` is per game.** Never assume every game belongs to `birdbox774`; the butler target is
  built from each game's own owner.

---

## Case 1: a normal new game

Nothing to edit. Publish it on itch.io, press **Sync**, and it appears — then follow
[`PUBLISHING.md`](PUBLISHING.md) to give it a channel so it can be installed and patched.

---

## Case 2: a draft or unlisted game

A draft doesn't appear on your public profile page, so the scraper can't see it. Add it by hand to
`%LOCALAPPDATA%\MyGameLauncher\config.json` under `ManualGames`. A hand-written entry **wins** over
a scraped one with the same slug, so this also works as an override for anything the scraper gets
wrong.

```json
{
  "ItchProfileUsername": "birdbox774",
  "ManualGames": [
    {
      "Id": "secret-prototype",
      "ItchGameId": 0,
      "Title": "Secret Prototype",
      "Owner": "birdbox774",
      "Author": "Birdbox774",
      "IsCollaboration": false,
      "PageUrl": "https://birdbox774.itch.io/secret-prototype",
      "CoverImageUrl": "",
      "Description": "Not announced yet",
      "Genre": "Puzzle",
      "Platforms": ["windows"],
      "Channel": "windows"
    }
  ]
}
```

| field | required | notes |
|---|---|---|
| `Id` | yes | the itch slug — the last part of the page URL. Also the install folder name. |
| `Owner` | yes | account hosting the game. Combined with `Id` and `Channel` into the butler target. |
| `Title` | yes | shown in the UI |
| `Channel` | no | defaults to `DefaultChannel` (`windows`) |
| `ItchGameId` | no | numeric itch id. Needed for butlerd's delta path; leave `0` and the launcher uses the CLI fallback (full download). |
| `PageUrl` | no | powers the "itch.io page" button |
| `CoverImageUrl` | no | falls back to a letter tile |
| `Platforms` | no | display only |

Close the launcher before editing the file — it rewrites `config.json` on exit and would overwrite
your changes.

---

## Case 3: changing where or how one game installs

Use `GameOverrides`, keyed by slug. These survive a catalog sync, so a refresh updates the title and
art without clobbering your install settings.

```json
{
  "GameOverrides": {
    "logic-rift": {
      "Channel": "windows-beta",
      "InstallDirectory": "E:\\Games\\LogicRift",
      "ExecutableRelativePath": "Binaries\\Win64\\LogicRift.exe",
      "LaunchArguments": "-windowed",
      "PreferredSourceId": "wharf"
    }
  }
}
```

| field | use it when |
|---|---|
| `Channel` | the game's channel isn't `windows` — e.g. a beta track |
| `InstallDirectory` | this game should live outside `BaseInstallDirectory` |
| `ExecutableRelativePath` | auto-detection picks the wrong `.exe` |
| `LaunchArguments` | the game needs flags |
| `PreferredSourceId` | force a content source: `wharf` or `r2-chunks` |

The launcher also writes install state here — `InstalledBuildId`, `InstalledVersion`,
`InstalledSizeBytes`, `LastPlayedUtc`, `LastUpdatedUtc`. Don't hand-edit those; they're how update
checks survive a restart. If a game gets stuck thinking it's installed, clear `InstalledBuildId`.

---

## Case 4: a different itch.io account

`ItchProfileUsername` in `config.json`. It is the **itch.io account name**, not your Windows user
name — that mistake is why the library was empty for a long time.

---

## Global settings

All in `%LOCALAPPDATA%\MyGameLauncher\config.json`:

| setting | default | meaning |
|---|---|---|
| `ItchProfileUsername` | `birdbox774` | whose profile page builds the library |
| `BaseInstallDirectory` | under `%LOCALAPPDATA%` | root for per-game install folders |
| `DefaultChannel` | `windows` | channel tried when a game has none |
| `AutoCheckUpdates` | `true` | check every game for updates on startup |
| `CloseOnLaunch` | `false` | close the launcher when a game starts |
| `MaxBandwidthKbps` | `0` | 0 = unlimited (**not yet enforced** — see [`ROADMAP.md`](ROADMAP.md)) |
| `GlobalLaunchArguments` | empty | appended to every game's arguments |

---

## When a game doesn't show up

1. Is it on <https://itch.io/profile/birdbox774>? If not, the scraper can't see it → Case 2.
2. Check `launcher.log`, next to `Launcher.UI.exe`. A sync logs its count and any diagnostic:
   `Library synced: 6 games from birdbox774 (itch.io). No warnings.`
3. The library rail shows a diagnostic banner when a sync degrades to cache or finds nothing.
4. If itch.io changes its page markup, the parser is the thing to fix:
   `ItchProfileCatalogService.ParseProfilePage`. The catalog falls back to
   `cache\catalog.json` so the library survives a markup change rather than emptying.
   **When fixing it, copy the new markup from a real `curl` into the test fixture** — a fixture
   written from memory once passed every test while the launcher found zero games.
