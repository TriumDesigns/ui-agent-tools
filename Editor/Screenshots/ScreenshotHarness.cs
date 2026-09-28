// UIAgentTools - screenshots of prefabs and scene UI at every configured resolution.
//
// Prefabs are rendered in an isolated preview scene (nothing is ever saved).
// Scenes are opened one at a time in memory; each root canvas is captured on
// its own (inactive popups included - they are enabled temporarily), plus a
// composite of all screen-space canvases that are active by default. The
// scene is never saved and, in the Editor, your open scenes are restored.
//
// How each kind of UI is captured:
//   Screen Space canvas  -> once per resolution in config.json; CanvasScaler is
//                           applied exactly as the device would (computed here,
//                           because CanvasScaler doesn't tick in batch mode)
//   World Space canvas   -> one tight orthographic capture of the panel at its
//                           own aspect ratio (HMD panels, diegetic UI)
//   Widget prefab (RectTransform root, no Canvas)
//                        -> fixed-size root: one tight capture;
//                           stretch-anchored root: treated as a screen, per resolution
//
// Requirements: a graphics device. Never pass -nographics for screenshot runs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UIAgentTools
{
    [Serializable]
    public class CaptureRecord
    {
        public string file;
        public string source;
        public string part;
        public string mode;
        public string label;
        public int width;
        public int height;
        public bool blank;
        public string note;
    }

    [Serializable]
    public class CaptureManifest
    {
        public string generatedAtUtc;
        public List<CaptureRecord> captures = new List<CaptureRecord>();
    }

    public static class ScreenshotHarness
    {
        public class CaptureSummary
        {
            public int Images;
            public int Blank;
            public List<string> Failures = new List<string>();

            public override string ToString()
            {
                return "Captured " + Images + " screenshot(s)" + (Blank > 0 ? ", " + Blank + " came out BLANK (see manifest.md)" : "") +
                       (Failures.Count > 0 ? ". " + Failures.Count + " failure(s): " + string.Join("; ", Failures.ToArray()) : ".");
            }
        }

        // ---------------------------------------------------------------- entry points

        public static CaptureSummary CaptureAll()
        {
            var cfg = UIAgentConfig.Load();
            return Capture(UIMapper.FindAllTargets(cfg), cfg, true);
        }

        public static CaptureSummary CaptureTarget(string assetPath)
        {
            var cfg = UIAgentConfig.Load();
            return Capture(new List<string> { UIMapper.NormalizeAssetPath(assetPath) }, cfg, false);
        }

        private static CaptureSummary Capture(List<string> targets, UIAgentConfig cfg, bool replaceAll)
        {
            RequireGraphics();
            var summary = new CaptureSummary();
            string outDir = UIAgentPaths.ScreenshotsDir;
            UIAgentPaths.EnsureDir(outDir);

            var manifest = replaceAll ? new CaptureManifest() : (LoadManifest() ?? new CaptureManifest());
            if (replaceAll)
            {
                foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);
            }
            else
            {
                foreach (var t in targets)
                {
                    string prefix = UIAgentPaths.FileKey(t) + "__";
                    foreach (var f in Directory.GetFiles(outDir, prefix + "*.png")) File.Delete(f);
                    manifest.captures.RemoveAll(c => string.Equals(c.source, t, StringComparison.OrdinalIgnoreCase));
                }
            }

            var prefabs = targets.Where(t => t.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).ToList();
            var scenes = targets.Where(t => t.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)).ToList();

            try
            {
                for (int i = 0; i < prefabs.Count; i++)
                {
                    UIMapper.Progress("Screenshots", prefabs[i], i, prefabs.Count + scenes.Count);
                    try { CapturePrefab(prefabs[i], cfg, outDir, manifest.captures); }
                    catch (Exception e) { summary.Failures.Add(prefabs[i] + ": " + e.Message); Debug.LogError("[UIAgentTools] Screenshot failed for " + prefabs[i] + ": " + e); }
                }

                if (scenes.Count > 0)
                {
                    SceneAccess.WithIsolatedScenes(() =>
                    {
                        for (int i = 0; i < scenes.Count; i++)
                        {
                            UIMapper.Progress("Screenshots", scenes[i], prefabs.Count + i, prefabs.Count + scenes.Count);
                            try { CaptureScene(scenes[i], cfg, outDir, manifest.captures); }
                            catch (Exception e) { summary.Failures.Add(scenes[i] + ": " + e.Message); Debug.LogError("[UIAgentTools] Screenshot failed for " + scenes[i] + ": " + e); }
                        }
                    });
                }
            }
            finally
            {
                UIMapper.ClearProgress();
            }

            manifest.generatedAtUtc = DateTime.UtcNow.ToString("o");
            manifest.captures.Sort((a, b) => string.CompareOrdinal(a.file, b.file));
            WriteManifest(manifest);

            foreach (var c in manifest.captures)
            {
                if (!targets.Contains(c.source)) continue;
                summary.Images++;
                if (c.blank) summary.Blank++;
            }
            return summary;
        }

        private static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("No graphics device (Unity was started with -nographics). Screenshots need a GPU: run without -nographics.");
        }

        // ---------------------------------------------------------------- prefab

        private static void CapturePrefab(string path, UIAgentConfig cfg, string outDir, List<CaptureRecord> records)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new ArgumentException("Could not load prefab.");

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                string note = null;
                if (!inst.activeSelf) { inst.SetActive(true); note = "prefab root is inactive by default; activated for capture"; }

                var rootCanvases = RootCanvases(inst.transform);
                if (rootCanvases.Count == 0)
                {
                    var rt = inst.GetComponent<RectTransform>();
                    if (rt == null)
                    {
                        var uiDoc = TypeLookup.UIDocument;
                        string why = uiDoc != null && inst.GetComponentInChildren(uiDoc, true) != null
                            ? "UI Toolkit document - open it in the UI Builder, or capture the scene that hosts it"
                            : "no Canvas or RectTransform root";
                        records.Add(new CaptureRecord { source = path, part = "-", mode = "skipped", note = why, file = "" });
                        return;
                    }
                    CaptureWidget(scene, inst, rt, path, cfg, outDir, records, note);
                    return;
                }

                var screenCanvases = rootCanvases.Where(c => c.renderMode != RenderMode.WorldSpace).ToList();
                var worldCanvases = rootCanvases.Where(c => c.renderMode == RenderMode.WorldSpace).ToList();
                if (screenCanvases.Count > 0) CaptureScreenSpace(scene, true, screenCanvases, path, "screen", cfg, outDir, records, note);
                foreach (var wc in worldCanvases)
                    CaptureFlat(scene, true, (RectTransform)wc.transform, path, PartName(wc.transform), "world-space", cfg, outDir, records, note);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void CaptureWidget(Scene scene, GameObject inst, RectTransform rt, string path, UIAgentConfig cfg, string outDir, List<CaptureRecord> records, string note)
        {
            var host = new GameObject("UIAgentTools_TempCanvas", typeof(RectTransform));
            SceneManager.MoveGameObjectToScene(host, scene);
            var canvas = host.AddComponent<Canvas>();
            host.layer = inst.layer;

            bool stretched = rt.anchorMin != rt.anchorMax;
            if (stretched)
            {
                // Behaves like a full screen/panel: show it at each device resolution.
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                inst.transform.SetParent(host.transform, false);
                CaptureScreenSpace(scene, true, new List<Canvas> { canvas }, path, "widget", cfg, outDir, records,
                    Join(note, "widget prefab without a Canvas; scaled against " + cfg.widgetReferenceWidth + "x" + cfg.widgetReferenceHeight), true);
            }
            else
            {
                canvas.renderMode = RenderMode.WorldSpace;
                var hostRt = (RectTransform)host.transform;
                inst.transform.SetParent(host.transform, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                RebuildLayout(new List<Canvas> { canvas });
                var size = rt.rect.size;
                if (size.x <= 0f || size.y <= 0f) size = new Vector2(Mathf.Max(1f, rt.sizeDelta.x), Mathf.Max(1f, rt.sizeDelta.y));
                hostRt.sizeDelta = size;
                hostRt.position = Vector3.zero;
                RebuildLayout(new List<Canvas> { canvas });
                CaptureFlat(scene, true, rt, path, "widget", "widget", cfg, outDir, records, Join(note, "widget prefab captured at its own size"));
            }
        }

        // ---------------------------------------------------------------- scene

        private static void CaptureScene(string path, UIAgentConfig cfg, string outDir, List<CaptureRecord> records)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var allRootCanvases = new List<Canvas>();
            foreach (var r in scene.GetRootGameObjects()) allRootCanvases.AddRange(RootCanvases(r.transform));
            if (allRootCanvases.Count == 0)
            {
                records.Add(new CaptureRecord { source = path, part = "-", mode = "skipped", note = "no uGUI canvases in scene", file = "" });
                return;
            }

            var originalEnabled = allRootCanvases.ToDictionary(c => c, c => c.enabled);

            // 1) composite of every screen-space canvas that is visible by default
            var visibleScreen = allRootCanvases.Where(c => c.renderMode != RenderMode.WorldSpace && c.enabled && c.gameObject.activeInHierarchy).ToList();
            if (visibleScreen.Count > 0)
                CaptureScreenSpace(scene, false, visibleScreen, path, "all-visible", cfg, outDir, records, "every screen-space canvas active by default, composited");

            // 2) each root canvas on its own (inactive ones activated temporarily)
            foreach (var c in allRootCanvases)
            {
                foreach (var other in allRootCanvases) other.enabled = other == c;
                string note = null;
                if (!c.gameObject.activeInHierarchy)
                {
                    for (var t = c.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                    note = "inactive by default; activated for capture";
                }
                if (!originalEnabled[c]) note = Join(note, "Canvas component disabled by default; enabled for capture");

                if (c.renderMode == RenderMode.WorldSpace)
                    CaptureFlat(scene, false, (RectTransform)c.transform, path, PartName(c.transform), "world-space", cfg, outDir, records, note);
                else
                    CaptureScreenSpace(scene, false, new List<Canvas> { c }, path, PartName(c.transform), cfg, outDir, records, note);
            }
            // The scene is discarded by the next OpenScene / WithIsolatedScenes; nothing is saved.
        }

        // ---------------------------------------------------------------- capture primitives

        private static void CaptureScreenSpace(Scene scene, bool isPreviewScene, List<Canvas> canvases, string source, string part, UIAgentConfig cfg,
                                               string outDir, List<CaptureRecord> records, string note, bool widgetScaling = false)
        {
            var cam = CreateCamera(scene, isPreviewScene, cfg, LayerMaskOf(canvases));
            try
            {
                foreach (var c in canvases)
                {
                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    c.worldCamera = cam;
                    c.planeDistance = 1f;
                }
                foreach (var res in cfg.resolutions)
                {
                    if (res == null) continue;
                    cam.orthographic = false;
                    cam.fieldOfView = 60f;
                    // Screen Space - Camera canvases follow the camera, so park it far from the
                    // scene content with a tight far plane: only the canvas (at planeDistance 1)
                    // is inside the frustum, never the 3D scene behind it.
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 3f;
                    cam.transform.SetPositionAndRotation(new Vector3(0f, 3000f, 0f), Quaternion.identity);
                    foreach (var c in canvases)
                    {
                        if (widgetScaling) ApplyReferenceScale(c, res.width, res.height, cfg.widgetReferenceWidth, cfg.widgetReferenceHeight);
                        else ApplyCanvasScaler(c, res.width, res.height, cfg);
                    }
                    string file = FileName(source, part, res.label, res.width, res.height);
                    bool blank = RenderToPng(cam, canvases, res.width, res.height, cfg, outDir + "/" + file);
                    records.Add(new CaptureRecord { file = file, source = source, part = part, mode = "screen-space", label = res.label, width = res.width, height = res.height, blank = blank, note = note });
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cam.gameObject);
            }
        }

        private static void CaptureFlat(Scene scene, bool isPreviewScene, RectTransform rt, string source, string part, string mode, UIAgentConfig cfg,
                                        string outDir, List<CaptureRecord> records, string note)
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            var canvases = canvas != null ? new List<Canvas> { canvas.rootCanvas } : new List<Canvas>();
            RebuildLayout(canvases);

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners); // 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right
            float worldW = Vector3.Distance(corners[0], corners[3]);
            float worldH = Vector3.Distance(corners[0], corners[1]);
            float pxW = rt.rect.width, pxH = rt.rect.height;
            if (worldW <= 1e-6f || worldH <= 1e-6f || pxW <= 0f || pxH <= 0f)
            {
                records.Add(new CaptureRecord { source = source, part = part, mode = mode, file = "", note = Join(note, "zero-size rect, nothing to capture") });
                return;
            }

            float scale = cfg.flatCaptureMaxPixels / Mathf.Max(pxW, pxH);
            int w = Mathf.Clamp(Mathf.RoundToInt(pxW * scale), 16, 8192);
            int h = Mathf.Clamp(Mathf.RoundToInt(pxH * scale), 16, 8192);

            var cam = CreateCamera(scene, isPreviewScene, cfg, LayerMaskOf(canvases));
            try
            {
                Vector3 center = (corners[0] + corners[2]) * 0.5f;
                Vector3 forward = rt.forward;
                Vector3 up = (corners[1] - corners[0]).normalized;
                float d = Mathf.Max(worldW, worldH);
                cam.orthographic = true;
                cam.orthographicSize = worldH * 0.5f;
                cam.transform.SetPositionAndRotation(center - forward * d, Quaternion.LookRotation(forward, up));
                cam.nearClipPlane = d * 0.5f;
                cam.farClipPlane = d * 1.5f;

                string file = FileName(source, part, mode, w, h);
                bool blank = RenderToPng(cam, canvases, w, h, cfg, outDir + "/" + file);
                records.Add(new CaptureRecord { file = file, source = source, part = part, mode = mode, label = mode, width = w, height = h, blank = blank, note = note });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cam.gameObject);
            }
        }

        private static Camera CreateCamera(Scene scene, bool isPreviewScene, UIAgentConfig cfg, int cullingMask)
        {
            var go = new GameObject("UIAgentTools_CaptureCamera");
            go.hideFlags = HideFlags.DontSave;
            SceneManager.MoveGameObjectToScene(go, scene);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false; // rendered manually only
            if (isPreviewScene) cam.scene = scene;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = cfg.BackgroundColor();
            cam.cullingMask = cullingMask;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            return cam;
        }

        /// <summary>Renders twice (the first pass lets canvases resize to the target), saves a PNG, returns true if the image is blank.</summary>
        private static bool RenderToPng(Camera cam, List<Canvas> canvases, int w, int h, UIAgentConfig cfg, string filePath)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.antiAliasing = 1;
            rt.Create();
            var previous = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;
                cam.aspect = (float)w / h;
                RebuildLayout(canvases);
                cam.Render();
                RebuildLayout(canvases);
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply(false);
                File.WriteAllBytes(filePath, tex.EncodeToPNG());
                return IsBlank(tex, cfg.BackgroundColor());
            }
            finally
            {
                RenderTexture.active = previous;
                cam.targetTexture = null;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static bool IsBlank(Texture2D tex, Color background)
        {
            Color32 bg = background;
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i += 7)
            {
                var p = px[i];
                if (Math.Abs(p.r - bg.r) > 3 || Math.Abs(p.g - bg.g) > 3 || Math.Abs(p.b - bg.b) > 3) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- scaling & layout

        /// <summary>Reproduces CanvasScaler's scale factor for a given screen size, then disables the scaler (in memory only).</summary>
        private static void ApplyCanvasScaler(Canvas canvas, int width, int height, UIAgentConfig cfg)
        {
            var scalerType = TypeLookup.CanvasScaler;
            var scaler = scalerType != null ? canvas.GetComponent(scalerType) as Behaviour : null;
            if (scaler == null) { canvas.scaleFactor = 1f; return; }

            var so = new SerializedObject(scaler);
            float scaleFactor = 1f;
            float refPpu = FloatProp(so, "m_ReferencePixelsPerUnit", 100f);
            int mode = IntProp(so, "m_UiScaleMode", 0); // ConstantPixelSize=0, ScaleWithScreenSize=1, ConstantPhysicalSize=2
            switch (mode)
            {
                case 1:
                {
                    var refRes = so.FindProperty("m_ReferenceResolution") != null ? so.FindProperty("m_ReferenceResolution").vector2Value : new Vector2(800, 600);
                    if (refRes.x <= 0f || refRes.y <= 0f) refRes = new Vector2(800, 600);
                    int match = IntProp(so, "m_ScreenMatchMode", 0); // MatchWidthOrHeight=0, Expand=1, Shrink=2
                    float m = Mathf.Clamp01(FloatProp(so, "m_MatchWidthOrHeight", 0f));
                    if (match == 1) scaleFactor = Mathf.Min(width / refRes.x, height / refRes.y);
                    else if (match == 2) scaleFactor = Mathf.Max(width / refRes.x, height / refRes.y);
                    else
                    {
                        float logW = Mathf.Log(width / refRes.x, 2f);
                        float logH = Mathf.Log(height / refRes.y, 2f);
                        scaleFactor = Mathf.Pow(2f, Mathf.Lerp(logW, logH, m));
                    }
                    break;
                }
                case 2:
                {
                    float dpi = cfg.physicalDpi;
                    float spriteDpi = FloatProp(so, "m_DefaultSpriteDPI", 96f);
                    int unit = IntProp(so, "m_PhysicalUnit", 3); // Centimeters=0, Millimeters=1, Inches=2, Points=3, Picas=4
                    float target = 1f;
                    switch (unit)
                    {
                        case 0: target = 2.54f; break;
                        case 1: target = 25.4f; break;
                        case 2: target = 1f; break;
                        case 3: target = 72f; break;
                        case 4: target = 6f; break;
                    }
                    scaleFactor = dpi / target;
                    refPpu = refPpu * target / Mathf.Max(1f, spriteDpi);
                    break;
                }
                default:
                    scaleFactor = FloatProp(so, "m_ScaleFactor", 1f);
                    break;
            }

            scaler.enabled = false; // OnDisable resets the canvas, so set our values after
            canvas.scaleFactor = Mathf.Max(0.0001f, scaleFactor);
            canvas.referencePixelsPerUnit = refPpu;
        }

        private static void ApplyReferenceScale(Canvas canvas, int width, int height, int refW, int refH)
        {
            float logW = Mathf.Log((float)width / refW, 2f);
            float logH = Mathf.Log((float)height / refH, 2f);
            canvas.scaleFactor = Mathf.Pow(2f, Mathf.Lerp(logW, logH, 0.5f));
        }

        private static readonly Dictionary<Type, MethodInfo> ForceMeshMethods = new Dictionary<Type, MethodInfo>();

        private static void RebuildLayout(List<Canvas> canvases)
        {
            Canvas.ForceUpdateCanvases();

            var rebuilder = TypeLookup.LayoutRebuilder;
            var rebuild = rebuilder != null ? rebuilder.GetMethod("ForceRebuildLayoutImmediate", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(RectTransform) }, null) : null;
            var tmpType = TypeLookup.TmpText;

            foreach (var c in canvases)
            {
                if (c == null) continue;
                if (tmpType != null)
                {
                    foreach (var tmp in c.GetComponentsInChildren(tmpType, true)) ForceMeshUpdate(tmp);
                }
                if (rebuild != null)
                {
                    try { rebuild.Invoke(null, new object[] { (RectTransform)c.transform }); }
                    catch { /* layout rebuild is best-effort */ }
                }
            }
            Canvas.ForceUpdateCanvases();
        }

        private static void ForceMeshUpdate(Component tmp)
        {
            var type = tmp.GetType();
            MethodInfo mi;
            if (!ForceMeshMethods.TryGetValue(type, out mi))
            {
                mi = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => m.Name == "ForceMeshUpdate" && m.GetParameters().All(p => p.ParameterType == typeof(bool)))
                         .OrderBy(m => m.GetParameters().Length)
                         .FirstOrDefault();
                ForceMeshMethods[type] = mi;
            }
            if (mi == null) return;
            try
            {
                var ps = mi.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) args[i] = false;
                mi.Invoke(tmp, args);
            }
            catch
            {
                // best-effort
            }
        }

        // ---------------------------------------------------------------- helpers

        private static List<Canvas> RootCanvases(Transform root)
        {
            var list = new List<Canvas>();
            foreach (var c in root.GetComponentsInChildren<Canvas>(true))
            {
                bool nested = false;
                for (var p = c.transform.parent; p != null; p = p.parent)
                    if (p.GetComponent<Canvas>() != null) { nested = true; break; }
                if (!nested) list.Add(c);
            }
            return list;
        }

        private static int LayerMaskOf(List<Canvas> canvases)
        {
            int mask = 0;
            foreach (var c in canvases)
                foreach (var t in c.GetComponentsInChildren<Transform>(true))
                    mask |= 1 << t.gameObject.layer;
            return mask == 0 ? ~0 : mask;
        }

        private static string PartName(Transform t)
        {
            return UIAgentPaths.FileKey(UIAddress.Of(t));
        }

        private static string FileName(string source, string part, string label, int w, int h)
        {
            return UIAgentPaths.FileKey(source) + "__" + part + "__" + UIAgentPaths.FileKey(label) + "_" + w + "x" + h + ".png";
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "; " + b;
        }

        private static float FloatProp(SerializedObject so, string name, float fallback)
        {
            var p = so.FindProperty(name);
            return p != null ? p.floatValue : fallback;
        }

        private static int IntProp(SerializedObject so, string name, int fallback)
        {
            var p = so.FindProperty(name);
            return p != null ? p.intValue : fallback;
        }

        /// <summary>Drop manifest entries (and their PNGs) whose source path starts with a prefix.</summary>
        public static void RemoveFromManifest(string sourcePrefix)
        {
            var manifest = LoadManifest();
            if (manifest == null) return;
            var gone = manifest.captures.Where(c => c.source != null && c.source.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (gone.Count == 0) return;
            foreach (var c in gone)
            {
                if (string.IsNullOrEmpty(c.file)) continue;
                string f = UIAgentPaths.ScreenshotsDir + "/" + c.file;
                if (File.Exists(f)) File.Delete(f);
            }
            manifest.captures.RemoveAll(c => gone.Contains(c));
            WriteManifest(manifest);
        }

        private static string ManifestJsonPath { get { return UIAgentPaths.ScreenshotsDir + "/manifest.json"; } }

        private static CaptureManifest LoadManifest()
        {
            if (!File.Exists(ManifestJsonPath)) return null;
            try { return JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(ManifestJsonPath)); }
            catch { return null; }
        }

        private static void WriteManifest(CaptureManifest manifest)
        {
            UIAgentPaths.WriteAtomic(ManifestJsonPath, JsonUtility.ToJson(manifest, true));
            var sb = new StringBuilder();
            sb.AppendLine("# Screenshots");
            sb.AppendLine();
            sb.AppendLine("Generated (UTC): " + manifest.generatedAtUtc + ". Layout check only - HMD world-space panels still need an on-device look.");
            sb.AppendLine("BLANK = the image contains only the background colour (render problem, or the UI really is empty/transparent).");
            sb.AppendLine();
            foreach (var group in manifest.captures.GroupBy(c => c.source))
            {
                sb.AppendLine("## `" + group.Key + "`");
                foreach (var c in group)
                {
                    if (string.IsNullOrEmpty(c.file))
                    {
                        sb.AppendLine("- " + c.part + " - skipped: " + c.note);
                        continue;
                    }
                    sb.AppendLine("- `" + c.file + "` - " + c.part + ", " + c.mode + ", " + c.width + "x" + c.height + (c.blank ? " **BLANK**" : "") +
                                  (string.IsNullOrEmpty(c.note) ? "" : " (" + c.note + ")"));
                }
                sb.AppendLine();
            }
            UIAgentPaths.WriteAtomic(UIAgentPaths.ScreenshotsDir + "/manifest.md", sb.ToString());
        }
    }
}
