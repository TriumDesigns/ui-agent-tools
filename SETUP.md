# UI Agent Tools v0.2.2 – setup and usage

## A. One-time: host the package (5 min)
1. Unzip and put the `com.triumdesigns.ui-agent-tools/` folder in its own git repo (e.g. `triumdesigns/ui-agent-tools`).
   Keep the `.meta` files. They are required for git installs.
2. Tag it: `git tag v0.2.1 && git push --tags`.

(Just trying it on one project? Skip this and use step B1 option b.)

## B. Per project (10 min)
1. **Add the package.** In `Packages/manifest.json` under `"dependencies"`, use one of:
   - a) git (recommended): `"com.triumdesigns.ui-agent-tools": "https://github.com/TriumDesigns/ui-agent-tools.git#v0.2.2"`
   - b) local folder: `"com.triumdesigns.ui-agent-tools": "file:../../path/to/com.triumdesigns.ui-agent-tools"`
2. **Commit your project first** (git is your ultimate safety net).
3. In Unity, open Project Settings > Editor and set **Asset Serialization Mode = Force Text**, if it
   isn't already. Commit that change on its own, because it rewrites asset files.
4. Menu **UI Agent Tools > Install or Update Project Files**. This adds `ui-agent.sh/.ps1/.cmd`, a
   `AGENTS.md` block, a `.gitignore` block, `UIAgentMap/config.json` and `Assets/Editor/AgentEdits/`.
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
9. Commit `ui-agent.*`, `AGENTS.md`, `.gitignore`, `Assets/Editor/AgentEdits/`, and
   `UIAgentMap/` (the ignored parts stay out automatically).

## C. Daily use with your AI coding agent
Ask for UI changes in plain language, e.g. *"Make the login title say 'Welcome back', 20% larger,
and hide the Help button in the Main scene only."* Your agent follows `AGENTS.md`:
1. Reads `UIAgentMap/INDEX.md`, the relevant scene/prefab map, and `prefab-usage.md`.
2. Writes an edit script in `Assets/Editor/AgentEdits/` using `UIAgentEdit`.
3. Runs `./ui-agent.sh run AgentEdits.Edit_<date>_<topic>.Run`. The edit is validated, saved, re-mapped and re-validated.
4. Runs `./ui-agent.sh screenshots <asset>`, looks at the PNGs, and reports.

You can keep Unity open the whole time. Just **save your scene/prefab before asking**: it refuses
to touch assets with unsaved Editor changes. Don't be in Play Mode.
Review with `git diff`. The map `.md` diffs show structurally what changed.

## D. Commands
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

Environment overrides: `UNITY_EDITOR_PATH` (custom Unity install), `UI_AGENT_MODE=batch|editor`,
`UI_AGENT_TIMEOUT`, `UI_AGENT_PICKUP`.

## E. Updating the package
Bump the git tag in each `manifest.json`, then run menu **UI Agent Tools > Install or Update Project Files**
(or `./ui-agent.sh install`) to refresh the wrapper scripts and the AGENTS.md block.

## Verify on first use (things only your Unity install can confirm)
- `selftest` passes. It covers mapping, validation, prefab and scene edits, rollback, addressing
  and screenshots in your Unity version and render pipeline.
- Screenshot scale: a phone-portrait PNG of a known screen should match what you see in the Game
  view at that resolution. If it doesn't, tell me which render pipeline and Unity version you're on.
- If `selftest` reports BLANK screenshots, rendering UI to a texture isn't working with that pipeline
  or graphics API. Everything else still works; only the visual check is unavailable.
