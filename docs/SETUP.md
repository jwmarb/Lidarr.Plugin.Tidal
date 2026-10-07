<h1 align="center">
  Tidal for Lidarr — Setup Guide 🔑
</h1>
<p align="center">
  A <strong>step-by-step walkthrough</strong> that takes you from a Tidal subscription to <em>automatically imported lossless FLAC</em>, including the login dance and the settings that silently block downloads.
</p>

<br>

## What this guide covers 🗺️

Getting this plugin working means filling in three indexer fields, pointing the download client at a folder, and turning on two Lidarr settings that are easy to miss. Two things make Tidal fiddlier than most sources. Its login is a **browser handshake**, not a password field. And its lossless files need **FFmpeg** before Lidarr will read them correctly.

Work through the stages in order. Each one ends with something you can check, so you find out *where* it broke instead of discovering at the end that nothing works.

| Stage | What you do | What proves it worked |
| --- | --- | --- |
| [1](#stage-1--prerequisites-) | Confirm Lidarr supports plugins and your subscription is current | Lidarr shows a **System → Plugins** page |
| [2](#stage-2--give-lidarr-ffmpeg-) | Run Lidarr from an image that includes FFmpeg | `ffmpeg -version` prints inside the container |
| [3](#stage-3--install-the-plugin-) | Install from the GitHub URL, restart, and check which build you got | The download client offers **Remux To FLAC** |
| [4](#stage-4--choose-a-config-path-) | Pick a folder for the saved login | An absolute path such as `/config/tidal` |
| [5](#stage-5--log-in-to-tidal-) | Generate a login URL, approve it, paste the redirect back | Green **Test**, and a `lastUser.json` on disk |
| [6](#stage-6--add-the-download-client-) | Create the Tidal download client and test it | Green **Test**, client saves |
| [7](#stage-7--the-settings-that-block-everything-) | Enable the protocol, allow FLAC | Tidal shows as allowed |
| [8](#stage-8--verify-end-to-end-) | Search, grab, and confirm the import | FLAC files in your library |

> [!NOTE]
> Every **Test** message, log line, and default value in this guide was checked against a running Lidarr `3.1.2.4913` (plugins branch) with the current build of this plugin. Older builds word some of them differently (Stage 3 says how to tell). Tidal's own login pages can change their wording; if they do, the URL shapes shown here are what matter.

## Stage 1 — Prerequisites 📦

**Lidarr on the plugins branch.** Stable Lidarr has no plugin support. You need a build with a **System → Plugins** page, and if that page is missing nothing in this guide will help — switch to the `plugins` branch first. The [hotio](https://hotio.dev/containers/lidarr/) and [Servarr wiki](https://wiki.servarr.com/lidarr/installation) docs cover both. Stage 2 builds on hotio's `pr-plugins` image.

**A current, paid Tidal subscription.** In April 2024 Tidal [folded *HiFi* and *HiFi Plus* into a single plan](https://www.musicbusinessworldwide.com/tidal-streamlines-offerings-merging-hifi-and-hifi-plus-into-single-10-99-a-month-tier/) that includes lossless and Hi-Res FLAC, and dropped the US free tier. Guides that tell you to buy "HiFi or above" predate that change. Plans and prices differ by region, so check [tidal.com](https://tidal.com/) for yours.

To check, sign in at [listen.tidal.com](https://listen.tidal.com/) and confirm you can play a track in full.

> [!TIP]
> Logging in and being *allowed* lossless are separate things. Search results list FLAC releases for every album Tidal tags as lossless, because the indexer reads the album's tags, not your plan. Tidal decides what you actually receive at download time, so check the files themselves (Stage 8) and don't trust the release title alone.

## Stage 2 — Give Lidarr FFmpeg 🎞️

This is the step people skip, and skipping it does not produce an error. It quietly mislabels your library.

Tidal delivers lossless audio as a stream of small segments, and the plugin joins them into an `.m4a` file. That file plays fine, but its header claims it is zero seconds long. Lidarr reads that and reports a bitrate of `-2147483648 kbps`. It then sees no bitrate and no bit depth, so it labels your lossless FLAC as lossy **AAC**. Your quality profile then makes upgrade decisions based on the wrong label.

The plugin's **Remux To FLAC** option fixes this. It repackages each file into a real `.flac` without touching the audio, but it needs `ffmpeg` and `ffprobe`. The stock `ghcr.io/hotio/lidarr:pr-plugins` image includes neither, so build a thin image on top of it.

Create a `Dockerfile` next to your compose file:

```Dockerfile
FROM ghcr.io/hotio/lidarr:pr-plugins

# The Tidal plugin shells out to these for the FLAC remux.
# Do not add a USER line: this image's s6 init must start as root.
RUN apk add --no-cache ffmpeg
```

Then use `build:` in place of `image:`:

```yml
services:
  lidarr:
    build: .
    container_name: lidarr
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
    volumes:
      - /path/to/config/:/config
      - /path/to/downloads/:/downloads
      - /path/to/music:/music
    ports:
      - 8686:8686
    restart: unless-stopped
```

Bring it up, and confirm both tools are on the path:

```sh
docker compose up -d --build
docker compose exec lidarr ffmpeg -version
docker compose exec lidarr ffprobe -version
```

Each should print a version line, such as `ffmpeg version 6.1.2`. Rebuild with `docker compose build --pull` when you want a newer Lidarr, because `build:` does not pull updates on its own.

> [!NOTE]
> Not using Docker? Install FFmpeg from your OS package manager so the Lidarr process can find `ffmpeg` and `ffprobe` on its `PATH`. Stage 6's **Test** tells you if it cannot.

## Stage 3 — Install the plugin 🔌

1. In Lidarr, go to **System → Plugins**.
2. Paste this into the **GitHub URL** box and press **Install**:

   ```
   https://github.com/jwmarb/Lidarr.Plugin.Tidal
   ```

3. **Restart Lidarr** when it asks you to. The plugin does not load until you do.

After the restart, **System → Plugins** lists **Tidal**.

### Check which build you got

The installed version number doesn't tell you much, and neither does the **Owner** column: every build published so far still reports **TrevTV**, the upstream author. The reliable check is one field label. Go to **Settings → Download Clients**, press **+**, choose **Tidal**, and look at the second option. Then press **Cancel**; you set it up properly in Stage 6.

| The option reads | You have | What it means for this guide |
| --- | --- | --- |
| **Remux To FLAC** | The current build (`10.1.0.49-remux` or newer) | Everything below applies as written. |
| **Extract FLAC From M4A** | An older build, such as the `10.1.0.47-e2e` that the GitHub-URL install picks | It has no FFmpeg check in **Test** and the unhardened remux. `10.1.0.47` also lacks the rate-limit backoff. Install the current build as below. |

> [!WARNING]
> At the time of writing, installing from the GitHub URL picks an **older build**. Lidarr only accepts a release whose file is named `…net8.0.zip`, and the newest releases are not named that way. To install the current build by hand:
>
> 1. From the [Releases page](https://github.com/jwmarb/Lidarr.Plugin.Tidal/releases), download the `.zip` attached to the **newest** release, whatever its name.
> 2. Extract its `Lidarr.Plugin.Tidal.dll`, `.pdb`, and `.deps.json` into `<lidarr-config>/plugins/jwmarb/Lidarr.Plugin.Tidal/`, replacing what is there. With the compose file above, `<lidarr-config>` is `/path/to/config`.
> 3. Restart Lidarr, and repeat the check above.
>
> Until a correctly named release is published, ignore any **update** that **System → Plugins** offers for Tidal. Builds that report TrevTV are checked against upstream's repository, so their "update" is the upstream plugin, not this fork.

## Stage 4 — Choose a config path 📁

`Config Path` is the folder where the plugin saves your Tidal login. It writes one file there, `lastUser.json`, which holds your access and refresh tokens. That file is what lets the login survive a restart, so you do the browser handshake once rather than on every boot.

Choose a path **inside the container** that persists. A subfolder of Lidarr's own config volume is the simplest option:

```
/config/tidal
```

You do not have to create it. The plugin creates the folder the first time the indexer runs. It must be an **absolute** path, though: anything else is rejected on save with `Invalid Path: '<what you typed>'`.

Type it carefully the first time. The plugin keeps the first `Config Path` it is given until Lidarr restarts, so fixing a typo afterwards has no effect until you restart Lidarr.

> [!IMPORTANT]
> `lastUser.json` is a full account credential: anyone holding it can use your Tidal account until the refresh token is revoked. Keep `Config Path` out of shared or synced folders, and never attach it to a bug report.

## Stage 5 — Log in to Tidal 🔐

Tidal uses a **PKCE** browser login. The plugin generates a one-time login URL, you approve it in your browser, and Tidal sends you to a page whose address carries a short-lived code. You paste that address back, and the plugin trades the code for tokens. You never type your Tidal password into Lidarr.

The fiddly part is that **the login URL is not shown until the plugin has started once**. So the first pass below is a deliberate failure that brings it to life.

### 5a. Bring up the login URL

1. Go to **Settings → Indexers**, press **+**, and choose **Tidal** (in the *Other* section).
2. Type your `Config Path` from Stage 4. Leave everything else empty.
3. Press **Test**. It fails with *"Unable to connect to indexer, check the log for more details"*. **That is expected.**
4. Press **Cancel**, then **refresh the browser page** (F5).
5. Press **+** and choose **Tidal** again.

The **Tidal URL** field is now filled in with a link that begins `https://login.tidal.com/authorize?...`. If it is still blank, the Test never got far enough to start the plugin. That usually means `Config Path` was empty, or Lidarr restarted since. Repeat steps 2–5.

### 5b. Approve it and capture the redirect

1. **Copy the Tidal URL** and open it in a new browser tab.
2. Sign in to Tidal and press **Yes, continue** on the authorisation screen.
3. You land on a Tidal page reading **"Oops."** Despite appearances, **this is the success page**. The address bar now holds what you need:

   ```
   https://tidal.com/android/login/auth?code=eyJraWQiOi...
   ```

   Tidal may append more parameters after the code, such as `&state=...`. That is normal.

4. Copy the **entire** address from the address bar, including `https://`, everything after `code=`, and any parameters after it.

> [!CAUTION]
> **Treat this URL like a password.** For a few minutes it can be redeemed for a login to your account. Do not paste it into chat, issues, or screenshots.

### 5c. Paste it back and save

1. Back in Lidarr, in the same **Add Indexer** form, check that `Config Path` still holds your path. Retype it if the form was reset.
2. Paste the copied address into **Redirect Url**.
3. Press **Test**. It should now pass.
4. Press **Save**.

**Save** runs its own test as well. After a passing **Test**, that second test reuses the login already saved to `lastUser.json` rather than the spent code, which is why Test-then-Save works. Testing first is still worth it, because it shows you a failure while the form is still open.

Do this promptly. The code inside the redirect URL **expires within minutes**, and it is bound to the exact login URL that produced it. Don't press **Test** while you are away in the browser, either. Each time the indexer runs without an error, the plugin replaces the login URL, which orphans the one you opened. A Test with an empty `Redirect Url` counts, and so does a search by an already-saved Tidal indexer. If more than a few minutes pass, or Lidarr restarts in between, start again from 5a for a fresh link.

### How to tell it worked

A passing **Test** and a file on disk. On the host, look inside the directory your `/config` volume maps to:

```sh
ls -l /path/to/config/tidal/lastUser.json
```

From that point on, the plugin loads the saved login on every start and **refreshes the access token on its own**. You do not need to repeat the handshake when the token expires. You only repeat it if `lastUser.json` is deleted, or Tidal revokes the session or stops accepting its refresh token.

### When Test fails

One failure is shown on the form itself: `Invalid Path: '...'` under `Config Path` means the path is empty or relative. Use an absolute path such as `/config/tidal`.

Every other login failure shows the same generic text: *"Unable to connect to indexer, check the log for more details."* The real reason is in **System → Logs**, on the line just below `Tidal login failed:`.

| Log says | Meaning | Fix |
| --- | --- | --- |
| `The provided redirect URL looks wrong` | What you pasted does not begin with `https://` | Copy the full address bar contents, scheme included |
| `Authorization code not found in the redirect URL` | You pasted the **Tidal URL** (the login link), or a URL with no `code=` | Paste the address of the *"Oops."* page instead |
| `invalid_grant` … `The token has expired` | The code was older than a few minutes | Redo 5a–5c faster |
| `invalid_grant` … `Token has invalid payload` | The code is incomplete or garbled | Copy the address bar again, all of it |
| Any other `invalid_grant` | The code came from a different login link, or Lidarr restarted after you opened it | Start again at 5a, and use the link from that page load |
| No `Tidal login failed` line at all | No `Redirect Url` was entered and no saved login exists | That is the expected first pass of 5a |

> [!TIP]
> Copy the `Config Path` before you start. The form can lose it on cancel, and retyping a path during a time-limited login is how codes expire.

## Stage 6 — Add the download client 💾

1. Go to **Settings → Download Clients**, press **+**, and choose **Tidal** (again under *Other*).
2. Fill in the fields below.
3. Press **Test**, then **Save**.

| Field | What to put |
| --- | --- |
| `Download Path` | A staging folder Lidarr can write to and can see **inside its container**, for example `/downloads/tidal`. It must be an absolute path. Lidarr moves files out of it into your library, so it empties itself. Keep it *outside* your music root folder, or Lidarr raises a health warning. |
| `Remux To FLAC` | **On.** This is the fix from Stage 2. It needs FFmpeg. |
| `Re-encode AAC into MP3` | Leave **off** unless a device of yours cannot play AAC. It is a lossy-to-lossy conversion and only affects the 96 and 320 kbps releases. |
| `Save Synced Lyrics` | Optional. Writes a `.lrc` next to each track when time-synced lyrics exist. It only reaches your library if you add `lrc` in Stage 7. |
| `Use LRCLIB as Backup Lyric Provider` | Optional. Asks [LRCLIB](https://lrclib.net/) when Tidal has no lyrics for a track, or, with `Save Synced Lyrics` on, when Tidal has no *synced* lyrics. |
| `Download Delay` | Optional. Pauses between tracks to stay under Tidal's rate limit. Turn it on if large albums start failing partway through. |
| `Download Delay Minimum` / `Maximum` | The random pause range, in seconds. The defaults `3` and `5` are fine. |

**Test** is what catches a missing FFmpeg. With **Remux To FLAC** or **Re-encode AAC into MP3** enabled and no FFmpeg in the container, it fails with:

```
FFMPEG is required for this option but is not available to Lidarr. 'ffmpeg' could not be run. Install it in the Lidarr container and make sure it is on PATH. (...)
```

If you see that, go back to Stage 2. If the option is labelled **Extract FLAC From M4A** instead, you are on an older build whose **Test** does not check for FFmpeg at all. Stage 2's `ffmpeg -version` is then your only guard, so install the current build as described in [Stage 3](#check-which-build-you-got).

> [!WARNING]
> **Test does not check that Lidarr can write to the folder.** It only checks the path's *syntax*. The plugin creates missing folders when the first download starts, and the download fails if it cannot. Check it yourself, as the user Lidarr runs as:
>
> ```sh
> docker compose exec --user 1000:1000 lidarr sh -c \
>   'mkdir -p /downloads/tidal && touch /downloads/tidal/.write-test && rm /downloads/tidal/.write-test && echo writable'
> ```
>
> Substitute your `PUID:PGID` and path. `writable` means the account Lidarr runs as can write there. Keep the path under a mounted volume such as `/downloads`. A typo elsewhere still prints `writable`, but it writes inside the container where you will not find it.

The download client has **no credential fields**. It shares the login the indexer saved in Stage 5, so credentials are entered exactly once.

## Stage 7 — The settings that block everything 🚧

Both tests are green, and downloads may still not happen. The two settings below are why, and neither produces an obvious error.

### 1. Enable the Tidal download protocol ⚠️

**This is the big one.** When Lidarr first sees the plugin, it adds Tidal to every delay profile **switched off**. Every Tidal release is then rejected before it is grabbed.

Go to **Settings → Profiles → Delay Profiles**, press the wrench on **each** profile that existed before you installed the plugin, and toggle **Tidal** on. Tagged profiles count too: an artist matched by a tagged profile uses that profile, not the default. A profile you create *after* installing starts with Tidal already on.

The symptom when you forget is a rejection reason in an interactive search:

```
TidalDownloadProtocol is not enabled for this artist
```

### 2. Allow FLAC in your quality profile

Under **Settings → Profiles → Quality Profiles**, the profile your artists use must allow the quality you want to download. For each album, the indexer offers up to four releases:

| Release title contains | Lidarr parses it as | Offered when |
| --- | --- | --- |
| `[AAC (M4A) 96kbps]` | `AAC-VBR` | Always |
| `[AAC (M4A) 320kbps]` | `AAC-320` | Always |
| `[FLAC (M4A) Lossless]` | `FLAC` | Tidal tags the album `LOSSLESS` or `HIRES_LOSSLESS` |
| `[FLAC (M4A) 24bit Lossless]` | `FLAC 24bit` | Tidal tags the album `HIRES_LOSSLESS` |

FLAC lives inside the **Lossless** group, so expand it. The stock profiles behave like this:

| Stock profile | `FLAC` | `FLAC 24bit` | AAC |
| --- | --- | --- | --- |
| **Any** | ✅ | ✅ | ✅ |
| **Lossless** | ✅ | ✅ | ❌ |
| **Standard** | ❌ | ❌ | ✅ |

If your artists use **Standard**, Lidarr only ever grabs AAC. Switch them to **Lossless**, or enable the Lossless group in Standard.

### Optional, but you probably want these

Under **Settings → Media Management**:

- **Rename Tracks** — turn it on so Lidarr applies your track-naming format when it imports. The default format puts each album in its own folder; with renaming off, files land loose in the artist directory under their downloaded names.
- **Import Extra Files** — press **Show Advanced** to see it, turn it on, and add `lrc` to the extensions list. Without it, the `.lrc` lyric files from Stage 6 are left behind.

## Stage 8 — Verify end to end ✅

A valid configuration is not the same as a working pipeline. Prove it:

1. Pick an album in your library that Tidal definitely carries.
2. Open it and run an **Interactive Search**.
3. Confirm Tidal releases appear, titled like `Artist - Album (Year) [FLAC (M4A) Lossless] [WEB]`. If they are listed but rejected, the rejection reason points at Stage 7.
4. **Grab** the FLAC release and watch **Activity → Queue**.
5. Confirm it reaches **Imported**, then check the files in your library.

A healthy run looks like this: the queue item counts down as tracks download, then disappears as Lidarr imports it. The album's **History** tab then lists one *grabbed* event, one *imported* event for the download, and one per track (hover an icon to see which). The download folder ends up **empty**, which is correct because the files were moved into your library.

Then check the audio itself, because this is where a missing remux hides. Copy a track's real path from its file details in Lidarr, then run:

```sh
docker compose exec lidarr ffprobe -hide_banner "/music/<path copied from Lidarr>.flac"
```

A good file reports `Input #0, flac`, a full track duration, and a bitrate around `900`–`1000 kb/s` at `44100 Hz`. Hi-Res files run higher. The same file details in Lidarr should read **FLAC**, with a real bitrate and `16bit` or `24bit`.

If the files end in `.m4a`, or Lidarr shows **AAC-VBR** with `-2147483648 kbps`, the remux did not run. Check that **Remux To FLAC** is on and that Stage 2's `ffmpeg -version` works. The remux happens only at download time, so files that are already imported stay as they are. Delete the album's files and grab it again.

## Troubleshooting 🔧

| Symptom | Most likely cause |
| --- | --- |
| No **System → Plugins** page | Lidarr is not on the plugins branch (Stage 1) |
| Download client offers **Extract FLAC From M4A** | An older build is installed (Stage 3) |
| **Tidal URL** field is blank | The plugin has not run yet: Test once, cancel, refresh (Stage 5a) |
| Indexer **Test** says *"check the log for more details"* | Look up the `Tidal login failed` line in Stage 5's table |
| `Invalid Path` on `Config Path` or `Download Path` | Path is empty or relative; use an absolute path (Stages 4, 6) |
| Download client **Test** says `FFMPEG is required` | The image has no FFmpeg (Stage 2) |
| Tidal releases rejected: `TidalDownloadProtocol is not enabled` | Protocol off in a delay profile (Stage 7) |
| Releases found but only AAC is ever grabbed | Quality profile excludes FLAC; **Standard** does by default (Stage 7) |
| Imports show **AAC-VBR** at `-2147483648 kbps` | Remux off or FFmpeg missing; re-grab after fixing (Stages 2, 6, 8) |
| Album search slow, or `Tidal is rate limiting` in the log | Tidal's throttle is cumulative; wait several minutes, enable **Download Delay** |
| Album fails partway through | Rate limiting, or a track your plan or region cannot stream; check the log for the track's error |
| Searching for a song returns its whole album | Expected: Lidarr downloads albums, not standalone tracks |
| `.lrc` files missing from the library | `lrc` not added to **Import Extra Files** (Stage 7) |
| Worked for weeks, now every search fails | Session revoked or `lastUser.json` deleted: redo Stage 5 |

## Keeping your credentials safe 🔒

Two things in this setup are credentials:

- **The redirect URL** from Stage 5b. It is short-lived, but for those minutes it can be traded for a login to your account. Lidarr also keeps the last one in the indexer's `Redirect Url` field. It is spent and harmless by then, but do not paste it anywhere public.
- **`lastUser.json`** in your `Config Path`. It holds a refresh token that keeps working until revoked. Do not commit it, sync it, or attach it to an issue. If it leaks, change your Tidal password, delete the file, and redo Stage 5.

Lidarr shows the indexer's fields in clear text, and its settings API returns them, so treat Lidarr's config and API key as sensitive too.

---

Back to the [README](../README.md) for architecture, the settings reference, and known limitations.
