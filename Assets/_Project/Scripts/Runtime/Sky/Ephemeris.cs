using System;
using Unity.Mathematics;

namespace MoonBaseAlpha.Sky
{
    /// <summary>
    /// Sun / Earth geometry seen from a point on the Moon, double precision.
    ///  - Sun: Meeus ch.25 (low precision, ~0.01 deg).
    ///  - Moon: Meeus ch.47 truncated series (main 32 longitude/distance + 20 latitude terms, ~10").
    ///  - Lunar orientation: IAU 2009 (Archinal et al. 2011) incl. physical libration terms.
    ///  - Earth rotation: GMST.
    /// Mirrors Tools/sky/ephemeris_check.py (verified: 2030-06-14 22:00 UTC -> Sun el 20.4, Earth az 98.5 el 32.0,
    /// partial eclipse of the Sun by the Earth ~2030-06-15 18:10 UTC).
    /// Frames: ICRF (J2000 equatorial) km; Moon body-fixed (IAU); site ENU.
    /// </summary>
    public static class Ephemeris
    {
        const double D2R = Math.PI / 180.0;
        const double TtMinusUtc = 69.2;

        static readonly int[,] LR =
        {
            {0,0,1,0,6288774,-20905355},{2,0,-1,0,1274027,-3699111},{2,0,0,0,658314,-2955968},{0,0,2,0,213618,-569925},
            {0,1,0,0,-185116,48888},{0,0,0,2,-114332,-3149},{2,0,-2,0,58793,246158},{2,-1,-1,0,57066,-152138},
            {2,0,1,0,53322,-170733},{2,-1,0,0,45758,-204586},{0,1,-1,0,-40923,-129620},{1,0,0,0,-34720,108743},
            {0,1,1,0,-30383,104755},{2,0,0,-2,15327,10321},{0,0,1,2,-12528,0},{0,0,1,-2,10980,79661},
            {4,0,-1,0,10675,-34782},{0,0,3,0,10034,-23210},{4,0,-2,0,8548,-21636},{2,1,-1,0,-7888,24208},
            {2,1,0,0,-6766,30824},{1,0,-1,0,-5163,-8379},{1,1,0,0,4987,-16675},{2,-1,1,0,4036,-12831},
            {2,0,2,0,3994,-10445},{4,0,0,0,3861,-11650},{2,0,-3,0,3665,14403},{0,1,-2,0,-2689,-7003},
            {2,0,-1,2,-2602,0},{2,-1,-2,0,2390,10056},{1,0,1,0,-2348,6322},{2,-2,0,0,2236,-9884},
        };
        static readonly int[,] B =
        {
            {0,0,0,1,5128122},{0,0,1,1,280602},{0,0,1,-1,277693},{2,0,0,-1,173237},{2,0,-1,1,55413},
            {2,0,-1,-1,46271},{2,0,0,1,32573},{0,0,2,1,17198},{2,0,1,-1,9266},{0,0,2,-1,8822},
            {2,-1,0,-1,8216},{2,0,-2,-1,4324},{2,0,1,1,4200},{2,1,0,-1,-3359},{2,-1,-1,1,2463},
            {2,-1,0,1,2211},{2,-1,-1,-1,2065},{0,1,-1,-1,-1870},{4,0,-1,-1,1828},{0,1,0,1,-1794},
        };

        public struct SkyState
        {
            public double3 sunEnu;        // unit vector to the Sun, site ENU (x east, y north, z up)
            public double3 earthEnu;      // unit vector to the Earth centre
            public double sunDistanceKm, earthDistanceKm;
            public double earthAngularRadiusDeg, sunAngularRadiusDeg;
            public double earthLitFraction;     // illuminated fraction of the Earth disc seen from the Moon
            public double earthPhaseAngle;      // radians, Sun-Earth-Moon (0 = full Earth)
            public double sunVisibleFraction;   // 1 - fraction of the solar disc hidden by the Earth
            public double3x3 earthFixedFromEnu; // rotate an ENU vector into Earth body-fixed (ITRF-ish)
        }

        public static double JulianDateTT(DateTime utc)
        {
            double jd = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays + 2451545.0;
            return jd + TtMinusUtc / 86400.0;
        }

        static double3 EclToIcrf(double lamDeg, double betDeg, double dist, double T)
        {
            double lam = (lamDeg - 1.396971 * T) * D2R, bet = betDeg * D2R;   // precess to J2000 ecliptic
            double eps = 23.4392911 * D2R;
            double x = dist * Math.Cos(bet) * Math.Cos(lam);
            double y = dist * Math.Cos(bet) * Math.Sin(lam);
            double z = dist * Math.Sin(bet);
            return new double3(x, y * Math.Cos(eps) - z * Math.Sin(eps), y * Math.Sin(eps) + z * Math.Cos(eps));
        }

