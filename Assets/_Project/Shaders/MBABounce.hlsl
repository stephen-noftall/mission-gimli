// Soft indirect light shared by the rock shaders (CaveRock, LunarRegolith). Diffuse only: no specular, no glints.
//  - lamp bounce (Rendering/LampBounce.cs): one smoothed soft source per lamp group, E = I / (d^2 + r_s^2)
//  - pit bounce (Rendering/PitBounce.cs): the sunlit lip / walls of the pit relight the shadowed walls and floor;
//    up to 2 soft sources, masked to the pit cylinder below the rim (never leaks into the tubes)
#ifndef MBA_BOUNCE_INCLUDED
#define MBA_BOUNCE_INCLUDED

float4 _MBA_BouncePos[4];      // xyz world, w = softening^2
float4 _MBA_BounceCol[4];      // rgb = intensity x exposure (x colour), a = range
float _MBA_BounceCount;
float4 _MBA_PitBouncePos[2];
float4 _MBA_PitBounceCol[2];
float4 _MBA_PitMask;           // xy = pit centre (world xz), z = radius (m; > 0 = on), w = rim height (world y)
float4 _MBA_PitShape;          // xy = major-axis unit vector (xz), z = semi-major, w = semi-minor (m)

float3 MBA_SoftSource(float4 pos, float4 col, float3 p, float3 N, float wrapK)
{
    float3 d = pos.xyz - p;
    float d2 = dot(d, d);
    float3 L = d * rsqrt(max(d2, 1e-4));
    float wrap = saturate(dot(N, L) * (1 - wrapK) + wrapK);
    float fade = col.a > 0 ? saturate(1 - d2 / (col.a * col.a)) : 1;
    return col.rgb * (wrap * wrap * fade * fade / (d2 + pos.w));
}

// irradiance-like term: multiply by albedo
float3 MBA_LampBounce(float3 p, float3 N)
{
    float3 e = 0;
    [unroll] for (int k = 0; k < 4; k++)
        if (k < (int)_MBA_BounceCount) e += MBA_SoftSource(_MBA_BouncePos[k], _MBA_BounceCol[k], p, N, 0.5);
    return e;
}

float MBA_PitMaskAt(float3 p)
{
    if (_MBA_PitMask.z <= 0) return 0;
    float2 d = p.xz - _MBA_PitMask.xy;
    float u = dot(d, _MBA_PitShape.xy), v = d.x * _MBA_PitShape.y - d.y * _MBA_PitShape.x;
    float re = sqrt(u * u / (_MBA_PitShape.z * _MBA_PitShape.z) + v * v / (_MBA_PitShape.w * _MBA_PitShape.w));
    float inside = 1 - smoothstep(0.97, 1.05, re);  // the shaft ellipse; overhangs beyond it stay dark
    float below = smoothstep(_MBA_PitMask.w + 2, _MBA_PitMask.w - 3, p.y);
    return inside * below;
}

float3 MBA_PitBounce(float3 p, float3 N)
{
    float m = MBA_PitMaskAt(p);
    if (m <= 0) return 0;
    // more directional than the lamp bounce (keeps the wall relief); floors keep their existing look (the user's call:
    // the global "ground from below" + visor fill already stand in for the floor's indirect light)
    float3 e = MBA_SoftSource(_MBA_PitBouncePos[0], _MBA_PitBounceCol[0], p, N, 0.2) + MBA_SoftSource(_MBA_PitBouncePos[1], _MBA_PitBounceCol[1], p, N, 0.2);
    // floors keep their look (see above); ceilings / overhang roofs get none (the tube roof at the mouth stays dark)
    return e * m * (1 - saturate(N.y * 1.25)) * saturate(1 + N.y * 4);
}
#endif
