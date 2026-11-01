# UI Agent Tools – setup and usage

## A. Per project (10 min)
1. **Add the package.** In `Packages/manifest.json` under `"dependencies"`, use one of:
   - a) git (recommended): Package Manager > **+** > *Add package from git URL…* >
     `https://github.com/TriumDesigns/ui-agent-tools.git#v0.3.0`, or the same URL as the value of
     `"com.triumdesigns.ui-agent-tools"` in `Packages/manifest.json`. Pin a `#vX.Y.Z` tag.
   - b) local clone: `"com.triumdesigns.ui-agent-tools": "file:../../path/to/ui-agent-tools"`
     (the folder that contains `package.json`; keep the `.meta` files)
2. **Commit your project first** (git is your ultimate safety net).
3. In Unity, open Project Settings > Editor and set **Asset Serialization Mode = Force Text**, if it
   isn't already. Commit that change on its own, because it rewrites asset files.
4. Menu **UI Agent Tools > Install or Update Project Files**. This adds `ui-agent.sh/.ps1/.cmd`, a
   `AGENTS.md` block, `@AGENTS.md` imports in `CLAUDE.md` and `GEMINI.md`, a `.gitignore` block,
   `UIAgentMap/config.json` and `Assets/Editor/AgentEdits/`.
5. Edit `UIAgentMap/config.json`:
   - `resolutions`: the devices you ship to. Phones, foldable cover/unfolded, tablets. For HMD-only
     projects keep one entry; world-space panels are captured at their own size anyway.
   - `excludeFolders`: third-party UI you never want touched, e.g. `["Assets/Plugins", "Assets/Samples"]`.
   - `onlyBuildScenes: true` if test/sandbox scenes should be ignored.
6. Fill in the project-specific notes at the top of `AGENTS.md`: platforms, XR rig, conventions, off-limits folders.
7. From the project root, run:
   ```
   ./ui-agent.sh selftest     # must say "all checks passed"
   ./ui-agent.sh validate     # see what's already broken
   ./ui-agent.sh baseline     # ONLY if you accept those existing errors for now
   ./ui-agent.sh map          # generate the UI map
   ./ui-agent.sh screenshots  # first full screenshot set
   ```
   On Windows use `.\ui-agent.cmd` instead of `./ui-agent.sh`. In Git Bash (the default shell several
   coding agents use on Windows) `./ui-agent.sh` works too.
8. Open 2–3 PNGs from `UIAgentMap/screenshots/` and confirm the scale looks right (see "Verify on first use").
9. Commit `ui-agent.*`, `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `.gitignore`, `Assets/Editor/AgentEdits/`, and
   `UIAgentMap/` (the ignored parts stay out automatically).

## B. Daily use with your AI coding agent
Ask for UI changes in plain language, e.g. *"Make the login title say 'Welcome back', 20% larger,
and hide the Help button in the Main scene only."* Your agent follows `AGENTS.md`:
1. Reads `UIAgentMap/INDEX.md`, the relevant scene/prefab map, and `prefab-usage.md`.
2. Writes an edit script in `Assets/Editor/AgentEdits/` using `UIAgentEdit`.
3. Runs `./ui-agent.sh run AgentEdits.Edit_<date>_<topic>.Run`. The edit is validated, saved, re-mapped and re-validated.
4. Runs `./ui-agent.sh screenshots <asset>`, looks at the PNGs, and reports.

You can keep Unity open the whole time. Just **save your scene/prefab before asking**: it refuses
to touch assets with unsaved Editor changes. Don't be in Play Mode.
Review with `git diff`. The map `.md` diffs show structurally what changed.

## C. Commands
| Command | What it does |
|---|---|
| `map [asset]` | regenerate all maps, or one `.prefab`/`.unity` |
| `validate [asset]` | report in `UIAgentMap/validation-report.md`; fails on new errors |
| `screenshots [asset]` | PNGs + `UIAgentMap/screenshots/manifest.md` |
| `run <Namespace.Class.Method>` | run an edit script, then re-map + validate what it saved |
| `compile` | check that scripts compile |
| `selftest` | end-to-end check in this project |
| `baseline` | accept current errors as known (your decision, not the agent's) |
| `install` | refresh wrapper/AGENTS.md block after updating the package |
| `all` | map + validate + screenshots |
| `tests` | Unity Test Runner version of validate, for CI. Unity must be closed, and the package must be listed under `"testables"` in manifest.json |

`run` only executes `public static void` methods of classes in the `AgentEdits` assembly
(`Assets/Editor/AgentEdits/`).

Environment overrides: `UNITY_EDITOR_PATH` (custom Unity install), `UI_AGENT_MODE=batch|editor`,
`UI_AGENT_TIMEOUT`, `UI_AGENT_PICKUP`.

## D. Updating the package
Bump the `#vX.Y.Z` tag in each `manifest.json` (see [CHANGELOG.md](CHANGELOG.md)), then run menu **UI Agent Tools > Install or Update Project Files**
(or `./ui-agent.sh install`) to refresh the wrapper scripts and the AGENTS.md block.

## Verify on first use (things only your Unity install can confirm)
- `selftest` passes (it needs `com.unity.ugui`). It covers mapping, validation, prefab and scene edits, rollback, addressing
  and screenshots in your Unity version and render pipeline.
- Screenshot scale: a phone-portrait PNG of a known screen should match what you see in the Game
  view at that resolution. If it doesn't, [open an issue](https://github.com/TriumDesigns/ui-agent-tools/issues)
  with your render pipeline, graphics API and Unity version.
- In batch mode `selftest` only *warns* about blank screenshots (known issue, see CHANGELOG 0.2.2). Run
  `./ui-agent.sh screenshots` and open the PNGs. If they really are blank, UI rendering to a texture isn't
  working with that pipeline or graphics API. Everything else still works; only the visual check is unavailable.

## Windows: keep the project path short
Screenshot files are named after the asset path and Unity 2022 on Windows is limited to 260-character paths.
If screenshots fail with "Could not find a part of the path", move the Unity project to a shorter folder
(for example `C:\Projects\MyGame`).

## Tested with
Windows 11, Built-in render pipeline, batch mode: **Unity 2022.1.0f1** (Direct3D 11) and **Unity 6000.5.11f1**
(Direct3D 12) - install, `selftest`, real `screenshots` capture, and the `run`/path restrictions. The open-Editor
request bridge was verified on 2022.1. The package declares Unity 2021.3+; 2021.3 itself, URP/HDRP, macOS and Linux
have not been verified. Reports welcome.

Unity 6 projects: the default template no longer includes uGUI. Add `com.unity.ugui` (Package Manager) before
running `selftest` or editing uGUI.
