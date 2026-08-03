# AGENTS.md

This file defines **unbreakable rules** for any human or AI coding agent
working on this codebase — Claude Code, Cursor, Copilot, Codex CLI, or
anyone else. It is not a style guide; it is a contract. Violating any
"MUST" rule below is a bug, even if the code compiles and runs.

This project is the **C# rewrite of `telegram_media_grabber`** (a Python
Telegram media downloader/uploader with audiobook tagging). It does not
have working code yet. Before writing any code, read, in order:

1. `PROJECT_STATE.md` — what the existing Python app actually does, and
   the exact algorithms/invariants that must be preserved.
2. `CSHARP_PORT_GUIDE.md` — the architecture, service boundaries, and
   design decisions for *this* rewrite, including the specific bugs the
   Python version hit in production and the type-level fixes chosen to
   prevent them from recurring here.

Nothing in this file overrides those two documents where they're more
specific — this file is the general engineering contract; they're the
project-specific design.

---

## 1. Code Standards

1. **Nullable reference types are mandatory.** `<Nullable>enable</Nullable>`
   project-wide, no exceptions. The null-forgiving operator (`!`) requires
   an inline comment explaining why the compiler is provably wrong.
   `dynamic` is forbidden.
2. **XML doc comments are mandatory** on every public type and member
   (`///` summary at minimum; `<param>`/`<returns>`/`<exception>` where
   non-obvious). A doc comment states *why*/*contract*, not a restatement
   of the member name.
3. **One class per file, one responsibility per class.** No god classes.
   If a class is doing filename parsing *and* tagging *and* file I/O, it's
   three classes. Business logic (`Domain`, `Application`) MUST NOT
   reference `Cli` or any UI library — see `CSHARP_PORT_GUIDE.md` §3 for
   the exact layering (`Domain` → `Application` → `Infrastructure` →
   `Cli`; dependencies point one direction only).
4. **No project/class should grow past ~400 lines without being split.**
   The Python predecessor's `audiobook_processor.py` grew to 429 lines by
   accretion (see `PROJECT_STATE.md` §10) — that is the failure mode to
   avoid here, by splitting along the seams `CSHARP_PORT_GUIDE.md` §3
   already lays out, not by discovering new seams under deadline pressure.
5. **Dependency injection only — no service locator.** Every class takes
   its dependencies via constructor injection, resolved from interfaces
   defined in `Application` (or `Domain`), never by reaching into
   `IServiceProvider` outside `Program.cs`'s composition root.
6. **Every public API needs tests.** No PR/commit is "done" without xUnit
   tests for new behavior. See §5 below for the testing philosophy.
7. **NuGet dependencies are explicit and current**, referenced directly by
   the project that uses them (no relying on transitive references).

## 2. Domain Modeling Rules (project-specific, non-negotiable)

These exist because the Python predecessor got them wrong once, in
production, against a real user's media library — see `PROJECT_STATE.md`
§10 for the incident. Do not regress them.

1. **A message/source identifier (chat ID, message ID) MUST NEVER be
   usable as a chapter, episode, or volume number**, structurally — not
   just "don't do this," but the type system must make it impossible.
   See `CSHARP_PORT_GUIDE.md` §4 for `ChapterNumber`/`MessageReference` as
   separate types with no implicit or explicit conversion between them.
2. **A "volume" (a whole bundled book) and a "chapter" (one unit) are
   different `ContentUnitKind`s with separate number spaces.** A volume
   numbered 1 and a chapter numbered 1 are unrelated and MUST be able to
   coexist without collision, ambiguity, or one silently overwriting the
   other's metadata.
3. **Filename parsing is an ordered chain of small, independently
   testable strategies** (`IFilenameParser` implementations), never one
   growing function with sequential fallback branches. Adding support for
   a new real-world filename shape means adding a new class to the chain,
   not editing an existing one.
4. **Every parse result records which strategy matched and why**
   (`ParseResult.MatchedBy`, `Confidence`). "No match" is a valid, explicit
   result — never silently guess a number where a filename provides none;
   fall back to explicit inference (documented as such) or fail loudly.
5. **User-provided config-driven overrides win over parsed results, which
   win over inferred results.** See `CSHARP_PORT_GUIDE.md` §2 for the
   overrides feature design — implement this precedence exactly.

## 3. File Management Rules

1. **Atomic writes only.** Any file written to disk MUST be written to a
   temporary path in the same directory and then moved into place with an
   atomic rename (`File.Move` with a unique temp name, on the same
   volume). Never write directly to the final filename.
2. **No partial files survive a crash.** An interrupted write leaves a
   `.tmp`/`.part` file for resume or deletion — it is never renamed to the
   final name unless byte-complete.
3. **Filename sanitization is centralized in one place.** Every filename
   derived from remote/uploader-controlled data (Telegram captions,
   document filenames) MUST pass through one sanitizer before touching the
   filesystem: strip characters illegal on Windows even when running on
   Linux (portability), reject reserved device names, truncate safely,
   and reject path traversal (no `..`, no absolute paths, no drive
   letters from remote input).
4. **Deduplication must never lose data.** On a filename collision between
   two distinct logical items, suffix the new file (`name (1).ext`) —
   never overwrite. This single rule is why the Python predecessor's
   numbering bugs (§2 above) only ever produced *mislabeled* files, never
   *lost* ones — preserve it exactly.
5. **State and files must stay consistent.** A file is only recorded as
   "downloaded"/"processed" in persistent state after the atomic write/
   move that produced its final location has succeeded — never before.

## 4. Logging vs. UI Constraints

1. **`Console.Write`/`Console.WriteLine` are banned outside the `Cli`
   project.** All user-facing terminal output goes through the UI layer
   (Spectre.Console), reached via an injected reporter interface — see
   `CSHARP_PORT_GUIDE.md` §6.
2. **Backend logs never touch the terminal directly.**
   `Microsoft.Extensions.Logging` configured to a rotating file provider
   only (e.g. Serilog rolling file sink). No console logging provider
   registered — it would corrupt Spectre.Console's live displays.
3. **If a log message needs to be user-visible, it's surfaced
   deliberately through the reporter interface**, not as a side effect of
   a logging call.
4. **Use typed/categorized loggers** (`ILogger<T>`), never a shared
   untyped logger, so log origin is always traceable.

## 5. Concurrency Rules

1. **All I/O-bound Telegram/network work is `async`/`await`**, built on
   whatever MTProto client library is chosen (see `PROJECT_STATE.md` §6).
   Blocking calls MUST NOT run on the async path without `Task.Run`
   isolation when they risk blocking a thread-pool thread for non-trivial
   duration.
2. **Every concurrent operation is explicitly bounded** —
   `SemaphoreSlim` for download/upload concurrency, mirroring the Python
   predecessor's `asyncio.Semaphore` bound. Unbounded fan-out over an
   arbitrary message/file count is forbidden.
3. **State writes are serialized through a single writer**, not concurrent
   connections/threads racing SQLite. See `CSHARP_PORT_GUIDE.md` §5 for
   the recommended `Channel<T>`-based writer queue (or `SemaphoreSlim(1,1)`
   as the simpler fallback) — pick one, be consistent, document which.
4. **`CancellationToken` is threaded through every async call chain** and
   honored — graceful shutdown must finish or safely abort the current
   atomic write (per §3) before the process exits, never leave a
   half-renamed file or half-committed transaction.
5. **FloodWait/rate-limit errors are handled at the point of the call**,
   sleeping for exactly the duration the server specifies plus a small
   fixed safety buffer — never a growing/exponential multiple for this
   specific error type, and never retried in a tight loop. See
   `PROJECT_STATE.md` §5 for the exact shape to replicate.

## 6. Testing Standards

1. **xUnit**, hand-written fakes for external boundaries (Telegram client,
   filesystem where practical) over a mocking framework as the default —
   see `CSHARP_PORT_GUIDE.md` §8 for why (kept the Python test suite fast
   and free of mock-mismatch flakiness; expected to hold here too).
2. **Repository/persistence tests run against a real temp-file SQLite
   database**, not an in-memory substitute — catches real schema/SQL bugs.
3. **The filename-parsing test corpus in `PROJECT_STATE.md` §10 ships as
   the parser chain's test suite from the first commit that adds a
   parser** — don't wait to rediscover those cases against a live library
   a second time.
4. **`dotnet test` and a clean `dotnet build` with
   `TreatWarningsAsErrors=true`** are both required to pass before any
   change is considered done. This is the C# equivalent of the Python
   predecessor's `mypy --strict` gate — treat it with the same weight.

## 7. Mutating Operations

1. **Any operation that bulk-modifies files or state (re-tagging,
   reconciliation/repair, override application) MUST support a dry-run
   mode** that reports exactly what would change without touching disk or
   the database. See `CSHARP_PORT_GUIDE.md` §9 — this is not optional
   polish, it's the direct fix for a real incident where recovering
   mistagged files required manual reconstruction from a terminal
   scrollback.
2. **Config-schema compatibility with the Python version's
   `channels.yaml`/`.env` is a hard constraint** (`CSHARP_PORT_GUIDE.md`
   §7). An unknown config key MUST fail startup loudly, never silently
   no-op — this is what catches user typos in both the original and this
   port.

---

## Summary Checklist (for quick review)

- [ ] Nullable reference types enabled, zero warnings, no `dynamic`
- [ ] XML doc comments on all public members
- [ ] `Domain` → `Application` → `Infrastructure` → `Cli` dependency
      direction respected; no class over ~400 lines
- [ ] Constructor DI throughout; no service locator
- [ ] `MessageReference` and `ChapterNumber` remain structurally
      unconvertible; `Chapter`/`Volume` are separate number spaces
- [ ] Filename parsing is an ordered `IFilenameParser` chain; every
      result records `MatchedBy`/`Confidence`
- [ ] All disk writes atomic; centralized filename sanitization; no
      overwrite on collision
- [ ] No `Console.Write*` outside `Cli`; logs to rotating file only
- [ ] Concurrency explicitly bounded; single-writer state persistence;
      `CancellationToken` honored everywhere
- [ ] FloodWait handled with the exact server-duration-plus-buffer shape
- [ ] xUnit tests for all new public behavior; `dotnet test` +
      `TreatWarningsAsErrors` both clean
- [ ] Bulk-mutating operations support `--dry-run`
- [ ] Config schema stays compatible with the Python version; unknown
      keys fail startup
