using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Base
{
    /// <summary>
    /// Hardware and set pieces of the GIMLI-2 expedition (generic designs, no real brands):
    ///  - GIMLI-1: a small CLPS-class lander (~3.5 m, four legs, solar panel, dish) and its car-sized "first rover"
    ///    (ground-penetrating radar, cameras, gravimeter), plus seismometer stakes;
    ///  - GIMLI-2 relay beacons (dim emissive, slow blink; no lights);
    ///  - the seal: a fused rubble wall across a closed tube end, GIMLI-2's radar sweeping it;
    ///  - the sealed chamber behind it (frost-lined; a granite boulder on 3- and 5-level runs) and the vapour burst.
    /// </summary>
    public static class MissionBuilder
    {
        class M
        {
            public Material white = Mats.Make("G1White", new Color(0.88f, 0.88f, 0.86f), 0.1f, 0.4f);
            public Material grey = Mats.Make("G1Grey", new Color(0.5f, 0.51f, 0.53f), 0.6f, 0.45f, Mats.Brushed, 0.3f);
            public Material dark = Mats.Make("G1Dark", new Color(0.07f, 0.07f, 0.08f), 0.4f, 0.4f);
            public Material gold = Mats.Make("G1MLI", new Color(0.85f, 0.62f, 0.26f), 1f, 0.65f, Mats.Foil, 0.8f);
            public Material solar = Mats.Make("G1Solar", new Color(0.05f, 0.07f, 0.16f), 0.8f, 0.85f);
            public Material orange = Mats.Make("G1Flag", new Color(0.9f, 0.45f, 0.1f), 0.1f, 0.4f);
            // basalt albedo ~0.09 (linear) like the tube walls; matte (a glossy dark Lit surface reads wet and white in the lamps)
            public Material rock = Mats.Make("SealRubble", new Color(0.31f, 0.3f, 0.29f), 0.0f, 0.04f);
            public Material rockFused = Mats.Make("SealRubbleFused", new Color(0.24f, 0.235f, 0.23f), 0.0f, 0.12f);
            public Material bore = Mats.Make("BoreHole", new Color(0.005f, 0.005f, 0.005f), 0f, 0.1f);
        }
        static M m;
        static M Mt { get { if (m == null || m.white == null) m = new M(); return m; } }

        static void Col(GameObject go, Vector3 c, Vector3 s) { var b = go.AddComponent<BoxCollider>(); b.center = c; b.size = s; }

        // ------------------------------------------------------------------ GIMLI-1

        /// <summary>Small CLPS-class lander, ~3.5 m tall, origin on the ground. Faces +Z (its dish points that way).</summary>
        public static GameObject GimliLander(Transform parent)
        {
            var mt = Mt;
            var root = new GameObject("GIMLI-1 lander (CLPS)").transform;
            root.SetParent(parent, false);
            BaseBuilder.Obj(root, "Body", ProcMesh.Lathe(new List<Vector2> { new(0.9f, 0.8f), new(0.9f, 2.0f), new(0.75f, 2.2f) }, 8, false), mt.gold, Vector3.zero);
            BaseBuilder.Box(root, "Deck", new Vector3(0, 2.25f, 0), new Vector3(1.9f, 0.08f, 1.9f), mt.grey);
            for (int i = 0; i < 4; i++)
            {
                var q = Quaternion.Euler(0, i * 90f + 45f, 0);
                BaseBuilder.Tube(root, "Leg", 0.05f, mt.grey, q * new Vector3(0, 1.4f, 0.85f), q * new Vector3(0, 0.6f, 1.7f), q * new Vector3(0, 0.1f, 1.95f));
                BaseBuilder.Cyl(root, "Foot", q * new Vector3(0, 0.05f, 1.95f), 0.4f, 0.08f, mt.grey);
            }
            BaseBuilder.Obj(root, "Engine", ProcMesh.Lathe(new List<Vector2> { new(0.12f, 0.8f), new(0.25f, 0.3f) }, 12), mt.dark, Vector3.zero);
            // solar panel on a short mast (tilted towards the low Sun in the east)
            BaseBuilder.Tube(root, "PanelMast", 0.03f, mt.grey, new Vector3(-0.3f, 2.25f, -0.3f), new Vector3(-0.3f, 3.0f, -0.3f));
            BaseBuilder.Box(root, "SolarPanel", new Vector3(-0.3f, 3.05f, -0.3f), new Vector3(1.8f, 0.03f, 1.0f), mt.solar, new Vector3(0, 0, -55f));
            // comms dish (Earth is high in the east-southeast)
            var dish = BaseBuilder.Obj(root, "Dish", ProcMesh.Paraboloid(0.45f, 0.12f), mt.white, new Vector3(0.45f, 2.7f, 0.35f), Quaternion.Euler(-35f, 100f, 0));
            BaseBuilder.Tube(root, "DishMast", 0.025f, mt.grey, new Vector3(0.45f, 2.25f, 0.35f), new Vector3(0.45f, 2.68f, 0.35f));
            // seismometer + gravimeter boxes on the deck, a camera head
            BaseBuilder.Box(root, "Gravimeter", new Vector3(0.4f, 2.4f, -0.45f), new Vector3(0.35f, 0.25f, 0.35f), mt.white);
            BaseBuilder.Box(root, "CamHead", new Vector3(-0.6f, 2.45f, 0.6f), new Vector3(0.2f, 0.15f, 0.25f), mt.dark);
            BaseBuilder.Box(root, "Radiator", new Vector3(0, 1.4f, -0.92f), new Vector3(1.1f, 0.9f, 0.03f), mt.white);
            Col(root.gameObject, new Vector3(0, 1.4f, 0), new Vector3(2.2f, 2.8f, 2.2f));
            return root.gameObject;
        }

        /// <summary>GIMLI-1's rover, the size of a small car (2.4 x 1.7 m), origin on the ground, facing +Z.</summary>
        public static GameObject GimliRover(Transform parent)
        {
            var mt = Mt;
            var root = new GameObject("GIMLI-1 first rover").transform;
            root.SetParent(parent, false);
            BaseBuilder.Box(root, "Chassis", new Vector3(0, 0.75f, 0), new Vector3(1.5f, 0.45f, 2.3f), mt.white);
            BaseBuilder.Box(root, "MLITop", new Vector3(0, 1.0f, -0.1f), new Vector3(1.4f, 0.05f, 1.9f), mt.gold);
            for (int i = 0; i < 2; i++)
            foreach (float s in new[] { -1f, 1f })
            {
                float z = i == 0 ? 0.8f : -0.8f;
                BaseBuilder.Cyl(root, "Wheel", new Vector3(s * 0.92f, 0.36f, z), 0.72f, 0.26f, mt.dark, new Vector3(0, 0, 90));
                BaseBuilder.Tube(root, "Rocker", 0.04f, mt.grey, new Vector3(s * 0.75f, 0.75f, 0), new Vector3(s * 0.8f, 0.36f, z));
            }
            // ground-penetrating radar antenna slung under the front, camera mast, gravimeter, solar deck
            BaseBuilder.Box(root, "GPRAntenna", new Vector3(0, 0.22f, 1.35f), new Vector3(1.3f, 0.1f, 0.45f), mt.grey);
            BaseBuilder.Tube(root, "GPRArm", 0.03f, mt.grey, new Vector3(0, 0.6f, 1.1f), new Vector3(0, 0.27f, 1.3f));
            BaseBuilder.Tube(root, "Mast", 0.035f, mt.grey, new Vector3(0.45f, 1.0f, 0.8f), new Vector3(0.45f, 1.9f, 0.8f));
            BaseBuilder.Box(root, "StereoCams", new Vector3(0.45f, 1.98f, 0.85f), new Vector3(0.4f, 0.14f, 0.16f), mt.dark);
            BaseBuilder.Box(root, "Gravimeter", new Vector3(-0.4f, 1.12f, 0.5f), new Vector3(0.35f, 0.2f, 0.35f), mt.white);
            BaseBuilder.Box(root, "SolarDeck", new Vector3(0, 1.08f, -0.55f), new Vector3(1.4f, 0.03f, 1.0f), mt.solar, new Vector3(-12f, 0, 0));
            BaseBuilder.Obj(root, "UHFDish", ProcMesh.Paraboloid(0.18f, 0.05f), mt.white, new Vector3(-0.5f, 1.3f, -0.9f), Quaternion.Euler(-60f, 0, 0));
            Col(root.gameObject, new Vector3(0, 0.8f, 0.1f), new Vector3(1.9f, 1.5f, 2.8f));
            return root.gameObject;
        }

        /// <summary>A seismometer stake: a short post with an orange flag, origin on the ground.</summary>
        public static GameObject SeismoStake(Transform parent)
        {
            var mt = Mt;
            var go = new GameObject("Seismometer stake");
            go.transform.SetParent(parent, false);
            BaseBuilder.Cyl(go.transform, "Body", new Vector3(0, 0.12f, 0), 0.18f, 0.24f, mt.white);
            BaseBuilder.Cyl(go.transform, "Post", new Vector3(0, 0.45f, 0), 0.02f, 0.7f, mt.grey);
            BaseBuilder.Box(go.transform, "Flag", new Vector3(0.07f, 0.72f, 0), new Vector3(0.14f, 0.09f, 0.005f), mt.orange);
            return go;
        }

        // ------------------------------------------------------------------ GIMLI-2 relay beacons

        static Material beaconMat;
        /// <summary>Shared emissive material of every relay beacon (blinked by <see cref="BeaconBlink"/>).</summary>
        public static Material BeaconMat => beaconMat != null ? beaconMat : beaconMat = Mats.Emissive(Mats.Make("RelayBeacon", new Color(0.2f, 0.08f, 0.02f), 0f, 0.6f), BeaconBlink.Peak * 0.06f);

        /// <summary>A small relay beacon dropped by GIMLI-2: tripod, box, a dim blinking emissive puck. No collider, no light.</summary>
        public static GameObject Beacon(Transform parent)
        {
            var mt = Mt;
            var go = new GameObject("GIMLI-2 relay beacon");
            go.transform.SetParent(parent, false);
            for (int i = 0; i < 3; i++)
            {
                var q = Quaternion.Euler(0, i * 120f, 0);
                BaseBuilder.Tube(go.transform, "Leg", 0.015f, mt.grey, new Vector3(0, 0.45f, 0), q * new Vector3(0, 0.0f, 0.35f));
            }
            BaseBuilder.Box(go.transform, "Box", new Vector3(0, 0.52f, 0), new Vector3(0.22f, 0.16f, 0.22f), mt.white);
            BaseBuilder.Cyl(go.transform, "Whip", new Vector3(0.08f, 0.85f, 0.08f), 0.01f, 0.5f, mt.grey);
            var puck = BaseBuilder.Cyl(go.transform, "Lamp", new Vector3(0, 0.64f, 0), 0.12f, 0.07f, BeaconMat);
            puck.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            foreach (var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
            return go;
        }

        // ------------------------------------------------------------------ the seal

        /// <summary>Lumpy rock (displaced UV sphere), radius ~1.</summary>
        public static Mesh RockMesh(int seed, int lat = 7, int lon = 10)
        {
            var verts = new List<Vector3>(); var tris = new List<int>();
            var rnd = new System.Random(seed);
            float sx = 0.8f + (float)rnd.NextDouble() * 0.5f, sy = 0.55f + (float)rnd.NextDouble() * 0.4f, sz = 0.8f + (float)rnd.NextDouble() * 0.5f;
            float o1 = (float)rnd.NextDouble() * 50f, o2 = (float)rnd.NextDouble() * 50f;
            for (int i = 0; i <= lat; i++)
            {
                float th = Mathf.PI * i / lat;
                for (int j = 0; j < lon; j++)
                {
                    float ph = 2 * Mathf.PI * j / lon;
                    var d = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    float n = 0.75f + 0.45f * Mathf.PerlinNoise(o1 + d.x * 1.7f + d.y, o2 + d.z * 1.7f - d.y);
                    // facets: flatten towards a few planes (broken basalt blocks are angular)
                    n = Mathf.Lerp(n, Mathf.Round(n * 4f) / 4f, 0.5f);
                    verts.Add(new Vector3(d.x * sx, d.y * sy, d.z * sz) * n);
                }
            }
            for (int i = 0; i < lat; i++)
                for (int j = 0; j < lon; j++)
                {
                    int a = i * lon + j, b = i * lon + (j + 1) % lon, c = (i + 1) * lon + j, d = (i + 1) * lon + (j + 1) % lon;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            var mesh = new Mesh { name = "Rock" };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Angle of repose of the breakdown pile (deg): dry angular basalt rubble ~35 deg.</summary>
        public const float ReposeDeg = 35f;
        /// <summary>Bore hole of the drill on the pile face (local to the seal).</summary>
        public static readonly Vector3 BoreLocal = new Vector3(0f, 1.1f, 1.1f / Mathf.Tan(ReposeDeg * Mathf.Deg2Rad) - 0.25f);

        /// <summary>How far the pile reaches back into the seal from its toe (m), for a tube of this height.</summary>
        public static float PileDepth(float height) => height / Mathf.Tan(ReposeDeg * Mathf.Deg2Rad) + 1.5f;

        /// <summary>
        /// The seal (round 12, S10): a roof-collapse breakdown pile filling the tube (local +Z = into the seal, origin on
        /// the floor at the pile's toe). Angular basalt slabs and blocks from ~2 m down to gravel (a power-law size
        /// spread, the big ones rolled furthest, to the toe), on a talus face of fines at the angle of repose (~35 deg)
        /// that climbs to the roof. One combined, flat-shaded mesh in the tube walls' rock material. A sloped box collider
        /// is the pile face; the drill makes the bore hole visible.
        /// </summary>
        public static GameObject RubbleSeal(Transform parent, float halfWidth, float height, int seed, Material rockMat = null)
        {
            var mt = Mt;
            var mat = rockMat != null ? rockMat : mt.rock;
            var root = new GameObject("Rubble seal").transform;
            root.SetParent(parent, false);
            var rnd = new System.Random(seed);
            float R() => (float)rnd.NextDouble();
            float tanA = Mathf.Tan(ReposeDeg * Mathf.Deg2Rad), w = halfWidth * 1.15f, H = height * 1.05f;
            // the arch: half-width at height y (ellipse, flat floor)
            float ArchW(float y) => w * Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow(Mathf.Clamp01(y / H), 2f)));
            var verts = new List<Vector3>(); var tris = new List<int>();
            // talus face of fines: a noisy grid on the 35 deg slope, clipped by the arch (gaps between blocks read as gravel)
            {
                int nx = 24, ny = 16; float o1 = R() * 50f, o2 = R() * 50f;
                var grid = new int[(nx + 1) * (ny + 1)];
                for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++)
                {
                    float y = H * j / ny, aw = ArchW(y) + 0.6f, x = Mathf.Lerp(-aw, aw, i / (float)nx);
                    float bump = 0.35f * (Mathf.PerlinNoise(o1 + x * 0.6f, o2 + y * 0.6f) - 0.5f) + 0.12f * (Mathf.PerlinNoise(o2 + x * 2.3f, o1 + y * 2.3f) - 0.5f);
                    grid[j * (nx + 1) + i] = verts.Count;
                    verts.Add(new Vector3(x, y, y / tanA + bump));
                }
                for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int a = grid[j * (nx + 1) + i], b = grid[j * (nx + 1) + i + 1], c = grid[(j + 1) * (nx + 1) + i], d = grid[(j + 1) * (nx + 1) + i + 1];
                    tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }
            // blocks: a power-law size spread (many small, few big); big ones settle low (they roll to the toe)
            int nBlocks = Mathf.Clamp((int)(w * H * 2.2f), 120, 520);
            for (int k = 0; k < nBlocks; k++)
            {
                float s = Mathf.Lerp(0.12f, 2.0f, Mathf.Pow(R(), 2.6f));
                float yMax = Mathf.Lerp(H, H * 0.35f, Mathf.InverseLerp(0.6f, 2.0f, s));
                float y = Mathf.Pow(R(), 1.3f) * yMax;
                float aw = ArchW(y + s * 0.3f);
                if (aw < s * 0.5f) continue;
                float x = Mathf.Lerp(-aw, aw, R());
                float z = y / tanA - s * (0.15f + 0.25f * R());                    // half sunk into the slope
                var rot = Quaternion.Euler(R() * 40f - 20f, R() * 360f, R() * 40f - 20f) * Quaternion.Euler(-ReposeDeg * 0.6f, 0, 0);
                // slabs: flat roof plates (breakdown is mostly slabs that peeled off the ceiling layers)
                var dims = R() < 0.55f ? new Vector3(1.0f + 0.5f * R(), 0.3f + 0.25f * R(), 0.8f + 0.5f * R()) : new Vector3(0.8f + 0.4f * R(), 0.6f + 0.4f * R(), 0.7f + 0.4f * R());
                AddBlock(verts, tris, new Vector3(x, y + s * dims.y * 0.35f, z), rot, dims * s, rnd);
            }
            var mesh = new Mesh { name = "BreakdownPile", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0);
            FlatShade(mesh);
            BaseBuilder.Obj(root, "BreakdownPile", mesh, mat, Vector3.zero, Quaternion.identity, Vector3.one);
            // back plug (behind the roof contact: never seen, closes any gap to the roof)
            BaseBuilder.Obj(root, "FusedWall", RockMesh(seed * 7 + 1), mat, new Vector3(0, H * 0.5f, PileDepth(H) + 1.5f), Quaternion.identity, new Vector3(w * 1.2f, H * 0.7f, 2.5f), false);
            var bore = BaseBuilder.Cyl(root, "BoreHole", BoreLocal, 0.32f, 0.3f, mt.bore, new Vector3(90 - ReposeDeg, 0, 0));
            bore.SetActive(false);
            foreach (var c in root.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
            // the pile face: a box tilted to the repose angle (the rover can nose onto the toe, not over the pile)
            var face = new GameObject("PileCollider");
            face.transform.SetParent(root, false);
            float slopeLen = H / Mathf.Sin(ReposeDeg * Mathf.Deg2Rad);
            face.transform.localRotation = Quaternion.Euler(-ReposeDeg, 0, 0);
            face.transform.localPosition = Quaternion.Euler(-ReposeDeg, 0, 0) * new Vector3(0, -1.0f, slopeLen * 0.5f);
            var bc = face.AddComponent<BoxCollider>(); bc.size = new Vector3(w * 2f, 2.0f, slopeLen);
            Col(root.gameObject, new Vector3(0, H * 0.5f, PileDepth(H) * 0.75f), new Vector3(w * 2f, H, PileDepth(H) * 0.5f));
            return root.gameObject;
        }

        /// <summary>An angular block: a box with jittered corners (no two alike), its own vertices (flat shaded later).</summary>
        static void AddBlock(List<Vector3> verts, List<int> tris, Vector3 c, Quaternion rot, Vector3 size, System.Random rnd)
        {
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var o = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
                o += new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f) * 0.35f;
                corners[i] = c + rot * Vector3.Scale(o, size);
            }
            int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            foreach (var f in faces)
            {
                int b = verts.Count;
                foreach (int i in f) verts.Add(corners[i]);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2); tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        /// <summary>Split every triangle onto its own vertices: hard, angular facets (smooth normals read as blobs).</summary>
        static void FlatShade(Mesh m)
        {
            var v = m.vertices; var t = m.triangles;
            var nv = new Vector3[t.Length]; var nt = new int[t.Length];
            for (int i = 0; i < t.Length; i++) { nv[i] = v[t[i]]; nt[i] = i; }
            m.Clear(); m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = nv; m.triangles = nt;
            m.RecalculateNormals(); m.RecalculateBounds();
        }

        // ------------------------------------------------------------------ the sealed chamber

        /// <summary>
        /// The chamber behind the seal: an inside-out ellipsoid (invisible from outside, so it never shows through
        /// other tubes), frost-lined. granite: a pale grey granite boulder from the early Earth on the floor.
        /// Origin at the floor centre; +Z = away from the seal.
        /// </summary>
        public static GameObject FrostChamber(Transform parent, float radius, bool granite)
        {
            var root = new GameObject("Sealed chamber").transform;
            root.SetParent(parent, false);
            var frostTex = FrostTexture(granite ? 0.55f : 0.3f);
            var frost = Mats.Make("ChamberFrost", Color.white, 0f, 0.7f);
            frost.SetTexture("_BaseMap", frostTex);
            frost.SetTextureScale("_BaseMap", new Vector2(4, 2));
            Mats.Emissive(frost, new Color(0.012f, 0.016f, 0.02f), frostTex);
            frost.SetTextureScale("_EmissionMap", new Vector2(4, 2));
            var shell = BaseBuilder.Obj(root, "FrostShell", Inverted(RockMesh(911, 16, 24)), frost, new Vector3(0, radius * 0.45f, 0), Quaternion.identity, new Vector3(radius, radius * 0.6f, radius * 1.1f), false);
            if (granite)
            {
                var gtex = GraniteTexture();
                var gm = Mats.Make("GraniteBoulder", Color.white, 0f, 0.35f);
                gm.SetTexture("_BaseMap", gtex);
                gm.SetTextureScale("_BaseMap", new Vector2(2, 2));
                BaseBuilder.Obj(root, "GraniteBoulder", RockMesh(4114, 12, 18), gm, new Vector3(0.4f, 0.9f, radius * 0.25f), Quaternion.Euler(10, 35, 5), new Vector3(1.6f, 1.3f, 1.4f));
            }
            return root.gameObject;
        }

        static Mesh Inverted(Mesh src)
        {
            var mesh = Object.Instantiate(src);
            var t = mesh.triangles;
            for (int i = 0; i < t.Length; i += 3) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
            mesh.triangles = t;
            var n = mesh.normals; for (int i = 0; i < n.Length; i++) n[i] = -n[i];
            mesh.normals = n;
            var uv = new Vector2[mesh.vertexCount];
            var v = mesh.vertices;
            for (int i = 0; i < v.Length; i++) uv[i] = new Vector2(Mathf.Atan2(v[i].z, v[i].x) / (2 * Mathf.PI) + 0.5f, v[i].y * 0.5f + 0.5f);
            mesh.uv = uv;
            return mesh;
        }

        /// <summary>Dark basalt with white frost veins (ridged noise) and a frost coat that grows with coverage.</summary>
        public static Texture2D FrostTexture(float coverage)
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "FrostVeins", wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S * 8f, v = y / (float)S * 8f;
                float r = 1f - Mathf.Abs(Mathf.PerlinNoise(u, v) * 2f - 1f);
                float r2 = 1f - Mathf.Abs(Mathf.PerlinNoise(u * 2.3f + 7f, v * 2.3f + 3f) * 2f - 1f);
                float vein = Mathf.SmoothStep(0.86f, 0.97f, Mathf.Max(r, r2 * 0.97f));
                float coat = Mathf.SmoothStep(1f - coverage, 1.05f - coverage * 0.5f, Mathf.PerlinNoise(u * 0.5f + 20f, v * 0.5f + 11f));
                float f = Mathf.Clamp01(vein + coat * 0.8f);
                float sparkle = Mathf.PerlinNoise(u * 40f, v * 40f) > 0.8f ? 0.15f : 0f;
                var c = Color.Lerp(new Color(0.07f, 0.07f, 0.075f), new Color(0.82f, 0.88f, 0.95f), f) + new Color(sparkle, sparkle, sparkle) * f;
                px[y * S + x] = c;
            }
            tex.SetPixels32(px); tex.Apply(true);
            return tex;
        }

        /// <summary>Pale grey granite: quartz/feldspar grains with black biotite and a few pinkish K-feldspar specks.</summary>
        public static Texture2D GraniteTexture()
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "Granite", wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[S * S];
            var rnd = new System.Random(14321);
            for (int i = 0; i < px.Length; i++)
            {
                float r = (float)rnd.NextDouble();
                Color c = new Color(0.62f, 0.62f, 0.6f) * (0.9f + 0.2f * (float)rnd.NextDouble());
                px[i] = c;
            }
            // grains: blobs of 2-5 px
            for (int g = 0; g < 2600; g++)
            {
                int cx = rnd.Next(S), cy = rnd.Next(S), rr = 1 + rnd.Next(3);
                double k = rnd.NextDouble();
                Color c = k < 0.28 ? new Color(0.08f, 0.08f, 0.08f) : k < 0.4 ? new Color(0.72f, 0.56f, 0.52f) : k < 0.7 ? new Color(0.82f, 0.82f, 0.8f) : new Color(0.5f, 0.5f, 0.52f);
                for (int dy = -rr; dy <= rr; dy++) for (int dx = -rr; dx <= rr; dx++)
                    if (dx * dx + dy * dy <= rr * rr) px[((cy + dy + S) % S) * S + (cx + dx + S) % S] = c;
            }
            tex.SetPixels32(px); tex.Apply(true);
            return tex;
        }

        // ------------------------------------------------------------------ the vapour burst

        static Material burstMat;
        /// <summary>
        /// Gas venting from the bore hole into vacuum: it expands, cools and frosts into ice crystals that glitter in the
        /// headlights (real: a free expansion of moist gas into vacuum freezes it). Unlit, so it reads in the dark tube.
        /// </summary>
        public static ParticleSystem VapourBurst(Transform parent, Vector3 localPos, Vector3 localDir)
        {
            var go = new GameObject("Vapour burst");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.LookRotation(localDir);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false; main.loop = false; main.duration = 3f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.35f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.9f, 1f, 0.55f), new Color(1f, 1f, 1f, 0.9f));
            main.gravityModifier = 0.03f;   // ice grains fall slowly in 1/6 g; most fly ballistically
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 1500;
            var em = ps.emission;
            em.rateOverTime = 120f;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, 600) });
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 16f; sh.radius = 0.12f;
            var col = ps.colorOverLifetime; col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(new Color(0.8f, 0.85f, 0.95f), 0f), new GradientColorKey(Color.white, 0.3f), new GradientColorKey(new Color(0.9f, 0.95f, 1f), 1f) },
                         new[] { new GradientAlphaKey(0.7f, 0f), new GradientAlphaKey(0.9f, 0.2f), new GradientAlphaKey(0.4f, 0.7f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;
            var sz = ps.sizeOverLifetime; sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1.6f), new Keyframe(0.25f, 0.6f), new Keyframe(1, 0.25f)));   // vapour puff -> fine crystals
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.4f; noise.frequency = 0.6f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            if (burstMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                burstMat = new Material(shader) { name = "VapourBurst" };
                burstMat.SetTexture("_BaseMap", ProcTex.SoftDot());
                burstMat.SetFloat("_Surface", 1f);   // transparent
                burstMat.SetFloat("_Blend", 0f);
                burstMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                burstMat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                burstMat.SetFloat("_ZWrite", 0f);
                burstMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                burstMat.renderQueue = (int)RenderQueue.Transparent;
            }
            r.sharedMaterial = burstMat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            return ps;
        }
    }

    /// <summary>Blinks the shared relay-beacon emission: a slow, dim double flash every 3 s (one component, no lights).</summary>
    public class BeaconBlink : MonoBehaviour
    {
        public static readonly Color Peak = new Color(0.9f, 0.3f, 0.06f);
        public static float Level(float t)
        {
            float p = Mathf.Repeat(t, 3f);
            float f1 = Mathf.Exp(-Mathf.Pow((p - 0.15f) / 0.08f, 2)), f2 = Mathf.Exp(-Mathf.Pow((p - 0.55f) / 0.08f, 2));
            return 0.06f + 0.94f * Mathf.Max(f1, f2 * 0.6f);
        }
        void Update() => MissionBuilder.BeaconMat.SetColor("_EmissionColor", Peak * Level(Time.time));
    }

    /// <summary>
    /// GIMLI-2's ground-penetrating radar, shown as a soft light sweeping the rubble wall side to side and pulsing
    /// (a stand-in: real GPR is radio, invisible; the rover's status lamp marks the scan).
    /// </summary>
    public class RadarSweep : MonoBehaviour
    {
        public Light spot;
        public float sweepDeg = 28f, period = 4f, candela = 25f;
        PhysicalLight pl;
        void Start() { pl = spot != null ? spot.GetComponent<PhysicalLight>() : null; }
        void Update()
        {
            if (spot == null) return;
            float t = Time.time;
            spot.transform.localRotation = Quaternion.Euler(8f, sweepDeg * Mathf.Sin(2 * Mathf.PI * t / period), 0);
            if (pl != null) pl.physicalIntensity = candela * (0.35f + 0.65f * Mathf.Pow(0.5f + 0.5f * Mathf.Sin(2 * Mathf.PI * t / 1.3f), 3f));
        }

        public static RadarSweep Add(GameObject scout)
        {
            var go = new GameObject("RadarSweepLight");
            go.transform.SetParent(scout.transform, false);
            go.transform.localPosition = new Vector3(0, 0.5f, 1.1f);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot; l.spotAngle = 22f; l.innerSpotAngle = 6f; l.range = 18f;
            l.color = new Color(0.55f, 0.85f, 1f); l.shadows = LightShadows.None;
            go.AddComponent<PhysicalLight>().physicalIntensity = 25f;
            var rs = scout.AddComponent<RadarSweep>();
            rs.spot = l;
            return rs;
        }
    }
}
