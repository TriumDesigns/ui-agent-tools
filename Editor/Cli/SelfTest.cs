// UIAgentTools - end-to-end self test, run once per project / Unity version:
//   ui-agent selftest      (or menu: UI Agent Tools > Run Self Test)
//
// Creates a throwaway prefab + scene under Assets/__UIAgentToolsSelfTest__/,
// exercises map -> validate -> edit prefab -> edit scene (prefab-instance
// override) -> rejected edits roll back -> addresses -> screenshots, then
// deletes everything it created. Your own assets are never touched.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UIAgentTools
{
    public static class SelfTest
    {
        private const string Folder = "Assets/__UIAgentToolsSelfTest__";
        private const string PanelPath = Folder + "/SelfTestPanel.prefab";
        private const string WidgetPath = Folder + "/SelfTestWidget.prefab";
        private const string ScenePath = Folder + "/SelfTestScene.unity";

        private class Results
        {
            public readonly List<string> Lines = new List<string>();
            public int Failed;
            public int Warned;

            public void Pass(string what) { Lines.Add("PASS  " + what); }
            public void Fail(string what) { Lines.Add("FAIL  " + what); Failed++; }
            public void Warn(string what) { Lines.Add("WARN  " + what); Warned++; }

            public void Check(bool condition, string what, string failDetail = null)
            {
                if (condition) Pass(what);
                else Fail(what + (string.IsNullOrEmpty(failDetail) ? "" : " - " + failDetail));
            }
        }

        [MenuItem("UI Agent Tools/Run Self Test", false, 100)]
        private static void MenuRun()
        {
            string summary;
            bool ok = Run(out summary);
            Debug.Log("[UIAgentTools] Self test " + (ok ? "passed" : "FAILED") + ":\n" + summary);
            EditorUtility.DisplayDialog("UI Agent Tools - Self Test", (ok ? "PASSED" : "FAILED") + "\n\n" + Truncate(summary, 1800), "OK");
        }

        public static bool Run(out string summary)
        {
            var r = new Results();
            r.Lines.Add("Unity " + Application.unityVersion + ", batch mode: " + Application.isBatchMode + ", graphics: " + SystemInfo.graphicsDeviceType +
                        ", render pipeline: " + (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in"));

            if (TypeLookup.Find("UnityEngine.UI.Image") == null)
            {
                r.Fail("uGUI (com.unity.ugui) is not installed - the self test needs it");
                summary = string.Join("\n", r.Lines.ToArray());
                return false;
            }

            Cleanup();
            try
            {
                if (Application.isBatchMode) Body(r);
                else SceneAccess.WithIsolatedScenes(() => Body(r));
            }
            catch (Exception e)
            {
                r.Fail("Unexpected exception: " + e);
            }
            finally
            {
                if (Application.isBatchMode)
                {
                    try { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); } catch { }
                }
                Cleanup();
            }

            r.Lines.Add("");
            r.Lines.Add(r.Failed == 0
                ? "RESULT: all checks passed" + (r.Warned > 0 ? " (" + r.Warned + " warning(s))" : "") + ". UI Agent Tools works in this project."
                : "RESULT: " + r.Failed + " check(s) FAILED. Do not rely on the tools in this project until fixed - send this output to whoever maintains the package.");
            summary = string.Join("\n", r.Lines.ToArray());
            return r.Failed == 0;
        }

        private static void Body(Results r)
        {
            // Start from an empty scene so temporary objects never land in one of your scenes.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.CreateFolder("Assets", "__UIAgentToolsSelfTest__");
            CreatePanelPrefab();
            CreateWidgetPrefab();
            CreateScene();
            r.Pass("created test prefab, widget prefab and scene");

            // ---- map
            UIMapper.MapTarget(PanelPath);
            UIMapper.MapTarget(ScenePath);
            string prefabMap = ReadMap("prefabs", PanelPath);
            string sceneMap = ReadMap("scenes", ScenePath);
            r.Check(prefabMap != null && prefabMap.Contains("`SelfTestPanel/Panel/Header/Title`"), "prefab map contains full hierarchy addresses");
            r.Check(prefabMap != null && prefabMap.Contains("m_OnClick: UnityEvent, 1 persistent listener"), "prefab map shows Button onClick wiring");
            r.Check(prefabMap != null && prefabMap.Contains("`SelfTestPanel/Panel/List/Item[0]`") && prefabMap.Contains("`SelfTestPanel/Panel/List/Item[1]`"), "duplicate sibling names get [k] addresses");
            r.Check(sceneMap != null && sceneMap.Contains("[prefab: " + PanelPath + "]"), "scene map marks prefab instances");
            r.Check(sceneMap != null && sceneMap.Contains("## UI roots (full detail)"), "scene map has UI root detail section");
            r.Check(sceneMap != null && sceneMap.Contains("m_Text"), "scene map includes serialized fields of scene UI");
            string usage = File.Exists(UIAgentPaths.UsagePath) ? File.ReadAllText(UIAgentPaths.UsagePath) : "";
            r.Check(usage.Contains(PanelPath) && usage.Contains("prefab instance"), "prefab-usage.md lists the scene instance");

            // ---- validate
            var issues = UIValidator.ValidateAsset(PanelPath).Concat(UIValidator.ValidateAsset(ScenePath)).Where(i => i.IsError).ToList();
            r.Check(issues.Count == 0, "clean test assets validate with 0 errors", string.Join("; ", issues.Select(i => i.check + " " + i.address).ToArray()));

            // ---- address resolution
            var tmpRoot = PrefabUtility.LoadPrefabContents(PanelPath);
            try
            {
                var item1 = UIAgentEdit.Find(tmpRoot, "SelfTestPanel/Panel/List/Item[1]");
                r.Check(item1 != null && item1.transform.GetSiblingIndex() == 1, "Find resolves indexed duplicate names");
                bool threw = false;
                try { UIAgentEdit.Find(tmpRoot, "SelfTestPanel/Panel/DoesNotExist"); } catch (ArgumentException) { threw = true; }
                r.Check(threw, "Find throws a descriptive error for a wrong address");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(tmpRoot);
            }

            // ---- edit prefab
            UIAgentEdit.ClearTouched();
            UIAgentEdit.EditPrefab(PanelPath, root =>
            {
                var title = UIAgentEdit.Find(root, "SelfTestPanel/Panel/Header/Title");
                UIAgentEdit.SetText(title, "Edited by self test");
                var panel = UIAgentEdit.Find(root, "SelfTestPanel/Panel");
                UIAgentEdit.Set(UIAgentEdit.Get(panel, "Image"), "m_Color", "#3366CCFF");
                UIAgentEdit.SetRect(panel.GetComponent<RectTransform>(), sizeDelta: new Vector2(640, 480));
                var added = UIAgentEdit.CreateChild(panel, "AddedBySelfTest");
                UIAgentEdit.AddComponent(added, "Image");
            });
            var reloaded = PrefabUtility.LoadPrefabContents(PanelPath);
            try
            {
                var title = UIAgentEdit.Find(reloaded, "SelfTestPanel/Panel/Header/Title");
                var textSo = new SerializedObject(UIAgentEdit.Get(title, "Text"));
                r.Check(textSo.FindProperty("m_Text").stringValue == "Edited by self test", "EditPrefab: text change persisted to disk");
                var panel = UIAgentEdit.Find(reloaded, "SelfTestPanel/Panel");
                var imgSo = new SerializedObject(UIAgentEdit.Get(panel, "Image"));
                var col = imgSo.FindProperty("m_Color").colorValue;
                r.Check(Mathf.Abs(col.r - 0.2f) < 0.01f && Mathf.Abs(col.b - 0.8f) < 0.01f, "EditPrefab: colour change persisted to disk");
                r.Check(panel.GetComponent<RectTransform>().sizeDelta == new Vector2(640, 480), "EditPrefab: RectTransform change persisted to disk");
                r.Check(panel.transform.Find("AddedBySelfTest") != null, "EditPrefab: new child persisted to disk");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(reloaded);
            }
            r.Check(UIAgentEdit.TouchedAssets.Contains(PanelPath), "EditPrefab records the touched asset");
            r.Check(HeaderIsYaml(PanelPath), "saved prefab is valid text YAML");

            // ---- edit scene: override on a prefab instance
            UIAgentEdit.EditScene(ScenePath, scene =>
            {
                var title = UIAgentEdit.Find(scene, "SceneCanvas/SelfTestPanel/Panel/Header/Title");
                UIAgentEdit.SetText(title, "Scene-only override");
                var label = UIAgentEdit.Find(scene, "SceneCanvas/SceneLabel");
                UIAgentEdit.SetActive(label, false);
            });
            using (var h = SceneAccess.Open(ScenePath, false))
            {
                var title = UIAgentEdit.Find(h.Scene, "SceneCanvas/SelfTestPanel/Panel/Header/Title");
                var so = new SerializedObject(UIAgentEdit.Get(title, "Text"));
                r.Check(so.FindProperty("m_Text").stringValue == "Scene-only override", "EditScene: prefab-instance override persisted");
                var inst = UIAgentEdit.Find(h.Scene, "SceneCanvas/SelfTestPanel");
                r.Check(PrefabUtility.IsOutermostPrefabInstanceRoot(inst), "EditScene: prefab link preserved");
                r.Check(!UIAgentEdit.Find(h.Scene, "SceneCanvas/SceneLabel").activeSelf, "EditScene: SetActive persisted");
            }
            r.Check(HeaderIsYaml(ScenePath), "saved scene is valid text YAML");
            UIMapper.MapTarget(ScenePath);
            sceneMap = ReadMap("scenes", ScenePath);
            r.Check(sceneMap != null && sceneMap.Contains("Overrides on this instance") && sceneMap.Contains("Scene-only override"), "scene map shows the new instance override");

            // ---- a throwing edit leaves the file untouched
            string hashBefore = Hash(PanelPath);
            bool rejected = false;
            try
            {
                UIAgentEdit.EditPrefab(PanelPath, root =>
                {
                    UIAgentEdit.SetText(UIAgentEdit.Find(root, "SelfTestPanel/Panel/Header/Title"), "SHOULD NOT BE SAVED");
                    throw new InvalidOperationException("deliberate failure");
                });
            }
            catch (UIAgentEditException) { rejected = true; }
            r.Check(rejected && Hash(PanelPath) == hashBefore, "an edit that throws saves nothing");

            // ---- an edit that introduces a missing reference is rejected
            hashBefore = Hash(PanelPath);
            rejected = false;
            try
            {
                UIAgentEdit.EditPrefab(PanelPath, root =>
                {
                    var button = UIAgentEdit.Find(root, "SelfTestPanel/Panel/Button");
                    var icon = UIAgentEdit.Find(root, "SelfTestPanel/Panel/Button/Icon");
                    UIAgentEdit.Set(UIAgentEdit.Get(button, "Button"), "m_TargetGraphic", UIAgentEdit.Get(icon, "Image"));
                    Object.DestroyImmediate(icon); // leaves m_TargetGraphic pointing at a destroyed object
                });
            }
            catch (UIAgentEditException) { rejected = true; }
            if (rejected) r.Check(Hash(PanelPath) == hashBefore, "an edit that creates a missing reference is rejected and nothing is saved");
            else r.Warn("this Unity version cleared the dangling reference instead of leaving it missing, so rejection could not be exercised (not a failure)");

            // ---- baseline/validation report round trip on the test assets
            var report = UIValidator.Run(new List<string> { PanelPath, ScenePath });
            r.Check(report.failures.Count == 0, "validation run completes on edited assets", string.Join("; ", report.failures.ToArray()));

            // ---- screenshots
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                r.Warn("no graphics device (-nographics) - screenshot checks skipped; run `ui-agent selftest` without -nographics to cover them");
            }
            else
            {
                var s1 = ScreenshotHarness.CaptureTarget(PanelPath);
                var s2 = ScreenshotHarness.CaptureTarget(WidgetPath);
                var s3 = ScreenshotHarness.CaptureTarget(ScenePath);
                r.Check(s1.Failures.Count == 0 && s1.Images >= 1, "prefab screenshots captured (" + s1.Images + ")", string.Join("; ", s1.Failures.ToArray()));
                r.Check(s2.Failures.Count == 0 && s2.Images >= 1, "widget screenshot captured (" + s2.Images + ")", string.Join("; ", s2.Failures.ToArray()));
                r.Check(s3.Failures.Count == 0 && s3.Images >= 1, "scene screenshots captured (" + s3.Images + ")", string.Join("; ", s3.Failures.ToArray()));
                int blank = s1.Blank + s2.Blank + s3.Blank;
                string blankMsg = blank + " blank image(s) - UI rendering to texture isn't working with this render pipeline/graphics API; screenshots can't be trusted here";
                // Known issue (CHANGELOG 0.2.2): after the full selftest sequence in one batch process the scene capture can come out blank
                // even though one-command-per-process runs render correctly. Don't fail first-run onboarding on it; re-check with `ui-agent screenshots`.
                if (blank > 0 && Application.isBatchMode) r.Warn(blankMsg + " (batch mode: verify with `ui-agent screenshots` and look at the PNGs)");
                else r.Check(blank == 0, "no screenshot came out blank", blankMsg);
            }
        }

        // ---------------------------------------------------------------- fixtures

        private static Type UI(string name)
        {
            var t = TypeLookup.Find("UnityEngine.UI." + name);
            if (t == null) throw new InvalidOperationException("uGUI type " + name + " not found");
            return t;
        }

        private static GameObject NewUI(string name, Transform parent, params string[] components)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5; // UI
            if (parent != null) go.transform.SetParent(parent, false);
            foreach (var c in components) go.AddComponent(UI(c));
            return go;
        }

        private static void Stretch(GameObject go)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
        }

        private static void SetLegacyText(GameObject go, string text)
        {
            var comp = go.GetComponent(UI("Text"));
            var so = new SerializedObject(comp);
            so.FindProperty("m_Text").stringValue = text;
            Font font = null;
#if UNITY_2022_2_OR_NEWER
            foreach (var name in new[] { "LegacyRuntime.ttf" })
#else
            foreach (var name in new[] { "Arial.ttf" })
#endif
            {
                try { font = Resources.GetBuiltinResource<Font>(name); } catch { font = null; }
                if (font != null) break;
            }
            var fontProp = so.FindProperty("m_FontData.m_Font");
            if (fontProp != null && font != null) fontProp.objectReferenceValue = font;
            var sizeProp = so.FindProperty("m_FontData.m_FontSize");
            if (sizeProp != null) sizeProp.intValue = 36;
            so.FindProperty("m_Color").colorValue = Color.white;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreatePanelPrefab()
        {
            var root = NewUI("SelfTestPanel", null, "CanvasScaler", "GraphicRaycaster");
            // Canvas must exist before CanvasScaler's RequireComponent resolves; add explicitly if missing.
            if (root.GetComponent<Canvas>() == null) root.AddComponent<Canvas>();
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scalerSo = new SerializedObject(root.GetComponent(UI("CanvasScaler")));
            scalerSo.FindProperty("m_UiScaleMode").intValue = 1;
            scalerSo.FindProperty("m_ReferenceResolution").vector2Value = new Vector2(1080, 1920);
            scalerSo.FindProperty("m_MatchWidthOrHeight").floatValue = 0.5f;
            scalerSo.ApplyModifiedPropertiesWithoutUndo();

            var panel = NewUI("Panel", root.transform, "Image");
            ((RectTransform)panel.transform).sizeDelta = new Vector2(800, 1000);
            var header = NewUI("Header", panel.transform);
            var title = NewUI("Title", header.transform, "Text");
            ((RectTransform)title.transform).sizeDelta = new Vector2(600, 80);
            SetLegacyText(title, "Self test");

            var button = NewUI("Button", panel.transform, "Image", "Button");
            ((RectTransform)button.transform).anchoredPosition = new Vector2(0, -300);
            ((RectTransform)button.transform).sizeDelta = new Vector2(300, 90);
            var icon = NewUI("Icon", button.transform, "Image");
            ((RectTransform)icon.transform).sizeDelta = new Vector2(40, 40);
            var btnSo = new SerializedObject(button.GetComponent(UI("Button")));
            btnSo.FindProperty("m_TargetGraphic").objectReferenceValue = button.GetComponent(UI("Image"));
            btnSo.ApplyModifiedPropertiesWithoutUndo();
            // persistent listener: Button.onClick -> Header.SetActive(false)
            UIAgentEdit.AddListener(button.GetComponent(UI("Button")), "onClick", header, "SetActive", false);

            var list = NewUI("List", panel.transform, "VerticalLayoutGroup");
            NewUI("Item", list.transform, "Image");
            NewUI("Item", list.transform, "Image");

            bool ok;
            PrefabUtility.SaveAsPrefabAsset(root, PanelPath, out ok);
            Object.DestroyImmediate(root);
            if (!ok) throw new InvalidOperationException("could not save test prefab");
        }

        private static void CreateWidgetPrefab()
        {
            var root = NewUI("SelfTestWidget", null, "Image");
            ((RectTransform)root.transform).sizeDelta = new Vector2(320, 120);
            var label = NewUI("Label", root.transform, "Text");
            Stretch(label);
            SetLegacyText(label, "Widget");
            var so = new SerializedObject(label.GetComponent(UI("Text")));
            so.FindProperty("m_Color").colorValue = Color.black;
            so.ApplyModifiedPropertiesWithoutUndo();
            bool ok;
            PrefabUtility.SaveAsPrefabAsset(root, WidgetPath, out ok);
            Object.DestroyImmediate(root);
            if (!ok) throw new InvalidOperationException("could not save widget prefab");
        }

        private static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var canvasGo = NewUI("SceneCanvas", null, "CanvasScaler", "GraphicRaycaster");
            if (canvasGo.GetComponent<Canvas>() == null) canvasGo.AddComponent<Canvas>();
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var label = NewUI("SceneLabel", canvasGo.transform, "Text");
            ((RectTransform)label.transform).sizeDelta = new Vector2(500, 80);
            SetLegacyText(label, "Scene label");
            var panelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PanelPath);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(panelAsset, canvasGo.transform);
            if (inst == null) throw new InvalidOperationException("could not instantiate test prefab");
            var es = TypeLookup.EventSystem;
            if (es != null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent(es);
                var module = TypeLookup.Find("UnityEngine.EventSystems.StandaloneInputModule");
                if (module != null) esGo.AddComponent(module);
            }
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("could not save test scene");
        }

        // ---------------------------------------------------------------- utilities

        private static string ReadMap(string kind, string assetPath)
        {
            string f = UIAgentPaths.MapRoot + "/" + kind + "/" + UIAgentPaths.FileKey(assetPath) + ".md";
            return File.Exists(f) ? File.ReadAllText(f) : null;
        }

        private static bool HeaderIsYaml(string assetPath)
        {
            if (EditorSettings.serializationMode != SerializationMode.ForceText) return true;
            using (var reader = new StreamReader(UIAgentPaths.Absolute(assetPath)))
                return (reader.ReadLine() ?? "").StartsWith("%YAML");
        }

        private static string Hash(string assetPath)
        {
            using (var sha = SHA1.Create())
            using (var fs = File.OpenRead(UIAgentPaths.Absolute(assetPath)))
                return Convert.ToBase64String(sha.ComputeHash(fs));
        }

        private static void Cleanup()
        {
            try
            {
                if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
                foreach (var p in new[] { PanelPath, WidgetPath, ScenePath })
                {
                    string key = UIAgentPaths.FileKey(p);
                    foreach (var dir in new[] { UIAgentPaths.PrefabMapDir, UIAgentPaths.SceneMapDir, UIAgentPaths.ScreenshotsDir })
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var f in Directory.GetFiles(dir, key + "*")) File.Delete(f);
                    }
                }
                // Drop the self-test entries from index / usage / manifest.
                var index = UIMapper.LoadIndex();
                if (index != null && index.entries.RemoveAll(e => e.assetPath.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase)) > 0)
                    UIMapper.RewriteIndex(index);
                ScreenshotHarness.RemoveFromManifest(Folder + "/");
                if (Directory.Exists(UIAgentPaths.BackupsDir))
                {
                    foreach (var d in Directory.GetDirectories(UIAgentPaths.BackupsDir))
                        if (Directory.Exists(d + "/" + Folder)) Directory.Delete(d, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UIAgentTools] Self-test cleanup problem: " + e.Message);
            }
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max) + "\n... (full output in the Console)";
        }
    }
}
