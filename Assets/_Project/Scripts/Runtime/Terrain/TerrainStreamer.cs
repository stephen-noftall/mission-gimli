using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoonBaseAlpha.Terrain
{
    /// <summary>
    /// Streams the lunar surface around the camera:
    ///  - render LOD quadtree (32.8 km root, 0.5 m leaves) with skirts, swapped atomically when a
    ///    new LOD set is fully built so there are never holes;
    ///  - full-detail collider chunks (32 m, 0.5 m spacing) around the physics focus (the buggy);
    ///  - a static far ring out to ~33 km from the coarse grid so distant hills reach the true horizon.
    /// All geometry is in frame coordinates and follows <see cref="WorldOrigin"/> shifts.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class TerrainStreamer : MonoBehaviour
    {
        public const int Res = 64;
        public const double RootSize = 32768;
        public const double LeafSize = 32;
        public const double ColliderChunk = 32;

        public Material material;
        public Transform viewer;          // camera
        public Transform physicsFocus;    // buggy
        public Transform physicsFocus2;   // astronaut on foot (colliders are kept around both)
        public float lodFactor = 1.6f;
        public int colliderRadius = 2;    // chunks each side -> 5x5
        public int maxJobsPerFrame = 24;

        public HeightField Height => hf;
        public bool Ready { get; private set; }

        HeightField hf;
        Transform renderRoot, colliderRoot;
        GameObject farRing;

        class Chunk { public GameObject go; public Mesh mesh; public double3 centre; public int lastWanted; }
        struct Pending { public long key; public JobHandle handle; public Mesh.MeshDataArray data; public NativeArray<float> range; public double3 centre; public bool collider; }

        readonly Dictionary<long, Chunk> chunks = new();
        readonly Dictionary<long, Chunk> colliders = new();
        readonly List<Pending> pending = new();
        readonly HashSet<long> wanted = new();
        readonly HashSet<long> shown = new();
        readonly List<long> toBuild = new();
        int frame;

        // ------------------------------------------------------------------ setup

        public void Initialise(HeightField heightField)
        {
            hf = heightField;
            renderRoot = new GameObject("TerrainRender").transform;
            colliderRoot = new GameObject("TerrainColliders").transform;
            renderRoot.SetParent(transform, false);
            colliderRoot.SetParent(transform, false);
            WorldOrigin.Shifted += OnShift;
        }

        void OnDestroy()
        {
            WorldOrigin.Shifted -= OnShift;
            foreach (var p in pending) { p.handle.Complete(); p.data.Dispose(); p.range.Dispose(); }
            pending.Clear();
            foreach (var c in chunks.Values) if (c.mesh) Util.Kill(c.mesh);
            foreach (var c in colliders.Values) if (c.mesh) Util.Kill(c.mesh);
            hf.Dispose();
        }

        void OnShift(Vector3 delta)
        {
            // Chunk GameObjects are children of this (a root object) so they already moved.
        }

        /// <summary>Build everything needed around the given world positions right now (blocking).</summary>
        public void BuildNow(Vector3 viewerWorld, Vector3 focusWorld)
        {
            if (farRing == null) BuildFarRing();
            UpdateColliders(true, WorldOrigin.WorldToFrame(focusWorld));
            SelectLods(WorldOrigin.WorldToFrame(viewerWorld));
            foreach (var key in wanted) if (!chunks.ContainsKey(key) && !IsPending(key)) toBuild.Add(key);
            ScheduleRender(toBuild, int.MaxValue);
            toBuild.Clear();
            CompleteAll(true);
            Swap();
            Ready = true;
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            if (!hf.IsCreated) return;
            frame++;
            CompleteAll(false);
            if (physicsFocus != null || physicsFocus2 != null) UpdateColliders(false);
            if (viewer != null)
            {
                SelectLods(WorldOrigin.WorldToFrame(viewer.position));
                foreach (var key in wanted) if (!chunks.ContainsKey(key) && !IsPending(key)) toBuild.Add(key);
                // nearest first
                var vf = WorldOrigin.WorldToFrame(viewer.position);
                toBuild.Sort((a, b) => NodeDist(a, vf).CompareTo(NodeDist(b, vf)));
                ScheduleRender(toBuild, maxJobsPerFrame);
                toBuild.Clear();
                JobHandle.ScheduleBatchedJobs();
                if (!AnyRenderPending()) Swap();
            }
            if ((frame & 63) == 0) Evict();
        }

        void LateUpdate() => CompleteAll(false);

        // ------------------------------------------------------------------ LOD selection

        static long Key(int level, int ix, int iz) => ((long)level << 48) | ((long)(ix & 0xFFFFFF) << 24) | (long)(iz & 0xFFFFFF);
        static void Unkey(long k, out int level, out int ix, out int iz)
        {
            level = (int)(k >> 48);
            ix = (int)((k >> 24) & 0xFFFFFF); if ((ix & 0x800000) != 0) ix |= unchecked((int)0xFF000000);
            iz = (int)(k & 0xFFFFFF); if ((iz & 0x800000) != 0) iz |= unchecked((int)0xFF000000);
        }

        static double NodeSize(int level) => RootSize / (1 << level);
        static double2 NodeMin(int level, int ix, int iz)
        {
            double s = NodeSize(level);
            return new double2(-RootSize / 2 + ix * s, -RootSize / 2 + iz * s);
        }

        float NodeDist(long key, double3 v)
        {
            Unkey(key, out int l, out int ix, out int iz);
            double s = NodeSize(l);
            var mn = NodeMin(l, ix, iz);
            double dx = math.max(0, math.max(mn.x - v.x, v.x - (mn.x + s)));
            double dz = math.max(0, math.max(mn.y - v.z, v.z - (mn.y + s)));
            return (float)math.sqrt(dx * dx + dz * dz);
        }

        void SelectLods(double3 v)
        {
            wanted.Clear();
            int maxLevel = (int)math.round(math.log2(RootSize / LeafSize));
            float groundY = hf.Height(v.x, v.z, 64f);
            float hAbove = math.max(0, (float)v.y - groundY);
            Recurse(0, 0, 0, v, hAbove, maxLevel);
        }

        void Recurse(int level, int ix, int iz, double3 v, float hAbove, int maxLevel)
        {
            double s = NodeSize(level);
            var mn = NodeMin(level, ix, iz);
            double dx = math.max(0, math.max(mn.x - v.x, v.x - (mn.x + s)));
            double dz = math.max(0, math.max(mn.y - v.z, v.z - (mn.y + s)));
            double d = math.sqrt(dx * dx + dz * dz + hAbove * hAbove);
            if (level < maxLevel && d < s * lodFactor)
            {
                for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                    Recurse(level + 1, ix * 2 + i, iz * 2 + j, v, hAbove, maxLevel);
                return;
            }
            var key = Key(level, ix, iz);
            wanted.Add(key);
            if (chunks.TryGetValue(key, out var c)) c.lastWanted = frame;
        }

        // ------------------------------------------------------------------ building

        bool IsPending(long key) { foreach (var p in pending) if (p.key == key && !p.collider) return true; return false; }
        bool AnyRenderPending() { foreach (var p in pending) if (!p.collider) return true; return false; }

        void ScheduleRender(List<long> keys, int max)
        {
            int count = math.min(max, keys.Count);
            for (int k = 0; k < count; k++)
            {
                Unkey(keys[k], out int l, out int ix, out int iz);
                double s = NodeSize(l);
                var mn = NodeMin(l, ix, iz);
                float spacing = (float)(s / Res);
                var c = new double3(mn.x + s / 2, hf.Height(mn.x + s / 2, mn.y + s / 2, 64f), mn.y + s / 2);
                Schedule(keys[k], mn, c, spacing, Res, spacing, math.max(0.5f, spacing * 2f), true, false);
            }
        }

        void Schedule(long key, double2 mn, double3 centre, float spacing, int res, float minFeature, float skirt, bool normals, bool collider)
        {
            var data = Mesh.AllocateWritableMeshData(1);
            var range = new NativeArray<float>(2, Allocator.Persistent);
            var job = new ChunkMeshJob
            {
                hf = hf, min = mn, centre = centre, spacing = spacing, res = res, minFeature = collider ? 0f : minFeature,
                skirtDepth = skirt, normals = normals, mesh = data[0], heightRange = range,
            };
            pending.Add(new Pending { key = key, handle = job.Schedule(), data = data, range = range, centre = centre, collider = collider });
        }

        void CompleteAll(bool force)
        {
            List<Mesh> bake = null;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (!force && !p.handle.IsCompleted) continue;
                p.handle.Complete();
                var mesh = new Mesh { name = p.collider ? "TerrainCollider" : "TerrainChunk", indexFormat = IndexFormat.UInt32 };
                Mesh.ApplyAndDisposeWritableMeshData(p.data, mesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                mesh.RecalculateBounds();
                p.range.Dispose();
                pending.RemoveAt(i);
                var go = new GameObject(mesh.name);
                go.transform.SetParent(p.collider ? colliderRoot : renderRoot, false);
                go.transform.position = WorldOrigin.FrameToWorld(p.centre);
                var chunk = new Chunk { go = go, mesh = mesh, centre = p.centre, lastWanted = frame };
                if (p.collider)
                {
                    // chunks wholly inside the pit area have no triangles (the pit mesh owns that ground)
                    if (mesh.GetIndexCount(0) >= 3) (bake ??= new List<Mesh>()).Add(mesh);
                    colliders[p.key] = chunk;
                }
                else
                {
                    go.layer = MoonBaseAlpha.Rendering.RenderLayers.SurfaceTerrain;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = material;
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    go.SetActive(false);
                    chunks[p.key] = chunk;
                }
            }
            if (bake != null)
            {
                var ids = new NativeArray<EntityId>(bake.Count, Allocator.TempJob);
                for (int i = 0; i < bake.Count; i++) ids[i] = bake[i].GetEntityId();
                new BakeColliderJob { meshIds = ids }.Schedule(ids.Length, 1).Complete();
                ids.Dispose();
                foreach (var kv in colliders)
                {
                    if (kv.Value.go.GetComponent<MeshCollider>() != null || kv.Value.mesh.GetIndexCount(0) < 3) continue;
                    var mc = kv.Value.go.AddComponent<MeshCollider>();
                    mc.cookingOptions = MeshColliderCookingOptions.UseFastMidphase | MeshColliderCookingOptions.CookForFasterSimulation |
                                        MeshColliderCookingOptions.WeldColocatedVertices;
                    mc.sharedMesh = kv.Value.mesh;
                }
            }
        }

        void Swap()
        {
            foreach (var key in shown)
                if (!wanted.Contains(key) && chunks.TryGetValue(key, out var c)) c.go.SetActive(false);
            shown.Clear();
            foreach (var key in wanted)
                if (chunks.TryGetValue(key, out var c)) { c.go.SetActive(true); shown.Add(key); }
        }

        void Evict()
        {
            if (chunks.Count < 900) return;
            var old = new List<long>();
            foreach (var kv in chunks) if (!shown.Contains(kv.Key) && frame - kv.Value.lastWanted > 600) old.Add(kv.Key);
            foreach (var k in old) { Util.Kill(chunks[k].go); Util.Kill(chunks[k].mesh); chunks.Remove(k); }
        }

        // ------------------------------------------------------------------ colliders

        void UpdateColliders(bool block, double3? extra = null)
        {
            int res = (int)(ColliderChunk / 0.5);
            var centres = new List<int2>();
            if (extra.HasValue) centres.Add(ChunkOf(extra.Value));
            if (physicsFocus != null) centres.Add(ChunkOf(WorldOrigin.WorldToFrame(physicsFocus.position)));
            if (physicsFocus2 != null && physicsFocus2.gameObject.activeInHierarchy) centres.Add(ChunkOf(WorldOrigin.WorldToFrame(physicsFocus2.position)));
            bool scheduled = false;
            foreach (var cc in centres)
            for (int j = -colliderRadius; j <= colliderRadius; j++)
            for (int i = -colliderRadius; i <= colliderRadius; i++)
            {
                long key = Key(31, cc.x + i, cc.y + j);
                if (colliders.ContainsKey(key)) continue;
                bool isPending = false;
                foreach (var p in pending) if (p.key == key && p.collider) { isPending = true; break; }
                if (isPending) continue;
                var mn = new double2((cc.x + i) * ColliderChunk, (cc.y + j) * ColliderChunk);
                var c = new double3(mn.x + ColliderChunk / 2, hf.Height(mn.x + ColliderChunk / 2, mn.y + ColliderChunk / 2, 64f), mn.y + ColliderChunk / 2);
                Schedule(key, mn, c, 0.5f, res, 0f, 0f, false, true);
                scheduled = true;
            }
            if (scheduled) JobHandle.ScheduleBatchedJobs();
            if (block) CompleteAll(true);
            // drop colliders far from every focus (keep a margin to avoid thrash)
            var drop = new List<long>();
            foreach (var kv in colliders)
            {
                Unkey(kv.Key, out _, out int ix, out int iz);
                bool near = false;
                foreach (var cc in centres)
                    if (math.abs(ix - cc.x) <= colliderRadius + 1 && math.abs(iz - cc.y) <= colliderRadius + 1) { near = true; break; }
                if (!near) drop.Add(kv.Key);
            }
            foreach (var k in drop) { Util.Kill(colliders[k].go); Util.Kill(colliders[k].mesh); colliders.Remove(k); }
        }

        static int2 ChunkOf(double3 f) => new int2((int)math.floor(f.x / ColliderChunk), (int)math.floor(f.z / ColliderChunk));

        /// <summary>True when the collider chunk under the given world position is loaded.</summary>
        public bool HasColliderAt(Vector3 world)
        {
            var f = WorldOrigin.WorldToFrame(world);
            return colliders.ContainsKey(Key(31, (int)math.floor(f.x / ColliderChunk), (int)math.floor(f.z / ColliderChunk)));
        }

        // ------------------------------------------------------------------ far ring

        void BuildFarRing()
        {
            // Coarse ring from the far grid outside the quadtree root (|x|,|z| > 16.4 km) out to 32.8 km.
            const int n = 129; // 256 m spacing over 32.768 km half-size -> 2*... use 512 m spacing
            double half = hf.farHalf;
            double sp = 2 * half / (n - 1);
            var verts = new List<Vector3>(n * n);
            var norms = new List<Vector3>(n * n);
            var tris = new List<int>();
            var centre = new double3(0, 0, 0);
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double x = -half + i * sp, z = -half + j * sp;
                float y = hf.Height(x, z, 512f);
                verts.Add(new Vector3((float)x, y, (float)z));
            }
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float hl = verts[j * n + math.max(0, i - 1)].y, hr = verts[j * n + math.min(n - 1, i + 1)].y;
                float hd = verts[math.max(0, j - 1) * n + i].y, hu = verts[math.min(n - 1, j + 1) * n + i].y;
                norms.Add(new Vector3(hl - hr, (float)(2 * sp), hd - hu).normalized);
            }
            double inner = RootSize / 2 - sp;   // overlap the root slightly, push it down so the root wins
            for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                double x0 = -half + i * sp, z0 = -half + j * sp;
                if (x0 >= -inner && x0 + sp <= inner && z0 >= -inner && z0 + sp <= inner) continue;
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                tris.Add(a); tris.Add(c); tris.Add(d); tris.Add(a); tris.Add(d); tris.Add(b);
            }
            for (int k = 0; k < verts.Count; k++)
            {
                var vv = verts[k];
                double r = math.max(math.abs(vv.x), math.abs(vv.z));
                if (r < RootSize / 2 + sp) vv.y -= 3f;   // tuck under the detailed terrain
                verts[k] = vv;
            }
            var mesh = new Mesh { name = "FarRing", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            farRing = new GameObject("FarRing") { layer = MoonBaseAlpha.Rendering.RenderLayers.SurfaceTerrain };
            farRing.transform.SetParent(transform, false);
            farRing.transform.position = WorldOrigin.FrameToWorld(centre);
            farRing.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = farRing.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.On;
        }
    }
}
