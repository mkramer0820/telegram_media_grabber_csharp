# `config/channels.yaml` — full reference

This is the complete schema for `config/channels.yaml`, parsed by
`YamlConfigLoader` (`src/TelegramMediaGrabber.Infrastructure/Configuration/YamlConfigLoader.cs`)
into `ChannelsOptions`. Every level rejects unknown keys and fails loading
immediately with a clear error — there is no silent typo. Copy
`config/channels.example.yaml` and edit it; that file is not read by the
app itself.

**Config is the source of truth for behavior.** The default `dotnet run
--project src/TelegramMediaGrabber.Cli` invocation (no `--mode`, or
`--mode run` explicitly) does everything this file declares in one
continuous process: an initial catch-up download over every channel, then
real-time downloading of new messages as they arrive, plus — if
`upload_jobs` is non-empty — a periodic upload re-scan on
`upload_interval_seconds`. The single-purpose `--mode
download`/`upload`/`watch`/`verify`/`reprocess` commands stay available
for manual override/recovery (forcing an extra catch-up scan, re-tagging
after a mistake, etc.) — they're not the normal way to run this.

## Top level

| Key | Type | Default | Notes |
|---|---|---|---|
| `download_root` | string | `downloads` | Base directory non-audiobook channels' `output_subdir` is relative to. |
| `max_concurrent_downloads` | int, 1–50 | `5` | Shared semaphore across **all** channels in one run, not per-channel. |
| `channels` | list of [Channel](#channel) | `[]` | Download targets. |
| `upload_jobs` | list of [Upload job](#upload-job) | `[]` | Upload-mode targets. Empty means upload mode has nothing to send, and `--mode run`'s upload loop doesn't start at all. |
| `upload_interval_seconds` | int ≥ 1 | `600` | How often `--mode run` re-scans `upload_jobs` for new files. Each scan is still paced/batched exactly like a manual `--mode upload` run — a 1000-file backlog found in one scan doesn't get sent all at once, just normally batched/delayed like any other upload pass. No effect if `upload_jobs` is empty. |
| `test_mode` | bool | `false` | If true, downloads/uploads/tagging still happen for real, but state tracking is redirected to a fresh, disposable database instead of the real one — re-running a test never skips anything as already-done, and never leaves the real state DB thinking something happened that you were just experimenting with. Turn off (or remove) once you're done testing and want it tracked for real. |

## Channel

Each entry under `channels:`:

| Key | Type | Required | Notes |
|---|---|---|---|
| `id` | int or string | yes | See [Chat IDs and private channels](#chat-ids-and-private-channels) below. |
| `name` | string | yes | Label used in logs and the dashboard — not sent anywhere. |
| `output_subdir` | string | yes | Subfolder under `download_root` files land in first. For `audiobook_mode` channels this is just staging; files get moved out to `LOCAL_MEDIA_SERVER` after tagging. |
| `media_types` | list of `photo`/`video`/`document`/`audio` | no | Defaults to `[photo, video, document]` (audio excluded by default — most channels that post audio as "audio" rather than a raw document are audiobook/podcast channels, where you'll set this explicitly anyway). |
| `min_date` | ISO-8601 date, e.g. `"2026-06-01"` | no | Messages older than this are skipped. Scanning stops (not just filters) the instant an older message is hit, since Telegram returns messages newest-first. |
| `max_messages` | int ≥ 1 | no | Caps how many of the channel's most recent messages are even fetched this run, independent of `min_date`. Use this for a channel with thousands of old messages you don't want scanned at all — e.g. `max_messages: 200` only ever looks at the 200 newest. Combine both for a count cap *and* a date floor. |
| `episode_range` | `{start, end}` | no | See [Episode range filtering](#episode-range-filtering) below. |
| `auto_upload_target` | int or string | no | See [Auto-upload](#auto-upload) below. |
| `audiobook_mode` | bool | no | Default `false`. Enables tag + relocate post-processing. |
| `metadata` | `{author, novel_title}` | required iff `audiobook_mode: true` | `author` → Artist/AlbumArtist tag **only** (never part of the destination path). `novel_title` → Album tag **and** destination folder name, unless `media_server_subdir` overrides it. |
| `local_only` | bool | no | Default `false`. If true, tag/relocate under this app's own `download_root/Audiobooks` instead of `LOCAL_MEDIA_SERVER`. See [Keeping a channel local](#keeping-a-channel-local) below. |
| `media_server_subdir` | string | no | Exact destination folder name to use instead of deriving one from `novel_title`. Works with or without `local_only`. |
| `overrides` | list of [Override](#per-file-overrides) | no | Per-file corrections for episode number/subtitle when the filename can't be parsed automatically, or is parsed wrong. |

### Episode range filtering

`episode_range: {start: N, end: M}` narrows a channel's download to files
whose filename indicates an episode number inside `[N, M]` (inclusive).
Read from the raw filename via `EpisodeRangeExtractor`
(`src/TelegramMediaGrabber.Application/Parsing/EpisodeRangeExtractor.cs`),
trying three shapes in order:

1. `Ep <n>-<m>` / `Episode <n>-<m>` — an explicit bundle range.
2. `Ep <n>` (with or without trailing subtitle text) — a single episode.
3. A bare number or number-range anywhere in the filename, e.g. `1114`,
   `5-6`, `Example Novel 100-251`, `0001_0100_Title`.

A filename that doesn't match any of these is **always downloaded** — the
filter never silently drops a file it couldn't classify, it only narrows
files it *could* classify. A bundle file (e.g. `Example Novel 15-22.mp3`)
is downloaded if its range overlaps the requested window at all, not only
if fully contained.

```yaml
- id: "some_audiobook_channel"
  name: example_novel_reslice
  media_types: [audio, document]
  output_subdir: example_novel_reslice
  episode_range:
    start: 20
    end: 25
```

This is a filter on *what gets downloaded*, independent of
`audiobook_mode` — it works on any channel with numbered filenames, tagged
or not.

### Auto-upload

`auto_upload_target` immediately re-uploads each file downloaded from that
channel to another chat, right after it lands (and after audiobook
tagging, if `audiobook_mode` is on). It's dedup-tracked the same way
`upload_jobs` is (a shared `uploaded_files` state table keyed by target
chat + a fast filename/size/content-prefix hash), so re-running download
mode never re-sends a file that already went out. It's independent of
`upload_jobs` — you can use one, the other, or both.

```yaml
- id: "@some_public_channel"
  name: mirrored_channel
  output_subdir: mirrored_channel
  auto_upload_target: "@my_backup_channel"
```

### Keeping a channel local

By default, an `audiobook_mode` channel's tagged files are relocated to
`LOCAL_MEDIA_SERVER` (e.g. a Plex/Jellyfin library mount). `local_only: true`
keeps the same tagging/organizing behavior but relocates under this app's
own `download_root/Audiobooks` instead — for a channel you never want
leaving this app's own folder tree (no external mount configured, or you
just don't want this particular book synced to your media server).

The destination layout is always `{dest_root}/{novel_title}/...` —
**there's deliberately no author-level folder**, since most people browse
audiobooks by title, not author. Author stays in the ID3/MP4
Artist/AlbumArtist tags only. If you want a different folder name than
the title (or need to match an existing library's naming), set
`media_server_subdir` to the exact folder name to use — it works whether
or not `local_only` is set.

```yaml
- id: "@another_audiobook_channel"
  name: kept_local_only
  output_subdir: kept_local_staging
  audiobook_mode: true
  local_only: true
  media_server_subdir: "Custom Folder Name"
  metadata:
    author: "Some Author"
    novel_title: "Some Novel"
```

### Per-file overrides

```yaml
overrides:
  - match: "weird_upload_name.mp3"   # exact original filename, not a pattern
    kind: chapter                     # "chapter" or "volume"
    number: 42
    subtitle: "Optional subtitle"    # optional
  - match: "duplicate_repost.mp3"
    skip: true                        # never process this file at all
```

Matched by **exact** original filename — no wildcards or folder-level
rules (CSHARP_PORT_GUIDE.md §2). An override always wins over whatever the
filename parser would have inferred. `skip: true` and
`kind`/`number`/`subtitle` are mutually exclusive on one entry.

## Upload job

Each entry under `upload_jobs:`:

| Key | Type | Required | Notes |
|---|---|---|---|
| `source_dir` | string (path) | yes | Local directory scanned for files to upload. A missing directory contributes nothing (not an error) — "nothing to upload yet" is normal. |
| `target_chat` | int or string | yes | Same ID rules as a channel's `id` — see below. |
| `recursive` | bool | no | Default `false` (top-level files only). `true` also scans subdirectories. |

Multiple jobs can point at different source directories and different
target chats in the same run — there's no 1:1 constraint, and jobs are
processed in the order declared. **If you keep separate local folders per
channel** (a common pattern — e.g. each download channel's `output_subdir`
already is one), route each to its own destination with one job per
folder:

```yaml
upload_jobs:
  - source_dir: downloads/general_dump
    target_chat: -1001111111111       # your private "general" mirror
    recursive: false
  - source_dir: downloads/docs_archive
    target_chat: -1002222222222       # a different private mirror
    recursive: true
  - source_dir: downloads/podcast_raw
    target_chat: "@my_public_podcast_backup"
    recursive: false
```

Files within one job are batched into Telegram media groups (albums) of
up to 10 at a time, with a pause between batches; each file is checked
against upload state first and skipped if already sent to that specific
target chat.

## Chat IDs and private channels

`id` (channels) and `target_chat` (upload jobs) accept the same three
shapes, resolved by `ITelegramClient.ResolveEntityAsync`:

1. **`"@username"`**, a bare username with no `@` (e.g. `"some_audiobook_channel"`),
   or a public `https://t.me/username` link — only works for chats that
   *have* a public username. Most private channels don't. ("Username"
   here is just Telegram's term for the public `@handle` form of an ID —
   it applies to channels and groups too, not only people.)
2. **A numeric chat ID**, e.g. `-1001234567890`. This is the only reliable
   way to target a private channel, group, or supergroup by ID. The app
   resolves it against your account's own chat list
   (`Messages_GetAllChats`) — it can only find chats your account has
   already joined, and it never joins one on your behalf.
3. **A private invite link** (`https://t.me/+<hash>` or the older
   `t.me/joinchat/<hash>` form) — resolved via `Messages_CheckChatInvite`.
   Works if you've already joined that chat (mirrors the Python
   predecessor's `CheckChatInviteRequest` logic exactly). If you haven't
   joined it yet, the app raises a clear error rather than joining on your
   behalf — join from the Telegram app first, then re-run.

**Finding a chat's numeric ID the app already knows**: run `--mode
resolve-ids`. It resolves every configured `id`/`target_chat` (however
it's written) and prints each one's permanent numeric ID, title,
username, and kind — and caches that in the state database for later
reference (`IStateRepository.CacheResolvedEntityAsync`), independent of
the config file. By default it's read-only, so paste a numeric ID in by
hand if you want a given entry to stop depending on a username/link that
could change or expire later — or see the next section for automatic
recovery/rewrite.

### When a channel's username changes: `--mode resolve-ids --write`

Some public channels (especially reposted/aggregator content) periodically
rename their `@username`, which breaks any config entry still written as
that username — Telegram returns `USERNAME_NOT_OCCUPIED` and, without
recovery, that one bad entry would previously take down the whole batch.
Two things guard against this:

1. **Per-channel isolation**: a resolve failure for one channel is now
   caught and reported (like any other per-file error) instead of crashing
   the entire run — every other configured channel still processes
   normally. Check the dashboard/`logs/app.log` for which channel needs
   attention.
2. **Auto-recovery + config rewrite**: run `--mode resolve-ids --write`.
   For any channel with `audiobook_mode`/`metadata.novel_title` set, a
   failed username resolve automatically falls back to an exact title
   match against your account's own joined-chat list — no guessing at the
   new username required, and it never joins a channel on your behalf. Any
   entry (recovered this way, or already resolving fine) whose configured
   `id` differs from its permanent numeric chat ID gets that `id:` line
   rewritten in `config/channels.yaml` to the numeric ID, with the
   original value preserved in a trailing comment for traceability, e.g.:

   ```yaml
   id: "3679792134"  # was "bloodwarlockxfm77" (renamed) -- pinned to permanent chat ID by --write on 2026-08-05
   ```

   This is a targeted per-line text replace, not a full YAML round-trip —
   the rest of the file's comments/formatting are left untouched, and any
   line it can't uniquely match (e.g. `upload_jobs.target_chat`, which
   uses a different field name than `id:`) is left alone and reported
   rather than guessed at. Pinning to the numeric ID also means the *next*
   rename of that channel won't break it again, since the numeric chat ID
   never changes even when the username does.

   A channel with no `metadata.novel_title` (not `audiobook_mode`) that
   fails to resolve has no title to fall back on — it's reported as failed
   and needs a manual fix (find the new username/link and update the
   config, or use `--mode resolve-ids` on a corrected value to confirm it
   before saving).

**Finding a private channel/group's numeric ID manually:**

- Easiest if you use Telegram Desktop or the web app: open the channel,
  copy the link to any message in it. For a private channel the link
  looks like `https://t.me/c/1234567890/42` — the number after `/c/` is
  the *internal* channel ID; prefix it with `-100` to get the ID this app
  expects: `-1001234567890`.
- Alternatively, forward any message from the private chat to a
  "userinfobot"/"getidsbot"-style utility bot (search Telegram for one) —
  it replies with the numeric chat ID directly. Only do this with bots you
  trust; forwarding reveals the message content to them.
- Groups (not channels) you're a member of sometimes report their ID
  without the `-100` prefix, just a plain negative number
  (e.g. `-123456789`) — the resolver in this app accepts both forms.

## Environment variables (`.env`)

See `.env.example` for the full list (`TG_API_ID`, `TG_API_HASH`,
`TG_PHONE`, `TG_SESSION_NAME`, `LOCAL_MEDIA_SERVER`, plus optional path
overrides for the config file, state DB, and log file).
