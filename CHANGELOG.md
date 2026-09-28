# Changelog

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
