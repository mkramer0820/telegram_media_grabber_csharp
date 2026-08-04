# Telegram Media Grabber (C#)

C# rewrite of [telegram_media_grabber](https://github.com/mkramer0820/telegram_media_grabber)
(Python) — a Telegram channel/chat media downloader and uploader, with
audiobook-specific tagging/organization. Goal: a lighter-weight, single-file
distributable with a cleaner service architecture, informed by lessons
learned running the Python version against real libraries.

## Status

**Builds, tests green (117/117), and runs.** All four modes
(`download`/`upload`/`reprocess`/`verify`) are wired end-to-end.
`--mode reprocess` has been run against real generated audio files and
verified to tag/relocate correctly — it's fully offline (no Telegram
connection), so it's the only mode verified against real files without
credentials.

Download filtering beyond plain media-type/dedup: `ChannelOptions.MinDate`
(scan stops once an older message is hit), `MaxMessages` (caps how many of
a channel's most recent messages are even fetched), and `EpisodeRange`
(only download files whose filename indicates an episode number/range
overlapping the configured window — unparseable filenames are always
downloaded, never silently skipped; see `EpisodeRangeExtractor`).
`AutoUploadTarget` immediately re-uploads each downloaded file to another
chat, dedup-tracked like `upload_jobs`, independent of it. See
`config/channels.example.yaml` for worked examples of each.

**Known gap**: the Telegram client adapter (`WTelegramClientAdapter`)
compiles against the real WTelegramClient API and has structurally sound
FloodWait retry logic (independently unit tested), but has **not been
exercised against a live Telegram connection** — no API credentials were
available while building it. `download`/`upload`/`verify` modes will need
real credentials and a first live run to confirm end-to-end; see that
class's XML doc remarks for the specific known design wrinkle (a raw-message
cache keyed by `(chatId, messageId)`, populated by `IterMessagesAsync`/
`GetMessagesAsync`, that `DownloadMediaAsync` depends on).

## Building and running

```bash
dotnet build TelegramMediaGrabber.sln
dotnet test TelegramMediaGrabber.sln
```

```bash
cp .env.example .env                              # fill in TG_API_ID / TG_API_HASH / TG_PHONE
cp config/channels.example.yaml config/channels.yaml   # then edit it
dotnet run --project src/TelegramMediaGrabber.Cli                       # --mode run (default) — just do what config says
```

**Default behavior (`--mode run`, or no `--mode` at all)**: does
everything `config/channels.yaml` declares, continuously, in one process
— catch up on each channel's backlog, then download new messages in real
time as Telegram pushes them, and (if `upload_jobs` is non-empty)
periodically re-scan and upload on `upload_interval_seconds`. This is the
normal way to run it; see `CONFIG.md` for every field. Stop with Ctrl+C.

The single-purpose modes below stay available for manual
override/recovery — forcing an extra catch-up scan, re-verifying tags,
etc. — not for normal day-to-day use:

```bash
dotnet run --project src/TelegramMediaGrabber.Cli -- --mode download   # one-shot catch-up scan only
dotnet run --project src/TelegramMediaGrabber.Cli -- --mode upload     # one-shot upload_jobs scan only
dotnet run --project src/TelegramMediaGrabber.Cli -- --mode watch      # live-only, no backlog catch-up
dotnet run --project src/TelegramMediaGrabber.Cli -- --mode reprocess  # offline, no credentials needed
dotnet run --project src/TelegramMediaGrabber.Cli -- --mode verify
dotnet run --project src/TelegramMediaGrabber.Cli -- --mode upload --interval 300   # force a repeating upload scan manually
```

`--interval <seconds>` re-runs any of the single-purpose modes in a loop
instead of running once and exiting. It's a manual-override equivalent of
what `--mode run` already does automatically for uploads via
`upload_interval_seconds` in config — not meaningful for `watch`/`run`,
which already run continuously on their own.

**Testing without touching real state**: set `test_mode: true` in
`config/channels.yaml`. Downloads/uploads/tagging still happen for real,
but nothing is recorded in the real state database, so re-running a test
never skips files as "already done" and never leaves the real state
thinking something happened that was just a test. Turn it off once you're
ready to run for real.

## Shipping as a single executable

`dotnet publish` produces one self-contained `.exe` (or ELF binary on
Linux/macOS) with the .NET runtime and every dependency embedded —
nothing else needs to be installed on the machine you copy it to:

```bash
dotnet publish src/TelegramMediaGrabber.Cli -c Release -r win-x64   -p:SelfContained=true  # Windows
dotnet publish src/TelegramMediaGrabber.Cli -c Release -r linux-x64 -p:SelfContained=true  # Linux
dotnet publish src/TelegramMediaGrabber.Cli -c Release -r osx-x64   -p:SelfContained=true  # macOS (Intel)
```

`-p:SelfContained=true` has to be passed explicitly on the publish command
(it's deliberately not set in the `.csproj` — see the comment there: doing
so there would make even a plain `dotnet build`/IDE build emit a second,
RID-specific apphost stub alongside the normal build output, which is
confusing and not the deliverable).

**Deploying a new build without losing your session/state**: use
`scripts/publish-release.ps1` instead of copying `dotnet publish`'s output
by hand. A plain re-publish + copy silently overwrites `data/downloader.session`,
`data/state.db`, `.env`, and `config/channels.yaml` every time, forcing a
fresh Telegram login and losing all download/upload history. The script
never touches any of those (or `logs/`/`downloads/`/`uploads/`) — it only
ever refreshes the `.exe` and reference docs, and seeds `.env`/`config/channels.yaml`
on a brand-new deployment where they don't exist yet, never overwriting
them once they do:

