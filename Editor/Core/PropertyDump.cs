// UIAgentTools - readable dumps of serialized component data.
//
// Every line is printed with its exact SerializedProperty path (e.g.
// "m_Colors.m_NormalColor", "m_OnClick"), which is exactly what
// UIAgentEdit.Set(component, propertyPath, value) accepts. UnityEvents are
// printed as their persistent listeners (target -> method), which is how
// Button/Toggle/Slider wiring to your scripts becomes visible.

using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UIAgentTools
{
    public class DumpContext
    {
        public int MaxDepth = 4;
        public int MaxArrayElements = 16;
        public int MaxStringLength = 400;
        public int BrokenReferences;

        /// <summary>Called for every non-null object reference found (referencing component, property path, referenced object).</summary>
        public Action<UnityEngine.Object, string, UnityEngine.Object> OnReference;

        public UnityEngine.Object CurrentOwner;

        public static DumpContext FromConfig(UIAgentConfig cfg)
        {
            return new DumpContext
            {
                MaxDepth = cfg.maxFieldDepth,
                MaxArrayElements = cfg.maxArrayElements,
                MaxStringLength = cfg.maxStringLength,
            };
        }
    }

    public static class PropertyDump
    {
        private static readonly PropertyInfo InstanceIdProperty =
            typeof(SerializedProperty).GetProperty("objectReferenceInstanceIDValue", BindingFlags.Public | BindingFlags.Instance);

        private static readonly string[] CallStates = { "Off", "EditorAndRuntime", "RuntimeOnly" };

        public static void DumpObject(UnityEngine.Object target, StringBuilder sb, string indent, DumpContext ctx)
        {
            SerializedObject so;
            try
            {
                so = new SerializedObject(target);
            }
            catch (Exception e)
            {
                sb.Append(indent).Append("- (fields unreadable: ").Append(e.Message).AppendLine(")");
                return;
            }

            ctx.CurrentOwner = target;
            var it = so.GetIterator();
            if (!it.NextVisible(true)) return;
            do
            {
                if (it.name == "m_Script") continue;
                DumpProperty(it.Copy(), sb, indent, 0, ctx);
            }
            while (it.NextVisible(false));
        }

        private static void DumpProperty(SerializedProperty p, StringBuilder sb, string indent, int depth, DumpContext ctx)
        {
            if (p.propertyType == SerializedPropertyType.Generic && !p.isArray)
            {
                var calls = p.FindPropertyRelative("m_PersistentCalls.m_Calls");
                if (calls != null && calls.isArray)
                {
                    DumpUnityEvent(p, calls, sb, indent, ctx);
                    return;
                }
            }

            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                int n = p.arraySize;
                sb.Append(indent).Append("- ").Append(p.propertyPath).Append(": array[").Append(n).AppendLine("]");
                if (depth >= ctx.MaxDepth || n == 0) return;
                int shown = Math.Min(n, ctx.MaxArrayElements);
                for (int i = 0; i < shown; i++)
                    DumpProperty(p.GetArrayElementAtIndex(i), sb, indent + "  ", depth + 1, ctx);
                if (n > shown) sb.Append(indent).Append("  - ... ").Append(n - shown).AppendLine(" more element(s) not shown");
                return;
            }

            if (p.propertyType == SerializedPropertyType.Generic || p.propertyType == SerializedPropertyType.ManagedReference)
            {
                string label;
                if (p.propertyType == SerializedPropertyType.ManagedReference)
                    label = string.IsNullOrEmpty(p.managedReferenceFullTypename) ? "null [SerializeReference]" : p.managedReferenceFullTypename + " [SerializeReference]";
                else
                    label = p.type;
                sb.Append(indent).Append("- ").Append(p.propertyPath).Append(": {").Append(label).AppendLine("}");
                if (depth >= ctx.MaxDepth) return;

                var child = p.Copy();
                var end = p.GetEndProperty();
                if (!child.NextVisible(true)) return;
                while (!SerializedProperty.EqualContents(child, end))
                {
                    DumpProperty(child.Copy(), sb, indent + "  ", depth + 1, ctx);
                    if (!child.NextVisible(false)) break;
                }
                return;
            }

            sb.Append(indent).Append("- ").Append(p.propertyPath).Append(": ").AppendLine(FormatLeaf(p, ctx));
        }

        private static void DumpUnityEvent(SerializedProperty evt, SerializedProperty calls, StringBuilder sb, string indent, DumpContext ctx)
        {
            int n = calls.arraySize;
            sb.Append(indent).Append("- ").Append(evt.propertyPath).Append(": UnityEvent, ").Append(n).AppendLine(" persistent listener(s)");
            for (int i = 0; i < n; i++)
            {
                var call = calls.GetArrayElementAtIndex(i);
                var target = call.FindPropertyRelative("m_Target");
                var method = call.FindPropertyRelative("m_MethodName");
                var mode = call.FindPropertyRelative("m_Mode");
                var state = call.FindPropertyRelative("m_CallState");
                var typeName = call.FindPropertyRelative("m_TargetAssemblyTypeName");

                string targetText = target != null ? FormatReference(target, ctx) : "?";
                string methodText = method != null && !string.IsNullOrEmpty(method.stringValue) ? method.stringValue : "WARNING: no method";
                string arg = FormatListenerArgument(call, mode);
                string stateText = "?";
                if (state != null && state.intValue >= 0 && state.intValue < CallStates.Length) stateText = CallStates[state.intValue];

                sb.Append(indent).Append("  - [").Append(i).Append("] ").Append(targetText).Append(" -> ").Append(methodText)
                  .Append("(").Append(arg).Append(") [").Append(stateText).Append("]");
                if (typeName != null && !string.IsNullOrEmpty(typeName.stringValue))
                    sb.Append(" (target type: ").Append(ShortTypeName(typeName.stringValue)).Append(")");
                sb.AppendLine();
            }
        }

        private static string FormatListenerArgument(SerializedProperty call, SerializedProperty mode)
        {
            if (mode == null) return "";
            var args = call.FindPropertyRelative("m_Arguments");
            if (args == null) return "";
            // PersistentListenerMode: EventDefined=0, Void=1, Object=2, Int=3, Float=4, String=5, Bool=6
            switch (mode.intValue)
            {
                case 0: return "<dynamic>";
                case 1: return "";
                case 2:
                {
                    var o = args.FindPropertyRelative("m_ObjectArgument");
                    return o != null && o.objectReferenceValue != null ? o.objectReferenceValue.name : "null";
                }
                case 3: { var v = args.FindPropertyRelative("m_IntArgument"); return v != null ? v.intValue.ToString(CultureInfo.InvariantCulture) : ""; }
                case 4: { var v = args.FindPropertyRelative("m_FloatArgument"); return v != null ? v.floatValue.ToString("0.###", CultureInfo.InvariantCulture) : ""; }
                case 5: { var v = args.FindPropertyRelative("m_StringArgument"); return v != null ? Quote(v.stringValue, 120) : ""; }
                case 6: { var v = args.FindPropertyRelative("m_BoolArgument"); return v != null ? (v.boolValue ? "true" : "false") : ""; }
                default: return "";
            }
        }

        private static string ShortTypeName(string assemblyQualified)
        {
            int comma = assemblyQualified.IndexOf(',');
            return comma > 0 ? assemblyQualified.Substring(0, comma) : assemblyQualified;
        }

        public static string FormatLeaf(SerializedProperty p, DumpContext ctx)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (p.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    return FormatReference(p, ctx);
                case SerializedPropertyType.Integer:
                    return p.longValue.ToString(inv);
                case SerializedPropertyType.Boolean:
                    return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return p.type == "double" ? p.doubleValue.ToString("0.#####", inv) : p.floatValue.ToString("0.#####", inv);
                case SerializedPropertyType.String:
                    return Quote(p.stringValue, ctx.MaxStringLength);
                case SerializedPropertyType.Enum:
                {
                    int idx = p.enumValueIndex;
                    var names = p.enumNames;
                    if (idx >= 0 && names != null && idx < names.Length) return names[idx];
                    return p.intValue.ToString(inv) + " (flags/raw value)";
                }
                case SerializedPropertyType.Color:
                    return Fmt(p.colorValue);
                case SerializedPropertyType.Vector2:
                    return Fmt(p.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Fmt(p.vector3Value);
                case SerializedPropertyType.Vector4:
                {
                    var v = p.vector4Value;
                    return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ", " + F(v.w) + ")";
                }
                case SerializedPropertyType.Quaternion:
                    return "euler" + Fmt(p.quaternionValue.eulerAngles);
                case SerializedPropertyType.Rect:
                {
                    var r = p.rectValue;
                    return "(x " + F(r.x) + ", y " + F(r.y) + ", w " + F(r.width) + ", h " + F(r.height) + ")";
                }
                case SerializedPropertyType.Bounds:
                    return "center" + Fmt(p.boundsValue.center) + " size" + Fmt(p.boundsValue.size);
                case SerializedPropertyType.Vector2Int:
                    return "(" + p.vector2IntValue.x + ", " + p.vector2IntValue.y + ")";
                case SerializedPropertyType.Vector3Int:
                    return "(" + p.vector3IntValue.x + ", " + p.vector3IntValue.y + ", " + p.vector3IntValue.z + ")";
                case SerializedPropertyType.LayerMask:
                    return "mask " + p.intValue.ToString(inv);
                case SerializedPropertyType.ArraySize:
                    return p.intValue.ToString(inv);
                case SerializedPropertyType.Character:
                    return "'" + (char)p.intValue + "'";
                case SerializedPropertyType.AnimationCurve:
                    return "AnimationCurve (" + (p.animationCurveValue != null ? p.animationCurveValue.length : 0) + " keys)";
                default:
                    return "(" + p.propertyType + ")";
            }
        }

        public static string FormatReference(SerializedProperty p, DumpContext ctx)
        {
            var obj = p.objectReferenceValue;
            if (obj == null)
            {
                if (IsMissingReference(p))
                {
                    ctx.BrokenReferences++;
                    return "WARNING: MISSING REFERENCE (points at an object Unity can no longer find)";
                }
                return "none";
            }

            if (ctx.OnReference != null) ctx.OnReference(ctx.CurrentOwner, p.propertyPath, obj);
            return Describe(obj);
        }

        /// <summary>Human-readable description of a referenced object, including asset path / hierarchy address.</summary>
        public static string Describe(UnityEngine.Object obj)
        {
            if (obj == null) return "none";
            GameObject go = obj as GameObject;
            var comp = obj as Component;
            if (comp != null) go = comp.gameObject;
            string typeLabel = comp != null ? comp.GetType().Name : (go != null ? "GameObject" : obj.GetType().Name);

            if (go != null)
            {
                if (EditorUtility.IsPersistent(go))
                {
                    string assetPath = AssetDatabase.GetAssetPath(go);
                    bool isRoot = go.transform.parent == null;
                    return typeLabel + " in prefab asset `" + assetPath + "`" + (isRoot ? "" : " at `" + UIAddress.Of(go.transform) + "`");
                }
                return typeLabel + " on `" + UIAddress.Of(go.transform) + "`";
            }

            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return typeLabel + " `" + obj.name + "`";
            bool isSubAsset = AssetDatabase.IsSubAsset(obj);
            return typeLabel + " `" + obj.name + "` (`" + path + "`" + (isSubAsset ? ", sub-asset" : "") + ")";
        }

        /// <summary>
        /// True when an object-reference field is null only because the object it pointed to is gone
        /// (deleted asset, removed scene object). Uses reflection for the instance-ID check so this
        /// compiles on every Unity version, and falls back to the serialized fileID.
        /// </summary>
        public static bool IsMissingReference(SerializedProperty p)
        {
            if (p == null || p.propertyType != SerializedPropertyType.ObjectReference) return false;
            if (p.objectReferenceValue != null) return false;

            if (InstanceIdProperty != null)
            {
                try
                {
                    object v = InstanceIdProperty.GetValue(p, null);
                    if (v is int && (int)v != 0) return true;
                }
                catch
                {
                    // ignore - fall through to fileID check
                }
            }

            try
            {
                var fileId = p.FindPropertyRelative("m_FileID");
                if (fileId != null && fileId.propertyType == SerializedPropertyType.Integer && fileId.longValue != 0) return true;
            }
            catch
            {
                // ignore
            }
            return false;
        }

        public static string Quote(string s, int max)
        {
            if (s == null) return "\"\"";
            string t = s.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace("\"", "\\\"");
            if (t.Length > max) t = t.Substring(0, max) + "... (" + s.Length + " chars total)";
            return "\"" + t + "\"";
        }

        public static string F(float f) { return f.ToString("0.###", CultureInfo.InvariantCulture); }
        public static string Fmt(Vector2 v) { return "(" + F(v.x) + ", " + F(v.y) + ")"; }
        public static string Fmt(Vector3 v) { return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")"; }
        public static string Fmt(Color c) { return "rgba(" + F(c.r) + ", " + F(c.g) + ", " + F(c.b) + ", " + F(c.a) + ") #" + ColorUtility.ToHtmlStringRGBA(c); }
    }
}
