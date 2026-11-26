using System;
using System.IO;
using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// The world seed. Everything procedural (tunnel network, small craters / boulders, discovery
    /// placement) derives from it, so the same seed always produces the same Moon underground.
    /// Sources, in priority order: command line "-seed N", settings file, default.
    /// The real terrain data (LRO) never changes with the seed.
    /// </summary>
    public static class WorldSeed
    {
        public const uint Default = 20300614;
        static uint? seed;

        public static string SettingsPath => Path.Combine(Application.persistentDataPath, "settings.json");

        [Serializable] class Settings { public uint seed = Default; }

        public static uint Value
        {
            get
            {
                if (seed.HasValue) return seed.Value;
                seed = FromCommandLine() ?? FromFile() ?? Default;
                return seed.Value;
            }
        }

        /// <summary>Change the seed (persisted). The world must be rebuilt (scene reload) to apply.</summary>
        public static void Set(uint value, bool persist = true)
        {
            seed = value;
            if (!persist) return;
            try { File.WriteAllText(SettingsPath, JsonUtility.ToJson(new Settings { seed = value })); }
            catch (Exception e) { Debug.LogWarning($"[WorldSeed] could not save settings: {e.Message}"); }
        }

        static uint? FromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-seed" && uint.TryParse(args[i + 1], out var v)) return v;
            return null;
        }

        static uint? FromFile()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                return JsonUtility.FromJson<Settings>(File.ReadAllText(SettingsPath)).seed;
            }
            catch { return null; }
        }

        /// <summary>Derive an independent sub-seed for a named system.</summary>
        public static uint For(string system)
        {
            uint h = Value ^ 2166136261u;
            foreach (char c in system) { h ^= c; h *= 16777619u; }
            return h == 0 ? 1u : h;
        }
    }
}
