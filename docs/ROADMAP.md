# Roadmap

Honest state of the project, then what to build next in order.

---

## Where it stands

### Works, verified

| | |
|---|---|
| Library from itch.io | All 6 games load, including both collaborations under other accounts. Verified against the live profile page. |
| Upload probing | Each game's own page is read, so a zip with no platform traits is recognised. 5 of 6 games have a download; only `defintel` is web-only. |
| Offline / degraded start | Falls back to `cache\catalog.json` and says why in the banner. |
| UI | List + detail pane, cover art, status dots, COLLAB badges, real byte counts, blocker card with the exact `butler push` command. |
| Update detection | Build-id comparison against the channel head. |
| Install (full) | butler CLI `fetch`. Works with any key. |
| Install (patch) | butlerd `Install.Queue` → `Install.Perform`. Implemented, needs a full-scope key. |
| Verify / repair | `butler verify` against the build signature. |
| Launch + exit tracking | Process tracked, `LastPlayedUtc` persisted. |
| Publishing (CLI) | `tools/push-release.ps1`, dry-run verified. |
| Publishing (panel) | `tools/publish-panel.ps1` — local web UI: pick game, pick folder, dry run or push, live butler progress and the `Re-used …% of old` line. Loopback-only; verified against a real dry-run push. |

### Implemented but never exercised

**`logic-rift` is now pushed and installs from the launcher** (build `1939422`, `1.0.0`, 45.6 MB) —
so the publish → install half is proven end to end. But that channel has **one** build, and a patch
needs two: **no wharf patch has been applied yet.**

One more push to `birdbox774/logic-rift:windows` with a change in it is all that is missing, and it
is the only thing standing between "installs games" and the feature the launcher exists for.

### Not implemented — reported plainly, not faked

| | |
|---|---|
| R2 chunk transfer | `R2ChunkContentSource` detects versions and computes patch size from manifests. `InstallOrUpdateAsync` returns "not serving downloads yet" instead of producing a broken install. |
| Settings UI | Config is JSON-only. Changing the profile name or install path means editing a file. |
| `MaxBandwidthKbps` | The setting exists and is not enforced anywhere. |
| Multi-platform installs | Platform is display-only; the launcher always uses one channel per game. |
| Self-update | The launcher cannot update itself. |

---

## Next, in order

### 1. Push `logic-rift` a second time and prove the patch — *you*

`logic-rift` is already on the channel at `1.0.0`. Change something, rebuild, and push again — from
the panel:

```powershell
.\tools\publish-panel.ps1
```

Pick `logic-rift`, pick the rebuilt folder, leave the channel as `windows`, set the version to
`1.0.1`, **Dry run** once, then **Publish**. (Or from the command line:
`.\tools\push-release.ps1 -Folder D:\builds\LogicRift -Target logic-rift -Version 1.0.1`.)

Butler prints `Re-used 97.85% of old` on that second push — that line is the delta. The launcher then
shows `Update available · 1.1 MB patch` instead of a 45.6 MB re-download.

Needs the full-scope `ITCH_API_KEY` too: without it butlerd is unusable and the launcher falls back
to the CLI, which downloads the whole build even when a patch exists. See
[`PUBLISHING.md`](PUBLISHING.md) §2.

### 2. Settings UI

Small, and it removes the "edit a JSON file" step from everything in
[`ADDING-GAMES.md`](ADDING-GAMES.md): profile name, install root, default channel, auto-check
updates, close-on-launch, and a manual-game form. `LauncherConfig` and `LocalJsonConfigService`
already round-trip everything; this is a view plus commands on `MainViewModel`.

### 3. R2 chunk transfer — the player-facing path

The reason this matters: **butler needs an itch.io API key, and a key that can fetch builds must
never ship inside a launcher handed to players.** Today the launcher only patches on a machine with
your key. Until the CDN path works, what players get is a launcher that can list games and open itch
pages, not one that can update them.

`ChunkManifest` / `ManifestFile` / `ManifestChunk` in `R2ChunkContentSource.cs` already fix the
on-disk format, so the two halves can't drift. Three pieces:

1. **An uploader** (`tools/publish-chunks.ps1` or a small C# tool): walk a build folder, split each
   file into content-defined chunks, hash them, write `manifest.json`, upload only chunks the bucket
   doesn't have to `/<slug>/<channel>/`, then the manifest last so a half-uploaded build is never
   visible.
2. **`InstallOrUpdateAsync`**: fetch the manifest, diff against `.launcher\manifest.json`, download
   the missing chunks in parallel with real progress, reassemble, write the new local manifest
   atomically. Chunks already on disk from the previous version are reused — that reuse *is* the
   delta.
3. **`VerifyAndRepairAsync`**: re-hash local chunks, re-fetch mismatches.

Set `LAUNCHER_CDN_BASE_URL` and it takes priority over wharf automatically — it's registered first in
`App.BuildServices`. No UI change.

### 4. Bandwidth limit

Either honour `MaxBandwidthKbps` in the chunk downloader (a token bucket around the response stream)
or remove the setting. A setting that silently does nothing is worse than no setting.

### 5. Launcher self-update

Once players have it, you need a way to ship fixes: a version manifest on the same bucket, download
to a temp folder, swap on next start.

### Not planned

Achievements, cloud saves, mod management, a store front. Out of scope for a launcher whose job is
install-and-patch.

---

## Things to keep true

These are invariants, not preferences — each one exists because breaking it caused a real bug here.

- **Never embed an itch.io API key in the build.** See §3 for the consequence.
- **Never show a fabricated number.** No fake progress bars, no guessed sizes, no placeholder news.
  When a value is unknown the UI says `—` or explains why. The original build's fake progress is what
  hid the fact that nothing was ever being patched.
- **Every bound-collection mutation goes through `IUiDispatcher`.**
- **Don't swallow exceptions into a log nobody reads.** Startup failures get a message box.
- **Nothing installed system-wide.** butler is vendored under `tools\butler\`.
- **Scraper fixtures come from a real `curl`.** A fixture written from memory certified a parser that
  found zero games.
- **Platform icons on the profile grid do not tell you whether a game is downloadable.** itch only
  shows them when the uploader ticked platform traits. Read the game page —
  `UploadsChecked` must be true before claiming anything is browser-only.
