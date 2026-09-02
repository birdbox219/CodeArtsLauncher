# CodeArts Launcher

A game launcher for [birdbox774's](https://itch.io/profile/birdbox774) itch.io games. Players install
a game once and then get **delta updates** — a changed 45 MB build downloads as a couple of MB, not
45 MB again.

That is the whole point of the project. It is built on [wharf](https://docs.itch.ovh/wharf/master/),
the block-based patching system inside itch.io's `butler`: a build is split into blocks, and an update
transfers only the blocks that actually changed.

C# / .NET 9, WPF. Windows only.

---

## What works today

| | |
|---|---|
| Library | All 6 games load from the itch.io profile page, including 2 collaborations hosted under other accounts. |
| Install | Full install through butler. Works with any itch.io key. |
| Update | Build-id comparison against the channel head, then a wharf patch through butlerd. |
| Verify / repair | `butler verify` against the build signature. |
| Publishing | A local web panel — pick game, pick folder, push. Streams butler's progress and its `Re-used …% of old` line. |

`birdbox774/logic-rift` is published and installs from the launcher (channel `windows`, build
`1939422`, `1.0.0`). It has **one** build, so no patch has been applied yet — a second push to the
same channel is what first exercises the delta path.

## What is not built yet

Stated plainly rather than faked — the launcher reports these instead of pretending:

- **The R2 chunk CDN does not serve downloads.** `R2ChunkContentSource` detects versions and computes
  patch sizes from manifests, but returns "not serving downloads yet" instead of producing a broken
  install. This matters: butler needs an itch.io API key, and *a key that can fetch builds must never
  ship inside a launcher handed to players*. Until the CDN path works, patching only works on a
  machine that has your own key.
- No settings UI — configuration is a JSON file.
- No self-update, no bandwidth limit, one channel per game.

See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the order to build these in.

---

## Running it

```powershell
dotnet build                                                          # solution is GameLauncher.sln
dotnet test --filter "FullyQualifiedName!~ButlerDaemonIntegration"    # 66 tests, no network
dotnet run --project src\Launcher.UI                                  # the launcher
.\tools\publish-panel.ps1                                             # the publishing panel
```

The excluded test suite starts a real butlerd daemon; run it deliberately.

Patching (as opposed to full download) needs a full-scope key from
<https://itch.io/user/settings/api-keys> in `ITCH_API_KEY`. It is read at runtime and never compiled
into the build.

## Publishing a game

```powershell
.\tools\publish-panel.ps1     # opens http://localhost:5099
```

Pick the game, pick the **unpacked build folder** (never a zip — a zip is one opaque blob, so every
update would be a full download), dry-run it, publish. Full guide:
[`docs/PUBLISHING.md`](docs/PUBLISHING.md).

`Launcher.Publisher` is the developer's tool, **not** part of what players get. It runs butler with
the local itch.io key and browses the local filesystem, so it binds to `127.0.0.1` only, pins the
`Host` header, and requires a custom header on every POST. Never expose it on a network interface and
never ship it.

---

## Layout

```text
src/Launcher.Core          interfaces, models, view models — no UI framework
src/Launcher.Engine.Butler WharfContentSource, butlerd client
src/Launcher.UI            the WPF app
src/Launcher.Publisher     the publishing panel (ASP.NET Core, loopback only)
tests/Launcher.Core.Tests  xunit
tools/butler               vendored butler — nothing here installs system-wide
docs/                      publishing, adding games, architecture, roadmap
```

One dependency rule: **the UI knows about Core, Core knows about nothing.** Delivery lives behind
`IContentSource`, which is what lets the content source change from itch.io to a self-hosted CDN
without touching the UI.

| doc | what it answers |
|---|---|
| [`docs/PUBLISHING.md`](docs/PUBLISHING.md) | Publish a game so the launcher can patch it. Start here. |
| [`docs/ADDING-GAMES.md`](docs/ADDING-GAMES.md) | How a game gets into the library. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | What each file does, and where to make a given change. |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | What is implemented, what is not, what to build next. |

`butler.exe` and its two DLLs are committed on purpose: every dependency is vendored under the
project directory so a clone can build and publish without a system-wide install. butler is
[MIT-licensed](https://github.com/itchio/butler) and belongs to itch.io, not to this project.
