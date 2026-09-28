// UIAgentTools - UI structure mapper (prefabs AND scenes).
//
// Output (all under <ProjectRoot>/UIAgentMap):
//   INDEX.md            - overview of every mapped prefab and scene, with red flags
//   index.json          - the same, machine-readable
//   prefabs/<key>.md    - one per UI prefab: full hierarchy + every component's fields
//   scenes/<key>.md     - one per scene: outline of the whole scene, full detail of every
//                         UI root, prefab-instance overrides, and every non-UI script whose
//                         inspector fields point at UI (the scene's UI data flow)
//   prefab-usage.md     - reverse index: for every prefab, which scenes/prefabs instantiate
//                         it and which script fields reference it (runtime-spawned UI)
//
// Mapping is read-only: it never saves or marks anything dirty on disk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UIAgentTools
{
    [Serializable]
    public class MapIndexEntry
    {
        public string kind;            // "prefab" | "scene"
        public string assetPath;
        public string guid;
        public string mapFile;         // relative to UIAgentMap
        public string fileLastWriteUtc;
        public string mappedAtUtc;
        public bool mappedWithUnsavedEditorChanges;
        public int uiRoots;
        public int gameObjects;
        public int missingScripts;
        public int missingReferences;
        public int missingPrefabAssets;
        public bool usesUIToolkit;
        public List<string> outgoing = new List<string>(); // "targetPrefab\tfrom\tvia"
    }

    [Serializable]
    public class MapIndex
    {
        public string generatedAtUtc;
        public string unityVersion;
        public string serializationMode;
        public List<MapIndexEntry> entries = new List<MapIndexEntry>();
    }

    public static class UIMapper
    {
        // ---------------------------------------------------------------- targets

        /// <summary>UI prefabs: .prefab files (not model prefabs) inside the configured folders that contain a Canvas, a UIDocument, or have a RectTransform root.</summary>
        public static List<string> FindUIPrefabPaths(UIAgentConfig cfg)
        {
            var result = new List<string>();
            var uiDoc = TypeLookup.UIDocument;
            var guids = AssetDatabase.FindAssets("t:Prefab", ExistingFolders(cfg));
            int n = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (!cfg.IsIncluded(path)) continue;
                if (result.Contains(path)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null) continue;

                bool isUi = asset.GetComponent<RectTransform>() != null
                            || asset.GetComponentInChildren<Canvas>(true) != null
                            || asset.GetComponentInChildren<RectTransform>(true) != null;
                if (!isUi && uiDoc != null) isUi = asset.GetComponentInChildren(uiDoc, true) != null;
                if (isUi) result.Add(path);

                if (++n % 250 == 0) EditorUtility.UnloadUnusedAssetsImmediate();
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public static List<string> FindScenePaths(UIAgentConfig cfg)
        {
            var result = new List<string>();
            if (!cfg.includeScenes) return result;
            if (cfg.onlyBuildScenes)
            {
                foreach (var s in EditorBuildSettings.scenes)
                    if (s != null && s.enabled && !string.IsNullOrEmpty(s.path) && cfg.IsIncluded(s.path) && File.Exists(UIAgentPaths.Absolute(s.path)))
                        result.Add(s.path);
                return result;
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", ExistingFolders(cfg)))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                if (!cfg.IsIncluded(path) || result.Contains(path)) continue;
                result.Add(path);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>All targets (UI prefabs + scenes) in a stable order.</summary>
        public static List<string> FindAllTargets(UIAgentConfig cfg)
        {
            var all = FindUIPrefabPaths(cfg);
            all.AddRange(FindScenePaths(cfg));
            return all;
        }

        private static string[] ExistingFolders(UIAgentConfig cfg)
        {
            var list = new List<string>();
            foreach (var f in cfg.searchFolders)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string folder = f.Replace('\\', '/').TrimEnd('/');
                if (AssetDatabase.IsValidFolder(folder)) list.Add(folder);
            }
            if (list.Count == 0) list.Add("Assets");
            return list.ToArray();
        }

        // ---------------------------------------------------------------- entry points

        public class MapSummary
        {
            public int Prefabs;
            public int Scenes;
            public int MissingScripts;
            public int MissingReferences;
            public int MissingPrefabAssets;
            public List<string> Failures = new List<string>();

            public override string ToString()
            {
                return "Mapped " + Prefabs + " UI prefab(s) and " + Scenes + " scene(s). Flags: " + MissingScripts + " missing script(s), " +
                       MissingReferences + " missing reference(s), " + MissingPrefabAssets + " missing prefab asset(s)." +
                       (Failures.Count > 0 ? " " + Failures.Count + " asset(s) failed to map: " + string.Join("; ", Failures.ToArray()) : "");
            }
        }

        /// <summary>Map every UI prefab and scene. Replaces all previously generated map files.</summary>
        public static MapSummary MapAll()
        {
            var cfg = UIAgentConfig.Load();
            var summary = new MapSummary();
            PrepareOutput(true);

            var targets = FindAllTargets(cfg);
            var index = NewIndex();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Progress("Mapping UI", targets[i], i, targets.Count);
                    var entry = MapOne(targets[i], cfg, summary);
                    if (entry != null) index.entries.Add(entry);
                    if (i % 50 == 49) EditorUtility.UnloadUnusedAssetsImmediate();
                }
            }
            finally
            {
                ClearProgress();
            }

            WriteIndexAndUsage(index);
            return summary;
        }

        /// <summary>Map a single prefab or scene and update the index/usage files in place.</summary>
        public static MapSummary MapTarget(string assetPath)
        {
            var cfg = UIAgentConfig.Load();
            var summary = new MapSummary();
            PrepareOutput(false);
            assetPath = NormalizeAssetPath(assetPath);

            var index = LoadIndex() ?? NewIndex();
            index.entries.RemoveAll(e => string.Equals(e.assetPath, assetPath, StringComparison.OrdinalIgnoreCase));

            if (File.Exists(UIAgentPaths.Absolute(assetPath)))
            {
                var entry = MapOne(assetPath, cfg, summary);
                if (entry != null) index.entries.Add(entry);
            }
            else
            {
                // asset deleted: drop its map file
                DeleteMapFilesFor(assetPath);
            }
            index.generatedAtUtc = DateTime.UtcNow.ToString("o");
            WriteIndexAndUsage(index);
            return summary;
        }

        public static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) throw new ArgumentException("No asset path given.");
            string p = assetPath.Replace('\\', '/').Trim();
            string root = UIAgentPaths.ProjectRoot + "/";
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p = p.Substring(root.Length);
            if (p.StartsWith("./")) p = p.Substring(2);
            return p;
        }

        private static MapIndexEntry MapOne(string path, UIAgentConfig cfg, MapSummary summary)
        {
            try
            {
                MapIndexEntry entry;
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    entry = MapScene(path, cfg);
                    summary.Scenes++;
                }
                else if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    entry = MapPrefab(path, cfg);
                    summary.Prefabs++;
                }
                else
                {
                    throw new ArgumentException("Only .prefab and .unity assets can be mapped.");
                }
                summary.MissingScripts += entry.missingScripts;
                summary.MissingReferences += entry.missingReferences;
                summary.MissingPrefabAssets += entry.missingPrefabAssets;
                return entry;
            }
            catch (Exception e)
            {
                summary.Failures.Add(path + ": " + e.Message);
                Debug.LogError("[UIAgentTools] Failed to map '" + path + "': " + e);
                return null;
            }
        }

        // ---------------------------------------------------------------- prefab

        private static MapIndexEntry MapPrefab(string path, UIAgentConfig cfg)
        {
            var entry = NewEntry("prefab", path);
            var walk = new WalkState(cfg, path, entry);
            var sb = new StringBuilder();
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            sb.AppendLine("# Prefab: " + (asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)));
            sb.AppendLine();
            sb.AppendLine("- Asset: `" + path + "`");
            sb.AppendLine("- GUID: `" + entry.guid + "`");
            if (asset != null)
            {
                var type = PrefabUtility.GetPrefabAssetType(asset);
                sb.AppendLine("- Prefab type: " + type);
                if (type == PrefabAssetType.Variant)
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(asset);
                    if (source != null) sb.AppendLine("- Variant of: `" + AssetDatabase.GetAssetPath(source) + "` (edits here only override the base)");
                }
            }
            sb.AppendLine("- Edit with: `UIAgentEdit.EditPrefab(\"" + path + "\", root => { ... })` - addresses below start with the prefab root name.");
            sb.AppendLine("- Used by: see `prefab-usage.md` before changing anything that should not affect every user of this prefab.");
            sb.AppendLine();
            sb.AppendLine("## Hierarchy (full detail)");
            sb.AppendLine();

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                walk.WalkDetailed(root.transform, sb, 0);
                entry.uiRoots = 1;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            if (entry.usesUIToolkit) AppendUIToolkitNote(sb);
            AppendFlagsFooter(sb, entry);

            WriteMapFile(entry, UIAgentPaths.PrefabMapDir, "prefabs", path, sb);
            return entry;
        }

        // ---------------------------------------------------------------- scene

        private static MapIndexEntry MapScene(string path, UIAgentConfig cfg)
        {
            var entry = NewEntry("scene", path);
            var walk = new WalkState(cfg, path, entry);
            var sb = new StringBuilder();

            using (var handle = SceneAccess.Open(path, false))
            {
                var scene = handle.Scene;
                entry.mappedWithUnsavedEditorChanges = handle.WasDirty;
                var roots = scene.GetRootGameObjects();

                sb.AppendLine("# Scene: " + scene.name);
                sb.AppendLine();
                sb.AppendLine("- Asset: `" + path + "`");
                sb.AppendLine("- GUID: `" + entry.guid + "`");
                sb.AppendLine("- In Build Settings: " + (InBuildSettings(path) ? "yes" : "no"));
                if (handle.WasDirty)
                    sb.AppendLine("- WARNING: mapped from the open Editor scene WITH UNSAVED CHANGES - the file on disk differs. Save the scene and re-map before editing.");
                sb.AppendLine("- Edit with: `UIAgentEdit.EditScene(\"" + path + "\", scene => { ... })` - addresses below start with the scene root object name.");
                sb.AppendLine("- Prefab instances: edit the prefab asset (see prefabs/) to change every copy; edit here to create a scene-only override.");
                sb.AppendLine();

                // 1. Outline of everything
                sb.AppendLine("## Scene outline (names only)");
                sb.AppendLine();
                sb.AppendLine("Markers: [UI root] full detail below, [Canvas:mode], [prefab: path], (inactive), [MISSING SCRIPT], [MISSING PREFAB].");
                sb.AppendLine();
                int outlined = 0;
                bool truncated = false;
                foreach (var r in roots) walk.WalkOutline(r.transform, sb, 0, ref outlined, ref truncated);
                if (truncated) sb.AppendLine("- ... outline truncated at " + cfg.maxOutlineObjects + " objects (raise maxOutlineObjects in config.json)");
                sb.AppendLine();

                // 2. UI roots in full detail
                var uiRoots = FindSceneUIRoots(roots);
                entry.uiRoots = uiRoots.Count;
                sb.AppendLine("## UI roots (full detail) - " + uiRoots.Count + " found");
                sb.AppendLine();
                if (uiRoots.Count == 0) sb.AppendLine("_No UI in this scene (no RectTransform or UIDocument hierarchies)._");
                foreach (var uiRoot in uiRoots)
                {
                    sb.AppendLine("### `" + UIAddress.Of(uiRoot) + "`");
                    sb.AppendLine();
                    walk.WalkDetailed(uiRoot, sb, 0);
                    sb.AppendLine();
                }

                // 3. Scripts outside the UI that are wired to UI (data flow)
                sb.AppendLine("## Scripts outside UI roots that reference UI (inspector-wired data flow)");
                sb.AppendLine();
                int connected = walk.AppendUIConnectedScripts(roots, uiRoots, sb);
                if (connected == 0) sb.AppendLine("_None found._");
                sb.AppendLine();

                // 4. Event systems / input modules
                sb.AppendLine("## Event system and input modules");
                sb.AppendLine();
                AppendEventSystems(roots, sb);
                sb.AppendLine();

                // Missing-script / missing-prefab counts for objects outside UI roots were gathered by the outline walk.
            }

            if (entry.usesUIToolkit) AppendUIToolkitNote(sb);
            AppendFlagsFooter(sb, entry);
            WriteMapFile(entry, UIAgentPaths.SceneMapDir, "scenes", path, sb);
            return entry;
        }

        private static List<Transform> FindSceneUIRoots(GameObject[] roots)
        {
            var result = new List<Transform>();
            var uiDoc = TypeLookup.UIDocument;
            foreach (var r in roots)
            {
                foreach (var t in r.GetComponentsInChildren<Transform>(true))
                {
                    bool isRectRoot = t is RectTransform && (t.parent == null || !(t.parent is RectTransform));
                    bool isDocHost = uiDoc != null && t.GetComponent(uiDoc) != null && !HasRectAncestor(t);
                    if (isRectRoot || isDocHost)
                    {
                        if (!result.Contains(t)) result.Add(t);
                    }
                }
            }
            return result;
        }

        private static bool HasRectAncestor(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent) if (p is RectTransform) return true;
            return false;
        }

        private static void AppendEventSystems(GameObject[] roots, StringBuilder sb)
        {
            var esType = TypeLookup.EventSystem;
            if (esType == null)
            {
                sb.AppendLine("_uGUI EventSystems not present in this project._");
                return;
            }
            int found = 0;
            foreach (var r in roots)
            {
                foreach (var es in r.GetComponentsInChildren(esType, true))
                {
                    found++;
                    var names = es.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).ToArray();
                    sb.AppendLine("- `" + UIAddress.Of(es.transform) + "`" + (es.gameObject.activeInHierarchy ? "" : " (inactive)") + " - components: " + string.Join(", ", names));
                }
            }
            if (found == 0) sb.AppendLine("_No EventSystem in this scene (it may come from another additively loaded scene)._");
        }

        private static bool InBuildSettings(string path)
        {
            foreach (var s in EditorBuildSettings.scenes)
                if (s != null && string.Equals(s.path, path, StringComparison.OrdinalIgnoreCase)) return s.enabled;
            return false;
        }

        // ---------------------------------------------------------------- walk

        private class WalkState
        {
            private readonly UIAgentConfig _cfg;
            private readonly MapIndexEntry _entry;
            private readonly HashSet<string> _outgoing = new HashSet<string>();

            public WalkState(UIAgentConfig cfg, string assetPath, MapIndexEntry entry)
            {
                _cfg = cfg;
                _entry = entry;
            }

            private DumpContext NewContext()
            {
                var ctx = DumpContext.FromConfig(_cfg);
                ctx.OnReference = RecordReference;
                return ctx;
            }

            private void RecordReference(UnityEngine.Object owner, string propertyPath, UnityEngine.Object referenced)
            {
                GameObject go = referenced as GameObject;
                var comp = referenced as Component;
                if (comp != null) go = comp.gameObject;
                if (go == null || !EditorUtility.IsPersistent(go)) return;
                string prefabPath = AssetDatabase.GetAssetPath(go);
                if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return;
                var ownerComp = owner as Component;
                string from = ownerComp != null ? UIAddress.Of(ownerComp.transform) : "?";
                string via = (ownerComp != null ? ownerComp.GetType().Name : "?") + "." + propertyPath;
                AddOutgoing(prefabPath, from, "field " + via);
            }

            private void AddOutgoing(string prefabPath, string from, string via)
            {
                string key = prefabPath + "\t" + from + "\t" + via;
                if (_outgoing.Add(key)) _entry.outgoing.Add(key);
            }

            public void WalkOutline(Transform t, StringBuilder sb, int depth, ref int count, ref bool truncated)
            {
                if (truncated) { CountFlagsOnly(t); return; }
                if (count >= _cfg.maxOutlineObjects)
                {
                    truncated = true;
                    CountFlagsOnly(t);
                    return;
                }
                count++;
                var go = t.gameObject;
                var line = new StringBuilder();
                line.Append(new string(' ', depth * 2)).Append("- ").Append(UIAddress.Segment(t));
                if (t is RectTransform && (t.parent == null || !(t.parent is RectTransform))) line.Append(" [UI root]");
                var canvas = go.GetComponent<Canvas>();
                if (canvas != null) line.Append(" [Canvas:").Append(canvas.renderMode).Append("]");
                AppendPrefabMarker(go, line);
                if (!go.activeSelf) line.Append(" (inactive)");
                int missing = CountMissingScripts(go);
                if (missing > 0) line.Append(" [MISSING SCRIPT x").Append(missing).Append("]");
                sb.AppendLine(line.ToString());

                // Missing scripts outside UI roots are counted here; inside UI roots the detailed walk counts them.
                if (!IsInsideUI(t)) _entry.missingScripts += missing;
                _entry.gameObjects++;

                for (int i = 0; i < t.childCount; i++) WalkOutline(t.GetChild(i), sb, depth + 1, ref count, ref truncated);
            }

            private void CountFlagsOnly(Transform t)
            {
                _entry.gameObjects++;
                if (!IsInsideUI(t)) _entry.missingScripts += CountMissingScripts(t.gameObject);
                if (PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) RecordPrefabInstance(t.gameObject, false);
                for (int i = 0; i < t.childCount; i++) CountFlagsOnly(t.GetChild(i));
            }

            private static bool IsInsideUI(Transform t)
            {
                for (var cur = t; cur != null; cur = cur.parent) if (cur is RectTransform) return true;
                var uiDoc = TypeLookup.UIDocument;
                if (uiDoc != null)
                    for (var cur = t; cur != null; cur = cur.parent) if (cur.GetComponent(uiDoc) != null) return true;
                return false;
            }

            private void AppendPrefabMarker(GameObject go, StringBuilder line)
            {
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go) && !PrefabUtility.IsAnyPrefabInstanceRoot(go)) return;
                if (PrefabUtility.IsPrefabAssetMissing(go))
                {
                    line.Append(" [MISSING PREFAB]");
                    if (PrefabUtility.IsOutermostPrefabInstanceRoot(go) && !IsInsideUI(go.transform)) _entry.missingPrefabAssets++;
                    return;
                }
                string src = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                line.Append(" [prefab: ").Append(src).Append("]");
                if (PrefabUtility.IsOutermostPrefabInstanceRoot(go)) RecordPrefabInstance(go, false);
            }

            private void RecordPrefabInstance(GameObject go, bool countMissing)
            {
                if (PrefabUtility.IsPrefabAssetMissing(go))
                {
                    if (countMissing) _entry.missingPrefabAssets++;
                    return;
                }
                string src = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(src)) AddOutgoing(src, UIAddress.Of(go.transform), "prefab instance");
            }

            public void WalkDetailed(Transform t, StringBuilder sb, int depth)
            {
                string indent = new string(' ', depth * 2);
                var go = t.gameObject;

                var head = new StringBuilder();
                head.Append(indent).Append("- **").Append(go.name).Append("** `").Append(UIAddress.Of(t)).Append("`");
                if (!go.activeSelf) head.Append(" (inactive)");
                if (go.layer != 0) head.Append(" layer=").Append(LayerMask.LayerToName(go.layer));
                if (!string.IsNullOrEmpty(go.tag) && go.tag != "Untagged") head.Append(" tag=").Append(go.tag);
                sb.AppendLine(head.ToString());

                var rt = t as RectTransform;
                if (rt != null)
                {
                    sb.Append(indent).Append("  - RectTransform: anchorMin ").Append(PropertyDump.Fmt(rt.anchorMin))
                      .Append(" anchorMax ").Append(PropertyDump.Fmt(rt.anchorMax))
                      .Append(" pivot ").Append(PropertyDump.Fmt(rt.pivot))
                      .Append(" anchoredPosition ").Append(PropertyDump.Fmt(rt.anchoredPosition))
                      .Append(" sizeDelta ").Append(PropertyDump.Fmt(rt.sizeDelta))
                      .Append(" -> rect ").Append(PropertyDump.F(rt.rect.width)).Append("x").Append(PropertyDump.F(rt.rect.height));
                    if (rt.localScale != Vector3.one) sb.Append(" scale ").Append(PropertyDump.Fmt(rt.localScale));
                    if (rt.localRotation != Quaternion.identity) sb.Append(" rotation ").Append(PropertyDump.Fmt(rt.localEulerAngles));
                    if (rt.anchoredPosition3D.z != 0f) sb.Append(" z ").Append(PropertyDump.F(rt.anchoredPosition3D.z));
                    sb.AppendLine();
                }
                else
                {
                    sb.Append(indent).Append("  - Transform: position ").Append(PropertyDump.Fmt(t.localPosition))
                      .Append(" rotation ").Append(PropertyDump.Fmt(t.localEulerAngles))
                      .Append(" scale ").Append(PropertyDump.Fmt(t.localScale)).AppendLine();
                }

                if (PrefabUtility.IsAnyPrefabInstanceRoot(go)) AppendPrefabInstanceInfo(go, sb, indent + "  ");

                var ctx = NewContext();
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null)
                    {
                        sb.Append(indent).AppendLine("  - WARNING: MISSING SCRIPT (component whose script was deleted, renamed or failed to compile)");
                        _entry.missingScripts++;
                        continue;
                    }
                    if (comp is Transform) continue;

                    var line = new StringBuilder();
                    line.Append(indent).Append("  - `").Append(comp.GetType().Name).Append("`");
                    var beh = comp as Behaviour;
                    if (beh != null && !beh.enabled) line.Append(" (disabled)");
                    var mb = comp as MonoBehaviour;
                    if (mb != null)
                    {
                        var script = MonoScript.FromMonoBehaviour(mb);
                        string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                        if (!string.IsNullOrEmpty(scriptPath)) line.Append(" script: `").Append(scriptPath).Append("`");
                    }
                    var uiDoc = TypeLookup.UIDocument;
                    if (uiDoc != null && uiDoc.IsInstanceOfType(comp)) _entry.usesUIToolkit = true;
                    sb.AppendLine(line.ToString());

                    PropertyDump.DumpObject(comp, sb, indent + "    ", ctx);
                }
                _entry.missingReferences += ctx.BrokenReferences;

                for (int i = 0; i < t.childCount; i++) WalkDetailed(t.GetChild(i), sb, depth + 1);
            }

            private void AppendPrefabInstanceInfo(GameObject go, StringBuilder sb, string indent)
            {
                if (PrefabUtility.IsPrefabAssetMissing(go))
                {
                    sb.Append(indent).AppendLine("- WARNING: MISSING PREFAB ASSET - this is an instance of a prefab that no longer exists");
                    if (PrefabUtility.IsOutermostPrefabInstanceRoot(go)) _entry.missingPrefabAssets++;
                    return;
                }
                string src = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                bool outermost = PrefabUtility.IsOutermostPrefabInstanceRoot(go);
                sb.Append(indent).Append("- Prefab instance of `").Append(src).Append("`").AppendLine(outermost ? "" : " (nested inside another prefab instance)");
                if (!outermost) return;
                RecordPrefabInstance(go, false);

                try
                {
                    var addedComponents = PrefabUtility.GetAddedComponents(go);
                    var removedComponents = PrefabUtility.GetRemovedComponents(go);
                    var addedObjects = PrefabUtility.GetAddedGameObjects(go);
                    var mods = PrefabUtility.GetPropertyModifications(go) ?? new PropertyModification[0];

                    sb.Append(indent).Append("- Overrides on this instance: ").Append(mods.Length).Append(" property change(s), ")
                      .Append(addedComponents.Count).Append(" added component(s), ")
                      .Append(removedComponents.Count).Append(" removed component(s), ")
                      .Append(addedObjects.Count).AppendLine(" added child object(s)");

                    int shown = 0;
                    foreach (var m in mods)
                    {
                        if (m == null) continue;
                        if (shown++ >= 40)
                        {
                            sb.Append(indent).Append("  - ... ").Append(mods.Length - 40).AppendLine(" more property override(s)");
                            break;
                        }
                        string target = m.target != null ? m.target.GetType().Name + " on `" + (m.target is Component ? UIAddress.Of(((Component)m.target).transform) : (m.target is GameObject ? UIAddress.Of(((GameObject)m.target).transform) : m.target.name)) + "` (in prefab)" : "(missing target)";
                        string value = m.objectReference != null ? PropertyDump.Describe(m.objectReference) : PropertyDump.Quote(m.value, 120);
                        sb.Append(indent).Append("  - ").Append(m.propertyPath).Append(" = ").Append(value).Append("  <- ").AppendLine(target);
                    }
                    foreach (var ac in addedComponents)
                        if (ac != null && ac.instanceComponent != null)
                            sb.Append(indent).Append("  - + added component `").Append(ac.instanceComponent.GetType().Name).Append("` on `").Append(UIAddress.Of(ac.instanceComponent.transform)).AppendLine("`");
                    foreach (var rc in removedComponents)
                        if (rc != null && rc.assetComponent != null)
                            sb.Append(indent).Append("  - - removed component `").Append(rc.assetComponent.GetType().Name).AppendLine("`");
                    foreach (var ao in addedObjects)
                        if (ao != null && ao.instanceGameObject != null)
                            sb.Append(indent).Append("  - + added child `").Append(UIAddress.Of(ao.instanceGameObject.transform)).AppendLine("`");
                }
                catch (Exception e)
                {
                    sb.Append(indent).Append("- (could not read overrides: ").Append(e.Message).AppendLine(")");
                }
            }

            public int AppendUIConnectedScripts(GameObject[] roots, List<Transform> uiRoots, StringBuilder sb)
            {
                int count = 0;
                foreach (var r in roots)
                {
                    foreach (var mb in r.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        if (IsUnder(mb.transform, uiRoots)) continue;

                        bool touchesUI = false;
                        var probe = NewContext();
                        probe.OnReference = (owner, propPath, referenced) =>
                        {
                            RecordReference(owner, propPath, referenced);
                            if (ReferencesUI(referenced)) touchesUI = true;
                        };
                        var body = new StringBuilder();
                        PropertyDump.DumpObject(mb, body, "  ", probe);
                        _entry.missingReferences += probe.BrokenReferences;
                        if (!touchesUI) continue;

                        count++;
                        var script = MonoScript.FromMonoBehaviour(mb);
                        string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : "";
                        sb.Append("- `").Append(mb.GetType().Name).Append("` on `").Append(UIAddress.Of(mb.transform)).Append("`");
                        if (!string.IsNullOrEmpty(scriptPath)) sb.Append(" script: `").Append(scriptPath).Append("`");
                        if (!mb.enabled) sb.Append(" (disabled)");
                        sb.AppendLine();
                        sb.Append(body);
                    }
                }
                return count;
            }

            private static bool IsUnder(Transform t, List<Transform> roots)
            {
                for (var cur = t; cur != null; cur = cur.parent) if (roots.Contains(cur)) return true;
                return false;
            }

            private static bool ReferencesUI(UnityEngine.Object referenced)
            {
                GameObject go = referenced as GameObject;
                var comp = referenced as Component;
                if (comp != null) go = comp.gameObject;
                if (go == null) return false;
                if (go.GetComponent<RectTransform>() != null) return true;
                if (EditorUtility.IsPersistent(go) && go.GetComponentInChildren<RectTransform>(true) != null) return true;
                var uiDoc = TypeLookup.UIDocument;
                return uiDoc != null && go.GetComponent(uiDoc) != null;
            }

            private static int CountMissingScripts(GameObject go)
            {
                int n = 0;
                foreach (var c in go.GetComponents<Component>()) if (c == null) n++;
                return n;
            }
        }

        // ---------------------------------------------------------------- output

        private static MapIndexEntry NewEntry(string kind, string path)
        {
            return new MapIndexEntry
            {
                kind = kind,
                assetPath = path,
                guid = AssetDatabase.AssetPathToGUID(path),
                fileLastWriteUtc = File.GetLastWriteTimeUtc(UIAgentPaths.Absolute(path)).ToString("o"),
                mappedAtUtc = DateTime.UtcNow.ToString("o"),
            };
        }

        private static MapIndex NewIndex()
        {
            return new MapIndex
            {
                generatedAtUtc = DateTime.UtcNow.ToString("o"),
                unityVersion = Application.unityVersion,
                serializationMode = EditorSettings.serializationMode.ToString(),
            };
        }

        private static void AppendUIToolkitNote(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("## UI Toolkit note");
            sb.AppendLine("This asset hosts a UI Toolkit `UIDocument`. Its visual structure lives in the referenced `.uxml` / `.uss`");
            sb.AppendLine("files listed in the fields above - those are plain text, read and edit them directly.");
        }

        private static void AppendFlagsFooter(StringBuilder sb, MapIndexEntry e)
        {
            sb.AppendLine();
            sb.AppendLine("## Flags");
            sb.AppendLine("- Missing scripts: " + e.missingScripts);
            sb.AppendLine("- Missing references: " + e.missingReferences);
            sb.AppendLine("- Missing prefab assets: " + e.missingPrefabAssets);
            sb.AppendLine("- Mapped at (UTC): " + e.mappedAtUtc + " from file last written " + e.fileLastWriteUtc);
        }

        private static void WriteMapFile(MapIndexEntry entry, string dir, string relDir, string assetPath, StringBuilder sb)
        {
            UIAgentPaths.EnsureDir(dir);
            string file = UIAgentPaths.FileKey(assetPath) + ".md";
            UIAgentPaths.WriteAtomic(dir + "/" + file, sb.ToString());
            entry.mapFile = relDir + "/" + file;
        }

        private static void DeleteMapFilesFor(string assetPath)
        {
            string key = UIAgentPaths.FileKey(assetPath) + ".md";
            foreach (var dir in new[] { UIAgentPaths.PrefabMapDir, UIAgentPaths.SceneMapDir })
            {
                string f = dir + "/" + key;
                if (File.Exists(f)) File.Delete(f);
            }
        }

        private static void PrepareOutput(bool wipeMaps)
        {
            UIAgentPaths.EnsureDir(UIAgentPaths.MapRoot);
            UIAgentPaths.EnsureDir(UIAgentPaths.PrefabMapDir);
            UIAgentPaths.EnsureDir(UIAgentPaths.SceneMapDir);
            if (!wipeMaps) return;
            foreach (var dir in new[] { UIAgentPaths.PrefabMapDir, UIAgentPaths.SceneMapDir })
                foreach (var f in Directory.GetFiles(dir, "*.md")) File.Delete(f);
        }

        public static MapIndex LoadIndex()
        {
            if (!File.Exists(UIAgentPaths.IndexJsonPath)) return null;
            try { return JsonUtility.FromJson<MapIndex>(File.ReadAllText(UIAgentPaths.IndexJsonPath)); }
            catch { return null; }
        }

        /// <summary>Rewrite index.json, INDEX.md and prefab-usage.md from an index object.</summary>
        public static void RewriteIndex(MapIndex index)
        {
            WriteIndexAndUsage(index);
        }

        private static void WriteIndexAndUsage(MapIndex index)
        {
            index.entries.Sort((a, b) =>
            {
                int k = string.CompareOrdinal(a.kind, b.kind);
                return k != 0 ? k : string.Compare(a.assetPath, b.assetPath, StringComparison.OrdinalIgnoreCase);
            });
            index.unityVersion = Application.unityVersion;
            index.serializationMode = EditorSettings.serializationMode.ToString();
            UIAgentPaths.WriteAtomic(UIAgentPaths.IndexJsonPath, JsonUtility.ToJson(index, true));
            UIAgentPaths.WriteAtomic(UIAgentPaths.IndexMdPath, BuildIndexMarkdown(index));
            UIAgentPaths.WriteAtomic(UIAgentPaths.UsagePath, BuildUsageMarkdown(index));
        }

        private static string BuildIndexMarkdown(MapIndex index)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# UI Agent Map - index");
            sb.AppendLine();
            sb.AppendLine("- Generated (UTC): " + index.generatedAtUtc + " - Unity " + index.unityVersion);
            sb.AppendLine("- Asset serialization: " + index.serializationMode + (index.serializationMode == "ForceText" ? "" : "  **WARNING: set Project Settings > Editor > Asset Serialization to Force Text**"));
            sb.AppendLine("- A map is stale if the asset changed after it was mapped (compare fileLastWriteUtc in index.json). Re-map before editing a stale asset.");
            sb.AppendLine("- Other files: `prefab-usage.md` (who uses which prefab), `validation-report.md`, `screenshots/manifest.md`.");
            sb.AppendLine();
            foreach (var kind in new[] { "scene", "prefab" })
            {
                var list = index.entries.Where(e => e.kind == kind).ToList();
                sb.AppendLine("## " + (kind == "scene" ? "Scenes" : "UI prefabs") + " (" + list.Count + ")");
                sb.AppendLine();
                if (list.Count == 0) { sb.AppendLine("_none_"); sb.AppendLine(); continue; }
                sb.AppendLine("| Asset | Map | UI roots | Objects | Flags |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var e in list)
                {
                    var flags = new List<string>();
                    if (e.missingScripts > 0) flags.Add(e.missingScripts + " missing script");
                    if (e.missingReferences > 0) flags.Add(e.missingReferences + " missing ref");
                    if (e.missingPrefabAssets > 0) flags.Add(e.missingPrefabAssets + " missing prefab");
                    if (e.mappedWithUnsavedEditorChanges) flags.Add("unsaved editor changes");
                    if (e.usesUIToolkit) flags.Add("UI Toolkit");
                    sb.AppendLine("| `" + e.assetPath + "` | [" + e.mapFile + "](" + e.mapFile + ") | " + e.uiRoots + " | " + (e.kind == "scene" ? e.gameObjects.ToString() : "-") + " | " + (flags.Count == 0 ? "ok" : string.Join(", ", flags.ToArray())) + " |");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string BuildUsageMarkdown(MapIndex index)
        {
            var byPrefab = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in index.entries)
            {
                if (e.outgoing == null) continue;
                foreach (var o in e.outgoing)
                {
                    var parts = o.Split('\t');
                    if (parts.Length < 3) continue;
                    List<string> list;
                    if (!byPrefab.TryGetValue(parts[0], out list)) { list = new List<string>(); byPrefab[parts[0]] = list; }
                    list.Add("`" + e.assetPath + "` at `" + parts[1] + "` - " + parts[2]);
                }
            }
            var sb = new StringBuilder();
            sb.AppendLine("# Prefab usage (reverse index)");
            sb.AppendLine();
            sb.AppendLine("For each prefab: every mapped scene/prefab that contains an instance of it, and every inspector field that references it");
            sb.AppendLine("(prefabs assigned to script fields are usually instantiated at runtime). Editing a prefab asset affects ALL of these.");
            sb.AppendLine("Only references found inside mapped assets (UI prefabs + scenes) are listed.");
            sb.AppendLine();
            if (byPrefab.Count == 0) sb.AppendLine("_No prefab references found._");
            foreach (var kv in byPrefab)
            {
                sb.AppendLine("## `" + kv.Key + "`");
                kv.Value.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var line in kv.Value) sb.AppendLine("- " + line);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- progress

        internal static void Progress(string title, string info, int i, int n)
        {
            if (Application.isBatchMode) return;
            EditorUtility.DisplayProgressBar("UI Agent Tools - " + title, info, n <= 0 ? 0f : (float)i / n);
        }

        internal static void ClearProgress()
        {
            if (Application.isBatchMode) return;
            EditorUtility.ClearProgressBar();
        }
    }
}
