namespace MoonBaseAlpha
{
    /// <summary>Physical constants used across the simulation (SI units).</summary>
    public static class Lunar
    {
        /// <summary>Surface gravity at the Marius Hills (GM / r^2, r ~ 1736.3 km).</summary>
        public const float Gravity = 1.625f;
        /// <summary>Mean lunar radius (IAU), also the LOLA / LROC reference sphere.</summary>
        public const double ReferenceRadius = 1737400.0;

        /// <summary>Solar illuminance at 1 AU, lux (no atmosphere).</summary>
        public const float SolarIlluminance = 128000f;
        /// <summary>Sun angular radius at 1 AU, degrees.</summary>
        public const float SunAngularRadiusDeg = 0.2666f;
        /// <summary>Earth geometric albedo (for earthshine).</summary>
        public const float EarthGeometricAlbedo = 0.367f;
        public const double EarthRadiusKm = 6371.0;
        public const double SunRadiusKm = 696000.0;

        /// <summary>Normal albedo of the mare regolith at the site (Marius Hills mare basalt, ~0.07-0.12).</summary>
        public const float RegolithAlbedo = 0.11f;

        // Apollo-derived regolith mechanics (Bekker / Janosi parameters, "typical lunar soil").
        public const float SoilN = 1.0f;            // sinkage exponent
        public const float SoilKc = 1400f;          // cohesive modulus, N/m^(n+1)
        public const float SoilKphi = 820000f;      // frictional modulus, N/m^(n+2)
        public const float SoilCohesion = 170f;     // Pa
        public const float SoilFrictionDeg = 35f;   // internal friction angle
        public const float SoilShearK = 0.018f;     // shear deformation modulus, m
    }
}
