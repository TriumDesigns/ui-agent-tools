// UIAgentTools - hierarchy addresses.
//
// Every GameObject in a map file is labelled with an address such as
//   MainCanvas/Footer/Buttons/Button[1]
// The first segment is the root object (prefab root, or scene root object).
// "[k]" is appended only when several siblings share the same name, and k
// is the 0-based index among those same-named siblings. A literal "/" in an
// object name is written as "%2F". UIAgentEdit.Find resolves exactly the
// same format, so an address copied from a map file always works.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UIAgentTools
{
    public static class UIAddress
    {
        private static readonly Regex IndexedSegment = new Regex(@"^(.*)\[(\d+)\]$");

        public static string Of(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            var cur = t;
            while (cur != null)
            {
                parts.Add(Segment(cur));
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        public static string Segment(Transform t)
        {
            var siblings = Siblings(t);
            int total = 0;
            int mine = 0;
            foreach (var s in siblings)
            {
                if (s == null || s.name != t.name) continue;
                if (s == t) mine = total;
                total++;
            }
            string escaped = Escape(t.name);
            return total > 1 ? escaped + "[" + mine + "]" : escaped;
        }

        private static List<Transform> Siblings(Transform t)
        {
            var list = new List<Transform>();
            if (t.parent != null)
            {
                for (int i = 0; i < t.parent.childCount; i++) list.Add(t.parent.GetChild(i));
                return list;
            }
            var scene = t.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded)
            {
                foreach (var go in scene.GetRootGameObjects()) list.Add(go.transform);
            }
            else
            {
                list.Add(t);
            }
            return list;
        }

        public static string Escape(string name)
        {
            return (name ?? "").Replace("%", "%25").Replace("/", "%2F");
        }

        public static string Unescape(string segment)
        {
            return (segment ?? "").Replace("%2F", "/").Replace("%25", "%");
        }

        /// <summary>Resolve an address against a set of root objects. Throws a descriptive error if it can't.</summary>
        public static GameObject Resolve(IList<GameObject> roots, string address)
        {
            if (string.IsNullOrEmpty(address)) throw new ArgumentException("Address is empty.");
            var segments = address.Trim().Trim('/').Split('/');
            var level = new List<Transform>();
            foreach (var r in roots) if (r != null) level.Add(r.transform);

            Transform current = null;
            for (int i = 0; i < segments.Length; i++)
            {
                current = Pick(level, segments[i], address, i);
                level = new List<Transform>();
                for (int c = 0; c < current.childCount; c++) level.Add(current.GetChild(c));
            }
            return current.gameObject;
        }

        private static Transform Pick(List<Transform> candidates, string rawSegment, string fullAddress, int depth)
        {
            // 1) exact name match (handles names that literally contain "[n]")
            string exactName = Unescape(rawSegment);
            var exact = candidates.FindAll(c => c.name == exactName);
            if (exact.Count == 1) return exact[0];

            // 2) "Name[k]" = k-th among same-named siblings
            var m = IndexedSegment.Match(rawSegment);
            if (m.Success)
            {
                string name = Unescape(m.Groups[1].Value);
                int k = int.Parse(m.Groups[2].Value);
                var same = candidates.FindAll(c => c.name == name);
                if (k < same.Count) return same[k];
            }

            if (exact.Count > 1)
                throw new ArgumentException("Address '" + fullAddress + "' is ambiguous at segment " + (depth + 1) + " ('" + rawSegment + "'): " +
                                            exact.Count + " siblings share that name. Use '" + rawSegment + "[0]'..'[" + (exact.Count - 1) + "]' as shown in the map file.");

            var sb = new StringBuilder();
            sb.Append("Address '").Append(fullAddress).Append("' not found: no object named '").Append(rawSegment)
              .Append("' at segment ").Append(depth + 1).Append(". Objects available at that level: ");
            int shown = 0;
            foreach (var c in candidates)
            {
                if (shown++ > 40) { sb.Append(", ..."); break; }
                if (shown > 1) sb.Append(", ");
                sb.Append('\'').Append(Segment(c)).Append('\'');
            }
            if (candidates.Count == 0) sb.Append("(none - the parent has no children)");
            sb.Append(". Regenerate the map if the hierarchy changed.");
            throw new ArgumentException(sb.ToString());
        }
    }
}
