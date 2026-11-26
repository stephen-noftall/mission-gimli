using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Terrain;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// Streams the endless tunnel network around the viewer as 48 m chunks (1.5 m voxels, surface nets,
    /// seamless: each chunk samples a one-voxel border and owns the edges starting inside it).
    /// The pit box near the surface is owned by <see cref="CaveBuilder"/>; the tunnel mesh overlaps
    /// <see cref="PitOverlap"/> voxels into it (no hard cut), so the two meshes can't leave a crack.
    /// </summary>
    public class CaveStreamer : MonoBehaviour
    {
        public const int N = 32;              // cells per chunk edge
        public const float Voxel = 1.5f;
        public const float ChunkSize = N * Voxel;
        public const int PitOverlap = 2;      // voxels the tunnel mesh reaches into the pit box

        public Material material;
        public Transform viewer;
        public Transform physicsFocus, physicsFocus2;
        public float radius = 240f, verticalRadius = 110f;
        public int maxJobsPerFrame = 6;

        /// <summary>Voxel size of this streamer (the far ring uses 3 m).</summary>
        public float voxel = Voxel;
        /// <summary>Far ring: no colliders, no shadows, no physics foci; only chunks reaching beyond <see cref="innerRadius"/>.</summary>
        public bool renderOnly;
        public float innerRadius;
        /// <summary>On the near streamer: the coarse far ring that continues it (null/disabled = none).</summary>
        public CaveStreamer far;
        public float ChunkWorld => N * voxel;

        TunnelNetwork net;
        CaveBuilder pit;
        Transform root;

        class Chunk { public GameObject go; public Mesh mesh; public bool empty; }
        struct Pending { public int3 key; public JobHandle handle; public NativeArray<TunnelPrim> prims; public NativeList<float3> v, n; public NativeList<int> idx; public NativeArray<int> stats; }
        readonly Dictionary<int3, Chunk> chunks = new();
        readonly List<Pending> pending = new();
        readonly List<TunnelPrim> regionPrims = new();
        readonly List<TunnelPrim> tmp = new();
        float3 regionCentre = new float3(1e9f);
        public int ChunkCount => chunks.Count;
        /// <summary>Floating mesh islands removed so far (S4 diagnostics).</summary>
        public int IslandsDropped;
        public int PendingCount => pending.Count;

        public void Initialise(TunnelNetwork network, CaveBuilder pitBuilder)
        {
            net = network; pit = pitBuilder;
            root = new GameObject(renderOnly ? "FarTunnelChunks" : "TunnelChunks").transform;
            root.SetParent(transform, false);
        }

        void OnDestroy()
        {
            foreach (var p in pending) { p.handle.Complete(); Dispose(p); }
            pending.Clear();
            foreach (var c in chunks.Values) if (c.mesh) Util.Kill(c.mesh);
        }

        static void Dispose(Pending p) { p.prims.Dispose(); p.v.Dispose(); p.n.Dispose(); p.idx.Dispose(); p.stats.Dispose(); }

        void Update()
        {
            if (net == null || viewer == null) return;
            PushShaderGlobals();
            Complete(false);
            Request(WorldOrigin.WorldToFrame(viewer.position), maxJobsPerFrame);
            if (renderOnly) { JobHandle.ScheduleBatchedJobs(); Evict(WorldOrigin.WorldToFrame(viewer.position)); return; }
            if (physicsFocus2 != null && physicsFocus2.gameObject.activeInHierarchy)
                Request(WorldOrigin.WorldToFrame(physicsFocus2.position), 2);
            if (physicsFocus != null) Request(WorldOrigin.WorldToFrame(physicsFocus.position), 2);
            JobHandle.ScheduleBatchedJobs();
            Evict(WorldOrigin.WorldToFrame(viewer.position));
        }

        void LateUpdate() => Complete(false);

        /// <summary>One play-mode frame of streaming (tests: the Update budget instead of BuildNow).</summary>
        public void Tick() { Update(); LateUpdate(); }

        /// <summary>Build everything around a point now (blocking) - startup, teleports, tests.</summary>
        public void BuildNow(Vector3 world, float r = -1f)
        {
            var f = WorldOrigin.WorldToFrame(world);
            float old = radius;
            if (r > 0) radius = r;
            Request(f, int.MaxValue);
            JobHandle.ScheduleBatchedJobs();
            Complete(true);
            radius = old;
            PushShaderGlobals();
        }

        /// <summary>Show / hide every resident chunk (perf bench: cost of this ring).</summary>
        public void SetChunksVisible(bool on)
        {
            foreach (var c in chunks.Values) if (c.go) { var mr = c.go.GetComponent<MeshRenderer>(); if (mr) mr.enabled = on; }
        }

        public bool FarActive => far != null && far.enabled && far.radius > radius;
        /// <summary>Distance where everything ends in black (the under-rock shell sits here).</summary>
        public float DrawRadius => FarActive ? far.radius : radius;

        /// <summary>
        /// _MBA_TubeR = (near radius, far radius, shell radius, 0): CaveRock clips the near chunks beyond x and the
        /// far ring inside x (one hand-off circle, no double walls), and fades the soft fill out towards z.
        /// </summary>
        public void PushShaderGlobals()
        {
            if (renderOnly) return;
            float outer = DrawRadius;
            Shader.SetGlobalVector("_MBA_TubeR", new Vector4(FarActive ? radius : 0f, outer, MoonBaseAlpha.Rendering.UnderRockShell.Factor * outer, 0));
            if (far != null) far.innerRadius = radius;
        }

        void Request(double3 focus, int budget)
        {
            var c = (float3)focus;
            if (math.distance(c, regionCentre) > 60f)
            {
                regionCentre = c;
                var ext = new float3(radius + ChunkWorld * 2, verticalRadius + ChunkWorld * 2, radius + ChunkWorld * 2);
                net.Collect(c - ext, c + ext, 8f, regionPrims);
            }
            int3 k0 = (int3)math.floor((c - new float3(radius, verticalRadius, radius)) / ChunkWorld);
            int3 k1 = (int3)math.floor((c + new float3(radius, verticalRadius, radius)) / ChunkWorld);
            // nearest first
            var todo = new List<int3>();
            for (int x = k0.x; x <= k1.x; x++)
            for (int y = k0.y; y <= k1.y; y++)
            for (int z = k0.z; z <= k1.z; z++)
            {
                var key = new int3(x, y, z);
                if (chunks.ContainsKey(key) || IsPending(key)) continue;
                var centre = ((float3)key + 0.5f) * ChunkWorld;
                if (math.lengthsq((centre - c).xz) > (radius + ChunkWorld) * (radius + ChunkWorld)) continue;
                // far ring: skip chunks entirely inside the near streamer's radius (the near mesh draws those)
                if (renderOnly && math.length(math.abs((centre - c).xz) + ChunkWorld * 0.5f) < innerRadius) continue;
                todo.Add(key);
            }
            todo.Sort((a, b) => math.distancesq(((float3)a + 0.5f) * ChunkWorld, c).CompareTo(math.distancesq(((float3)b + 0.5f) * ChunkWorld, c)));
            int scheduled = 0;
            foreach (var key in todo)
            {
                if (scheduled >= budget) break;
                float3 mn = (float3)key * ChunkWorld, mx = mn + ChunkWorld;
                tmp.Clear();
                // generous margin + canonical order: two chunks sharing a face must evaluate the exact same
                // field there (smooth-union is order dependent), otherwise hairline cracks leak light
                foreach (var p in regionPrims) if (TunnelNetwork.OverlapsBox(p, mn, mx, 16f)) tmp.Add(p);
                tmp.Sort(TunnelNetwork.Compare);
                if (tmp.Count == 0) { chunks[key] = new Chunk { empty = true }; continue; }
                var prims = new NativeArray<TunnelPrim>(tmp.ToArray(), Allocator.Persistent);
                var pd = new Pending
                {
                    key = key, prims = prims,
                    v = new NativeList<float3>(4096, Allocator.Persistent),
                    n = new NativeList<float3>(4096, Allocator.Persistent),
                    idx = new NativeList<int>(16384, Allocator.Persistent),
                    stats = new NativeArray<int>(2, Allocator.Persistent),
                };
                var job = new TunnelChunkJob
                {
                    prims = prims, baseCell = key * N, origin = mn, s = voxel, box = pit != null ? pit.Box : new float4(1e9f), boxMinY = pit != null ? pit.BoxMinY : 1e9f,
                    vertices = pd.v, normals = pd.n, indices = pd.idx, stats = pd.stats,
                };
                pd.handle = job.Schedule();
                pending.Add(pd);
                scheduled++;
            }
        }

        /// <summary>
        /// Tests (S4): mesh the chunk holding a frame point (as the game does) and count floating islands: found = before
        /// the filter, left = after it (0 when the filter works).
        /// </summary>
        public static (int found, int left) IslandsAt(TunnelNetwork network, float3 framePoint, float vox)
        {
            float cw = N * vox;
            var key = (int3)math.floor(framePoint / cw);
            float3 mn = (float3)key * cw, mx = mn + cw;
            var list = new List<TunnelPrim>(); network.Collect(mn - 40f, mx + 40f, 8f, list);
            var sel = new List<TunnelPrim>(); foreach (var p in list) if (TunnelNetwork.OverlapsBox(p, mn, mx, 16f)) sel.Add(p);
            sel.Sort(TunnelNetwork.Compare);
            if (sel.Count == 0) return (0, 0);
            var prims = new NativeArray<TunnelPrim>(sel.ToArray(), Allocator.TempJob);
            var v = new NativeList<float3>(4096, Allocator.TempJob); var n = new NativeList<float3>(4096, Allocator.TempJob);
            var idx = new NativeList<int>(16384, Allocator.TempJob); var st = new NativeArray<int>(2, Allocator.TempJob);
            new TunnelChunkJob { prims = prims, baseCell = key * N, origin = mn, s = vox, box = new float4(1e9f), boxMinY = 1e9f,
                                 vertices = v, normals = n, indices = idx, stats = st }.Run();
            var res = (st[0] + st[1], st[1]);
            prims.Dispose(); v.Dispose(); n.Dispose(); idx.Dispose(); st.Dispose();
            return res;
        }

        /// <summary>
        /// Tests (S4): mesh a 2x2x2 block of chunks around a frame point, weld the chunk meshes (shared border vertices are
        /// bit-identical) and count small components that touch none of the block's outer faces: islands that cross a
        /// chunk face, which the per-chunk filter can't see. filter = the game's per-chunk filter on.
        /// </summary>
        public static int IslandsBlock(TunnelNetwork network, float3 framePoint, float vox, bool filter)
        {
            float cw = N * vox;
            var k0 = (int3)math.floor(framePoint / cw - 0.5f);
            var pos = new List<float3>(); var tri = new List<int>();
            var list = new List<TunnelPrim>();
            float3 bmn = (float3)k0 * cw, bmx = bmn + 2f * cw;
            network.Collect(bmn - 40f, bmx + 40f, 8f, list);
            for (int dz = 0; dz < 2; dz++) for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
            {
                var key = k0 + new int3(dx, dy, dz);
                float3 mn = (float3)key * cw, mx = mn + cw;
                var sel = new List<TunnelPrim>(); foreach (var p in list) if (TunnelNetwork.OverlapsBox(p, mn, mx, 16f)) sel.Add(p);
                sel.Sort(TunnelNetwork.Compare);
                if (sel.Count == 0) continue;
                var prims = new NativeArray<TunnelPrim>(sel.ToArray(), Allocator.TempJob);
                var v = new NativeList<float3>(4096, Allocator.TempJob); var n = new NativeList<float3>(4096, Allocator.TempJob);
                var idx = new NativeList<int>(16384, Allocator.TempJob); var st = new NativeArray<int>(2, Allocator.TempJob);
                new TunnelChunkJob { prims = prims, baseCell = key * N, origin = mn, s = vox, box = new float4(1e9f), boxMinY = 1e9f,
                                     vertices = v, normals = n, indices = idx, stats = st, keepIslands = !filter }.Run();
                int b0 = pos.Count;
                for (int i = 0; i < v.Length; i++) pos.Add(v[i] + mn);
                for (int i = 0; i < idx.Length; i++) tri.Add(b0 + idx[i]);
                prims.Dispose(); v.Dispose(); n.Dispose(); idx.Dispose(); st.Dispose();
            }
            if (tri.Count == 0) return 0;
            // weld by position
            var weld = new Dictionary<long, int>(); var map = new int[pos.Count];
            for (int i = 0; i < pos.Count; i++)
            {
                var q = (int3)math.round(pos[i] * 64f);
                long h = ((long)q.x * 73856093L) ^ ((long)q.y * 19349663L) ^ ((long)q.z * 83492791L);
                if (!weld.TryGetValue(h, out int w)) { w = i; weld[h] = i; }
                map[i] = w;
            }
            var par = new int[pos.Count]; for (int i = 0; i < par.Length; i++) par[i] = i;
            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
            for (int t = 0; t < tri.Count; t += 3)
            {
                int a = Find(map[tri[t]]), b = Find(map[tri[t + 1]]), c = Find(map[tri[t + 2]]);
                if (b != a) par[b] = a; c = Find(c); if (c != a) par[c] = a;
            }
            var lo = new Dictionary<int, float3>(); var hi = new Dictionary<int, float3>(); var edge = new HashSet<int>();
            float e0 = 0.5f * vox, e1 = 2f * cw - 0.5f * vox;
            foreach (int ti in tri)
            {
                int r = Find(map[ti]); var p = pos[ti]; var l = p - bmn;
                lo[r] = lo.TryGetValue(r, out var a) ? math.min(a, p) : p; hi[r] = hi.TryGetValue(r, out var b) ? math.max(b, p) : p;
                if (math.any(l < e0) || math.any(l > e1)) edge.Add(r);
            }
            int count = 0;
            foreach (var r in lo.Keys) if (!edge.Contains(r) && math.cmax(hi[r] - lo[r]) < TunnelChunkJob.IslandMax) count++;
            return count;
        }

        bool IsPending(int3 key) { foreach (var p in pending) if (p.key.Equals(key)) return true; return false; }

        void Complete(bool force)
        {
            var bake = new List<(Chunk c, Mesh m)>();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (!force && !p.handle.IsCompleted) continue;
                p.handle.Complete();
                IslandsDropped += p.stats[0];
                var ch = new Chunk();
                if (p.idx.Length == 0) ch.empty = true;
                else
                {
                    var mesh = new Mesh { name = $"Tunnel_{p.key.x}_{p.key.y}_{p.key.z}", indexFormat = IndexFormat.UInt32 };
                    mesh.SetVertices(p.v.AsArray());
                    mesh.SetNormals(p.n.AsArray());
                    mesh.SetIndices(p.idx.AsArray(), MeshTopology.Triangles, 0);
                    mesh.RecalculateBounds();
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(root, false);
                    go.transform.position = WorldOrigin.FrameToWorld(new double3(p.key.x, p.key.y, p.key.z) * ChunkWorld);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = material;
                    mr.shadowCastingMode = renderOnly ? ShadowCastingMode.Off : ShadowCastingMode.TwoSided;
                    ch.go = go; ch.mesh = mesh;
                    if (!renderOnly) bake.Add((ch, mesh));
                }
                chunks[p.key] = ch;
                Dispose(p);
                pending.RemoveAt(i);
            }
            if (bake.Count > 0)
            {
                var ids = new NativeArray<EntityId>(bake.Count, Allocator.TempJob);
                for (int i = 0; i < bake.Count; i++) ids[i] = bake[i].m.GetEntityId();
                new BakeColliderJob { meshIds = ids }.Schedule(ids.Length, 1).Complete();
                ids.Dispose();
                foreach (var (c, m) in bake)
                {
                    var mc = c.go.AddComponent<MeshCollider>();
                    mc.cookingOptions = MeshColliderCookingOptions.UseFastMidphase | MeshColliderCookingOptions.CookForFasterSimulation |
                                        MeshColliderCookingOptions.WeldColocatedVertices;
                    mc.sharedMesh = m;
                }
            }
        }

        void Evict(double3 focus)
        {
            if (chunks.Count < 400) return;
            var c = (float3)focus;
            float lim = radius + ChunkWorld * 3;
            var drop = new List<int3>();
            foreach (var kv in chunks)
            {
                var centre = ((float3)kv.Key + 0.5f) * ChunkWorld;
                bool nearOther = false;
                if (!renderOnly)
                foreach (var t in new[] { physicsFocus, physicsFocus2 })
                    if (t != null && t.gameObject.activeInHierarchy && math.distance(centre, (float3)WorldOrigin.WorldToFrame(t.position)) < ChunkWorld * 3) nearOther = true;
                if (!nearOther && math.distance(centre.xz, c.xz) > lim) drop.Add(kv.Key);
            }
            foreach (var k in drop)
            {
                var ch = chunks[k];
                if (ch.go) Util.Kill(ch.go);
                if (ch.mesh) Util.Kill(ch.mesh);
                chunks.Remove(k);
            }
        }
    }

    /// <summary>Samples the network SDF over one chunk (+1 voxel border) and meshes it with surface nets.</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct TunnelChunkJob : IJob
    {
        [ReadOnly] public NativeArray<TunnelPrim> prims;
        public int3 baseCell;      // global voxel index of the chunk's min corner (key * N)
        public float3 origin;      // frame position of the chunk's min corner (= baseCell * s)
        public float s;            // voxel size (m)
        public float4 box;         // pit box (xz min/max) - owned by the pit mesh
        public float boxMinY;
        public NativeList<float3> vertices, normals;
        public NativeList<int> indices;
        /// <summary>Round 12 (S4): keep the small mesh islands that touch no chunk face (tests: count them unfiltered).</summary>
        public bool keepIslands;
        /// <summary>[0] = islands dropped, [1] = islands left after the filter (length 2; optional).</summary>
        public NativeArray<int> stats;
        /// <summary>An island smaller than this (bounding-box extent, m) is a sliver, not cave.</summary>
        public const float IslandMax = 12f;

        const int N = CaveStreamer.N;
        const int G = N + 3;       // samples: -1 .. N+1

        float F(float3 p) => -TunnelSdf.Eval(prims, prims.Length, p, out _, out _);   // > 0 air

        // sample positions from global integer voxel coords: neighbouring chunks evaluate bit-identical points
        float3 P(int i, int j, int k) => (float3)(baseCell + new int3(i - 1, j - 1, k - 1)) * s;

        public void Execute()
        {
            var d = new NativeArray<float>(G * G * G, Allocator.Temp);
            bool anyAir = false, anyRock = false;
            for (int k = 0; k < G; k++)
            for (int j = 0; j < G; j++)
            for (int i = 0; i < G; i++)
            {
                float v = F(P(i, j, k));
                d[(k * G + j) * G + i] = v;
                if (v > 0) anyAir = true; else anyRock = true;
            }
            if (!anyAir || !anyRock) return;

            const int C = G - 1;   // cells: -1 .. N
            var cellVert = new NativeArray<int>(C * C * C, Allocator.Temp);
            for (int q = 0; q < cellVert.Length; q++) cellVert[q] = -1;
            for (int k = 0; k < C; k++)
            for (int j = 0; j < C; j++)
            for (int i = 0; i < C; i++)
            {
                int mask = 0;
                for (int q = 0; q < 8; q++)
                    if (d[((k + ((q >> 2) & 1)) * G + j + ((q >> 1) & 1)) * G + i + (q & 1)] > 0) mask |= 1 << q;
                if (mask == 0 || mask == 255) continue;
                float3 sum = 0; int cnt = 0;
                for (int e = 0; e < 12; e++)
                {
                    int axis = e >> 2, o = e & 3;
                    int3 a = axis == 0 ? new int3(0, o & 1, o >> 1) : axis == 1 ? new int3(o & 1, 0, o >> 1) : new int3(o & 1, o >> 1, 0);
                    int3 b = a; b[axis] = 1;
                    float da = d[((k + a.z) * G + j + a.y) * G + i + a.x], db = d[((k + b.z) * G + j + b.y) * G + i + b.x];
                    if ((da > 0) == (db > 0)) continue;
                    sum += math.lerp((float3)a, (float3)b, da / (da - db));
                    cnt++;
                }
                float3 lp = (new float3(i - 1, j - 1, k - 1) + sum / cnt) * s;     // chunk-local
                float3 wp = P(i, j, k) + (sum / cnt) * s;
                cellVert[(k * C + j) * C + i] = vertices.Length;
                vertices.Add(lp);
                const float h = 0.4f;
                float3 g = new float3(F(wp + new float3(h, 0, 0)) - F(wp - new float3(h, 0, 0)),
                                      F(wp + new float3(0, h, 0)) - F(wp - new float3(0, h, 0)),
                                      F(wp + new float3(0, 0, h)) - F(wp - new float3(0, 0, h)));
                float gl = math.length(g);
                normals.Add(gl > 1e-6f ? g / gl : new float3(0, 1, 0));   // F > 0 is air: grad F points into the air
            }

            // quads for edges whose start sample lies inside this chunk (sample index 1..N)
            for (int k = 1; k <= N; k++)
            for (int j = 1; j <= N; j++)
            for (int i = 1; i <= N; i++)
            {
                float d0 = d[(k * G + j) * G + i];
                bool s0 = d0 > 0;
                // the pit mesh owns the box; stop PitOverlap voxels inside its faces so the two meshes overlap
                float3 sp = P(i, j, k);
                float ov = CaveStreamer.PitOverlap * s;
                bool inPit = sp.y >= boxMinY + ov && sp.x > box.x + ov && sp.x < box.z - ov && sp.z > box.y + ov && sp.z < box.w - ov;
                if (inPit) continue;
                // x edge: cells (i, j-1..j, k-1..k) in cell index space (cell index = sample index)
                if ((d[(k * G + j) * G + i + 1] > 0) != s0) Quad(V(cellVert, i, j - 1, k - 1), V(cellVert, i, j, k - 1), V(cellVert, i, j, k), V(cellVert, i, j - 1, k), s0);
                if ((d[(k * G + j + 1) * G + i] > 0) != s0) Quad(V(cellVert, i - 1, j, k - 1), V(cellVert, i - 1, j, k), V(cellVert, i, j, k), V(cellVert, i, j, k - 1), s0);
                if ((d[((k + 1) * G + j) * G + i] > 0) != s0) Quad(V(cellVert, i - 1, j - 1, k), V(cellVert, i, j - 1, k), V(cellVert, i, j, k), V(cellVert, i - 1, j, k), s0);
            }
            // orientation: faces must point into the air
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                float3 fn = math.cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                if (math.dot(fn, normals[a] + normals[b] + normals[c]) < 0) { indices[t + 1] = c; indices[t + 2] = b; }
            }
            int dropped = keepIslands ? 0 : Islands(true);
            int left = Islands(false);
            if (stats.IsCreated && stats.Length >= 2) { stats[0] += dropped; stats[1] += left; }
        }

        static int Root(NativeArray<int> par, int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }

        /// <summary>
        /// Connected components of the triangle mesh; a component that touches no chunk face can't join the rest of the
        /// cave in any chunk, so if it is small it is a detached blob (smooth-union + wall noise in a junction corner).
        /// Returns how many there are; with <paramref name="remove"/> their triangles are dropped.
        /// </summary>
        int Islands(bool remove)
        {
            int nv = vertices.Length;
            if (nv == 0 || indices.Length == 0) return 0;
            var par = new NativeArray<int>(nv, Allocator.Temp);
            for (int i = 0; i < nv; i++) par[i] = i;
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = Root(par, indices[t]);
                int b = Root(par, indices[t + 1]); if (b != a) par[b] = a;
                int c = Root(par, indices[t + 2]); if (c != a) par[c] = a;
            }
            var lo = new NativeArray<float3>(nv, Allocator.Temp); var hi = new NativeArray<float3>(nv, Allocator.Temp);
            var border = new NativeArray<bool>(nv, Allocator.Temp); var used = new NativeArray<bool>(nv, Allocator.Temp);
            for (int i = 0; i < nv; i++) { lo[i] = new float3(1e9f); hi[i] = new float3(-1e9f); }
            float e0 = 0.5f * s, e1 = (N - 0.5f) * s;
            for (int t = 0; t < indices.Length; t++)
            {
                int v = indices[t], r = Root(par, v); var p = vertices[v];
                used[r] = true; lo[r] = math.min(lo[r], p); hi[r] = math.max(hi[r], p);
                if (math.any(p < e0) || math.any(p > e1)) border[r] = true;
            }
            var drop = new NativeArray<bool>(nv, Allocator.Temp);
            int count = 0;
            for (int r = 0; r < nv; r++)
            {
                if (!used[r] || border[r] || math.cmax(hi[r] - lo[r]) >= IslandMax) continue;
                drop[r] = true; count++;
            }
            if (remove && count > 0)
            {
                int w = 0;
                for (int t = 0; t < indices.Length; t += 3)
                {
                    if (drop[Root(par, indices[t])]) continue;
                    indices[w] = indices[t]; indices[w + 1] = indices[t + 1]; indices[w + 2] = indices[t + 2]; w += 3;
                }
                indices.ResizeUninitialized(w);
            }
            return count;
        }

        static int V(NativeArray<int> cv, int i, int j, int k)
        {
            const int C = G - 1;
            if (i < 0 || j < 0 || k < 0 || i >= C || j >= C || k >= C) return -1;
            return cv[(k * C + j) * C + i];
        }

        void Quad(int a, int b, int c, int d2, bool flip)
        {
            if (a < 0 || b < 0 || c < 0 || d2 < 0) return;
            indices.Add(a); indices.Add(b); indices.Add(c);
            indices.Add(a); indices.Add(c); indices.Add(d2);
        }
    }
}