        public static double3 SunGeocentric(double T)
        {
            double L0 = 280.46646 + 36000.76983 * T;
            double M = 357.52911 + 35999.05029 * T;
            double e = 0.016708634 - 0.000042037 * T;
            double Mr = M * D2R;
            double C = (1.914602 - 0.004817 * T) * Math.Sin(Mr) + (0.019993 - 0.000101 * T) * Math.Sin(2 * Mr) + 0.000289 * Math.Sin(3 * Mr);
            double nu = (M + C) * D2R;
            double R = 1.000001018 * (1 - e * e) / (1 + e * Math.Cos(nu)) * 149597870.7;
            return EclToIcrf(L0 + C, 0, R, T);
        }

        public static double3 MoonGeocentric(double T)
        {
            double Lp = 218.3164477 + 481267.88123421 * T;
            double D = 297.8501921 + 445267.1114034 * T;
            double M = 357.5291092 + 35999.0502909 * T;
            double Mp = 134.9633964 + 477198.8675055 * T;
            double F = 93.2720950 + 483202.0175233 * T;
            double E = 1 - 0.002516 * T;
            double A1 = 119.75 + 131.849 * T, A2 = 53.09 + 479264.290 * T, A3 = 313.45 + 481266.484 * T;
            double sl = 0, sr = 0, sb = 0;
            for (int i = 0; i < LR.GetLength(0); i++)
            {
                double arg = (LR[i, 0] * D + LR[i, 1] * M + LR[i, 2] * Mp + LR[i, 3] * F) * D2R;
                double k = Math.Pow(E, Math.Abs(LR[i, 1]));
                sl += LR[i, 4] * k * Math.Sin(arg);
                sr += LR[i, 5] * k * Math.Cos(arg);
            }
            for (int i = 0; i < B.GetLength(0); i++)
            {
                double arg = (B[i, 0] * D + B[i, 1] * M + B[i, 2] * Mp + B[i, 3] * F) * D2R;
                sb += B[i, 4] * Math.Pow(E, Math.Abs(B[i, 1])) * Math.Sin(arg);
            }
            sl += 3958 * Math.Sin(A1 * D2R) + 1962 * Math.Sin((Lp - F) * D2R) + 318 * Math.Sin(A2 * D2R);
            sb += -2235 * Math.Sin(Lp * D2R) + 382 * Math.Sin(A3 * D2R) + 175 * Math.Sin((A1 - F) * D2R) + 175 * Math.Sin((A1 + F) * D2R)
                  + 127 * Math.Sin((Lp - Mp) * D2R) - 115 * Math.Sin((Lp + Mp) * D2R);
            return EclToIcrf(Lp + sl / 1e6, sb / 1e6, 385000.56 + sr / 1000.0, T);
        }

        static double3x3 Rz(double deg) { double c = Math.Cos(deg * D2R), s = Math.Sin(deg * D2R); return new double3x3(c, s, 0, -s, c, 0, 0, 0, 1); }
        static double3x3 Rx(double deg) { double c = Math.Cos(deg * D2R), s = Math.Sin(deg * D2R); return new double3x3(1, 0, 0, 0, c, s, 0, -s, c); }

        /// <summary>IAU 2009 lunar orientation: ICRF -> Moon body-fixed.</summary>
        public static double3x3 MoonBodyFromIcrf(double d)
        {
            double T = d / 36525.0;
            double[] E =
            {
                0, 125.045 - 0.0529921 * d, 250.089 - 0.1059842 * d, 260.008 + 13.0120009 * d, 176.625 + 13.3407154 * d,
                357.529 + 0.9856003 * d, 311.589 + 26.4057084 * d, 134.963 + 13.0649930 * d, 276.617 + 0.3287146 * d,
                34.226 + 1.7484877 * d, 15.134 - 0.1589763 * d, 119.743 + 0.0036096 * d, 239.961 + 0.1643573 * d,
                25.053 + 12.9590088 * d,
            };
            double S(int i) => Math.Sin(E[i] * D2R);
            double C(int i) => Math.Cos(E[i] * D2R);
            double a0 = 269.9949 + 0.0031 * T - 3.8787 * S(1) - 0.1204 * S(2) + 0.0700 * S(3) - 0.0172 * S(4) + 0.0072 * S(6) - 0.0052 * S(10) + 0.0043 * S(13);
            double d0 = 66.5392 + 0.0130 * T + 1.5419 * C(1) + 0.0239 * C(2) - 0.0278 * C(3) + 0.0068 * C(4) - 0.0029 * C(6) + 0.0009 * C(7) + 0.0008 * C(10) - 0.0009 * C(13);
            double W = 38.3213 + 13.17635815 * d - 1.4e-12 * d * d + 3.5610 * S(1) + 0.1208 * S(2) - 0.0642 * S(3) + 0.0158 * S(4) + 0.0252 * S(5)
                       - 0.0066 * S(6) - 0.0047 * S(7) - 0.0046 * S(8) + 0.0028 * S(9) + 0.0052 * S(10) + 0.0040 * S(11) + 0.0019 * S(12) - 0.0044 * S(13);
            // row-major constructor: double3x3(m00, m01, m02, m10, ...)
            return math.mul(Rz(W), math.mul(Rx(90 - d0), Rz(90 + a0)));
        }

