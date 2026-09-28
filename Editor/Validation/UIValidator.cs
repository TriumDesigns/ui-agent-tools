// UIAgentTools - validation of prefabs and scenes.
//
// Errors (these fail validation and make UIAgentEdit roll an edit back):
//   missing-script         component whose script can't be found
//   missing-reference      object field pointing at something that no longer exists
//   missing-prefab-asset   scene/prefab instance of a prefab that was deleted
//   listener-method-missing  UnityEvent listener whose method doesn't exist on its target
// Warnings (reported, never fail):
//   selectable-no-target-graphic, canvas-no-raycaster, scene-no-eventsystem,
//   tmp-no-font, zero-size-graphic, serialization-not-text
//
// Pre-existing problems can be accepted into validation-baseline.json
// (ui-agent baseline) so that only NEW errors fail. The agent must never
// update the baseline without the user's explicit OK.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UIAgentTools
{
    [Serializable]
    public class ValidationIssue
    {
        public string severity; // "error" | "warning"
        public string check;
        public string asset;
        public string address;
        public string message;

        public string Key { get { return asset + "|" + address + "|" + check; } }
        public bool IsError { get { return severity == "error"; } }
    }

    [Serializable]
    public class ValidationBaseline
    {
        public string createdAtUtc;
        public List<string> acceptedErrorKeys = new List<string>();
    }

    [Serializable]
    public class ValidationReport
    {
        public string generatedAtUtc;
        public int targetsChecked;
        public int newErrors;
        public int baselinedErrors;
        public int warnings;
        public List<ValidationIssue> issues = new List<ValidationIssue>();
        public List<string> newErrorKeys = new List<string>();
        public List<string> failures = new List<string>();
    }

    public static class UIValidator
    {
        // ---------------------------------------------------------------- per asset

        public static List<ValidationIssue> ValidateAsset(string assetPath)
        {
            assetPath = UIMapper.NormalizeAssetPath(assetPath);
            if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                var root = PrefabUtility.LoadPrefabContents(assetPath);
                try
                {
                    return ValidateRoots(assetPath, new[] { root }, false);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                using (var h = SceneAccess.Open(assetPath, false))
                    return ValidateRoots(assetPath, h.Scene.GetRootGameObjects(), true);
            }
            throw new ArgumentException("Only .prefab and .unity assets can be validated: " + assetPath);
        }

        /// <summary>Validate in-memory hierarchies (used by UIAgentEdit before saving).</summary>
        public static List<ValidationIssue> ValidateRoots(string assetLabel, IList<GameObject> roots, bool isScene)
        {
            var issues = new List<ValidationIssue>();
            var selectableType = TypeLookup.Selectable;
            var raycasterType = TypeLookup.BaseRaycaster;
            var tmpType = TypeLookup.TmpText;
            var graphicType = TypeLookup.Graphic;
            bool sceneHasInteractiveCanvas = false;
            bool sceneHasEventSystem = false;

            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    string address = UIAddress.Of(t);

                    // missing prefab asset
                    if (PrefabUtility.IsOutermostPrefabInstanceRoot(go) && PrefabUtility.IsPrefabAssetMissing(go))
                        Add(issues, "error", "missing-prefab-asset", assetLabel, address,
                            "Instance of a prefab asset that no longer exists (deleted or its .meta GUID changed).");

                    var comps = go.GetComponents<Component>();
                    int missingIndex = 0;
                    foreach (var c in comps)
                    {
                        if (c == null)
                        {
                            Add(issues, "error", "missing-script", assetLabel, address + "#" + (missingIndex++),
                                "Component with a missing script (deleted/renamed script, or the script has compile errors).");
                            continue;
                        }
                        if (c is Transform) continue;
                        CheckSerializedFields(c, assetLabel, address, issues);
                    }

                    if (TypeLookup.EventSystem != null && go.GetComponent(TypeLookup.EventSystem) != null) sceneHasEventSystem = true;

                    // uGUI checks
                    if (selectableType != null)
                    {
                        foreach (var sel in go.GetComponents(selectableType))
                        {
                            if (sel == null) continue;
                            var so = new SerializedObject(sel);
                            var transition = so.FindProperty("m_Transition");
                            var targetGraphic = so.FindProperty("m_TargetGraphic");
                            // Transition: None=0, ColorTint=1, SpriteSwap=2, Animation=3
                            if (transition != null && targetGraphic != null && (transition.intValue == 1 || transition.intValue == 2) && targetGraphic.objectReferenceValue == null)
                                Add(issues, "warning", "selectable-no-target-graphic", assetLabel, address,
                                    sel.GetType().Name + " uses a " + (transition.intValue == 1 ? "Color Tint" : "Sprite Swap") + " transition but has no Target Graphic, so pressed/disabled states won't show.");
                        }
                    }

                    var canvas = go.GetComponent<Canvas>();
                    if (canvas != null && IsRootCanvas(t) && selectableType != null && go.GetComponentsInChildren(selectableType, true).Length > 0)
                    {
                        sceneHasInteractiveCanvas = true;
                        bool hasRaycaster = raycasterType != null && go.GetComponent(raycasterType) != null;
                        if (!hasRaycaster)
                            Add(issues, "warning", "canvas-no-raycaster", assetLabel, address,
                                "Root canvas contains interactive elements but has no raycaster component (GraphicRaycaster / TrackedDeviceGraphicRaycaster / etc.) - clicks, rays and pokes will pass through it.");
                    }

                    if (tmpType != null)
                    {
                        var tmp = go.GetComponent(tmpType);
                        if (tmp != null)
                        {
                            var so = new SerializedObject(tmp);
                            var font = so.FindProperty("m_fontAsset");
                            if (font != null && font.objectReferenceValue == null && !PropertyDump.IsMissingReference(font))
                                Add(issues, "warning", "tmp-no-font", assetLabel, address,
                                    "TextMeshPro text has no Font Asset; it will fall back to the TMP Settings default font at runtime.");
                        }
                    }

                    if (graphicType != null && go.activeInHierarchy)
                    {
                        var rt = t as RectTransform;
                        var g = go.GetComponent(graphicType) as Behaviour;
                        if (rt != null && g != null && g.enabled && (Mathf.Approximately(rt.rect.width, 0f) || Mathf.Approximately(rt.rect.height, 0f)))
                            Add(issues, "warning", "zero-size-graphic", assetLabel, address,
                                g.GetType().Name + " has a zero-width or zero-height rect, so it renders nothing.");
                    }
                }
            }

            if (isScene && sceneHasInteractiveCanvas && !sceneHasEventSystem && TypeLookup.EventSystem != null)
                Add(issues, "warning", "scene-no-eventsystem", assetLabel, "",
                    "Scene has interactive canvases but no EventSystem (fine only if another loaded scene provides one).");

            return issues;
        }

        private static bool IsRootCanvas(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent) if (p.GetComponent<Canvas>() != null) return false;
            return true;
        }

        private static void CheckSerializedFields(Component c, string asset, string address, List<ValidationIssue> issues)
        {
            SerializedObject so;
            try { so = new SerializedObject(c); }
            catch { return; }

            var it = so.GetIterator();
            bool enter = true;
            int guard = 0;
            while (it.NextVisible(enter))
            {
                if (++guard > 200000) break; // pathological components
                enter = true;

                if (it.propertyType == SerializedPropertyType.String) { enter = false; continue; }

                if (it.propertyType == SerializedPropertyType.ObjectReference)
                {
                    if (PropertyDump.IsMissingReference(it))
                        Add(issues, "error", "missing-reference", asset, address + "::" + c.GetType().Name + "." + it.propertyPath,
                            c.GetType().Name + "." + it.propertyPath + " points at an object that no longer exists.");
                    continue;
                }

                if (it.propertyType == SerializedPropertyType.Generic && !it.isArray)
                {
                    var calls = it.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    if (calls != null && calls.isArray) CheckListeners(c, it.propertyPath, calls, asset, address, issues);
                }
            }
        }

        private static void CheckListeners(Component owner, string eventPath, SerializedProperty calls, string asset, string address, List<ValidationIssue> issues)
        {
            for (int i = 0; i < calls.arraySize; i++)
            {
                var call = calls.GetArrayElementAtIndex(i);
                var target = call.FindPropertyRelative("m_Target");
                var method = call.FindPropertyRelative("m_MethodName");
                if (target == null || method == null) continue;
                var obj = target.objectReferenceValue;
                string where = owner.GetType().Name + "." + eventPath + "[" + i + "]";
                if (obj == null) continue; // missing target is reported as missing-reference; empty target is a "None" slot
                if (string.IsNullOrEmpty(method.stringValue))
                {
                    Add(issues, "warning", "listener-no-method", asset, address + "::" + where, where + " has a target but no method selected.");
                    continue;
                }
                if (!HasMethod(obj.GetType(), method.stringValue))
                    Add(issues, "error", "listener-method-missing", asset, address + "::" + where,
                        where + " calls " + obj.GetType().Name + "." + method.stringValue + "(), which no longer exists (renamed or removed?).");
            }
        }

        private static bool HasMethod(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(flags | BindingFlags.DeclaredOnly))
                    if (m.Name == name) return true;
            }
            return false;
        }

        private static void Add(List<ValidationIssue> list, string severity, string check, string asset, string address, string message)
        {
            list.Add(new ValidationIssue { severity = severity, check = check, asset = asset, address = address, message = message });
        }

        // ---------------------------------------------------------------- comparison used by UIAgentEdit

        /// <summary>Throws if 'after' has more errors of any check type than 'before' (renames can't hide a new error this way).</summary>
        public static void EnsureNoNewErrors(List<ValidationIssue> before, List<ValidationIssue> after, string assetPath)
        {
            var beforeCounts = CountErrorsByCheck(before);
            var afterCounts = CountErrorsByCheck(after);
            var problems = new List<string>();
            foreach (var kv in afterCounts)
            {
                int b;
                beforeCounts.TryGetValue(kv.Key, out b);
                if (kv.Value > b)
                {
                    problems.Add(kv.Key + ": " + b + " -> " + kv.Value);
                    var beforeKeys = new HashSet<string>(before.Where(x => x.IsError && x.check == kv.Key).Select(x => x.Key));
                    foreach (var issue in after.Where(x => x.IsError && x.check == kv.Key && !beforeKeys.Contains(x.Key)).Take(10))
                        problems.Add("   " + issue.address + " - " + issue.message);
                }
            }
            if (problems.Count > 0)
                throw new UIAgentEditException("Edit of '" + assetPath + "' was rejected because it introduces new validation errors (nothing was saved):\n" + string.Join("\n", problems.ToArray()));
        }

        private static Dictionary<string, int> CountErrorsByCheck(List<ValidationIssue> issues)
        {
            var d = new Dictionary<string, int>();
            foreach (var i in issues)
            {
                if (!i.IsError) continue;
                int n;
                d.TryGetValue(i.check, out n);
                d[i.check] = n + 1;
            }
            return d;
        }

        // ---------------------------------------------------------------- project-wide run + report

        public static ValidationReport Run(IList<string> targets)
        {
            var report = new ValidationReport { generatedAtUtc = DateTime.UtcNow.ToString("o") };
            var baseline = LoadBaseline();
            var accepted = new HashSet<string>(baseline.acceptedErrorKeys);

            if (EditorSettings.serializationMode != SerializationMode.ForceText)
                report.issues.Add(new ValidationIssue
                {
                    severity = "warning", check = "serialization-not-text", asset = "ProjectSettings", address = "",
                    message = "Asset Serialization is " + EditorSettings.serializationMode + ". Set Project Settings > Editor > Asset Serialization Mode to Force Text so scene/prefab diffs are reviewable."
                });

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    UIMapper.Progress("Validating", targets[i], i, targets.Count);
                    try
                    {
                        report.issues.AddRange(ValidateAsset(targets[i]));
                        report.targetsChecked++;
                    }
                    catch (Exception e)
                    {
                        report.failures.Add(targets[i] + ": " + e.Message);
                    }
                    if (i % 50 == 49) EditorUtility.UnloadUnusedAssetsImmediate();
                }
            }
            finally
            {
                UIMapper.ClearProgress();
            }

            foreach (var issue in report.issues)
            {
                if (!issue.IsError) { report.warnings++; continue; }
                if (accepted.Contains(issue.Key)) report.baselinedErrors++;
                else { report.newErrors++; report.newErrorKeys.Add(issue.Key); }
            }
            return report;
        }

        public static void WriteReport(ValidationReport report)
        {
            UIAgentPaths.EnsureDir(UIAgentPaths.MapRoot);
            UIAgentPaths.WriteAtomic(UIAgentPaths.ReportJsonPath, JsonUtility.ToJson(report, true));

            var newKeys = new HashSet<string>(report.newErrorKeys);
            var sb = new StringBuilder();
            sb.AppendLine("# Validation report");
            sb.AppendLine();
            sb.AppendLine("- Generated (UTC): " + report.generatedAtUtc);
            sb.AppendLine("- Assets checked: " + report.targetsChecked);
            sb.AppendLine("- **New errors: " + report.newErrors + "**" + (report.newErrors == 0 ? " (pass)" : " (FAIL)"));
            sb.AppendLine("- Known errors accepted in validation-baseline.json: " + report.baselinedErrors);
            sb.AppendLine("- Warnings: " + report.warnings);
            if (report.failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Assets that could not be validated (FAIL)");
                foreach (var f in report.failures) sb.AppendLine("- " + f);
            }
            AppendSection(sb, "New errors (must be fixed)", report.issues.Where(i => i.IsError && newKeys.Contains(i.Key)));
            AppendSection(sb, "Known errors (baselined)", report.issues.Where(i => i.IsError && !newKeys.Contains(i.Key)));
            AppendSection(sb, "Warnings", report.issues.Where(i => !i.IsError));
            UIAgentPaths.WriteAtomic(UIAgentPaths.ReportMdPath, sb.ToString());
        }

        private static void AppendSection(StringBuilder sb, string title, IEnumerable<ValidationIssue> issues)
        {
            var list = issues.ToList();
            sb.AppendLine();
            sb.AppendLine("## " + title + " (" + list.Count + ")");
            if (list.Count == 0) { sb.AppendLine("_none_"); return; }
            foreach (var group in list.GroupBy(i => i.asset))
            {
                sb.AppendLine();
                sb.AppendLine("### `" + group.Key + "`");
                foreach (var i in group)
                    sb.AppendLine("- [" + i.check + "] `" + i.address + "` - " + i.message);
            }
        }

        public static ValidationBaseline LoadBaseline()
        {
            if (!File.Exists(UIAgentPaths.BaselinePath)) return new ValidationBaseline();
            try
            {
                var b = JsonUtility.FromJson<ValidationBaseline>(File.ReadAllText(UIAgentPaths.BaselinePath));
                return b ?? new ValidationBaseline();
            }
            catch
            {
                return new ValidationBaseline();
            }
        }

        public static int WriteBaseline(ValidationReport report)
        {
            var b = new ValidationBaseline { createdAtUtc = DateTime.UtcNow.ToString("o") };
            foreach (var i in report.issues) if (i.IsError && !b.acceptedErrorKeys.Contains(i.Key)) b.acceptedErrorKeys.Add(i.Key);
            b.acceptedErrorKeys.Sort(StringComparer.Ordinal);
            UIAgentPaths.WriteAtomic(UIAgentPaths.BaselinePath, JsonUtility.ToJson(b, true));
            return b.acceptedErrorKeys.Count;
        }
    }
}