```powershell
./scripts/publish-release.ps1 -DeployDir "D:\wherever\you\keep\this"
```

Safe to run repeatedly against the same deploy folder for every new
release.

The **real, distributable file** lands in
`src/TelegramMediaGrabber.Cli/bin/Release/net9.0/<rid>/publish/TelegramMediaGrabber.Cli.exe`
(tens of MB — it embeds the runtime). Publishing also leaves a much
smaller (~150 KB) intermediate apphost stub one level up, at
`bin/Release/net9.0/<rid>/TelegramMediaGrabber.Cli.exe` — that one is a
normal by-product of the build step publish runs internally, requires the
sibling DLLs in that same folder to run, and is **not** what you want to
copy anywhere; only the one inside the `publish/` subfolder is fully
self-contained. Copy that one (plus your `.env`, `config/channels.yaml`,
and a `data/`/`logs/` working directory alongside it) anywhere and run it
directly; the `.pdb` files next to it are debug symbols only, safe to
leave out of a distribution. Verified end-to-end: published, copied to a
clean directory with no project files, and run standalone against a real
audio fixture in `--mode reprocess` — tagged and relocated correctly.

`PublishTrimmed` is deliberately off (see the comment in
`TelegramMediaGrabber.Cli.csproj`) — YamlDotNet, TagLibSharp, Serilog, and
WTelegramClient all lean on reflection in ways trimming can silently break
without a build error, so the binary is larger (~39 MB) than a trimmed one
would be, in exchange for not risking a runtime failure trimming wouldn't
catch at build time.

**Self-service for someone who only has the built `.exe`** (no source
checkout): the `publish/` folder also includes `.env.example`,
`config/channels.example.yaml`, `README.md`, and `CONFIG.md` — copied in
automatically by the `.csproj`, so nothing extra needs to be fetched from
the repo. Relative paths (session file, state DB, config, logs) are
anchored to wherever `.env`/`.env.example` is found (searched upward from
the running program's own location, not whatever directory it happened to
be launched from) — so as long as those example files stay next to the
`.exe`, `config/`, `data/`, and `logs/` all resolve there too, regardless
of whether you run it by double-clicking, from a terminal, or via a
scheduled task with some other working directory. Running it with no
`.env` set up yet, or one still holding the example's placeholder values,
prints exactly what to do instead of a raw error — no source access or
prior context needed.

See **`CONFIG.md`** for the full `config/channels.yaml` field reference,
including private-channel chat ID resolution. Logs go to `logs/app.log`
(rotating file, never the console — AGENTS.md §4).

## Documents

- **`CONFIG.md`** — full `config/channels.yaml` schema reference: every
  field on a channel and an upload job, how chat ID resolution works for
  public vs. private channels, and worked multi-channel/multi-job examples.
- **`PROJECT_STATE.md`** — what the *original Python app* does, in detail:
  algorithms worth preserving exactly (atomic writes, FloodWait retry
  shape, dedup rules, filename-parsing behavior), data model, config
  schema, and lessons learned from real production bugs (§10) that shaped
  this rewrite's design.
- **`CSHARP_PORT_GUIDE.md`** — the design/instruction set this rewrite was
  built from: project structure, domain model, DI/hosting approach,
  persistence strategy, testing approach, the metadata-overrides feature
  design (not yet implemented — see below), and distribution plan.

## Project layout

```
src/
├── TelegramMediaGrabber.Domain/          ContentUnitKind, ChapterNumber, MessageReference, ParseResult — zero dependencies.
├── TelegramMediaGrabber.Application/     Filename parser chain, DownloadManager, UploadManager, ReprocessService,
│                                         VerifyService, and every interface (ITelegramClient, IStateRepository,
│                                         IAudiobookTagger) — depends only on Domain.
├── TelegramMediaGrabber.Infrastructure/  SqliteStateRepository, YamlConfigLoader, TagLibAudiobookTagger,
│                                         WTelegramClientAdapter — the concrete adapters.
└── TelegramMediaGrabber.Cli/             Program.cs composition root, Spectre.Console dashboards, mode dispatch.
tests/
├── TelegramMediaGrabber.Domain.Tests/
├── TelegramMediaGrabber.Application.Tests/       Hand-written fakes (no mocking framework) — see AGENTS.md §6.
└── TelegramMediaGrabber.Infrastructure.Tests/    Real temp-file SQLite, real ffmpeg-generated audio fixtures.
```

## What's not done yet

- Live verification of the Telegram adapter (see "Known gap" above).
- A Docker image (the Python predecessor ships one). Not needed for
  distribution now that single-file `dotnet publish` works (see "Shipping
  as a single executable" above) — Docker would only be worth adding for
  a containerized deployment target specifically, not as the packaging
  mechanism.

## For AI coding agents

**`AGENTS.md`** contains the non-negotiable engineering rules for this
repo (tool-agnostic — read by Claude Code, Cursor, Copilot, Codex CLI,
etc.). `CLAUDE.md` just points here to avoid duplicated/drifting rules
across tool-specific files. Read `AGENTS.md` before writing any code.

## License

MIT — see `LICENSE`.
