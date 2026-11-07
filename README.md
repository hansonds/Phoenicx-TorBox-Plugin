<p align="center"><img src="logo.png" alt="Torvia — Straight from TorBox to Jellyfin" width="640"></p>

<h1 align="center">Torvia</h1>
<p align="center"><b>Straight from TorBox to Jellyfin.</b><br>
A Jellyfin plugin that turns your TorBox movies and series into real Jellyfin libraries —<br>
and streams them <b>directly from TorBox to your Jellyfin apps</b>, with no load on your server.</p>

<p align="center">
  <a href="https://github.com/hansonds/Torvia-Jellyfin/releases/latest"><img src="https://img.shields.io/github/v/release/hansonds/Torvia-Jellyfin?label=version&color=8b5cf6" alt="Version"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.x-00a4dc" alt="Jellyfin 12.x">
  <img src="https://img.shields.io/badge/TorBox-API-22d3ee" alt="TorBox">
</p>

---

## ✨ Why Torvia

Normally, to watch your TorBox downloads in Jellyfin, the server has to **download the video and
pass it on** to your app (for example with an rclone mount). Every viewer then uses your server's
bandwidth, and a few 4K streams at once can make everything buffer.

**Torvia skips the middle step.** Jellyfin keeps the library, the posters and everyone's watch
history — but the video itself goes **straight from TorBox to the app**:

```
          Library, posters, watch history
 Your app  ◄──────────────────────────────►  Your Jellyfin server   (tiny traffic)
    ▲
    │  The movie itself
    └──────────────────────────────────────  TorBox  (no server in between)
```

When the app **direct plays** the file (plays it as it is, without converting), your server only
hands out the link. **No video passes through your server: no bandwidth, no CPU, no disk.**
That means faster starts, quicker seeking, smooth 4K, and many people can watch at the same time,
even on a small server.

---

## 🚀 Features

### Library
- 🎬 **Movies and series as normal Jellyfin libraries** — posters, details, ratings,
  collections, search: everything Jellyfin normally does.
- 📥 **Your whole TorBox account** — torrents, usenet and web downloads (each can be turned off).
- 🔄 **Automatic sync** — at startup and on a schedule you choose (every 30 minutes up to once a
  day, or only manually). New downloads appear, deleted ones disappear. **Sync now** button too.
- 🏷️ **TorBox tags → separate libraries** — tag downloads on TorBox (e.g. `cinema`, `movies`,
  `tv-shows`) and each tag becomes its own folder, so it can be its own Jellyfin library.
  Upper / lower case doesn't matter.
- 🧹 **Clean names from messy releases** — removes site names (`www.Site.company - …`), quality
  tags and release junk; reads `S01E02`, `1x02`, season folders, years, and titles in any language
  (Tamil, Hindi, Chinese, …).
- ✏️ **Name fixes** — correct any title by hand: `text in the TorBox name = Title (Year)`.
- 🎞️ **Versions** — the same movie in 2160p and 1080p shows as one movie with a version choice.
- 💬 **Subtitles** — subtitles inside the video work as usual; separate subtitle files from the
  download (`.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`, `.smi`) are added next to the movie with their
  language (`.en`, `.ta`, `.hi`, …, forced and SDH too) so they appear in every app.

### Playback
- ⚡ **Direct from TorBox** — the app streams from TorBox's servers, not through yours.
- 👥 **Watch history per user** — watched status, resume points and *Continue Watching* are kept
  on your Jellyfin server for every user, like any other movie.
- 🔑 **Fresh link every play** — TorBox links expire; Torvia gets a new one each time, so nothing
  ever goes stale.
- 📱 **Works with Jellyfin apps** — Jellyfin web and Jellyfin Media Player get the TorBox link
  directly; other apps (Android, Android TV, iPhone, Kodi, …) ask the server as usual and are
  instantly redirected to TorBox.
- ⬇️ **Downloads for offline viewing** — the *Download* button in apps gives the real video from
  TorBox.
- 🛟 **Safe fallback** — if TorBox doesn't give a link at that moment, Jellyfin plays it the normal
  way instead of failing.

### Safe and private
- 🔒 **Your TorBox API key never leaves the server.** Apps only get short-lived TorBox links.
- ✍️ **Signed play links** — nobody can guess a link to your files.
- 🛡️ **Outage protection** — if TorBox is down or answers with an empty list, your library is
  kept as it is. A big clean-up on TorBox is only applied when it's still the same 6 hours later.
- 🚦 **Gentle on TorBox** — link requests are limited and reused for a short time, so even big
  library scans don't flood TorBox.
- 🗂️ **Your other libraries are untouched** — local, OneDrive, NAS, WebDAV movies play exactly as
  before.

---

## 📦 Installation

**You need:** Jellyfin **12.x** and a **TorBox** account with its API key.

### 1. Add the repository
Jellyfin **Dashboard → Plugins → Repositories → +**

| Field | Value |
|---|---|
| Name | `Torvia` |
| URL | `https://raw.githubusercontent.com/hansonds/Torvia-Jellyfin/gh-pages/manifest.json` |

