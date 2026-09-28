// UIAgentTools - opening scenes without disturbing the user's Editor.
//
// Rules:
//  * Batch mode: scenes are opened in Single mode (there is nothing to preserve).
//  * Editor open: a scene that is already loaded is used in place; any other
//    scene is opened additively and closed again afterwards (without saving).
//  * Nothing in this package ever saves a scene except UIAgentEdit.EditScene,
//    and that refuses to touch a scene with unsaved user changes.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UIAgentTools
{
    public sealed class SceneHandle : IDisposable
    {
        public Scene Scene { get; private set; }
        public string Path { get; private set; }

        /// <summary>True when the scene was already open in the Editor before we touched it.</summary>
        public bool WasAlreadyOpen { get; private set; }

        /// <summary>True when the scene had unsaved changes in the Editor at the time we opened it.</summary>
        public bool WasDirty { get; private set; }

        private readonly bool _closeOnDispose;
        private readonly bool _wasPresentButUnloaded;
        private bool _disposed;

        internal SceneHandle(Scene scene, string path, bool wasAlreadyOpen, bool wasDirty, bool closeOnDispose, bool wasPresentButUnloaded)
        {
            Scene = scene;
            Path = path;
            WasAlreadyOpen = wasAlreadyOpen;
            WasDirty = wasDirty;
            _closeOnDispose = closeOnDispose;
            _wasPresentButUnloaded = wasPresentButUnloaded;
        }

        internal void MarkDisposed() { _disposed = true; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_closeOnDispose) return;
            if (!Scene.IsValid() || !Scene.isLoaded) return;
            if (SceneManager.sceneCount <= 1) return; // never close the last scene
            EditorSceneManager.CloseScene(Scene, !_wasPresentButUnloaded);
        }
    }

    public static class SceneAccess
    {
        public static SceneHandle Open(string scenePath, bool forEdit)
        {
            if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Not a scene path: '" + scenePath + "'");
            if (!System.IO.File.Exists(UIAgentPaths.Absolute(scenePath)))
                throw new ArgumentException("Scene not found: '" + scenePath + "'");

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("The Editor is in Play Mode. Exit Play Mode and run again.");

            var existing = SceneManager.GetSceneByPath(scenePath);
            if (existing.IsValid() && existing.isLoaded)
            {
                // In batch mode there is no interactive user who could have "unsaved changes" -
                // a dirty flag here only ever means an earlier step of this same batch run (e.g.
                // editing a prefab that this scene holds an instance of) touched it. Refusing would
                // block legitimate multi-step edit scripts; EditScene saves whatever is currently
                // in memory, so nothing is lost.
                if (forEdit && existing.isDirty && !Application.isBatchMode)
                    throw new InvalidOperationException("Scene '" + scenePath + "' is open in the Editor with unsaved changes. " +
                                                        "Save or discard those changes in Unity first, then run again.");
                return new SceneHandle(existing, scenePath, true, existing.isDirty, false, false);
            }

            if (Application.isBatchMode)
            {
                var single = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                return new SceneHandle(single, scenePath, false, false, false, false);
            }

            bool presentUnloaded = existing.IsValid() && !existing.isLoaded;
            Scene opened;
            try
            {
                opened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Could not open '" + scenePath + "' additively (" + e.Message + "). " +
                                                    "If your current scene is Untitled, save it first, then run again.", e);
            }
            return new SceneHandle(opened, scenePath, false, false, true, presentUnloaded);
        }

        /// <summary>Throw away in-memory changes to a scene handle's scene (used when an edit fails validation).</summary>
        public static void DiscardChanges(SceneHandle handle)
        {
            if (handle == null || !handle.Scene.IsValid()) return;
            string path = handle.Path;

            if (Application.isBatchMode)
            {
                // Reload from disk so later commands in this process see the saved state.
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                handle.MarkDisposed();
                return;
            }

            if (!handle.WasAlreadyOpen)
            {
                if (SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(handle.Scene, true);
                handle.MarkDisposed();
                return;
            }

            ReloadFromDisk(path);
            handle.MarkDisposed();
        }

        /// <summary>Reload a scene that is open in the Editor from its file on disk (discarding in-memory state).</summary>
        public static void ReloadFromDisk(string scenePath)
        {
            var s = SceneManager.GetSceneByPath(scenePath);
            if (!s.IsValid() || !s.isLoaded) return;

            if (SceneManager.sceneCount == 1)
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                return;
            }
            bool wasActive = SceneManager.GetActiveScene() == s;
            EditorSceneManager.CloseScene(s, true);
            var reopened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            if (wasActive) SceneManager.SetActiveScene(reopened);
        }

        /// <summary>
        /// Run an action that needs to open scenes in Single mode and modify them in memory
        /// (screenshots). In the Editor, requires all open scenes to be saved, and restores the
        /// user's exact scene setup afterwards. Nothing is ever saved.
        /// </summary>
        public static void WithIsolatedScenes(Action action)
        {
            if (Application.isBatchMode)
            {
                action();
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("The Editor is in Play Mode. Exit Play Mode and run again.");

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.isDirty)
                    throw new InvalidOperationException("Scene '" + (string.IsNullOrEmpty(s.path) ? s.name : s.path) +
                                                        "' has unsaved changes. Save it in Unity first (scene screenshots temporarily swap the open scenes).");
            }

            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                action();
            }
            finally
            {
                // Discard whatever the action left in memory, then restore the user's scenes.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var restorable = new List<SceneSetup>();
                foreach (var s in setup) if (s != null && !string.IsNullOrEmpty(s.path)) restorable.Add(s);
                if (restorable.Count > 0)
                {
                    bool anyActive = false;
                    foreach (var s in restorable) if (s.isActive) anyActive = true;
                    if (!anyActive) restorable[0].isActive = true;
                    bool anyLoaded = false;
                    foreach (var s in restorable) if (s.isLoaded) anyLoaded = true;
                    if (!anyLoaded) restorable[0].isLoaded = true;
                    EditorSceneManager.RestoreSceneManagerSetup(restorable.ToArray());
                }
            }
        }
    }
}
