// UIAgentTools - the ONLY way an AI coding agent should change prefabs and scenes.
//
// Every edit goes through Unity's own serialization (never text-editing
// YAML), and is transactional:
//   1. back up the file (+ .meta) to UIAgentMap/backups/<timestamp>/
//   2. load it (prefab: isolated prefab contents; scene: open without
//      disturbing the user's other scenes), validate  -> "before"
//   3. run the edit callback
//   4. validate again -> "after"; if any error type increased, the edit is
//      discarded and nothing is written
//   5. save through Unity, then re-load from disk and validate again; if
//      that fails, the backup is restored
//
// Typical use from an edit script in Assets/Editor/AgentEdits/:
//
//   UIAgentEdit.EditPrefab("Assets/UI/LoginPanel.prefab", root => {
//       var title = UIAgentEdit.Find(root, "LoginPanel/Header/Title");
//       UIAgentEdit.SetText(title, "Welcome back");
//   });

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UIAgentTools
{
    public class UIAgentEditException : Exception
    {
        public UIAgentEditException(string message) : base(message) { }
        public UIAgentEditException(string message, Exception inner) : base(message, inner) { }
    }

    public static class UIAgentEdit
    {
        private static readonly List<string> Touched = new List<string>();

        /// <summary>Assets saved by UIAgentEdit since the last ClearTouched() (used by `ui-agent run` to re-map them).</summary>
        public static IList<string> TouchedAssets { get { return Touched.AsReadOnly(); } }

        public static void ClearTouched() { Touched.Clear(); }

        // ================================================================ transactions

        public static void EditPrefab(string prefabPath, Action<GameObject> edit)
        {
            if (edit == null) throw new ArgumentNullException("edit");
            prefabPath = RequireAsset(prefabPath, ".prefab");
            RequireNotPlaying();

            if (!Application.isBatchMode)
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && string.Equals(stage.assetPath, prefabPath, StringComparison.OrdinalIgnoreCase) && stage.scene.isDirty)
                    throw new UIAgentEditException("'" + prefabPath + "' is open in Prefab Mode with unsaved changes. Save or discard them in Unity first.");
            }

            string backup = Backups.Create(prefabPath);
            List<ValidationIssue> before;
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                before = UIValidator.ValidateRoots(prefabPath, new[] { root }, false);
                try
                {
                    edit(root);
                }
                catch (Exception e)
                {
                    throw new UIAgentEditException("Edit of '" + prefabPath + "' threw (nothing was saved): " + Unwrap(e).Message, Unwrap(e));
                }
                var after = UIValidator.ValidateRoots(prefabPath, new[] { root }, false);
                UIValidator.EnsureNoNewErrors(before, after, prefabPath);

                bool success;
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out success);
                if (!success) throw new UIAgentEditException("Unity reported that saving '" + prefabPath + "' failed (nothing was changed on disk).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            VerifyOrRestore(prefabPath, backup, before);
            MarkTouched(prefabPath);
            Debug.Log("[UIAgentTools] Saved prefab '" + prefabPath + "' (backup: " + backup + ")");
        }

        public static void EditScene(string scenePath, Action<Scene> edit)
        {
            if (edit == null) throw new ArgumentNullException("edit");
            scenePath = RequireAsset(scenePath, ".unity");
            RequireNotPlaying();

            string backup = Backups.Create(scenePath);
            List<ValidationIssue> before;
            using (var handle = SceneAccess.Open(scenePath, true))
            {
                bool saved = false;
                try
                {
                    before = UIValidator.ValidateRoots(scenePath, handle.Scene.GetRootGameObjects(), true);
                    try
                    {
                        edit(handle.Scene);
                    }
                    catch (Exception e)
                    {
                        throw new UIAgentEditException("Edit of '" + scenePath + "' threw (nothing was saved): " + Unwrap(e).Message, Unwrap(e));
                    }
                    var after = UIValidator.ValidateRoots(scenePath, handle.Scene.GetRootGameObjects(), true);
                    UIValidator.EnsureNoNewErrors(before, after, scenePath);

                    EditorSceneManager.MarkSceneDirty(handle.Scene);
                    if (!EditorSceneManager.SaveScene(handle.Scene))
                        throw new UIAgentEditException("Unity reported that saving '" + scenePath + "' failed.");
                    saved = true;
                }
                finally
                {
                    if (!saved) SceneAccess.DiscardChanges(handle);
                }
            }

            VerifyOrRestore(scenePath, backup, before);
            MarkTouched(scenePath);
            Debug.Log("[UIAgentTools] Saved scene '" + scenePath + "' (backup: " + backup + ")");
        }

        private static void VerifyOrRestore(string assetPath, string backup, List<ValidationIssue> before)
        {
            try
            {
                string abs = UIAgentPaths.Absolute(assetPath);
                var info = new FileInfo(abs);
                if (!info.Exists || info.Length == 0) throw new UIAgentEditException("Saved file is missing or empty.");
                if (EditorSettings.serializationMode == SerializationMode.ForceText)
                {
                    using (var reader = new StreamReader(abs))
                    {
                        string first = reader.ReadLine() ?? "";
                        if (!first.StartsWith("%YAML")) throw new UIAgentEditException("Saved file does not start with a YAML header.");
                    }
                }
                if (!File.Exists(abs + ".meta")) throw new UIAgentEditException("Saved asset has no .meta file.");

                // In batch mode the saved scene is still loaded; reload it from disk so we verify the file, not memory.
                if (Application.isBatchMode && assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Single);
                var reloaded = UIValidator.ValidateAsset(assetPath);
                UIValidator.EnsureNoNewErrors(before, reloaded, assetPath);
            }
            catch (Exception e)
            {
                Backups.Restore(backup, assetPath);
                throw new UIAgentEditException("Post-save verification of '" + assetPath + "' failed, the original file was restored from backup: " + e.Message, e);
            }
        }

        // ================================================================ finding things

        public static GameObject Find(GameObject prefabRoot, string address)
        {
            if (prefabRoot == null) throw new ArgumentNullException("prefabRoot");
            return UIAddress.Resolve(new[] { prefabRoot }, address);
        }

        public static GameObject Find(Scene scene, string address)
        {
            return UIAddress.Resolve(scene.GetRootGameObjects(), address);
        }

        public static T Get<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c == null) throw new UIAgentEditException("'" + UIAddress.Of(go.transform) + "' has no " + typeof(T).Name + ". It has: " + ComponentList(go));
            return c;
        }

        /// <summary>Get a component by type name ("Image", "TextMeshProUGUI", "MyNamespace.LoginController").</summary>
        public static Component Get(GameObject go, string typeName)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t.Name == typeName || t.FullName == typeName) return c;
            }
            var resolved = TypeLookup.FindComponentType(typeName);
            if (resolved != null)
            {
                var c = go.GetComponent(resolved);
                if (c != null) return c;
            }
            throw new UIAgentEditException("'" + UIAddress.Of(go.transform) + "' has no component '" + typeName + "'. It has: " + ComponentList(go));
        }

        private static string ComponentList(GameObject go)
        {
            return string.Join(", ", go.GetComponents<Component>().Select(c => c == null ? "<missing script>" : c.GetType().Name).ToArray());
        }

        // ================================================================ changing values

        /// <summary>
        /// Set a serialized field by its property path (exactly as printed in the map files) and verify
        /// it took. Records undo and prefab-instance overrides correctly. Supported values: bool, int,
        /// long, float, double, string, Color (or "#RRGGBB[AA]"), Vector2/3/4, Rect, enum (name or int),
        /// UnityEngine.Object or an asset path string ("Assets/Art/icon.png" or "Assets/Art/atlas.png::iconName"
        /// for sub-assets such as sprites), and null for object fields. Array sizes: "field.Array.size".
        /// </summary>
        public static void Set(Object target, string propertyPath, object value)
        {
            if (target == null) throw new ArgumentNullException("target");
            var so = new SerializedObject(target);
            var p = so.FindProperty(propertyPath);
            if (p == null) throw new UIAgentEditException("'" + target.GetType().Name + "' has no serialized property '" + propertyPath + "'. " + SuggestProperties(so, propertyPath));

            Assign(p, value, target, propertyPath);
            so.ApplyModifiedProperties();
            MarkChanged(target);

            // verify
            so.Update();
            var check = so.FindProperty(propertyPath);
            if (!Matches(check, value))
                throw new UIAgentEditException("Setting " + target.GetType().Name + "." + propertyPath + " did not take effect (Unity rejected the value - wrong type?). Current value: " + PropertyDump.FormatLeaf(check, new DumpContext()));
        }

        private static void Assign(SerializedProperty p, object value, Object target, string path)
        {
            var inv = CultureInfo.InvariantCulture;
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.ArraySize:
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.Character:
                        p.longValue = Convert.ToInt64(value, inv);
                        return;
                    case SerializedPropertyType.Boolean:
                        p.boolValue = Convert.ToBoolean(value, inv);
                        return;
                    case SerializedPropertyType.Float:
                        if (p.type == "double") p.doubleValue = Convert.ToDouble(value, inv);
                        else p.floatValue = Convert.ToSingle(value, inv);
                        return;
                    case SerializedPropertyType.String:
                        p.stringValue = value == null ? "" : Convert.ToString(value, inv);
                        return;
                    case SerializedPropertyType.Color:
                        p.colorValue = ToColor(value);
                        return;
                    case SerializedPropertyType.Vector2:
                        p.vector2Value = (Vector2)value;
                        return;
                    case SerializedPropertyType.Vector3:
                        p.vector3Value = (Vector3)value;
                        return;
                    case SerializedPropertyType.Vector4:
                        p.vector4Value = (Vector4)value;
                        return;
                    case SerializedPropertyType.Rect:
                        p.rectValue = (Rect)value;
                        return;
                    case SerializedPropertyType.Enum:
                        AssignEnum(p, value);
                        return;
                    case SerializedPropertyType.ObjectReference:
                        p.objectReferenceValue = ToObject(value, p);
                        return;
                    default:
                        throw new UIAgentEditException("Property type " + p.propertyType + " can't be set with Set(); set its child fields instead (see the map file for their paths).");
                }
            }
            catch (InvalidCastException)
            {
                throw new UIAgentEditException("Wrong value type for " + target.GetType().Name + "." + path + " (" + p.propertyType + "): got " + (value == null ? "null" : value.GetType().Name) + ".");
            }
            catch (FormatException)
            {
                throw new UIAgentEditException("Value '" + value + "' can't be converted for " + target.GetType().Name + "." + path + " (" + p.propertyType + ").");
            }
        }

        private static void AssignEnum(SerializedProperty p, object value)
        {
            var s = value as string;
            if (s != null)
            {
                var names = p.enumNames;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], s, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.enumDisplayNames[i], s, StringComparison.OrdinalIgnoreCase))
                    {
                        p.enumValueIndex = i;
                        return;
                    }
                }
                throw new UIAgentEditException("'" + s + "' is not a value of this enum. Valid: " + string.Join(", ", names));
            }
            if (value is Enum) { p.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture); return; }
            p.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static Color ToColor(object value)
        {
            if (value is Color) return (Color)value;
            if (value is Color32) return (Color32)value;
            var s = value as string;
            Color c;
            if (s != null && ColorUtility.TryParseHtmlString(s.StartsWith("#") ? s : "#" + s, out c)) return c;
            throw new InvalidCastException();
        }

        private static Object ToObject(object value, SerializedProperty p)
        {
            if (value == null) return null;
            var obj = value as Object;
            if (obj != null) return obj;
            var s = value as string;
            if (s == null) throw new InvalidCastException();

            string path = s;
            string subName = null;
            int sep = s.IndexOf("::", StringComparison.Ordinal);
            if (sep >= 0) { path = s.Substring(0, sep); subName = s.Substring(sep + 2); }
            path = UIMapper.NormalizeAssetPath(path);

            string wanted = ExpectedTypeName(p);
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null) throw new UIAgentEditException("No asset at '" + path + "'.");

            if (subName == null)
            {
                if (wanted == null || IsTypeNamed(main.GetType(), wanted)) return main;
                var mainGo = main as GameObject;
                if (mainGo != null)
                {
                    foreach (var comp in mainGo.GetComponents<Component>())
                        if (comp != null && IsTypeNamed(comp.GetType(), wanted)) return comp;
                }
            }

            var all = AssetDatabase.LoadAllAssetsAtPath(path);
            foreach (var a in all)
            {
                if (a == null) continue;
                if (subName != null && a.name != subName) continue;
                if (wanted == null || IsTypeNamed(a.GetType(), wanted)) return a;
            }
            throw new UIAgentEditException("Asset '" + s + "' has nothing of type '" + (wanted ?? "any") + "'" + (subName != null ? " named '" + subName + "'" : "") +
                                            ". Contents: " + string.Join(", ", all.Where(a => a != null).Select(a => a.GetType().Name + " '" + a.name + "'").ToArray()));
        }

        private static string ExpectedTypeName(SerializedProperty p)
        {
            // p.type looks like "PPtr<$Sprite>" for script-declared fields or "PPtr<Sprite>" for built-ins
            string t = p.type;
            if (string.IsNullOrEmpty(t) || !t.StartsWith("PPtr<")) return null;
            t = t.Substring(5).TrimEnd('>').TrimStart('$');
            return string.IsNullOrEmpty(t) || t == "Object" ? null : t;
        }

        private static bool IsTypeNamed(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType) if (t.Name == name) return true;
            return false;
        }

        private static bool Matches(SerializedProperty p, object value)
        {
            if (p == null) return false;
            var inv = CultureInfo.InvariantCulture;
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.ArraySize:
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.Character:
                        return p.longValue == Convert.ToInt64(value, inv);
                    case SerializedPropertyType.Boolean: return p.boolValue == Convert.ToBoolean(value, inv);
                    case SerializedPropertyType.Float:
                        return p.type == "double"
                            ? Math.Abs(p.doubleValue - Convert.ToDouble(value, inv)) < 1e-9
                            : Mathf.Approximately(p.floatValue, Convert.ToSingle(value, inv));
                    case SerializedPropertyType.String: return p.stringValue == (value == null ? "" : Convert.ToString(value, inv));
                    case SerializedPropertyType.ObjectReference: return value == null ? p.objectReferenceValue == null : p.objectReferenceValue != null;
                    default: return true; // structs/enums/colors: Unity either takes them or throws
                }
            }
            catch
            {
                return false;
            }
        }

        private static string SuggestProperties(SerializedObject so, string wanted)
        {
            var names = new List<string>();
            var it = so.GetIterator();
            if (it.NextVisible(true))
            {
                do { if (it.name != "m_Script") names.Add(it.propertyPath); }
                while (it.NextVisible(false) && names.Count < 80);
            }
            string lower = wanted.ToLowerInvariant().Replace("m_", "");
            var close = names.Where(n => n.ToLowerInvariant().Replace("m_", "").Contains(lower) || lower.Contains(n.ToLowerInvariant().Replace("m_", ""))).ToList();
            return (close.Count > 0 ? "Did you mean: " + string.Join(", ", close.ToArray()) + "? " : "") + "Top-level properties: " + string.Join(", ", names.ToArray());
        }

        /// <summary>Set the text of a TextMeshPro or legacy uGUI Text on this object.</summary>
        public static void SetText(GameObject go, string text)
        {
            var tmp = TypeLookup.TmpText != null ? go.GetComponent(TypeLookup.TmpText) : null;
            if (tmp != null) { Set(tmp, "m_text", text); return; }
            var legacy = TypeLookup.LegacyText != null ? go.GetComponent(TypeLookup.LegacyText) : null;
            if (legacy != null) { Set(legacy, "m_Text", text); return; }
            throw new UIAgentEditException("'" + UIAddress.Of(go.transform) + "' has no TextMeshPro or Text component. It has: " + ComponentList(go));
        }

        public static void SetActive(GameObject go, bool active)
        {
            Undo.RecordObject(go, "UI Agent edit");
            go.SetActive(active);
            MarkChanged(go);
        }

        public static void SetName(GameObject go, string newName)
        {
            if (string.IsNullOrEmpty(newName)) throw new ArgumentException("Name can't be empty.");
            Undo.RecordObject(go, "UI Agent edit");
            go.name = newName;
            MarkChanged(go);
        }

        /// <summary>Change any subset of a RectTransform's layout values (null = leave unchanged).</summary>
        public static void SetRect(RectTransform rt, Vector2? anchorMin = null, Vector2? anchorMax = null, Vector2? pivot = null,
                                   Vector2? anchoredPosition = null, Vector2? sizeDelta = null)
        {
            if (rt == null) throw new ArgumentNullException("rt");
            Undo.RecordObject(rt, "UI Agent edit");
            if (anchorMin.HasValue) rt.anchorMin = anchorMin.Value;
            if (anchorMax.HasValue) rt.anchorMax = anchorMax.Value;
            if (pivot.HasValue) rt.pivot = pivot.Value;
            if (anchoredPosition.HasValue) rt.anchoredPosition = anchoredPosition.Value;
            if (sizeDelta.HasValue) rt.sizeDelta = sizeDelta.Value;
            MarkChanged(rt);
        }

        public static void SetSiblingIndex(GameObject go, int index)
        {
            Undo.RegisterFullObjectHierarchyUndo(go.transform.parent != null ? go.transform.parent.gameObject : go, "UI Agent edit");
            go.transform.SetSiblingIndex(index);
            MarkChanged(go.transform);
        }

        // ================================================================ structure

        /// <summary>Create a child object (with a RectTransform if the parent is UI).</summary>
        public static GameObject CreateChild(GameObject parent, string name, int siblingIndex = -1)
        {
            if (parent == null) throw new ArgumentNullException("parent");
            GameObject go = parent.GetComponent<RectTransform>() != null ? new GameObject(name, typeof(RectTransform)) : new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.layer = parent.layer;
            if (siblingIndex >= 0) go.transform.SetSiblingIndex(siblingIndex);
            Undo.RegisterCreatedObjectUndo(go, "UI Agent edit");
            MarkChanged(go);
            return go;
        }

        /// <summary>Add a component by type (e.g. AddComponent(go, "UnityEngine.UI.Image") or AddComponent(go, "Image")).</summary>
        public static Component AddComponent(GameObject go, string typeName)
        {
            var type = TypeLookup.FindComponentType(typeName);
            if (type == null) throw new UIAgentEditException("Unknown component type '" + typeName + "'.");
            return AddComponent(go, type);
        }

        public static Component AddComponent(GameObject go, Type type)
        {
            var c = Undo.AddComponent(go, type);
            if (c == null) throw new UIAgentEditException("Unity refused to add " + type.Name + " to '" + UIAddress.Of(go.transform) + "' (duplicate of a disallowed component, or a missing [RequireComponent] dependency?).");
            MarkChanged(go);
            return c;
        }

        public static void RemoveComponent(Component c)
        {
            if (c == null) throw new ArgumentNullException("c");
            if (PrefabUtility.IsPartOfPrefabInstance(c) && !PrefabUtility.IsAddedComponentOverride(c))
                throw new UIAgentEditException("Component " + c.GetType().Name + " on '" + UIAddress.Of(c.transform) + "' comes from a prefab instance. Remove it in the prefab asset instead (or disable it here).");
            Undo.DestroyObjectImmediate(c);
        }

        /// <summary>Instantiate a prefab under a parent, keeping the prefab link (never use Object.Instantiate for this).</summary>
        public static GameObject InstantiatePrefab(string prefabPath, GameObject parent, int siblingIndex = -1)
        {
            prefabPath = RequireAsset(prefabPath, ".prefab");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset == null) throw new UIAgentEditException("Could not load prefab '" + prefabPath + "'.");
            var inst = PrefabUtility.InstantiatePrefab(asset, parent.transform) as GameObject;
            if (inst == null) throw new UIAgentEditException("Unity refused to instantiate '" + prefabPath + "' here (a prefab can't contain itself).");
            if (siblingIndex >= 0) inst.transform.SetSiblingIndex(siblingIndex);
            Undo.RegisterCreatedObjectUndo(inst, "UI Agent edit");
            MarkChanged(inst);
            return inst;
        }

        /// <summary>Delete an object. Refuses objects that belong to a prefab instance (edit the prefab asset or deactivate instead).</summary>
        public static void Delete(GameObject go)
        {
            if (go == null) throw new ArgumentNullException("go");
            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go) && !PrefabUtility.IsAddedGameObjectOverride(go))
                throw new UIAgentEditException("'" + UIAddress.Of(go.transform) + "' is part of a prefab instance. Delete it in the prefab asset, or SetActive(false) it here.");
            Undo.DestroyObjectImmediate(go);
        }

        public static void SetParent(GameObject go, GameObject newParent, bool keepWorldPosition = false)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go) && !PrefabUtility.IsAddedGameObjectOverride(go))
                throw new UIAgentEditException("'" + UIAddress.Of(go.transform) + "' is inside a prefab instance and can't be re-parented here. Do it in the prefab asset.");
            if (newParent == null) throw new ArgumentNullException("newParent");
            if (keepWorldPosition) Undo.SetTransformParent(go.transform, newParent.transform, "UI Agent edit");
            else go.transform.SetParent(newParent.transform, false);
            MarkChanged(go.transform);
        }

        // ================================================================ events

        /// <summary>Add a persistent (inspector-visible) listener, e.g. AddListener(button, "onClick", controller, "OnLoginPressed").</summary>
        public static void AddListener(Component source, string eventMember, Object target, string methodName)
        {
            var evt = GetEvent(source, eventMember) as UnityEvent;
            if (evt == null) throw new UIAgentEditException(source.GetType().Name + "." + eventMember + " is not a parameterless UnityEvent. Use the overloads with an argument, or Set() the m_PersistentCalls fields.");
            var mi = FindMethod(target, methodName, Type.EmptyTypes);
            var action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), target, mi);
            Undo.RecordObject(source, "UI Agent edit");
            UnityEventTools.AddPersistentListener(evt, action);
            MarkChanged(source);
        }

        public static void AddListener(Component source, string eventMember, Object target, string methodName, string argument)
        {
            var evt = RequireEventBase(source, eventMember);
            var mi = FindMethod(target, methodName, new[] { typeof(string) });
            Undo.RecordObject(source, "UI Agent edit");
            UnityEventTools.AddStringPersistentListener(evt, (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), target, mi), argument);
            MarkChanged(source);
        }

        public static void AddListener(Component source, string eventMember, Object target, string methodName, int argument)
        {
            var evt = RequireEventBase(source, eventMember);
            var mi = FindMethod(target, methodName, new[] { typeof(int) });
            Undo.RecordObject(source, "UI Agent edit");
            UnityEventTools.AddIntPersistentListener(evt, (UnityAction<int>)Delegate.CreateDelegate(typeof(UnityAction<int>), target, mi), argument);
            MarkChanged(source);
        }

        public static void AddListener(Component source, string eventMember, Object target, string methodName, float argument)
        {
            var evt = RequireEventBase(source, eventMember);
            var mi = FindMethod(target, methodName, new[] { typeof(float) });
            Undo.RecordObject(source, "UI Agent edit");
            UnityEventTools.AddFloatPersistentListener(evt, (UnityAction<float>)Delegate.CreateDelegate(typeof(UnityAction<float>), target, mi), argument);
            MarkChanged(source);
        }

        public static void AddListener(Component source, string eventMember, Object target, string methodName, bool argument)
        {
            var evt = RequireEventBase(source, eventMember);
            var mi = FindMethod(target, methodName, new[] { typeof(bool) });
            Undo.RecordObject(source, "UI Agent edit");
            UnityEventTools.AddBoolPersistentListener(evt, (UnityAction<bool>)Delegate.CreateDelegate(typeof(UnityAction<bool>), target, mi), argument);
            MarkChanged(source);
        }

        /// <summary>Remove every persistent listener on an event whose target and method match.</summary>
        public static int RemoveListener(Component source, string eventMember, Object target, string methodName)
        {
            var evt = RequireEventBase(source, eventMember);
            int removed = 0;
            Undo.RecordObject(source, "UI Agent edit");
            for (int i = evt.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                if (evt.GetPersistentTarget(i) == target && evt.GetPersistentMethodName(i) == methodName)
                {
                    UnityEventTools.RemovePersistentListener(evt, i);
                    removed++;
                }
            }
            MarkChanged(source);
            return removed;
        }

        private static UnityEventBase RequireEventBase(Component source, string member)
        {
            var evt = GetEvent(source, member);
            if (evt == null) throw new UIAgentEditException(source.GetType().Name + " has no UnityEvent named '" + member + "'.");
            return evt;
        }

        private static UnityEventBase GetEvent(Component source, string member)
        {
            if (source == null) throw new ArgumentNullException("source");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            for (var t = source.GetType(); t != null; t = t.BaseType)
            {
                var prop = t.GetProperty(member, flags | BindingFlags.DeclaredOnly);
                if (prop != null && typeof(UnityEventBase).IsAssignableFrom(prop.PropertyType) && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(source, null) as UnityEventBase;
                var field = t.GetField(member, flags | BindingFlags.DeclaredOnly);
                if (field != null && typeof(UnityEventBase).IsAssignableFrom(field.FieldType))
                    return field.GetValue(source) as UnityEventBase;
            }
            return null;
        }

        private static MethodInfo FindMethod(Object target, string name, Type[] args)
        {
            if (target == null) throw new ArgumentNullException("target");
            var mi = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, args, null);
            if (mi == null || mi.ReturnType != typeof(void))
                throw new UIAgentEditException(target.GetType().Name + " has no public void " + name + "(" + string.Join(", ", args.Select(a => a.Name).ToArray()) + ") method.");
            return mi;
        }

        // ================================================================ helpers

        /// <summary>Call after changing a component or GameObject directly (not via Set) so Unity records it.</summary>
        public static void MarkChanged(Object o)
        {
            if (o == null) return;
            EditorUtility.SetDirty(o);
            if (PrefabUtility.IsPartOfPrefabInstance(o)) PrefabUtility.RecordPrefabInstancePropertyModifications(o);
        }

        private static void MarkTouched(string assetPath)
        {
            if (!Touched.Contains(assetPath)) Touched.Add(assetPath);
        }

        private static string RequireAsset(string path, string extension)
        {
            path = UIMapper.NormalizeAssetPath(path);
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                throw new UIAgentEditException("'" + path + "' is not a " + extension + " asset.");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new UIAgentEditException("'" + path + "' is outside Assets/. Package and read-only assets can't be edited.");
            if (!File.Exists(UIAgentPaths.Absolute(path)))
                throw new UIAgentEditException("'" + path + "' does not exist.");
            return path;
        }

        private static void RequireNotPlaying()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new UIAgentEditException("The Editor is in Play Mode. Exit Play Mode and run again.");
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }
    }

    internal static class Backups
    {
        public static string Create(string assetPath)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string dir = UIAgentPaths.BackupsDir + "/" + stamp;
            string dest = dir + "/" + assetPath;
            UIAgentPaths.EnsureDir(Path.GetDirectoryName(dest));
            string src = UIAgentPaths.Absolute(assetPath);
            File.Copy(src, dest, true);
            if (File.Exists(src + ".meta")) File.Copy(src + ".meta", dest + ".meta", true);
            Prune();
            return dest;
        }

        public static void Restore(string backupFile, string assetPath)
        {
            string dest = UIAgentPaths.Absolute(assetPath);
            File.Copy(backupFile, dest, true);
            if (File.Exists(backupFile + ".meta")) File.Copy(backupFile + ".meta", dest + ".meta", true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && !Application.isBatchMode)
                SceneAccess.ReloadFromDisk(assetPath);
            Debug.LogWarning("[UIAgentTools] Restored '" + assetPath + "' from backup " + backupFile);
        }

        private static void Prune()
        {
            try
            {
                if (!Directory.Exists(UIAgentPaths.BackupsDir)) return;
                int keep = UIAgentConfig.Load().keepBackups;
                var dirs = Directory.GetDirectories(UIAgentPaths.BackupsDir).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
                for (int i = keep; i < dirs.Count; i++) Directory.Delete(dirs[i], true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UIAgentTools] Could not prune backups: " + e.Message);
            }
        }
    }
}
