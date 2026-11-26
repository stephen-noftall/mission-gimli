using System.Collections.Generic;
using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Round 12 (P5): one message log for radio lines, prompts and warnings (the helmet HUD's log panel draws it).
    /// A warning that stays true (EDGE AHEAD) is added once per approach: <see cref="AddOnce"/> with a key, cleared by
    /// <see cref="Clear(string)"/> when the condition ends.
    /// </summary>
    public static class MessageLog
    {
        public enum Kind { Houston, Crew, Warning, Prompt, System }
        public struct Entry { public string who, text; public Kind kind; public float time; }

        public const int Cap = 60;
        static readonly List<Entry> entries = new();
        static readonly HashSet<string> active = new();
        public static IReadOnlyList<Entry> Entries => entries;
        /// <summary>Bumped on every add (the panel can skip work when nothing changed).</summary>
        public static int Version { get; private set; }

        public static void Add(string text, Kind kind, string who = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            // round 13 (T1): a prompt right after a prompt replaces it, so hints never push the crew's banter off the panel
            if (kind == Kind.Prompt && entries.Count > 0 && entries[entries.Count - 1].kind == Kind.Prompt) entries.RemoveAt(entries.Count - 1);
            entries.Add(new Entry { who = who, text = text, kind = kind, time = Time.unscaledTime });
            while (entries.Count > Cap) entries.RemoveAt(0);
            Version++;
        }

        /// <summary>Add once while <paramref name="key"/> stays active (e.g. "edge"): repeated calls do nothing.</summary>
        public static bool AddOnce(string key, string text, Kind kind)
        {
            if (!active.Add(key)) return false;
            Add(text, kind);
            return true;
        }

        /// <summary>The condition behind <paramref name="key"/> ended: the next <see cref="AddOnce"/> logs again.</summary>
        public static void Clear(string key) => active.Remove(key);
        public static bool IsActive(string key) => active.Contains(key);

        public static void Reset() { entries.Clear(); active.Clear(); Version++; }
    }
}
