// UIAgentTools - one implementation of every command, shared by batch mode
// (Cli.cs, Unity closed) and the request bridge (RequestWatcher.cs, Unity open).
//
// Commands:
//   map [asset]          regenerate the whole UI map, or one prefab/scene
//   validate [asset]     validate everything (or one asset); fails on NEW errors
//   baseline             accept all current errors as known (user-approved only!)
//   screenshots [asset]  capture screenshots of everything (or one asset)
//   run <Type.Method>    run an edit script (public static void Method()), then
//                        re-map + validate every asset it saved
//   compile              report whether the project's scripts compile
//   selftest             end-to-end check of this package in this project/Unity version
//   install              (re)install project files: wrapper scripts, config, AGENTS.md block
//   all                  map + validate + screenshots

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UIAgentTools
{
    [Serializable]
    public class CommandResult
    {
        public string id;
        public string command;
        public string target;
        public bool ok;
        public int exitCode;
        public string summary;
        public string finishedAtUtc;
        public List<string> messages = new List<string>();
    }

    public static class CommandRunner
    {
        public static readonly string[] Commands = { "map", "validate", "baseline", "screenshots", "run", "compile", "selftest", "install", "all" };

        public static CommandResult Execute(string command, string target)
        {
            var result = new CommandResult { command = command, target = target ?? "" };
            var log = new List<string>();
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Log && !message.StartsWith("[UIAgentTools]")) return;
                string prefix = type == LogType.Log ? "" : type.ToString().ToUpperInvariant() + ": ";
                if (log.Count < 400) log.Add(prefix + Truncate(message, 2000));
            };
            Application.logMessageReceived += capture;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("The Editor is in Play Mode. Exit Play Mode and run again.");

                string summary;
                bool ok = Dispatch(command, target, out summary);
                result.ok = ok;
                result.exitCode = ok ? 0 : 1;
                result.summary = summary;
            }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                result.ok = false;
                result.exitCode = 1;
                result.summary = e.GetType().Name + ": " + e.Message;
                log.Add("EXCEPTION: " + e);
            }
            finally
            {
                Application.logMessageReceived -= capture;
                UIMapper.ClearProgress();
            }
            result.messages = log;
            result.finishedAtUtc = DateTime.UtcNow.ToString("o");
            return result;
        }

        private static bool Dispatch(string command, string target, out string summary)
        {
            bool hasTarget = !string.IsNullOrEmpty(target);
            if (command != "compile" && command != "install" && CompileTracker.HasErrors)
            {
                summary = "The project has script compile errors, so Unity is running stale code. Fix them first:\n" + CompileTracker.Describe();
                return false;
            }

            switch (command)
            {
                case "map":
                {
                    var s = hasTarget ? UIMapper.MapTarget(target) : UIMapper.MapAll();
                    summary = s.ToString() + " See UIAgentMap/INDEX.md.";
                    return s.Failures.Count == 0;
                }
                case "validate":
                {
                    var cfg = UIAgentConfig.Load();
                    var targets = hasTarget ? new List<string> { UIMapper.NormalizeAssetPath(target) } : UIMapper.FindAllTargets(cfg);
                    var report = UIValidator.Run(targets);
                    UIValidator.WriteReport(report);
                    summary = "Validated " + report.targetsChecked + " asset(s): " + report.newErrors + " NEW error(s), " + report.baselinedErrors +
                              " baselined error(s), " + report.warnings + " warning(s)" + (report.failures.Count > 0 ? ", " + report.failures.Count + " asset(s) could not be checked" : "") +
                              ". See UIAgentMap/validation-report.md.";
                    return report.newErrors == 0 && report.failures.Count == 0;
                }
                case "baseline":
                {
                    var cfg = UIAgentConfig.Load();
                    var report = UIValidator.Run(UIMapper.FindAllTargets(cfg));
                    int n = UIValidator.WriteBaseline(report);
                    report = UIValidator.Run(UIMapper.FindAllTargets(cfg));
                    UIValidator.WriteReport(report);
                    summary = "Baseline written with " + n + " accepted error(s) (UIAgentMap/validation-baseline.json). Only new errors will fail from now on.";
                    return report.failures.Count == 0;
                }
                case "screenshots":
                {
                    var s = hasTarget ? ScreenshotHarness.CaptureTarget(target) : ScreenshotHarness.CaptureAll();
                    summary = s.ToString() + " See UIAgentMap/screenshots/manifest.md.";
                    return s.Failures.Count == 0;
                }
                case "run":
                    return RunEditScript(target, out summary);
                case "compile":
                    summary = CompileTracker.HasErrors ? "Compile errors:\n" + CompileTracker.Describe() : "Scripts compile cleanly.";
                    return !CompileTracker.HasErrors;
                case "selftest":
                    return SelfTest.Run(out summary);
                case "install":
                    summary = ProjectInstaller.Install(false);
                    return true;
                case "all":
                {
                    var m = UIMapper.MapAll();
                    var report = UIValidator.Run(UIMapper.FindAllTargets(UIAgentConfig.Load()));
                    UIValidator.WriteReport(report);
                    var s = ScreenshotHarness.CaptureAll();
                    summary = m + "\nValidation: " + report.newErrors + " new error(s), " + report.warnings + " warning(s).\n" + s;
                    return m.Failures.Count == 0 && report.newErrors == 0 && report.failures.Count == 0 && s.Failures.Count == 0;
                }
                default:
                    summary = "Unknown command '" + command + "'. Valid: " + string.Join(", ", Commands);
                    return false;
            }
        }

        private static bool RunEditScript(string methodPath, out string summary)
        {
            if (string.IsNullOrEmpty(methodPath))
            {
                summary = "run needs a method, e.g. AgentEdits.Edit_LoginTitle.Run";
                return false;
            }
            var method = ResolveMethod(methodPath);
            if (method == null)
            {
                summary = "Could not find public static void " + methodPath + "(). Check the namespace/class/method names, and that the script is a public static method of a class inside Assets/Editor/AgentEdits/ (the AgentEdits assembly - `run` refuses anything else) and compiled.";
                return false;
            }

            UIAgentEdit.ClearTouched();
            try
            {
                method.Invoke(null, null);
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                summary = "Edit script failed: " + inner.Message +
                          (UIAgentEdit.TouchedAssets.Count > 0 ? "\nAssets already saved by earlier steps of this script: " + string.Join(", ", UIAgentEdit.TouchedAssets.ToArray()) : "\nNothing was saved.");
                Debug.LogError("[UIAgentTools] " + inner);
                RemapTouched();
                return false;
            }

            if (Application.isBatchMode) AssetDatabase.SaveAssets();
            var touched = UIAgentEdit.TouchedAssets.ToList();
            if (touched.Count == 0)
            {
                summary = "Edit script ran but saved nothing (did it call UIAgentEdit.EditPrefab / EditScene?).";
                return true;
            }

            RemapTouched();
            var report = UIValidator.Run(touched);
            UIValidator.WriteReport(report);
            summary = "Saved " + touched.Count + " asset(s): " + string.Join(", ", touched.ToArray()) + ". Re-mapped them. Validation: " +
                      report.newErrors + " new error(s), " + report.warnings + " warning(s). Next: `ui-agent screenshots <asset>` and look at the result.";
            return report.newErrors == 0;
        }

        private static void RemapTouched()
        {
            foreach (var a in UIAgentEdit.TouchedAssets.ToList())
            {
                try { UIMapper.MapTarget(a); }
                catch (Exception e) { Debug.LogWarning("[UIAgentTools] Could not re-map " + a + ": " + e.Message); }
            }
        }

        public static MethodInfo ResolveMethod(string methodPath)
        {
            int dot = methodPath.LastIndexOf('.');
            if (dot <= 0 || dot == methodPath.Length - 1) return null;
            string typeName = methodPath.Substring(0, dot);
            string methodName = methodPath.Substring(dot + 1);
            var type = TypeLookupFresh(typeName);
            if (type == null) return null;
            var mi = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return mi;
        }

        // Only the AgentEdits assembly (Assets/Editor/AgentEdits/AgentEdits.asmdef) may be run, so `run` (and the request bridge)
        // can never invoke arbitrary static methods from Unity, the project or its packages.
        // Edit scripts are new types; don't use TypeLookup's cache (it may have cached a miss before compilation).
        private static Type TypeLookupFresh(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "AgentEdits") continue;
                try { return asm.GetType(fullName, false); } catch { }
            }
            return null;
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    /// <summary>Tracks script compile errors so commands can refuse to run on stale code.</summary>
    [InitializeOnLoad]
    public static class CompileTracker
    {
        private static readonly List<string> Errors = new List<string>();

        static CompileTracker()
        {
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
        }

        private static void OnStarted(object context) { Errors.Clear(); }

        private static void OnAssemblyFinished(string assembly, CompilerMessage[] messages)
        {
            foreach (var m in messages)
                if (m.type == CompilerMessageType.Error && Errors.Count < 50)
                    Errors.Add(m.file + "(" + m.line + "): " + m.message);
        }

        public static bool HasErrors
        {
            get { return EditorUtility.scriptCompilationFailed || Errors.Count > 0; }
        }

        public static string Describe()
        {
            if (Errors.Count == 0) return "(Unity reports compile errors - see the Console or the log file for details.)";
            return string.Join("\n", Errors.ToArray());
        }
    }
}
