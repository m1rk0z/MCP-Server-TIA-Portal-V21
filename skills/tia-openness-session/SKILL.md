---
name: tia-openness-session
description: How to plan and run a TIA Portal V19/V21 working session through the TIA Portal MCP server so the person at TIA Portal confirms Openness access once, the right Portal version is used, and nothing is saved by accident. Use it before any multi-step TIA Portal job - listing panels and screens, exporting, importing, compiling, saving - or when a tia_* tool fails to attach or load.
---

# Planning a TIA Portal session

Every `Attach()` to a running TIA Portal costs a manual "Openness access" confirmation from whoever
is sitting at it, and the dialog expires after about a minute. The server attaches once and keeps
the session; the job of the agent is to not waste that.

## Before starting

1. **Pick the right server.** An Openness client works only with the TIA Portal version it was built
   for: use the V19 server for V19 projects and the V21 server for V21 projects. A `.ap19` will not
   open in V21 without an upgrade, which must never happen silently.
2. `tia_instances` - lists running instances and their projects **without attaching** (no dialog).
3. Decide how to get a session:
   - project not open → `tia_open_project` with `with_ui: false`. Headless needs no confirmation and
     is faster (roughly 30-60 s for a large project). Preferred for read-only and batch work.
   - project already open and being edited by a person → `tia_attach`, and **tell the user first**
     that a confirmation dialog is coming.
4. If something does not load, `tia_session` shows which Openness assemblies were found and where.

## During the session

- Write down the whole plan first (what to read, export, import, compile) and run it in one session.
  Never detach and re-attach between steps.
- Read before writing: `hmi_panels` first (classic and Unified behave differently), then the read
  tools. On WinCC classic use `details: true` on `hmi_screens`/`hmi_tags`, narrowed with `names`.
- Every tool marked *writes* needs an explicit OK from the user in chat for that operation.
- `tia_compile` after changes, and report **errors and warnings** with their path.
- `tia_save` only when the user agrees with the compile result. Nothing saves implicitly.
- Large results: export to a folder and analyse the files instead of pulling everything into the
  conversation.

## Ending

- `tia_detach`. A project opened headless closes with it; an attached TIA Portal stays open.
- If the MCP client started while the TIA machine or VM was off, the server will have timed out and
  will not reconnect by itself: restart the client after the machine is up.

## Safety

- PLC tools are read-only on purpose. Do not work around that with other scripts.
- Never download to a PLC or panel: that is done by a person from TIA Portal.
- Before any import into an HMI, use the `tia-hmi-safe-import` skill.
