using System;
using System.IO;
using UnityEngine;

namespace MoonBaseAlpha.Terrain
{
    /// <summary>terrain.json written by Tools/terrain/build_terrain.py.</summary>
    [Serializable]
    public class TerrainMeta
    {
        [Serializable] public class Site { public string name; public double lat, lon, originHeight, R0, referenceRadius; }
        [Serializable] public class Grid { public string file; public int n; public double spacing, heightOffset, heightScale; public string maskFile; }
        [Serializable] public class Pit { public float[] center; public float rimHeight, surroundHeight, rilleAzimuthDown, rilleAzimuthUp; }

        public int version;
        public Site site;
        public Grid near, far;
        public Pit pit;

        public static string Folder => Path.Combine(Application.streamingAssetsPath, "Terrain");

        public static TerrainMeta Load()
        {
            var path = Path.Combine(Folder, "terrain.json");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "Terrain data missing. Run: python Tools/terrain/build_terrain.py (from the project root).", path);
            return JsonUtility.FromJson<TerrainMeta>(File.ReadAllText(path));
        }
    }
}
