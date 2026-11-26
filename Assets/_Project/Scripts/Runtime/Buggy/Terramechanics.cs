using Unity.Mathematics;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>Ground material the wheel is on.</summary>
    public struct Soil
    {
        public float n, kc, kphi, c, tanPhi, K;
        public bool rigid;              // bare rock: no sinkage, Coulomb-like friction
        public float rockMu;

        /// <summary>Apollo "typical lunar soil" (Bekker/Janosi), Lunar Sourcebook / LRV studies.</summary>
        public static Soil Regolith => new Soil
        {
            n = Lunar.SoilN, kc = Lunar.SoilKc, kphi = Lunar.SoilKphi, c = Lunar.SoilCohesion,
            tanPhi = math.tan(math.radians(Lunar.SoilFrictionDeg)), K = Lunar.SoilShearK,
        };

        /// <summary>Loose talus / thin dust over rock (pit floor, tube mouth): firmer, rougher.</summary>
        public static Soil Talus => new Soil
        {
            n = 1.1f, kc = 5000f, kphi = 3.0e6f, c = 300f, tanPhi = math.tan(math.radians(38f)), K = 0.012f,
        };

        /// <summary>Bare tube-floor basalt: rigid; wire-mesh + titanium chevrons on rock.</summary>
        public static Soil Basalt => new Soil
        {
            rigid = true, rockMu = 0.75f, K = 0.006f, tanPhi = 0.75f, n = 1, kc = 1, kphi = 1,
        };
    }

    /// <summary>
    /// Wheel-soil interaction for a rigid-ish wire-mesh wheel (Bekker pressure-sinkage,
    /// Janosi-Hanamoto shear, compaction resistance). All quantities SI.
    /// </summary>
    public static class Terramechanics
    {
        public struct Result
        {
            public float sinkage;         // m
            public float contactLength;   // m
            public float maxShear;        // N, Ac + W tan(phi)
            public float compaction;      // N, rolling resistance from soil compaction
        }

        public static Result Contact(in Soil s, float load, float radius, float width)
        {
            var r = new Result();
            if (load <= 0) return r;
            if (s.rigid)
            {
                // flexible wire tyre deflects ~2 cm on rock: contact patch from tyre deflection
                r.sinkage = 0;
                r.contactLength = 2f * math.sqrt(math.max(1e-4f, 2f * radius * 0.02f));
                r.maxShear = s.rockMu * load;
                r.compaction = 0.012f * load;   // hysteresis of the wire mesh only
                return r;
            }
            // Bekker sinkage for a rigid wheel: z0 = [3W / ((3-n)(kc/b+kphi) b sqrt(2r))]^(2/(2n+1))
            float k = s.kc / width + s.kphi;
            float z0 = math.pow(3f * load / ((3f - s.n) * k * width * math.sqrt(2f * radius)), 2f / (2f * s.n + 1f));
            z0 = math.min(z0, radius * 0.6f);
            r.sinkage = z0;
            r.contactLength = radius * math.acos(math.clamp(1f - z0 / radius, -1f, 1f));
            float area = width * r.contactLength;
            r.maxShear = area * s.c + load * s.tanPhi;
            r.compaction = width * k * math.pow(z0, s.n + 1f) / (s.n + 1f);
            return r;
        }

        /// <summary>Janosi-Hanamoto shear mobilisation: fraction of max shear for a shear displacement j.</summary>
        public static float ShearFraction(float j, float K)
        {
            j = math.abs(j);
            if (j < 1e-6f) return 0;
            float x = j / K;
            return 1f - (1f - math.exp(-x)) / x;
        }
    }
}
