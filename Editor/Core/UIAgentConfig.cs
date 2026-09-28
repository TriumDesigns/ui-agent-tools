// UIAgentTools - per-project configuration, read from UIAgentMap/config.json.
// Every field has a safe default, so a missing or partial config file is fine.

using System;
using System.IO;
using UnityEngine;

namespace UIAgentTools
{
    [Serializable]
    public class ScreenResolution
    {
        public string label = "custom";
        public int width = 1080;
        public int height = 1920;

        public ScreenResolution() { }

        public ScreenResolution(string label, int width, int height)
        {
            this.label = label;
            this.width = width;
            this.height = height;
        }
    }

    [Serializable]
    public class UIAgentConfig
    {
        // Only assets under these folders are mapped/validated/captured.
        public string[] searchFolders = { "Assets" };

        // Assets under these folders are skipped (e.g. "Assets/Plugins", "Assets/Samples").
        public string[] excludeFolders = new string[0];

        public bool includeScenes = true;

        // true = only scenes listed (and enabled) in Build Settings.
        public bool onlyBuildScenes = false;

        // Scene outline (names-only hierarchy) is truncated after this many objects.
        public int maxOutlineObjects = 4000;

        // Serialized-field dump limits (keeps map files readable on huge components).
        public int maxArrayElements = 16;
        public int maxFieldDepth = 4;
        public int maxStringLength = 400;

        // Longest side, in pixels, of world-space canvas and widget screenshots.
        public int flatCaptureMaxPixels = 1600;

        // DPI assumed for CanvasScaler "Constant Physical Size" during screenshots.
        public float physicalDpi = 420f;

        // Reference resolution used to scale stretched widget prefabs that have no Canvas of their own.
        public int widgetReferenceWidth = 1080;
        public int widgetReferenceHeight = 1920;

        // Screenshot background colour (hex, no alpha).
        public string screenshotBackground = "#141417";

        // How many backup snapshots UIAgentEdit keeps under UIAgentMap/backups.
        public int keepBackups = 30;

        public ScreenResolution[] resolutions =
        {
            new ScreenResolution("phone_portrait", 1080, 2400),
            new ScreenResolution("phone_landscape", 2400, 1080),
            new ScreenResolution("foldable_cover", 1080, 2092),
            new ScreenResolution("foldable_unfolded", 2208, 1840),
            new ScreenResolution("tablet_portrait", 1600, 2560),
        };

        public static UIAgentConfig Load()
        {
            var config = new UIAgentConfig();
            string path = UIAgentPaths.ConfigPath;
            if (File.Exists(path))
            {
                try
                {
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(path), config);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[UIAgentTools] Could not parse " + path + " - using defaults. " + e.Message);
                    config = new UIAgentConfig();
                }
            }
            config.Sanitize();
            return config;
        }

        public static string DefaultJson()
        {
            return JsonUtility.ToJson(new UIAgentConfig(), true);
        }

        private void Sanitize()
        {
            if (searchFolders == null || searchFolders.Length == 0) searchFolders = new[] { "Assets" };
            if (excludeFolders == null) excludeFolders = new string[0];
            if (resolutions == null || resolutions.Length == 0) resolutions = new UIAgentConfig().resolutions;
            maxOutlineObjects = Mathf.Max(100, maxOutlineObjects);
            maxArrayElements = Mathf.Clamp(maxArrayElements, 1, 256);
            maxFieldDepth = Mathf.Clamp(maxFieldDepth, 1, 10);
            maxStringLength = Mathf.Clamp(maxStringLength, 40, 10000);
            flatCaptureMaxPixels = Mathf.Clamp(flatCaptureMaxPixels, 128, 8192);
            physicalDpi = Mathf.Clamp(physicalDpi, 72f, 1000f);
            widgetReferenceWidth = Mathf.Max(64, widgetReferenceWidth);
            widgetReferenceHeight = Mathf.Max(64, widgetReferenceHeight);
            keepBackups = Mathf.Clamp(keepBackups, 1, 1000);
            foreach (var r in resolutions)
            {
                if (r == null) continue;
                r.width = Mathf.Clamp(r.width, 16, 8192);
                r.height = Mathf.Clamp(r.height, 16, 8192);
                if (string.IsNullOrEmpty(r.label)) r.label = r.width + "x" + r.height;
            }
        }

        public bool IsIncluded(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string p = assetPath.Replace('\\', '/');
            bool inSearch = false;
            foreach (var f in searchFolders)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string folder = f.Replace('\\', '/').TrimEnd('/');
                if (p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) { inSearch = true; break; }
            }
            if (!inSearch) return false;
            foreach (var f in excludeFolders)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string folder = f.Replace('\\', '/').TrimEnd('/');
                if (p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public Color BackgroundColor()
        {
            Color c;
            if (!ColorUtility.TryParseHtmlString(screenshotBackground, out c)) c = new Color(0.08f, 0.08f, 0.09f, 1f);
            c.a = 1f;
            return c;
        }
    }
}
