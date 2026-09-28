// UIAgentTools - lets the ui-agent wrapper drive an Editor that is already open.
//
// Unity refuses to start a second (batch-mode) instance on a project that is
// open in the Editor. So when the wrapper sees the project is open, it drops a
// request file into UIAgentMap/requests/ and this watcher, running inside the
// open Editor, picks it up:
//
//   <id>.request.json    written by the wrapper   {"id","command","target"}
//   <id>.processing      written here on pickup (so the wrapper knows the Editor is alive)
//   <id>.result.json     written here when done (CommandResult)
//
// Before each request the watcher refreshes the AssetDatabase so files the agent
// changed on disk (edit scripts, C# code) are imported and compiled first. The
// Editor must not be in Play Mode. If Unity is in the background it may poll
// more slowly; clicking the Unity window speeds it up.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UIAgentTools
{
    [Serializable]
    internal class CommandRequest
    {
        public string id = "";
        public string command = "";
        public string target = "";
    }

    [InitializeOnLoad]
    internal static class RequestWatcher
    {
        private const double PollSeconds = 0.5;
        private const double CompileWaitSeconds = 180;
        private static double _nextPoll;
        private static bool _busy;

        static RequestWatcher()
        {
            if (Application.isBatchMode) return;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (_busy) return;
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollSeconds;

            string dir = UIAgentPaths.RequestsDir;
            if (!Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.request.json"); }
            catch { return; }
            if (files.Length == 0) return;
            Array.Sort(files, StringComparer.Ordinal);
            string file = files[0];
            string id = Path.GetFileName(file).Replace(".request.json", "");
            string processing = dir + "/" + id + ".processing";
            string resultPath = dir + "/" + id + ".result.json";

            CommandRequest req;
            try
            {
                req = JsonUtility.FromJson<CommandRequest>(File.ReadAllText(file));
                if (req == null || string.IsNullOrEmpty(req.command)) throw new Exception("empty request");
            }
            catch (Exception e)
            {
                WriteResult(resultPath, new CommandResult { id = id, ok = false, exitCode = 1, summary = "Unreadable request file: " + e.Message });
                TryDelete(file);
                return;
            }

            if (!File.Exists(processing))
            {
                try { File.WriteAllText(processing, DateTime.UtcNow.ToString("o")); } catch { }
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Finish(file, processing, resultPath, new CommandResult { id = id, command = req.command, ok = false, exitCode = 1, summary = "The Editor is in Play Mode. Exit Play Mode and run again." });
                return;
            }

            // Import anything changed on disk (edit scripts, code) and wait for compilation to finish.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            AssetDatabase.Refresh();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            // For `run`, the edit script may still be waiting to compile right after a refresh.
            if (req.command == "run" && CommandRunner.ResolveMethod(req.target ?? "") == null && !CompileTracker.HasErrors)
            {
                double waited = SecondsSince(processing);
                if (waited < CompileWaitSeconds) return; // keep waiting; request stays pending
            }

            _busy = true;
            try
            {
                var result = CommandRunner.Execute(req.command, req.target);
                result.id = id;
                Finish(file, processing, resultPath, result);
                try { UIAgentPaths.WriteAtomic(UIAgentPaths.LastResultPath, JsonUtility.ToJson(result, true)); } catch { }
            }
            finally
            {
                _busy = false;
            }
        }

        private static double SecondsSince(string markerFile)
        {
            try
            {
                DateTime started;
                if (DateTime.TryParse(File.ReadAllText(markerFile), null, System.Globalization.DateTimeStyles.RoundtripKind, out started))
                    return (DateTime.UtcNow - started.ToUniversalTime()).TotalSeconds;
            }
            catch { }
            return 0;
        }

        private static void Finish(string requestFile, string processing, string resultPath, CommandResult result)
        {
            WriteResult(resultPath, result);
            TryDelete(requestFile);
            TryDelete(processing);
        }

        private static void WriteResult(string path, CommandResult result)
        {
            try { UIAgentPaths.WriteAtomic(path, JsonUtility.ToJson(result, true)); }
            catch (Exception e) { Debug.LogError("[UIAgentTools] Could not write " + path + ": " + e.Message); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
