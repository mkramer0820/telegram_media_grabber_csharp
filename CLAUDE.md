# CLAUDE.md

This project's engineering rules live in **`AGENTS.md`**, not here —
that file is written to be tool-agnostic (Claude Code, Cursor, Copilot,
Codex CLI, etc. all read it) so the rules never drift out of sync across
multiple tool-specific instruction files.

**Read `AGENTS.md` in full and follow it exactly as written, as if its
contents were this file.** Also read `PROJECT_STATE.md` and
`CSHARP_PORT_GUIDE.md`, which `AGENTS.md` itself requires reading first —
they contain the project-specific architecture and design decisions that
the general rules in `AGENTS.md` reference and depend on.

Do not duplicate `AGENTS.md`'s content into this file. If a Claude-specific
workflow note is ever needed that doesn't belong in the tool-agnostic
file, add it below this line — otherwise this file should stay this short.
