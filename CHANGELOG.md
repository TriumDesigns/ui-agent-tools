# Changelog

## 0.3.0
- License: now released under the Apache License 2.0 (`LICENSE.md`), with `license`, `repository`,
  `documentationUrl`, `changelogUrl` and `licensesUrl` in `package.json`.
- Security: `run` (and the Editor request bridge) now only executes public static methods of the
  `AgentEdits` assembly instead of any static method in any loaded assembly. Asset paths given to
  `map`/`validate`/`screenshots` and `UIAgentEdit` must be inside `Assets/` or `Packages/`.
  Breaking: edit scripts outside `Assets/Editor/AgentEdits/` (the `AgentEdits` assembly) can no longer be run.
- `Install or Update Project Files` also adds a marked `@AGENTS.md` import to `CLAUDE.md` and `GEMINI.md`
  so Claude Code and Gemini-based agents pick up the workflow rules (Codex reads `AGENTS.md` directly).
- `selftest`: blank screenshots are a warning (not a failure) in batch mode. This is the known issue from 0.2.2
  and it affects more than the scene capture: on Unity 2022.1 and 6000.5 the selftest's own batch sequence reports
  10 blank images, while a normal `ui-agent screenshots` run renders correctly on both (checked by viewing the PNG).
  Root cause still unknown.
- `selftest` looks up the built-in UI font per Unity version, which removes `LegacyRuntime.ttf` errors on 2022.1.
- Docs rewritten for public use (per-agent setup, security model, tested-with notes); removed the
  internal "host the package" step. Added `.gitattributes`, issue/PR templates, SECURITY/CONTRIBUTING and a
  lint workflow.

## 0.2.3
- Docs: `SETUP.md` and `README.md` now show the git install line using HTTPS instead of SSH
  (`https://github.com/TriumDesigns/ui-agent-tools.git#v0.2.3`) - no per-developer SSH key needed,
  just read access to the repo and git authenticated to GitHub over HTTPS (e.g. via `gh auth login`
  or Git Credential Manager). Verified end-to-end in real Unity 2022.1: resolves over HTTPS to the
  tagged commit and compiles with zero errors.
- `package.json`: corrected author name to "Trium Designs".

## 0.2.2
- Fix: `UIAgentEdit.EditScene` could wrongly refuse to edit a scene in batch mode ("is open in
  the Editor with unsaved changes") when an earlier step of the same run - e.g. editing a prefab
  that the scene holds an instance of - had merely caused Unity to re-sync that instance,
  marking the scene dirty as a harmless side effect rather than a real unsaved edit. In batch
  mode there is no interactive user whose work could be lost, so `SceneAccess.Open` no longer
  applies that guard there; found by running `ui-agent selftest` end-to-end in real Unity
  2022.1 for the first time.
- Fix: `SETUP.md` at the package root had no `.meta` file, so Unity would silently generate one
  with a fresh random GUID on each consumer's first import (the same class of bug 0.2.0 already
  fixed for other files). Added a committed `SETUP.md.meta` so the GUID is stable across installs.
- Known issue (not fixed, low impact): `ui-agent selftest`'s own screenshot check can report its
  scene screenshot as blank in pure batch mode after exercising its full map/edit/validate/reject
  sequence in one continuous Unity process, even though `map`/`validate`/`screenshots`/`run` used
  the normal documented way (one command per Unity invocation) render scene screenshots correctly
  - verified directly against real fixtures. Several attempts to reproduce the blank result outside
  `SelfTest.cs`'s own exact sequence did not reproduce it, so the root cause is still unknown.
  Flagged for follow-up; see SETUP.md's existing note about blank screenshots.

## 0.2.1
- Renamed package to UI Agent Tools (`com.triumdesigns.ui-agent-tools`) to reflect that nothing here
  is Claude-specific: the CLI (`ui-agent.sh/.ps1/.cmd`), the map/validation/screenshot output, and the
  `UIAgentEdit` API are usable by any coding agent (Claude, ChatGPT/Codex, Gemini, Cursor, etc.) or a
  human developer. The instructions file is now `AGENTS.md`, the cross-tool convention several agents
  read the same way Claude Code reads `CLAUDE.md`.

## 0.2.0
- Scenes are covered everywhere: mapping (outline, full UI-root detail, prefab-instance overrides,
  scene scripts wired to UI, event systems), validation, screenshots and editing.
- New `prefab-usage.md` reverse index (scene instances and inspector fields that reference each prefab).
- New transactional editing API `UIAgentEdit` (EditPrefab / EditScene) with validation-gated saves,
  backups and automatic restore; edit scripts compile in their own `AgentEdits` assembly.
- UnityEvent listeners (Button.onClick etc.) are shown and validated (missing methods are errors).
- Validation baseline so pre-existing problems don't block work; only new errors fail.
- Screenshots rewritten: preview scenes (fixes batch-mode crash), CanvasScaler applied correctly per
  resolution, world-space canvases captured at their own aspect, widget prefabs wrapped in a canvas,
  inactive popups captured, blank-capture detection, graphics-device check.
- `ui-agent` wrapper (bash + PowerShell + cmd) that drives an already-open Editor through a request
  bridge, or starts batch mode when Unity is closed; compile-error reporting.
- Self test command that exercises the whole pipeline in any project.
- Fixes from 0.1: `-nographics` screenshots, `-quit` with `-runTests`, model/package prefabs mapped,
  missing .meta files for git installs, UnityEvent dumps.

## 0.1.0
- Initial prefab mapper, validation tests and screenshot harness.