### 2. Install
**Plugins → Catalog → Torvia → Install**, then **restart Jellyfin**.

### 3. Set it up
**Dashboard → Torvia**
1. Paste your **TorBox API key** (torbox.app → Settings → API key) and click **Save**.
2. Click **Test API key** — it should say *Connected as …*.
3. Click **Sync now**.

### 4. Add the libraries
Reopen the Torvia page — it shows the folders to add. In
**Dashboard → Libraries → Add Media Library**:

| Library type | Folder |
|---|---|
| **Movies** | the *Movies* folder (e.g. `/config/data/phoenicx-torbox/Movies`) |
| **Shows** | the *Shows* folder (e.g. `/config/data/phoenicx-torbox/Shows`) |
| Movies **or** Shows | each **tag folder** listed (e.g. `…/Tags/cinema`) — match the type to what you tagged |

That's it. Jellyfin scans the libraries and your TorBox movies appear with posters.

> **Docker:** the folders are inside Jellyfin's own `/config` folder, so nothing in your Docker
> setup has to change.

### Updating
New versions install themselves overnight (Jellyfin's *Update Plugins* task) and take effect after
a restart. To update now: **Catalog → Torvia → pick the newest version → Install → restart**.

---

## ⚙️ Settings

| Setting | What it does | Default |
|---|---|---|
| **TorBox API key** | The only required setting. | — |
| Public server address | Optional. Leave empty: each app gets the address it connected with. Only fill it in if TorBox videos don't start behind your reverse proxy. | empty (automatic) |
| Library folder | Where the library files go. | Jellyfin's data folder |
| Sync interval | How often TorBox is checked. | every 3 hours |
| Play straight from TorBox | Apps stream from TorBox directly. | on |
| Separate folder for each TorBox tag | One library per tag. | on |
| Include usenet / web downloads | Also list those downloads. | on |
| Name fixes | Fix titles that come out wrong, one per line: `Kaithi.2019 = Kaithi (2019)` | empty |

The page also shows the last sync result: movies, series, episodes, subtitles, tag folders, and any
file names that couldn't be read.

### Start a sync from a script or bot
Create an API key in **Dashboard → API Keys**, then:
```bash
curl -X POST "https://your-jellyfin-server/PhoenicxTorBox/Sync" -H 'Authorization: MediaBrowser Token="YOUR_API_KEY"'
curl "https://your-jellyfin-server/PhoenicxTorBox/Status" -H 'Authorization: MediaBrowser Token="YOUR_API_KEY"'
```

---

## 📱 Apps

| App | Direct from TorBox |
|---|---|
| Jellyfin web (browser), Jellyfin Media Player / Desktop | ✔ gets the TorBox link |
| Jellyfin Android, Android TV, Findroid, Wholphin | ✔ redirected to TorBox |
| iPhone / iPad: Streamyfin, Swiftfin, Infuse | ✔ redirected to TorBox |
| Kodi (JellyCon) | ✔ redirected to TorBox |

**Direct play is the key.** The video goes TorBox → app only when the app plays the file as it is.
If an app wants to convert it (format the device can't play, or a quality / bitrate limit set in
the app), that conversion has to happen on the server. Set the app's streaming quality to
**Maximum / Auto** for the best result.

**Check it yourself:** while a TorBox movie plays, the server's network traffic should barely move
(`docker stats` → *NET I/O*), or watch the browser's Network tab: the video comes from a TorBox
address (`…tb-cdn…`).

---

## ❓ FAQ

**Does deleting something on TorBox remove it from Jellyfin?**
Yes, at the next sync. Watch history is kept by Jellyfin, so if you add it again later it usually
comes back as watched.

**What if TorBox is down?**
Browsing still works (everything is stored in Jellyfin); TorBox movies play again once TorBox is
back. Your library is never emptied because of an outage.

**How many movies can it handle?**
No limit in the plugin — as many as your TorBox plan keeps. Each item is a ~100-byte file. The
first scan of a big library takes a while because Jellyfin reads every new title once.

**Can many people watch at once?**
Yes. Since the video comes from TorBox, your server isn't the bottleneck. TorBox allows using an
account from different places; very heavy use falls under TorBox's fair-use rules.

**What happens when Jellyfin updates?**
Small updates: nothing, it keeps working. A new major Jellyfin version may need a new Torvia
version — your libraries and watch history stay in the meantime.

---

## 🛠️ How it works

For every movie and episode Torvia writes a tiny `.strm` file (a link instead of a video), e.g.
`Movies/Dune (2021)/Dune (2021).strm`, holding a signed link to your own server:
`https://your-jellyfin-server/PhoenicxTorBox/Play/t/123/4?sig=…`

Jellyfin treats these as normal library items. When an app presses play, Torvia asks TorBox for a
fresh download link and hands it to the app (or redirects the app's normal stream request to it),
so the app streams the file straight from TorBox.

---

<p align="center">
  <b>Torvia</b> : Made by Hanson D with ❤️<br>
  <a href="https://phoenicx.org">phoenicx.org</a>
</p>

<p align="center"><sub>Torvia is an independent project and is not affiliated with TorBox or Jellyfin.</sub></p>
