using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Buggy;
using MoonBaseAlpha.Terrain;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>
    /// Wheel tracks: ribbons a few cm above the ground behind each rear wheel, drawn with a multiply decal
    /// (<c>MoonBaseAlpha/TrackDecal</c>: slightly darker, chevron tread). Only on soft ground (regolith, talus);
    /// none on bare tube basalt. The live track is a few long merged meshes (256 points each, rebuilt only for the
    /// chunk being laid down); the last ~5 km are kept. Pre-made tracks (cargo lander -> crane pad, GIMLI-1's
    /// radar traverses) come from <see cref="BuildPath"/>. Real: with no wind or rain, tracks on the Moon last for
    /// millions of years (Apollo LRV tracks are still visible from orbit).
    /// </summary>
    public class TyreTracks : MonoBehaviour
    {
        public const float Spacing = 0.4f;          // m between track points
        public const float Width = 0.28f;           // MR-4 tyre width
        public const float KeepMetres = 5000f;      // per wheel
        public const int ChunkPoints = 256;
        public const float Lift = 0.03f;

        static Material mat;
        public static Material Mat
        {
            get
            {
                if (mat == null)
                {
                    var sh = Shader.Find("MoonBaseAlpha/TrackDecal");
                    mat = sh != null ? new Material(sh) { name = "TrackDecal" } : new Material(Shader.Find("Hidden/Internal-Colored"));
                }
                return mat;
            }
        }

        /// <summary>CPU cost of the live update (ms, smoothed), for the perf report.</summary>
        public static float AvgMs { get; private set; }
        /// <summary>Metres of live track laid (all wheels).</summary>
        public float LaidMetres { get; private set; }
        public int ChunkCount => chunks.Count + (strips == null ? 0 : strips.Length);

        public BuggyController rover;
        Transform root;

        class Strip
        {
            public readonly List<Vector3> pts = new(), ups = new();
            public float length;
            public Mesh mesh; public GameObject go;
        }
        Strip[] strips;
        int[] wheelIdx;
        readonly List<(Strip s, int wheel)> chunks = new();
        readonly float[] kept = new float[8];
        readonly System.Diagnostics.Stopwatch sw = new();

        public static TyreTracks Attach(BuggyController rover)
        {
            var go = new GameObject("Tyre tracks (live)");
            var t = go.AddComponent<TyreTracks>();
            t.rover = rover; t.root = go.transform;
            return t;
        }

        void Awake() { root = transform; }

        void Init()
        {
            var list = new List<int>();
            for (int i = 0; i < rover.wheels.Length; i++) if (!rover.wheels[i].front) list.Add(i);
            wheelIdx = list.ToArray();
            strips = new Strip[wheelIdx.Length];
        }

        void LateUpdate() { if (rover != null) Sample(); }

        /// <summary>Lay track points under the rear wheels (called every frame; tests call it after stepping).</summary>
        public void Sample()
        {
            sw.Restart();
            if (strips == null) Init();
            for (int k = 0; k < wheelIdx.Length; k++)
            {
                var w = rover.wheels[wheelIdx[k]];
                bool soft = w.grounded && !w.soil.rigid;
                if (!soft) { End(k); continue; }
                var p = root.InverseTransformPoint(w.contactPoint);
                var s = strips[k];
                if (s == null)
                {
                    s = strips[k] = NewStrip();
                    s.pts.Add(p); s.ups.Add(w.contactNormal);
                    continue;
                }
                var last = s.pts[s.pts.Count - 1];
                float d = Vector3.Distance(p, last);
                if (d > 3f) { End(k); strips[k] = NewStrip(); strips[k].pts.Add(p); strips[k].ups.Add(w.contactNormal); continue; }   // teleport / jump
                if (d < Spacing) continue;
                s.pts.Add(p); s.ups.Add(w.contactNormal); s.length += d; LaidMetres += d;
                Rebuild(s, true);
                if (s.pts.Count >= ChunkPoints)
                {
                    End(k);
                    var n = strips[k] = NewStrip();
                    n.pts.Add(p); n.ups.Add(w.contactNormal);   // continue from the shared point
                }
            }
            sw.Stop();
            AvgMs = Mathf.Lerp(AvgMs, (float)sw.Elapsed.TotalMilliseconds, 0.05f);
        }

        Strip NewStrip()
        {
            var s = new Strip();
            s.go = new GameObject("TrackChunk");
            s.go.transform.SetParent(root, false);
            s.mesh = new Mesh { name = "Track" };
            s.mesh.MarkDynamic();
            s.go.AddComponent<MeshFilter>().sharedMesh = s.mesh;
            var r = s.go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            return s;
        }

        void End(int k)
        {
            var s = strips[k];
            if (s == null) return;
            strips[k] = null;
            if (s.pts.Count < 2) { Destroy(s); return; }
            Rebuild(s, false);
            chunks.Add((s, k));
            kept[k] += s.length;
            // forget the oldest chunks beyond ~5 km per wheel
            while (kept[k] > KeepMetres)
            {
                int i = chunks.FindIndex(c => c.wheel == k);
                if (i < 0) break;
                kept[k] -= chunks[i].s.length;
                Destroy(chunks[i].s);
                chunks.RemoveAt(i);
            }
        }

        static void Destroy(Strip s) { if (s.go != null) Util.Kill(s.go); if (s.mesh != null) Util.Kill(s.mesh); }

        static void Rebuild(Strip s, bool openEnd) => Fill(s.mesh, s.pts, s.ups, Width, openEnd);

        /// <summary>Ribbon mesh along points (local space) with ground normals; uv.x across, uv.y along (m); alpha tapers the ends.</summary>
        public static void Fill(Mesh mesh, IList<Vector3> pts, IList<Vector3> ups, float width, bool openEnd, float alpha = 1f)
        {
            int n = pts.Count;
            var v = new Vector3[n * 2]; var uv = new Vector2[n * 2]; var col = new Color[n * 2];
            var tri = new int[Mathf.Max(0, n - 1) * 6];
            float along = 0;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) along += Vector3.Distance(pts[i], pts[i - 1]);
                var t = pts[Mathf.Min(n - 1, i + 1)] - pts[Mathf.Max(0, i - 1)];
                var up = ups[i].sqrMagnitude > 0.5f ? ups[i] : Vector3.up;
                var side = Vector3.Cross(up, t).normalized * (width * 0.5f);
                var c = pts[i] + up * Lift;
                v[2 * i] = c - side; v[2 * i + 1] = c + side;
                uv[2 * i] = new Vector2(0, along); uv[2 * i + 1] = new Vector2(1, along);
                float a = alpha * ((i == 0 || (i == n - 1 && !openEnd)) ? 0f : 1f);
                col[2 * i] = col[2 * i + 1] = new Color(1, 1, 1, a);
            }
            for (int i = 0; i < n - 1; i++)
            {
                int o = i * 6, a = 2 * i;
                tri[o] = a; tri[o + 1] = a + 2; tri[o + 2] = a + 1;
                tri[o + 3] = a + 1; tri[o + 4] = a + 2; tri[o + 5] = a + 3;
            }
            mesh.Clear();
            mesh.vertices = v; mesh.uv = uv; mesh.colors = col; mesh.triangles = tri;
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Pre-made tracks of a wheeled vehicle along a frame-space polyline (resampled every 0.5 m, with a gentle
        /// meander), two ribbons <paramref name="gauge"/> apart, draped on the height field. A few merged meshes.
        /// </summary>
        public static GameObject BuildPath(Transform parent, string name, HeightField hf, IList<double3> path, float gauge, float width, float meander, int seed, float alpha = 0.85f)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            // resample the centreline
            var centre = new List<double3>();
            for (int k = 0; k < path.Count - 1; k++)
            {
                double len = math.distance(path[k].xz, path[k + 1].xz);
                int steps = math.max(1, (int)(len / 0.5));
                for (int s = 0; s < steps; s++) centre.Add(math.lerp(path[k], path[k + 1], s / (double)steps));
            }
            centre.Add(path[path.Count - 1]);
            float run = 0;
            var offs = new List<double3>(centre.Count);
            for (int i = 0; i < centre.Count; i++)
            {
                var t = math.normalizesafe((centre[math.min(centre.Count - 1, i + 1)] - centre[math.max(0, i - 1)]).xz);
                var nrm = new double2(-t.y, t.x);
                if (i > 0) run += (float)math.distance(centre[i].xz, centre[i - 1].xz);
                float m = meander * (Mathf.PerlinNoise(seed * 0.37f, run / 60f) - 0.5f) * 2f;
                var c = centre[i]; c.x += nrm.x * m; c.z += nrm.y * m;
                offs.Add(c);
            }
            foreach (float side in new[] { -0.5f, 0.5f })
            {
                var pts = new List<Vector3>(); var ups = new List<Vector3>();
                for (int i = 0; i < offs.Count; i++)
                {
                    var t = math.normalizesafe((offs[math.min(offs.Count - 1, i + 1)] - offs[math.max(0, i - 1)]).xz);
                    var nrm = new double2(-t.y, t.x);
                    double x = offs[i].x + nrm.x * gauge * side, z = offs[i].z + nrm.y * gauge * side;
                    float h = hf.Height(x, z, 0f);
                    float hx = hf.Height(x + 0.5, z, 0f) - hf.Height(x - 0.5, z, 0f), hz = hf.Height(x, z + 0.5, 0f) - hf.Height(x, z - 0.5, 0f);
                    var w = WorldOrigin.FrameToWorld(new double3(x, h, z));
                    pts.Add(w - root.position);
                    ups.Add(new Vector3(-hx, 1f, -hz).normalized);
                    if (pts.Count >= 512 || i == offs.Count - 1)
                    {
                        var go = new GameObject("TrackChunk");
                        go.transform.SetParent(root, false);
                        var mesh = new Mesh { name = name };
                        Fill(mesh, pts, ups, width, false, alpha);
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var r = go.AddComponent<MeshRenderer>();
                        r.sharedMaterial = Mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                        var lastP = pts[pts.Count - 1]; var lastU = ups[ups.Count - 1];
                        pts.Clear(); ups.Clear();
                        pts.Add(lastP); ups.Add(lastU);
                    }
                }
            }
            return root.gameObject;
        }
    }
}
