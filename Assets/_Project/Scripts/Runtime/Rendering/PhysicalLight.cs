using System.Collections.Generic;
using UnityEngine;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>
    /// A light with a physical intensity: lux for directional lights, candela for spot/point lights.
    /// URP uses the value directly (1/d^2 falloff for punctual lights), so Unity intensity =
    /// physical x camera exposure. <see cref="ExposureController"/> applies that every frame, which
    /// keeps the whole range from 128,000 lux sunlight to a 10 m headlight pool (~100 lux) in a
    /// sane float range for the HDR buffer.
    /// </summary>
    [ExecuteAlways, RequireComponent(typeof(Light))]
    public class PhysicalLight : MonoBehaviour
    {
        public static readonly List<PhysicalLight> All = new();
        public float physicalIntensity = 1000f;
        public Light Light { get; private set; }

        void OnEnable() { Light = GetComponent<Light>(); if (!All.Contains(this)) All.Add(this); }
        void OnDisable() => All.Remove(this);
    }
}
