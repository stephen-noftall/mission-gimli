using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MoonBaseAlpha.Cave
{
    /// <summary>
    /// Inertial-navigation log for the map: the track you've driven/walked, and the tunnel segments
    /// your lamps have "seen" (any network tube or chamber within ~120 m of you). Fog of war otherwise.
    /// </summary>
    public class NavTracker : MonoBehaviour
    {
        public struct Edge { public int level; public double3 a, b; public float hw, hv; public bool chamber; }

        public Transform focus;
        public TunnelNetwork network;
        public float MapRange = 1200f;   // metres across the map

        public readonly List<double3> Track = new();
        public readonly List<Edge> KnownEdges = new();
        readonly HashSet<long> known = new();
        readonly List<TunnelPrim> tmp = new();
        float nextScan;

        void Update()
        {
            if (focus == null || network == null) return;
            var f = WorldOrigin.WorldToFrame(focus.position);
            if (Track.Count == 0 || math.distance(Track[Track.Count - 1], f) > 4.0) Track.Add(f);
            if (Track.Count > 20000) Track.RemoveRange(0, 5000);
            if (Time.time >= nextScan) { nextScan = Time.time + 0.5f; Scan(f); }
        }

        public void Scan(double3 f)
        {
            var c = (float3)f;
            network.Collect(c - new float3(120f, 80f, 120f), c + new float3(120f, 80f, 120f), 0f, tmp);
            foreach (var p in tmp)
            {
                if (p.type == TunnelPrim.Shaft) continue;
                long key = ((long)math.hash(p.a) << 32) ^ math.hash(p.b) ^ p.type;
                if (!known.Add(key)) continue;
                var b = p.type == TunnelPrim.Tube ? p.b : p.a + new float3(0.1f, 0, 0.1f);
                KnownEdges.Add(new Edge { level = p.level, a = p.a, b = b, hw = math.max(p.hw, p.hwEnd), hv = p.hv, chamber = p.type == TunnelPrim.Chamber });
            }
        }
    }
}
