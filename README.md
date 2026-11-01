# UI Agent Tools (com.triumdesigns.ui-agent-tools)

Lets an AI coding agent understand and safely change Unity UI in **prefabs and scenes**, in any Unity project:
mobile XR, WebXR, HMD/OpenXR, or non-XR games. Everything here is a plain Unity Editor package, shell/
PowerShell scripts, and Markdown/JSON output, so nothing is tied to one vendor. Claude Code, OpenAI Codex,
Google Antigravity, Gemini CLI, Cursor, Aider, or a human developer can all drive the same
`map → read → write edit script → run → validate → screenshot` workflow. The package has no compile-time
dependency on uGUI, TextMeshPro, UI Toolkit or any XR package (they are detected at runtime). Editing and the
self test target uGUI (`com.unity.ugui`).

Licensed under the [Apache License 2.0](LICENSE.md): free for personal and commercial projects.
Status: **0.x** - used in real projects, but expect rough edges; please
[open an issue](https://github.com/TriumDesigns/ui-agent-tools/issues).

## What it does

| Piece | What your agent gets |
|---|---|
| **Map** (`ui-agent map`) | `UIAgentMap/INDEX.md` + one Markdown file per UI prefab and per scene. Each file has the full hierarchy with an exact address per object, RectTransform layout, every serialized field by property path, UnityEvent listeners (Button → script method), prefab-instance overrides, scene scripts whose inspector fields point at UI, and event systems. `prefab-usage.md` shows who instantiates or references each prefab. |
| **Validate** (`ui-agent validate`) | Errors: missing scripts, missing references, missing prefab assets, UnityEvent listeners calling methods that no longer exist. Warnings: raycaster, EventSystem, target graphic, TMP font, zero-size graphic, serialization mode. A baseline accepts pre-existing errors, so only new errors fail. |
| **Edit** (`UIAgentEdit` + `ui-agent run`) | Transactional edits through Unity's own serialization, never YAML text. Each edit backs up the file, validates before and after, and saves only if no new errors. It then re-loads from disk and restores the backup if verification fails. Prefab links and scene overrides are recorded correctly. |
| **Screenshots** (`ui-agent screenshots`) | PNGs of every prefab and scene canvas at each configured resolution (CanvasScaler applied), plus world-space panels at their own aspect ratio. Inactive popups are included. Blank captures are flagged. |
| **Wrapper** (`ui-agent.sh` / `.cmd`) | One command line for all of the above. Works with Unity **open** (talks to the running Editor) or **closed** (batch mode). |
| **Self test** (`ui-agent selftest`) | Proves the whole pipeline works in *this* project and Unity version, using throwaway assets it deletes afterwards. |

## Requirements
- Unity 2021.3 or newer.
- Recommended: Project Settings > Editor > Asset Serialization = **Force Text**.
- Screenshots need a GPU, so batch runs for `screenshots`/`selftest`/`all` never use `-nographics`.
  On a headless Linux CI machine, install `xvfb`.

## Install
Full guide: [SETUP.md](SETUP.md). In short:
1. Package Manager > **+** > *Add package from git URL…* >
   `https://github.com/TriumDesigns/ui-agent-tools.git#v0.3.0`
   (or add `"com.triumdesigns.ui-agent-tools": "https://github.com/TriumDesigns/ui-agent-tools.git#v0.3.0"` to
   `Packages/manifest.json`, or use a `file:` path).
2. Menu **UI Agent Tools > Install or Update Project Files**.
3. Run `./ui-agent.sh selftest`, then `./ui-agent.sh baseline` (optional), then `./ui-agent.sh map`.
4. Ask your agent for a UI change in plain language. It reads `AGENTS.md` and follows the workflow.

## Using it with your agent
The installer writes the workflow rules to `AGENTS.md` and adds a one-line `@AGENTS.md` import to `CLAUDE.md`
and `GEMINI.md`, so each tool finds the same instructions:

| Agent | How it picks up the rules | What to allow |
|---|---|---|
| **Claude Code** | `CLAUDE.md` imports `AGENTS.md` | Allow the wrapper in your permissions, e.g. `Bash(./ui-agent.sh:*)` (Windows: `.\ui-agent.cmd`) |
| **OpenAI Codex** | reads `AGENTS.md` directly | The wrapper starts Unity, which writes outside the workspace (Unity logs, Hub install path): use a sandbox/approval mode that allows it |
| **Google Antigravity / Gemini CLI** | reads `AGENTS.md`; `GEMINI.md` imports it | Allow the terminal commands for `ui-agent` |
| **Others** (Cursor, Aider, ...) | point them at `AGENTS.md` | - |

Tools change quickly; if your agent ignores the rules, tell it to read `AGENTS.md` first.

## Files it creates in your project
- `ui-agent.sh`, `ui-agent.ps1`, `ui-agent.cmd`: the wrapper scripts.
- `AGENTS.md`: gets a marked block with the workflow rules. Your own text is kept.
- `CLAUDE.md`, `GEMINI.md`: a marked one-line `@AGENTS.md` import (skipped if the file already imports it).
- `.gitignore`: gets a marked block.
- `Assets/Editor/AgentEdits/AgentEdits.asmdef`: an isolated assembly for your agent's edit scripts.
- `UIAgentMap/`: maps, config, report and baseline (commit these), plus backups, screenshots and logs (git-ignored).

## Known limits
- Screenshots show the authored layout. Content that scripts fill in at runtime (REST data, lists
  built in `Start`) isn't there, and XR interaction still needs an on-device check.
- UI Toolkit: UXML/USS files are plain text, so your agent edits them directly. The map only records how
  each `UIDocument` is hosted.
- Only prefabs and scenes are edited transactionally. ScriptableObject/other assets are out of scope.
- The scene outline truncates after `maxOutlineObjects` (4000 by default). UI roots are never truncated.

## Security model
Edit scripts are ordinary C# that runs inside your Editor, and the request bridge runs whatever the
wrapper sends it, so only use this on projects and with agents you trust. `run` only executes public static
methods in the `AgentEdits` assembly, and asset paths must be inside `Assets/` or `Packages/`.
See [SECURITY.md](.github/SECURITY.md) for reporting vulnerabilities.

## Contributing
See [CONTRIBUTING.md](.github/CONTRIBUTING.md).

## License
Copyright 2026 Trium Designs. Licensed under the [Apache License, Version 2.0](LICENSE.md).
Unity is a trademark of Unity Technologies; Claude, Codex, Antigravity and Gemini are trademarks of their
respective owners. This project is not affiliated with or endorsed by any of them.
