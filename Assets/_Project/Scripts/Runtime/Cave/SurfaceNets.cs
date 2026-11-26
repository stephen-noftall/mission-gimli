using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MoonBaseAlpha.Cave
{
    /// <summary>Samples the cave density on a regular grid (parallel over z slices).</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SampleDensityJob : IJobParallelFor
    {
        public CaveField field;
        public float3 origin;      // frame position of grid point (0,0,0)
        public float cell;
        public int3 n;             // grid points per axis
        [ReadOnly] public NativeArray<float> surface;   // n.x * n.z column heights
        [NativeDisableParallelForRestriction] public NativeArray<float> density;

        public void Execute(int k)
        {
            for (int j = 0; j < n.y; j++)
            for (int i = 0; i < n.x; i++)
            {
                float3 p = origin + new float3(i, j, k) * cell;
                float sy = surface[k * n.x + i];
                float above = p.y - sy;
                float f;
                if (above > 3f) f = above;
                else f = math.max(above, -field.CaveSdf(p));
                density[(k * n.y + j) * n.x + i] = f;
            }
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SurfaceColumnJob : IJobParallelFor
    {
        public Terrain.HeightField hf;
        public float3 origin;
        public float cell;
        public int nx;
        [WriteOnly] public NativeArray<float> surface;
        public void Execute(int idx)
        {
            int i = idx % nx, k = idx / nx;
            surface[idx] = hf.Height(origin.x + i * cell, origin.z + k * cell, 0f);
        }
    }

    /// <summary>
    /// Naive surface nets over a density grid: one vertex per sign-changing cell (mean of edge
    /// crossings), one quad per sign-changing edge. Produces a watertight, well-shaped mesh without
    /// marching-cubes tables. Outer cells are snapped to the box's x/z faces so the mesh meets the
    /// heightfield terrain exactly along the box boundary.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SurfaceNetsJob : IJob
    {
        public CaveField field;
        public float3 origin;
        public float cell;
        public int3 n;     // grid points
        [ReadOnly] public NativeArray<float> density;
        [ReadOnly] public NativeArray<float> surface;
        public NativeList<float3> vertices;
        public NativeList<float3> normals;
        public NativeList<int> indices;

        float D(int i, int j, int k) => density[(k * n.y + j) * n.x + i];

        public void Execute()
        {
            int3 c = n - 1;   // cells
            var cellVert = new NativeArray<int>(c.x * c.y * c.z, Allocator.Temp);
            for (int i = 0; i < cellVert.Length; i++) cellVert[i] = -1;

            for (int k = 0; k < c.z; k++)
            for (int j = 0; j < c.y; j++)
            for (int i = 0; i < c.x; i++)
            {
                // corner signs
                int mask = 0;
                for (int q = 0; q < 8; q++)
                    if (D(i + (q & 1), j + ((q >> 1) & 1), k + ((q >> 2) & 1)) > 0) mask |= 1 << q;
                if (mask == 0 || mask == 255) continue;

                float3 sum = 0; int cnt = 0;
                // 12 edges
                for (int e = 0; e < 12; e++)
                {
                    int axis = e >> 2;              // 0:x 1:y 2:z
                    int o = e & 3;
                    int3 a = axis == 0 ? new int3(0, o & 1, o >> 1) : axis == 1 ? new int3(o & 1, 0, o >> 1) : new int3(o & 1, o >> 1, 0);
                    int3 b = a; b[axis] = 1;
                    float da = D(i + a.x, j + a.y, k + a.z), db = D(i + b.x, j + b.y, k + b.z);
                    if ((da > 0) == (db > 0)) continue;
                    float t = da / (da - db);
                    sum += math.lerp((float3)a, (float3)b, t);
                    cnt++;
                }
                float3 local = sum / cnt;
                float3 p = origin + (new float3(i, j, k) + local) * cell;
                if (i == 0) p.x = origin.x;
                if (i == c.x - 1) p.x = origin.x + c.x * cell;
                if (k == 0) p.z = origin.z;
                if (k == c.z - 1) p.z = origin.z + c.z * cell;
                // On the box faces the surface is the terrain: put it exactly on the height function.
                if (i == 0 || k == 0 || i == c.x - 1 || k == c.z - 1)
                {
                    float sy = field.hf.Height(p.x, p.z, 0f);
                    if (math.abs(p.y - sy) < 1.5f * cell) p.y = sy;
                }
                cellVert[(k * c.y + j) * c.x + i] = vertices.Length;
                vertices.Add(p);
                normals.Add(Normal(p));
            }

            // quads
            for (int k = 0; k < n.z; k++)
            for (int j = 0; j < n.y; j++)
            for (int i = 0; i < n.x; i++)
            {
                float d0 = D(i, j, k);
                bool s0 = d0 > 0;
                // x edge
                if (i < c.x && j > 0 && k > 0 && j < c.y + 0 && k < c.z + 0)
                {
                    bool s1 = D(i + 1, j, k) > 0;
                    if (s0 != s1) Quad(V(cellVert, c, i, j - 1, k - 1), V(cellVert, c, i, j, k - 1), V(cellVert, c, i, j, k), V(cellVert, c, i, j - 1, k), s0);
                }
                // y edge
                if (j < c.y && i > 0 && k > 0 && i < c.x && k < c.z)
                {
                    bool s1 = D(i, j + 1, k) > 0;
                    if (s0 != s1) Quad(V(cellVert, c, i - 1, j, k - 1), V(cellVert, c, i - 1, j, k), V(cellVert, c, i, j, k), V(cellVert, c, i, j, k - 1), s0);
                }
                // z edge
                if (k < c.z && i > 0 && j > 0 && i < c.x && j < c.y)
                {
                    bool s1 = D(i, j, k + 1) > 0;
                    if (s0 != s1) Quad(V(cellVert, c, i - 1, j - 1, k), V(cellVert, c, i, j - 1, k), V(cellVert, c, i, j, k), V(cellVert, c, i - 1, j, k), s0);
                }
            }
        }

        static int V(NativeArray<int> cv, int3 c, int i, int j, int k) => cv[(k * c.y + j) * c.x + i];

        void Quad(int a, int b, int c2, int d, bool flip)
        {
            if (a < 0 || b < 0 || c2 < 0 || d < 0) return;
            // choose the shorter diagonal
            bool diag = math.lengthsq(vertices[a] - vertices[c2]) <= math.lengthsq(vertices[b] - vertices[d]);
            if (!flip)
            {
                if (diag) { T(a, b, c2); T(a, c2, d); } else { T(a, b, d); T(b, c2, d); }
            }
            else
            {
                if (diag) { T(a, c2, b); T(a, d, c2); } else { T(a, d, b); T(b, d, c2); }
            }
        }

        void T(int a, int b, int c2) { indices.Add(a); indices.Add(b); indices.Add(c2); }

        float F(float3 p)
        {
            float sy = field.hf.Height(p.x, p.z, 0f);
            float above = p.y - sy;
            return above > 3f ? above : math.max(above, -field.CaveSdf(p));
        }

        float3 Normal(float3 p)
        {
            const float h = 0.35f;
            float3 g = new float3(F(p + new float3(h, 0, 0)) - F(p - new float3(h, 0, 0)),
                                  F(p + new float3(0, h, 0)) - F(p - new float3(0, h, 0)),
                                  F(p + new float3(0, 0, h)) - F(p - new float3(0, 0, h)));
            float l = math.length(g);
            return l > 1e-6f ? g / l : new float3(0, 1, 0);
        }
    }
}
