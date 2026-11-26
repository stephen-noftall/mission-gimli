using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Terrain;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// The pit: builds the mesh + colliders around the Marius Hills Pit (surface, funnel, shaft, tube mouth)
    /// at startup, and owns the box the heightfield terrain leaves empty for it. The tunnels beyond are
    /// streamed by <see cref="CaveStreamer"/> from the same <see cref="TunnelNetwork"/>.
    /// </summary>
    public class CaveBuilder : MonoBehaviour
    {
        public Material material;
        /// <summary>Tube walls inside the pit box (beyond the shaft wall, under the rock): same material as the streamed tunnels.</summary>
        public Material tunnelMaterial;
        public float cell = 1f;
        public float binSize = 32f;

        public CaveField Field { get; private set; }
        public TunnelNetwork Network { get; private set; }
        public float4 Box { get; private set; }      // frame xz min/max
        public float BoxMinY { get; private set; }

        NativeArray<TunnelPrim> boxPrims;

        /// <summary>Step 1: network + pit box (call before terrain is built).</summary>
        public void Plan(ref HeightField hf)
        {
            float rim = hf.rimHeight;
            float mouthFloor = rim - 48.5f;
            var hfCopy = hf;
            Network = new TunnelNetwork(WorldSeed.For("tunnels"), hf.pitCentre, hf.pitAxis, rim, mouthFloor, hf.innerA * 1.1f)
            {
                surfaceAt = xz => hfCopy.DataHeight(xz.x, xz.y),
            };
            // round 11: the lava-flow layout, seeded by the mission code (MOON unless another code was launched)
            Network.UseFlowLayout(Mission.TubeCode);
            float r = hf.funnelA + 24f;
            float2 mn = math.floor(hf.pitCentre - r), mx = math.ceil(hf.pitCentre + r);
            Box = new float4(mn.x, mn.y, mx.x, mx.y);
            hf.caveBox = Box;
            hf.hasCaveBox = 1;

            var list = new List<TunnelPrim>();
            Network.Collect(new float3(mn.x, mouthFloor - 20, mn.y), new float3(mx.x, rim + 20, mx.y), 10f, list);
            if (boxPrims.IsCreated) boxPrims.Dispose();
            boxPrims = new NativeArray<TunnelPrim>(list.ToArray(), Allocator.Persistent);
            // crane platform lands on the pit floor, a little towards the far (uphill) side of the shaft
            var landing = hf.pitCentre - hf.pitAxis * 6f;
            Field = new CaveField
            {
                hf = hf, prims = boxPrims, primCount = boxPrims.Length,
                rim = rim, pitFloor = rim - 40f, mouthFloorY = mouthFloor, landingSpot = landing,
            };
            {
                // ramp: from the outer edge of the pad blend (19 m past the landing centre) to 8.5 m inside the pit box
                // face, where the tunnel chunks' 2-voxel overlap begins (they don't know about the fill)
                float2 ax = hf.pitAxis;
                float tFace = math.min(ax.x != 0 ? math.max((mn.x - hf.pitCentre.x) / ax.x, (mx.x - hf.pitCentre.x) / ax.x) : 1e9f,
                                       ax.y != 0 ? math.max((mn.y - hf.pitCentre.y) / ax.y, (mx.y - hf.pitCentre.y) / ax.y) : 1e9f);
                var f0 = Field;
                f0.rampU0 = math.dot(landing - hf.pitCentre, ax) + 19f;
                f0.rampU1 = tFace - CaveStreamer.PitOverlap * CaveStreamer.Voxel - 4f;
                var a = Network.mouthFloor; var b = Network.NodePos(0, 0, 0);
                float ua = math.dot(a.xz - hf.pitCentre, ax), ub = math.dot(b.xz - hf.pitCentre, ax);
                f0.rampEndY = math.lerp(a.y, b.y, math.saturate((f0.rampU1 - ua) / (ub - ua)));
                if (Network.IsFlow)
                {
                    // the flow layout's floor is not a straight line from the mouth: meet the real (SDF) tube floor there
                    var exz = hf.pitCentre + ax * f0.rampU1;
                    float fy = Network.FloorY(new float3(exz.x, f0.rampEndY, exz.y), 4f, 6f);
                    if (!float.IsNaN(fy)) f0.rampEndY = fy;
                }
                Field = f0;
                Debug.Log($"[Cave] floor ramp u {f0.rampU0:0.0}..{f0.rampU1:0.0} m, {f0.pitFloor + 1.5f - f0.rampEndY:0.0} m drop, grade {f0.RampGradeDeg:0.0} deg");
            }
        }

        void OnDestroy()
        {
            if (boxPrims.IsCreated) boxPrims.Dispose();
        }

        /// <summary>Refresh the height function used by the pit mesh (e.g. after levelling a work pad).</summary>
        public void SetHeightField(HeightField hf) { var f = Field; f.hf = hf; Field = f; }

        /// <summary>Frame xz on the ramp centreline at distance u along the pit axis (+ lateral offset v).</summary>
        public float2 RampXZ(float u, float v = 0f)
        {
            var hf = Field.hf;
            return hf.pitCentre + hf.pitAxis * u + new float2(hf.pitAxis.y, -hf.pitAxis.x) * v;
        }

        /// <summary>Step 2: generate the pit mesh + colliders (blocking, ~1 s).</summary>
        public void Build()
        {
            var f = Field;
            float yMin = f.mouthFloorY - 6f;
            float yMax = float.MinValue;
            for (float x = Box.x; x <= Box.z; x += 4)
            for (float z = Box.y; z <= Box.w; z += 4)
                yMax = math.max(yMax, f.hf.Height(x, z, 4f));
            yMax += 4f;
            var origin = new float3(Box.x, math.floor(yMin), Box.y);
            BoxMinY = origin.y;
            var n = new int3((int)math.round((Box.z - Box.x) / cell) + 1,
                             (int)math.ceil((yMax - origin.y) / cell) + 1,
                             (int)math.round((Box.w - Box.y) / cell) + 1);

            var surface = new NativeArray<float>(n.x * n.z, Allocator.TempJob);
            var density = new NativeArray<float>(n.x * n.y * n.z, Allocator.TempJob);
            var verts = new NativeList<float3>(200000, Allocator.TempJob);
            var norms = new NativeList<float3>(200000, Allocator.TempJob);
            var idx = new NativeList<int>(1200000, Allocator.TempJob);

            var h1 = new SurfaceColumnJob { hf = f.hf, origin = origin, cell = cell, nx = n.x, surface = surface }.Schedule(n.x * n.z, 64);
            var h2 = new SampleDensityJob { field = f, origin = origin, cell = cell, n = n, surface = surface, density = density }.Schedule(n.z, 1, h1);
            var h3 = new SurfaceNetsJob { field = f, origin = origin, cell = cell, n = n, density = density, surface = surface, vertices = verts, normals = norms, indices = idx }.Schedule(h2);
            h3.Complete();

            BuildBins(verts.AsArray(), norms.AsArray(), idx.AsArray());
            Debug.Log($"[Cave] pit grid {n}, {verts.Length} verts, {idx.Length / 3} tris, box {Box}, {f.primCount} tunnel prims");

            surface.Dispose(); density.Dispose(); verts.Dispose(); norms.Dispose(); idx.Dispose();
        }

        void BuildBins(NativeArray<float3> v, NativeArray<float3> nrm, NativeArray<int> idx)
        {
            var f = Field;
            var bins = new Dictionary<int2, (List<Vector3> v, List<Vector3> n, List<int> t, List<int> tube, Dictionary<int, int> map)>();
            for (int t = 0; t < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                float3 pa = v[a], pb = v[b], pc = v[c];
                float3 fn = math.cross(pb - pa, pc - pa);
                if (math.dot(fn, nrm[a] + nrm[b] + nrm[c]) < 0) { (b, c) = (c, b); }
                float3 cen = (pa + pb + pc) / 3f;
                var key = new int2((int)math.floor(cen.x / binSize), (int)math.floor(cen.z / binSize));
                if (!bins.TryGetValue(key, out var bin))
                {
                    bin = (new List<Vector3>(), new List<Vector3>(), new List<int>(), new List<int>(), new Dictionary<int, int>());
                    bins[key] = bin;
                }
                int Map(int src)
                {
                    if (!bin.map.TryGetValue(src, out int dst))
                    {
                        dst = bin.v.Count;
                        bin.map[src] = dst;
                        float3 lp = v[src] - new float3(key.x * binSize, 0, key.y * binSize);
                        bin.v.Add(lp); bin.n.Add(nrm[src]);
                    }
                    return dst;
                }
                var dst = tunnelMaterial != null && IsTubeWall(f, cen) ? bin.tube : bin.t;
                dst.Add(Map(a)); dst.Add(Map(b)); dst.Add(Map(c));
            }

            var meshes = new List<Mesh>();
            // the pit's tube walls are fixed geometry near the mouth: never clipped by the near/far hand-off
            Material pitTube = null;
            if (tunnelMaterial != null) { pitTube = new Material(tunnelMaterial) { name = "CaveRockPit" }; pitTube.SetFloat("_FarRing", -1f); }
            foreach (var kv in bins)
            {
                var mesh = new Mesh { name = $"Pit_{kv.Key.x}_{kv.Key.y}", indexFormat = IndexFormat.UInt32 };
                bool hasTube = kv.Value.tube.Count > 0;
                mesh.SetVertices(kv.Value.v); mesh.SetNormals(kv.Value.n);
                mesh.subMeshCount = hasTube ? 2 : 1;
                mesh.SetTriangles(kv.Value.t, 0);
                if (hasTube) mesh.SetTriangles(kv.Value.tube, 1);
                mesh.RecalculateBounds();
                var go = new GameObject(mesh.name) { layer = MoonBaseAlpha.Rendering.RenderLayers.Pit };
                go.transform.SetParent(transform, false);
                go.transform.position = WorldOrigin.FrameToWorld(new double3(kv.Key.x * binSize, 0, kv.Key.y * binSize));
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = hasTube ? new[] { material, pitTube } : new[] { material };
                mr.shadowCastingMode = ShadowCastingMode.TwoSided;
                meshes.Add(mesh);
            }
            var ids = new NativeArray<EntityId>(meshes.Count, Allocator.TempJob);
            for (int i = 0; i < meshes.Count; i++) ids[i] = meshes[i].GetEntityId();
            new BakeColliderJob { meshIds = ids }.Schedule(ids.Length, 1).Complete();
            ids.Dispose();
            foreach (Transform child in transform)
            {
                var mc = child.gameObject.AddComponent<MeshCollider>();
                mc.cookingOptions = MeshColliderCookingOptions.UseFastMidphase | MeshColliderCookingOptions.CookForFasterSimulation |
                                    MeshColliderCookingOptions.WeldColocatedVertices;
                mc.sharedMesh = child.GetComponent<MeshFilter>().sharedMesh;
            }
        }

        /// <summary>
        /// Pit-mesh triangle that belongs to the first tube (mouth to the pit-box face): outside the shaft
        /// ellipse (radius > 1.3) and more than 3 m below the local surface, or under the deep overhang (radius > 1.05, > 20 m down). These get the tunnel material, so
        /// the tube looks the same before and after the pit-box face (the regolith shader is sun-lit).
        /// </summary>
        public static bool IsTubeWall(in CaveField f, float3 p)
        {
            float2 d = p.xz - f.hf.pitCentre;
            float u = math.dot(d, f.hf.pitAxis), v = d.x * f.hf.pitAxis.y - d.y * f.hf.pitAxis.x;
            float re = math.sqrt(u * u / (f.hf.innerA * f.hf.innerA) + v * v / (f.hf.innerB * f.hf.innerB));
            float depth = f.hf.DataHeight(p.x, p.z) - p.y;
            // + the deep overhang (shaft flare below ~28 m): floor and walls under the rim rock get the unlit rock
            // material too - with sun on them, only the shadow maps kept them dark and the coarse cascades leaked
            return (re > 1.3f && depth > 3f) || (re > 1.05f && depth > 20f);
        }

        /// <summary>A floor point inside the first tube, the given distance from the pit mouth.</summary>
        public double3 TubeFloorPoint(float along, out float2 dir)
        {
            var net = Network;
            var a = net.mouthFloor;
            var b = net.NodePos(0, 0, 0);
            float2 d = math.normalize(b.xz - a.xz);
            dir = d;
            float len = math.length(b.xz - a.xz);
            float t = math.saturate(along / len);
            var p = math.lerp(a, b, t);
            return new double3(p.x, p.y + 1.0f, p.z);
        }

        /// <summary>
        /// Can the sky be seen from here? 1 on the surface and in the open pit, 0 under the roof of a tube
        /// (the terrain above is rock - the sky must not show through gaps in the cave mesh).
        /// </summary>
        public float SkyVisibility(double3 framePos)
        {
            var f = Field;
            float surf = f.hf.DataHeight(framePos.x, framePos.z);
            if ((float)framePos.y > surf - 3f) return 1f;
            float2 d = (float2)framePos.xz - f.hf.pitCentre;
            float u = math.dot(d, f.hf.pitAxis), v = d.x * f.hf.pitAxis.y - d.y * f.hf.pitAxis.x;
            float re = math.sqrt(u * u / (f.hf.innerA * f.hf.innerA) + v * v / (f.hf.innerB * f.hf.innerB));
            return math.saturate((1.25f - re) / 0.3f);   // inside the open shaft: visible; beyond its rim: under rock
        }

        /// <summary>0 at/above the pit floor in daylight .. 1 deep underground (for ambient fill fading).</summary>
        public float Darkness(double3 framePos)
        {
            var f = Field;
            float surf = f.hf.DataHeight(framePos.x, framePos.z);
            float depth = surf - (float)framePos.y;
            if (depth < 5f) return 0f;
            float2 d = (float2)framePos.xz - f.hf.pitCentre;
            float r = math.length(d);
            float inShaft = math.smoothstep(f.hf.innerA * 1.3f, f.hf.innerA * 0.8f, r);
            float deep = math.smoothstep(5f, 30f, depth);
            return deep * (1f - 0.7f * inShaft);
        }
    }
}
