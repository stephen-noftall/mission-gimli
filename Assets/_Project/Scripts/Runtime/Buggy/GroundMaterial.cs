using System;
using UnityEngine;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// Which soil a wheel is on. Surface = mature regolith. Pit floor / tube mouth = talus (regolith
    /// that slid in, mixed with breakdown). Deeper in the tube = bare basalt: regolith forms from
    /// micrometeorite gardening, which the roof blocks, so the tube floor is essentially clean rock.
    /// </summary>
    public static class GroundMaterial
    {
        /// <summary>Set by the cave system: (world position) -> soil. Null = regolith everywhere.</summary>
        public static Func<Vector3, Soil> Provider;

        public static Soil At(Vector3 world) => Provider != null ? Provider(world) : Soil.Regolith;
    }
}
