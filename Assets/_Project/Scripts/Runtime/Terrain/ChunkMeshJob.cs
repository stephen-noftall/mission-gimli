using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoonBaseAlpha.Terrain
{
    /// <summary>
    /// Builds one square terrain chunk mesh (res x res quads) from the height function.
    /// Vertices are relative to <see cref="centre"/> (frame coordinates) so they stay small floats.
    /// Optional skirts hide cracks between LOD levels; quads inside the cave box are dropped
    /// (the cave mesh owns that area).
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct ChunkMeshJob : IJob
    {
        public HeightField hf;
        public double2 min;          // frame xz of the chunk's SW corner
        public double3 centre;       // frame position the vertices are relative to
        public float spacing;
        public int res;
        public float minFeature;
        public float skirtDepth;     // 0 = no skirts
        public bool normals;
        public Mesh.MeshData mesh;
        public NativeArray<float> heightRange;   // out: min, max (relative to centre.y)

        static bool InBox(float4 b, double x0, double z0, double x1, double z1) =>
            x0 >= b.x && x1 <= b.z && z0 >= b.y && z1 <= b.w;

        public void Execute()
        {
            int n = res + 1;
            int b = n + 2;                                     // grid with 1-vertex border
            var h = new NativeArray<float>(b * b, Allocator.Temp);
            for (int j = 0; j < b; j++)
            for (int i = 0; i < b; i++)
            {
                double x = min.x + (i - 1) * (double)spacing;
                double z = min.y + (j - 1) * (double)spacing;
                h[j * b + i] = hf.Height(x, z, minFeature) - (float)centre.y;
            }

            int skirtVerts = skirtDepth > 0 ? 4 * n : 0;
            int vcount = n * n + skirtVerts;
            int stride = normals ? 6 : 3;
            var attrs = new NativeArray<VertexAttributeDescriptor>(normals ? 2 : 1, Allocator.Temp);
            attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
            if (normals) attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
            mesh.SetVertexBufferParams(vcount, attrs);
            var v = mesh.GetVertexData<float>();

            float ox = (float)(min.x - centre.x), oz = (float)(min.y - centre.z);
            float hmin = float.MaxValue, hmax = float.MinValue;
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int vi = (j * n + i) * stride;
                float y = h[(j + 1) * b + (i + 1)];
                v[vi] = ox + i * spacing; v[vi + 1] = y; v[vi + 2] = oz + j * spacing;
                hmin = math.min(hmin, y); hmax = math.max(hmax, y);
                if (normals)
                {
                    float hl = h[(j + 1) * b + i], hr = h[(j + 1) * b + i + 2];
                    float hd = h[j * b + i + 1], hu = h[(j + 2) * b + i + 1];
                    float3 nn = math.normalize(new float3(hl - hr, 2 * spacing, hd - hu));
                    v[vi + 3] = nn.x; v[vi + 4] = nn.y; v[vi + 5] = nn.z;
                }
            }

            // skirts: S, N, W, E edges
            if (skirtVerts > 0)
            {
                int k = n * n;
                for (int e = 0; e < 4; e++)
                for (int t = 0; t < n; t++, k++)
                {
                    int src = e == 0 ? t : e == 1 ? (n - 1) * n + t : e == 2 ? t * n : t * n + n - 1;
                    int si = src * stride, di = k * stride;
                    v[di] = v[si]; v[di + 1] = v[si + 1] - skirtDepth; v[di + 2] = v[si + 2];
                    if (normals) { v[di + 3] = v[si + 3]; v[di + 4] = v[si + 4]; v[di + 5] = v[si + 5]; }
                }
                hmin -= skirtDepth;
            }

            // indices
            var idx = new NativeList<int>(res * res * 6 + skirtVerts * 6, Allocator.Temp);
            for (int j = 0; j < res; j++)
            for (int i = 0; i < res; i++)
            {
                if (hf.hasCaveBox != 0)
                {
                    double x0 = min.x + i * (double)spacing, x1 = x0 + spacing;
                    double z0 = min.y + j * (double)spacing, z1 = z0 + spacing;
                    if (x0 >= hf.caveBox.x && x1 <= hf.caveBox.z && z0 >= hf.caveBox.y && z1 <= hf.caveBox.w) continue;
                }
                int a = j * n + i, bb = a + 1, c = a + n, d = c + 1;
                // alternate the diagonal for a less directional look
                if (((i + j) & 1) == 0) { idx.Add(a); idx.Add(c); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(bb); }
                else { idx.Add(a); idx.Add(c); idx.Add(bb); idx.Add(bb); idx.Add(c); idx.Add(d); }
            }
            if (skirtVerts > 0)
            {
                int bse = n * n;
                for (int t = 0; t < res; t++)
                {
                    // no skirts over the cave area (they would hang across the pit)
                    if (hf.hasCaveBox != 0)
                    {
                        double a0 = t * (double)spacing, a1 = a0 + spacing;
                        double edgeLen = res * (double)spacing;
                        float4 cb = hf.caveBox;
                        bool sIn = InBox(cb, min.x + a0, min.y, min.x + a1, min.y);
                        bool nIn = InBox(cb, min.x + a0, min.y + edgeLen, min.x + a1, min.y + edgeLen);
                        bool wIn = InBox(cb, min.x, min.y + a0, min.x, min.y + a1);
                        bool eIn = InBox(cb, min.x + edgeLen, min.y + a0, min.x + edgeLen, min.y + a1);
                        int top0s = t, top1s = t + 1, s0s = bse + t, s1s = bse + t + 1;
                        if (!sIn) { idx.Add(top0s); idx.Add(top1s); idx.Add(s1s); idx.Add(top0s); idx.Add(s1s); idx.Add(s0s); }
                        int top0n = (n - 1) * n + t, s0n = bse + n + t;
                        if (!nIn) { idx.Add(top0n); idx.Add(s0n + 1); idx.Add(top0n + 1); idx.Add(top0n); idx.Add(s0n); idx.Add(s0n + 1); }
                        int top0w = t * n, top1w = (t + 1) * n, s0w = bse + 2 * n + t;
                        if (!wIn) { idx.Add(top0w); idx.Add(s0w + 1); idx.Add(top1w); idx.Add(top0w); idx.Add(s0w); idx.Add(s0w + 1); }
                        int top0e = t * n + n - 1, top1e = (t + 1) * n + n - 1, s0e = bse + 3 * n + t;
                        if (!eIn) { idx.Add(top0e); idx.Add(top1e); idx.Add(s0e + 1); idx.Add(top0e); idx.Add(s0e + 1); idx.Add(s0e); }
                        continue;
                    }
                    // south edge (z min) faces -z
                    int top0 = t, top1 = t + 1, s0 = bse + t, s1 = bse + t + 1;
                    idx.Add(top0); idx.Add(top1); idx.Add(s1); idx.Add(top0); idx.Add(s1); idx.Add(s0);
                    // north edge
                    top0 = (n - 1) * n + t; top1 = top0 + 1; s0 = bse + n + t; s1 = s0 + 1;
                    idx.Add(top0); idx.Add(s1); idx.Add(top1); idx.Add(top0); idx.Add(s0); idx.Add(s1);
                    // west edge
                    top0 = t * n; top1 = (t + 1) * n; s0 = bse + 2 * n + t; s1 = s0 + 1;
                    idx.Add(top0); idx.Add(s1); idx.Add(top1); idx.Add(top0); idx.Add(s0); idx.Add(s1);
                    // east edge
                    top0 = t * n + n - 1; top1 = (t + 1) * n + n - 1; s0 = bse + 3 * n + t; s1 = s0 + 1;
                    idx.Add(top0); idx.Add(top1); idx.Add(s1); idx.Add(top0); idx.Add(s1); idx.Add(s0);
                }
            }

            mesh.SetIndexBufferParams(idx.Length, IndexFormat.UInt32);
            mesh.GetIndexData<int>().CopyFrom(idx.AsArray());
            mesh.subMeshCount = 1;
            float size = res * spacing;
            var bounds = new Bounds(new Vector3(ox + size * 0.5f, (hmin + hmax) * 0.5f, oz + size * 0.5f),
                                    new Vector3(size, math.max(0.01f, hmax - hmin), size));
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, idx.Length) { bounds = bounds, vertexCount = vcount },
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            heightRange[0] = hmin; heightRange[1] = hmax;
        }
    }

    public struct BakeColliderJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<EntityId> meshIds;
        public void Execute(int i) => Physics.BakeMesh(meshIds[i], false, MeshColliderCookingOptions.UseFastMidphase |
                                                                             MeshColliderCookingOptions.CookForFasterSimulation |
                                                                             MeshColliderCookingOptions.WeldColocatedVertices);
    }
}
