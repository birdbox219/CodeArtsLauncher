# Publishing a game so the launcher can patch it

This is the guide to the part that is **on your side**. The launcher code is finished enough to
install and update games, but it cannot do either until a game has a *wharf channel* on itch.io.
`logic-rift` has one; the other five games do not, which is why nothing else is updatable yet.

---

## 1. Why your current uploads don't work for patching

You have zip uploads on itch.io — `MackarelOnPlateFinalLogicRift.zip` (44 MB),
`UnderTheTreeprototype.zip` (105 MB), `BicBrickBread2.zip` (42 MB), and so on. Those are fine for
people downloading from the website, but they cannot be patched, for one reason:

> A zip is a single opaque file. Change one texture in your game and the whole compressed stream
> changes, so a "diff" of two zips is the size of the whole zip.

Wharf — the patching system built into `butler` — works on **unpacked folders**. It splits your
files into blocks, remembers the hash of every block, and on the next upload sends only the blocks
that changed. That is what makes a 40 MB game update in 2 MB.

So the workflow is: **keep uploading zips if you want them on the website, but also `butler push`
the unpacked folder to a channel.** The launcher only ever looks at channels.

Terminology, once:

| term | meaning |
|---|---|
| **butler** | itch.io's command-line tool. Lives at `tools/butler/butler.exe`. |
| **wharf** | the patching protocol inside butler. You never call it directly. |
| **channel** | a named release track for one game, e.g. `windows`. Holds a series of builds. |
| **build** | one push to a channel. Gets a numeric id. Patches are computed between builds. |
| **target** | `owner/game-slug:channel`, e.g. `birdbox774/logic-rift:windows`. |

---

## 2. One-time setup

### Log butler in

```powershell
tools\butler\butler.exe login
```

This opens a browser and writes a key to `%USERPROFILE%\.config\itch\butler_creds`. That key is
enough for everything in this document.

### Get a full-scope API key (needed for delta updates)

The key from `butler login` is wharf-scoped. It can push and fetch, but butlerd — the daemon that
actually *applies* patches — rejects it with `api key does not permit profile:me`. Without a
full-scope key the launcher still installs games, but by full download instead of by patch.

1. Go to <https://itch.io/user/settings/api-keys>
2. Generate a new key
3. Set it for your machine:

```powershell
[Environment]::SetEnvironmentVariable('ITCH_API_KEY', 'paste-key-here', 'User')
```

Restart the launcher (or your terminal) so it picks the variable up. The key is read at runtime and
**never compiled into the build** — see [`docs/ROADMAP.md`](ROADMAP.md) for why that constrains how
you eventually ship to players.

---

## 3. Publishing from the panel

The panel is a small web UI over `butler push`, run locally:

```powershell
.\tools\publish-panel.ps1
```

It opens <http://localhost:5099>. Then: **pick the game → choose the build folder → Publish.**

What it shows you that the command line does not:

- every game with its current channel, build id and version, and **`no channel`** on the ones that
  have never been pushed
- the folder you picked, checked before anything uploads: file count, total size, the `.exe` it
  found, and a refusal with the reason if you pointed it at a zip
- a warning when the channel you typed does not exist yet, because a new channel starts its own
  patch chain from zero
- butler's live progress, and its `Re-used 97.85% of old, added 1.1 MB fresh data` line at the end —
  that line is the delta

**Dry run** lists exactly what would be pushed and uploads nothing. Use it the first time.

**Skip the push if nothing changed** (`--if-changed`) is on by default, so re-pushing an unchanged
folder does not create a junk build.

The panel binds to `127.0.0.1` only, needs a header no other site can send before it will publish,
and is **not** part of the launcher players get — it runs butler with your local itch.io key and
browses your filesystem when you ask it to. Don't expose it, don't ship it.

---

## 4. Publishing from the command line

```powershell
.\tools\push-release.ps1 -Folder D:\builds\LogicRift-Win64 -Target logic-rift -Version 1.0.0
```

That's it. The script resolves the target to `birdbox774/logic-rift:windows`, checks the folder,
pushes it, and prints the channel state so you can see the build land.

Add `-DryRun` first if you want to see what it would do without uploading.

### What the script checks before uploading