        /// <summary>Site ENU basis vectors in Moon body-fixed coordinates.</summary>
        public static void EnuBasis(double latDeg, double lonDeg, out double3 east, out double3 north, out double3 up)
        {
            double p = latDeg * D2R, l = lonDeg * D2R;
            up = new double3(Math.Cos(p) * Math.Cos(l), Math.Cos(p) * Math.Sin(l), Math.Sin(p));
            east = new double3(-Math.Sin(l), Math.Cos(l), 0);
            north = new double3(-Math.Sin(p) * Math.Cos(l), -Math.Sin(p) * Math.Sin(l), Math.Cos(p));
        }

        public static SkyState Compute(DateTime utc, double latDeg, double lonDeg, double siteRadiusKm = 1736.0)
        {
            double jd = JulianDateTT(utc);
            double d = jd - 2451545.0;
            double T = d / 36525.0;
            double3 moon = MoonGeocentric(T);
            double3 sun = SunGeocentric(T);
            double3x3 R = MoonBodyFromIcrf(d);
            EnuBasis(latDeg, lonDeg, out var east, out var north, out var up);
            double3 site = up * siteRadiusKm;

            var s = new SkyState();
            double3 ToEnu(double3 icrfFromMoonCentre, out double dist)
            {
                double3 b = math.mul(R, icrfFromMoonCentre) - site;
                dist = math.length(b);
                b /= dist;
                return new double3(math.dot(b, east), math.dot(b, north), math.dot(b, up));
            }
            s.sunEnu = ToEnu(sun - moon, out s.sunDistanceKm);
            s.earthEnu = ToEnu(-moon, out s.earthDistanceKm);
            s.earthAngularRadiusDeg = Math.Asin(Lunar.EarthRadiusKm / s.earthDistanceKm) / D2R;
            s.sunAngularRadiusDeg = Math.Asin(Lunar.SunRadiusKm / s.sunDistanceKm) / D2R;

            double cosA = math.dot(math.normalize(moon), math.normalize(sun));
            s.earthPhaseAngle = Math.Acos(math.clamp(cosA, -1, 1));
            s.earthLitFraction = (1 + cosA) / 2;

            // eclipse: overlap of the solar disc by the Earth disc
            double sep = Math.Acos(math.clamp(math.dot(s.sunEnu, s.earthEnu), -1, 1)) / D2R;
            s.sunVisibleFraction = 1 - DiscOverlap(s.sunAngularRadiusDeg, s.earthAngularRadiusDeg, sep) /
                                       (Math.PI * s.sunAngularRadiusDeg * s.sunAngularRadiusDeg);

            // Earth orientation: ITRF <- ICRF is Rz(GMST); ICRF <- Moon body is R^T; body <- ENU is [E N U]
            double gmst = 280.46061837 + 360.98564736629 * (jd - TtMinusUtc / 86400.0 - 2451545.0);
            double3x3 earthFromIcrf = Rz(gmst % 360.0);
            double3x3 bodyFromEnu = new double3x3(east, north, up);   // columns
            s.earthFixedFromEnu = math.mul(earthFromIcrf, math.mul(math.transpose(R), bodyFromEnu));
            return s;
        }

        /// <summary>Area of intersection of two circles (radii r1, r2, centre distance d).</summary>
        public static double DiscOverlap(double r1, double r2, double d)
        {
            if (d >= r1 + r2) return 0;
            if (d <= Math.Abs(r1 - r2)) return Math.PI * Math.Min(r1, r2) * Math.Min(r1, r2);
            double a = r1 * r1 * Math.Acos((d * d + r1 * r1 - r2 * r2) / (2 * d * r1));
            double b = r2 * r2 * Math.Acos((d * d + r2 * r2 - r1 * r1) / (2 * d * r2));
            double c = 0.5 * Math.Sqrt((-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2));
            return a + b - c;
        }

        /// <summary>Lambert-sphere phase law: brightness of a sphere at phase angle a relative to full.</summary>
        public static double LambertPhase(double a) => (Math.Sin(a) + (Math.PI - a) * Math.Cos(a)) / Math.PI;

        /// <summary>ENU (x east, y north, z up) -> Unity (x east, y up, z north).</summary>
        public static UnityEngine.Vector3 EnuToUnity(double3 v) => new UnityEngine.Vector3((float)v.x, (float)v.z, (float)v.y);
    }
}
