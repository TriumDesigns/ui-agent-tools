// UIAgentTools - finds optional types (uGUI, TextMeshPro, UI Toolkit,
// EventSystems) by name across all loaded assemblies, so this package has
// no compile-time dependency on any of them and compiles in any project.

using System;
using System.Collections.Generic;

namespace UIAgentTools
{
    public static class TypeLookup
    {
        private static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>();

        public static Type Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            Type cached;
            if (Cache.TryGetValue(fullName, out cached)) return cached;

            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    found = asm.GetType(fullName, false);
                }
                catch
                {
                    found = null;
                }
                if (found != null) break;
            }
            Cache[fullName] = found;
            return found;
        }

        /// <summary>Resolve a component type from a short name ("Image") or full name ("UnityEngine.UI.Image").</summary>
        public static Type FindComponentType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var direct = Find(name);
            if (direct != null && typeof(UnityEngine.Component).IsAssignableFrom(direct)) return direct;

            Type match = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                if (types == null) continue;
                foreach (var t in types)
                {
                    if (t == null || t.Name != name) continue;
                    if (!typeof(UnityEngine.Component).IsAssignableFrom(t)) continue;
                    if (match != null && match != t)
                        throw new ArgumentException("Component type name '" + name + "' is ambiguous (" + match.FullName + " vs " + t.FullName + "). Use the full name.");
                    match = t;
                }
            }
            return match;
        }

        public static Type UIDocument { get { return Find("UnityEngine.UIElements.UIDocument"); } }
        public static Type Selectable { get { return Find("UnityEngine.UI.Selectable"); } }
        public static Type Graphic { get { return Find("UnityEngine.UI.Graphic"); } }
        public static Type CanvasScaler { get { return Find("UnityEngine.UI.CanvasScaler"); } }
        public static Type LayoutRebuilder { get { return Find("UnityEngine.UI.LayoutRebuilder"); } }
        public static Type LegacyText { get { return Find("UnityEngine.UI.Text"); } }
        public static Type BaseRaycaster { get { return Find("UnityEngine.EventSystems.BaseRaycaster"); } }
        public static Type EventSystem { get { return Find("UnityEngine.EventSystems.EventSystem"); } }
        public static Type TmpText { get { return Find("TMPro.TMP_Text"); } }
    }
}
