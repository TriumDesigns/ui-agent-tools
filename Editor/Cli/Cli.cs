// UIAgentTools - batch-mode entry points (Unity closed). Normally invoked by
// the ui-agent wrapper script rather than by hand:
//
//   Unity -batchmode -nographics -quit -projectPath <p> -logFile <log> \
//         -executeMethod UIAgentTools.Cli.Map [-agentTarget Assets/UI/Login.prefab]
//
// Screenshots/selftest/all need a GPU: omit -nographics for those.
// Each method writes UIAgentMap/last-result.json and exits with 0 (ok) or 1 (failed).

using System;
using UnityEditor;
using UnityEngine;

namespace UIAgentTools
{
    public static class Cli
    {
        public static void Map() { RunBatch("map"); }
        public static void Validate() { RunBatch("validate"); }
        public static void Baseline() { RunBatch("baseline"); }
        public static void Screenshots() { RunBatch("screenshots"); }
        public static void Run() { RunBatch("run"); }
        public static void Compile() { RunBatch("compile"); }
        public static void SelfTestAll() { RunBatch("selftest"); }
        public static void Install() { RunBatch("install"); }
        public static void All() { RunBatch("all"); }

        private static void RunBatch(string command)
        {
            string target = Arg("-agentTarget");
            string id = Arg("-agentRequestId") ?? ("batch-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            var result = CommandRunner.Execute(command, target);
            result.id = id;
            try
            {
                UIAgentPaths.WriteAtomic(UIAgentPaths.LastResultPath, JsonUtility.ToJson(result, true));
            }
            catch (Exception e)
            {
                Debug.LogError("[UIAgentTools] Could not write last-result.json: " + e.Message);
            }
            Debug.Log("[UIAgentTools] RESULT " + (result.ok ? "OK" : "FAILED") + ": " + result.summary);
            if (Application.isBatchMode) EditorApplication.Exit(result.exitCode);
        }

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
