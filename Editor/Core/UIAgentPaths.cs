// UIAgentTools - well-known locations. Everything generated lives in
// <ProjectRoot>/UIAgentMap (outside Assets, so Unity never imports it
// and it never ships in a build).

using System.IO;
using System.Text;
using UnityEngine;

namespace UIAgentTools
{
    public static class UIAgentPaths
    {
        public static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath).Replace('\\', '/'); }
        }

        public static string MapRoot { get { return ProjectRoot + "/UIAgentMap"; } }
        public static string PrefabMapDir { get { return MapRoot + "/prefabs"; } }
        public static string SceneMapDir { get { return MapRoot + "/scenes"; } }
        public static string ScreenshotsDir { get { return MapRoot + "/screenshots"; } }
        public static string BackupsDir { get { return MapRoot + "/backups"; } }
        public static string RequestsDir { get { return MapRoot + "/requests"; } }
        public static string LogsDir { get { return MapRoot + "/logs"; } }
        public static string ConfigPath { get { return MapRoot + "/config.json"; } }
        public static string IndexJsonPath { get { return MapRoot + "/index.json"; } }
        public static string IndexMdPath { get { return MapRoot + "/INDEX.md"; } }
        public static string UsagePath { get { return MapRoot + "/prefab-usage.md"; } }
        public static string BaselinePath { get { return MapRoot + "/validation-baseline.json"; } }
        public static string ReportMdPath { get { return MapRoot + "/validation-report.md"; } }
        public static string ReportJsonPath { get { return MapRoot + "/validation-report.json"; } }
        public static string LastResultPath { get { return MapRoot + "/last-result.json"; } }

        /// <summary>Absolute file-system path for a project-relative asset path ("Assets/...").</summary>
        public static string Absolute(string assetPath)
        {
            return ProjectRoot + "/" + assetPath.Replace('\\', '/');
        }

        /// <summary>
        /// Stable, collision-free file name for an asset path:
        /// "Assets/UI/Login Panel.prefab" -> "Assets__UI__Login_Panel.prefab".
        /// </summary>
        public static string FileKey(string assetPath)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            string p = assetPath.Replace('\\', '/');
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '/') { sb.Append("__"); continue; }
                if (c == ' ' || System.Array.IndexOf(invalid, c) >= 0) { sb.Append('_'); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>Write via a temp file + move so readers never see a half-written file.</summary>
        public static void WriteAtomic(string path, string contents)
        {
            EnsureDir(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