- The folder exists and is not empty
- You passed a **folder**, not a zip — it refuses a file, with the reason
- There is a plausible `.exe` in it (warns if not, because the launcher auto-detects the executable
  after install and would have nothing to run)

### Parameters

| parameter | meaning |
|---|---|
| `-Folder` | the unpacked build folder |
| `-Target` | `logic-rift`, or `owner/slug`, or a full `owner/slug:channel` |
| `-Owner` | defaults to `birdbox774`; set it for a collaboration |
| `-Channel` | defaults to `windows` |
| `-Version` | optional user-facing version like `1.2.0`; shown in the launcher |
| `-DryRun` | validate and print, upload nothing |

### Examples

```powershell
# Your own game, first release
.\tools\push-release.ps1 -Folder D:\builds\UnderTheTree -Target underthetreeprototype -Version 0.1.0

# A collaboration hosted under someone else's account
.\tools\push-release.ps1 -Folder D:\builds\pong -Target falcon-eye/what-can-possibly-go-pong:windows

# A second platform for the same game
.\tools\push-release.ps1 -Folder D:\builds\LogicRift-Linux -Target logic-rift -Channel linux
```

For a collaboration you need **upload rights on that game**. If you don't have them, the push fails
with a permissions error and the owner has to do it (or add you as an admin of the game).

---

## 5. Making the first real patch

A single push proves installation works, but there is nothing to patch *from*. To see the point of
the whole launcher:

1. Push once — `-Version 1.0.0`
2. Change something in the game and rebuild
3. Push the **same channel** again — `-Version 1.0.1`

On the second push butler prints how much it actually uploaded, e.g.:

```
For channel `windows`: last build is 1.0.0, downloading its signature
Pushing 44 MB (312 files, 0 dirs, 0 symlinks)
Re-used 97.85% of old, added 1.1 MB fresh data
```

That "re-used 97.85%" line is the delta. The launcher then offers an **Update** that downloads only
the fresh blocks, and the detail pane shows the saving, e.g. `Saves 42.9 MB (2% of a full download)`.

### Rules that keep patches small

- **Always push the folder, never a zip.** This is the single biggest factor.
- **Push the same channel every time.** A new channel name starts a new patch chain from zero.
- **Keep the folder layout stable.** Renaming or moving files makes them look new.
- Deterministic builds help. If your engine embeds a build timestamp in every asset bundle, every
  push will look like a bigger change than it is.

---

## 6. Verifying it worked

```powershell
tools\butler\butler.exe status birdbox774/logic-rift
```

Expect a channel, a build number, and a version. If it prints **"No channel found"**, the push
didn't happen — the state five of the six games are still in. `logic-rift` is the one that is pushed,
and it answers with build `1939422` at `1.0.0`.

Note that `butler status` shows the build id and version but **not its size** — that is expected, not
a failed push. The launcher reads the download size from itch.io's wharf API instead.

Then open the launcher. A published game shows an **Install** button and a real download size. An
unpublished one shows the reason and the exact command to fix it:

```
NOT PUBLISHED FOR PATCHING YET
itch.io has 'MackarelOnPlateFinalLogicRift.zip' (44 MB), but a zip upload cannot be patched.
Push the unpacked folder to a channel:
  butler push <folder> birdbox774/logic-rift:windows
```

---

## 7. Which of your games can do what today

| game | itch upload | web build | can be patched once pushed |
|---|---|---|---|
| `logic-rift` | 44 MB zip | yes | **pushed** — `windows`, build 1939422, `1.0.0` |
| `underthetreeprototype` | 105 MB zip | yes | yes |
| `bic-brick-bread-2` | 42 MB zip | yes | yes |
| `defintel` (DEFINITELY NOT PONG) | none | yes | no — web-only, nothing to install |
| `last-champion` (collab) | 94 MB | no | yes, with upload rights |
| `what-can-possibly-go-pong` (collab) | 56 MB zip | no | yes, with upload rights |

`defintel` has no downloadable file at all, so the launcher shows **Play in browser** and opens its
page. If you later export a desktop build for it, push it like any other and it becomes installable.

**Suggested first test:** `logic-rift` or `underthetreeprototype`. Both are yours, both already have
a desktop build zipped up, and you can unpack that same zip into a folder and push it as-is.
