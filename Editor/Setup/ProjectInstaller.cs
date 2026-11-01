// UIAgentTools - one-click project setup (menu: UI Agent Tools > Install / Update Project Files).
//
// Copies into the project root:
//   ui-agent.sh / ui-agent.ps1 / ui-agent.cmd   wrapper your coding agent runs for every command
//   UIAgentMap/config.json                        (only if missing)
//   AGENTS.md                                      adds/updates a marked block (your own text is kept)
//   CLAUDE.md / GEMINI.md                          one-line `@AGENTS.md` import so Claude Code and Gemini-based agents read it too
//   .gitignore                                     adds/updates a marked block
//   Assets/Editor/AgentEdits/                     folder for your agent's edit scripts
// Safe to run again after updating the package: it only refreshes its own files/blocks.

using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UIAgentTools
{
    public static class ProjectInstaller
    {
        private const string BlockStart = "<!-- ui-agent-tools:start -->";
        private const string BlockEnd = "<!-- ui-agent-tools:end -->";
        private const string GitStart = "# ui-agent-tools:start";
        private const string GitEnd = "# ui-agent-tools:end";

        [MenuItem("UI Agent Tools/Install or Update Project Files", false, 0)]
        private static void MenuInstall()
        {
            string report = Install(true);
            EditorUtility.DisplayDialog("UI Agent Tools", report, "OK");
        }

        public static string Install(bool interactive)
        {
            string pkg = PackageRoot();
            if (pkg == null) throw new InvalidOperationException("Could not locate the com.triumdesigns.ui-agent-tools package folder.");
            string root = UIAgentPaths.ProjectRoot;
            var log = new StringBuilder();

            // 1. wrapper scripts (always refreshed)
            foreach (var name in new[] { "ui-agent.sh", "ui-agent.ps1", "ui-agent.cmd" })
            {
                string src = pkg + "/Tools~/" + name;
                if (!File.Exists(src)) throw new FileNotFoundException("Package file missing: " + src);
                string text = File.ReadAllText(src).Replace("\r\n", "\n");
                if (name.EndsWith(".cmd") || name.EndsWith(".ps1")) text = text.Replace("\n", "\r\n");
                File.WriteAllText(root + "/" + name, text, new UTF8Encoding(false));
                log.AppendLine("Updated " + name);
            }
            MakeExecutable(root + "/ui-agent.sh");

            // 2. config
            UIAgentPaths.EnsureDir(UIAgentPaths.MapRoot);
            if (!File.Exists(UIAgentPaths.ConfigPath))
            {
                UIAgentPaths.WriteAtomic(UIAgentPaths.ConfigPath, UIAgentConfig.DefaultJson());
                log.AppendLine("Created UIAgentMap/config.json (edit it to match your target devices and folders)");
            }
            else log.AppendLine("Kept existing UIAgentMap/config.json");

            // 3. AGENTS.md block
            string snippet = File.ReadAllText(pkg + "/Templates~/AGENTS.md.snippet").Replace("\r\n", "\n").Trim();
            string block = BlockStart + "\n" + snippet + "\n" + BlockEnd;
            string agentsMd = root + "/AGENTS.md";
            string existing = File.Exists(agentsMd) ? File.ReadAllText(agentsMd).Replace("\r\n", "\n") : "";
            string updated = ReplaceBlock(existing, BlockStart, BlockEnd, block,
                "# " + Application.productName + "\n\n<!-- Project-specific notes for your AI coding agent go here (platforms, XR rig, conventions, off-limits folders). -->\n\n");
            File.WriteAllText(agentsMd, updated, new UTF8Encoding(false));
            log.AppendLine(existing.Length == 0 ? "Created AGENTS.md" : "Updated the UI Agent Tools block in AGENTS.md (your own text was kept)");

            // 3b. Claude Code reads CLAUDE.md and Gemini CLI/Antigravity read GEMINI.md; both can import AGENTS.md. Codex reads AGENTS.md directly.
            foreach (var name in new[] { "CLAUDE.md", "GEMINI.md" })
            {
                string path = root + "/" + name;
                string text = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
                bool ours = text.Contains(BlockStart);
                if (!ours && text.Contains("@AGENTS.md")) { log.AppendLine(name + " already imports AGENTS.md"); continue; }
                File.WriteAllText(path, ReplaceBlock(text, BlockStart, BlockEnd, BlockStart + "\n@AGENTS.md\n" + BlockEnd, ""), new UTF8Encoding(false));
                log.AppendLine((text.Length == 0 ? "Created " : "Updated the UI Agent Tools block in ") + name + " (imports AGENTS.md)");
            }

            // 4. .gitignore block
            string gitBlock = GitStart + "\nUIAgentMap/backups/\nUIAgentMap/requests/\nUIAgentMap/logs/\nUIAgentMap/screenshots/\nUIAgentMap/last-result.json\nUIAgentMap/*.tmp\n" + GitEnd;
            string gitignore = root + "/.gitignore";
            string gitText = File.Exists(gitignore) ? File.ReadAllText(gitignore).Replace("\r\n", "\n") : "";
            File.WriteAllText(gitignore, ReplaceBlock(gitText, GitStart, GitEnd, gitBlock, ""), new UTF8Encoding(false));
            log.AppendLine("Updated .gitignore (maps, config, report and baseline stay committed; backups/screenshots/logs are ignored)");

            // 5. edit-script folder
            if (!AssetDatabase.IsValidFolder("Assets/Editor")) AssetDatabase.CreateFolder("Assets", "Editor");
            if (!AssetDatabase.IsValidFolder("Assets/Editor/AgentEdits")) AssetDatabase.CreateFolder("Assets/Editor", "AgentEdits");
            // Own assembly: a mistake in an edit script can never break the project's other Editor code,
            // and it works even when Assets/Editor already belongs to a project asmdef.
            string asmdefPath = root + "/Assets/Editor/AgentEdits/AgentEdits.asmdef";
            if (!File.Exists(asmdefPath))
            {
                File.WriteAllText(asmdefPath,
                    "{\n    \"name\": \"AgentEdits\",\n    \"references\": [\"UIAgentTools.Editor\"],\n    \"includePlatforms\": [\"Editor\"],\n" +
                    "    \"excludePlatforms\": [],\n    \"allowUnsafeCode\": false,\n    \"overrideReferences\": false,\n    \"autoReferenced\": false,\n" +
                    "    \"defineConstraints\": [],\n    \"noEngineReferences\": false\n}\n", new UTF8Encoding(false));
                AssetDatabase.ImportAsset("Assets/Editor/AgentEdits/AgentEdits.asmdef", ImportAssetOptions.Default);
            }
            log.AppendLine("Edit scripts folder: Assets/Editor/AgentEdits/ (own assembly: AgentEdits.asmdef)");

            // 6. project settings advice
            if (EditorSettings.serializationMode != SerializationMode.ForceText)
                log.AppendLine("\nIMPORTANT: Asset Serialization is " + EditorSettings.serializationMode +
                               ". Set Project Settings > Editor > Asset Serialization > Mode = Force Text (commit first - it rewrites assets).");

            log.AppendLine("\nNext: run `./ui-agent.sh selftest` (or menu UI Agent Tools > Run Self Test), then `./ui-agent.sh map`.");
            return log.ToString();
        }

        private static string ReplaceBlock(string text, string start, string end, string block, string headerIfNew)
        {
            int s = text.IndexOf(start, StringComparison.Ordinal);
            int e = text.IndexOf(end, StringComparison.Ordinal);
            if (s >= 0 && e > s) return text.Substring(0, s) + block + text.Substring(e + end.Length);
            if (text.Length == 0) return headerIfNew + block + "\n";
            return text.TrimEnd('\n') + "\n\n" + block + "\n";
        }

        private static void MakeExecutable(string path)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) return;
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/chmod", "+x \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true });
                if (p != null) p.WaitForExit(5000);
            }
            catch
            {
                // Not fatal: `bash ui-agent.sh ...` works without the executable bit.
            }
        }

        public static string PackageRoot()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProjectInstaller).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath)) return info.resolvedPath.Replace('\\', '/');

            // Fallback: package copied somewhere under Assets/
            foreach (var guid in AssetDatabase.FindAssets("ProjectInstaller t:MonoScript"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!p.EndsWith("/Editor/Setup/ProjectInstaller.cs")) continue;
                string dir = Path.GetDirectoryName(UIAgentPaths.Absolute(p)).Replace('\\', '/');
                for (int i = 0; i < 4 && dir != null; i++)
                {
                    if (File.Exists(dir + "/package.json")) return dir;
                    dir = Path.GetDirectoryName(dir);
                    if (dir != null) dir = dir.Replace('\\', '/');
                }
            }
            return null;
        }
    }

    internal static class UIAgentMenus
    {
        [MenuItem("UI Agent Tools/Map All UI", false, 20)]
        private static void MapAll() { Show(CommandRunner.Execute("map", null)); }

        [MenuItem("UI Agent Tools/Validate All UI", false, 21)]
        private static void ValidateAll() { Show(CommandRunner.Execute("validate", null)); }

        [MenuItem("UI Agent Tools/Capture All Screenshots", false, 22)]
        private static void ScreenshotsAll() { Show(CommandRunner.Execute("screenshots", null)); }

        [MenuItem("Assets/UI Agent Tools/Map + Validate + Screenshot This Asset", false, 2000)]
        private static void ThisAsset()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            var sb = new StringBuilder();
            foreach (var cmd in new[] { "map", "validate", "screenshots" })
            {
                var r = CommandRunner.Execute(cmd, path);
                sb.AppendLine(cmd + ": " + (r.ok ? "ok" : "FAILED") + " - " + r.summary);
            }
            EditorUtility.DisplayDialog("UI Agent Tools", sb.ToString(), "OK");
        }

        [MenuItem("Assets/UI Agent Tools/Map + Validate + Screenshot This Asset", true)]
        private static bool ThisAssetValidate()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        }

        [MenuItem("UI Agent Tools/Open Map Folder", false, 40)]
        private static void OpenFolder()
        {
            UIAgentPaths.EnsureDir(UIAgentPaths.MapRoot);
            EditorUtility.RevealInFinder(UIAgentPaths.IndexMdPath.Length > 0 && File.Exists(UIAgentPaths.IndexMdPath) ? UIAgentPaths.IndexMdPath : UIAgentPaths.MapRoot);
        }

        private static void Show(CommandResult r)
        {
            Debug.Log("[UIAgentTools] " + r.command + ": " + (r.ok ? "ok" : "FAILED") + " - " + r.summary);
            EditorUtility.DisplayDialog("UI Agent Tools", (r.ok ? "OK\n\n" : "FAILED\n\n") + r.summary, "OK");
        }
    }
}
