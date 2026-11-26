"""
Reference implementation of the in-game ephemeris (mirrors Assets/_Project/Scripts/Runtime/Sky/Ephemeris.cs).
Prints Sun/Earth azimuth/elevation at the Marius Hills Pit and finds a lunar-morning start date.

Usage: python Tools/sky/ephemeris_check.py [year]
"""
import math, sys
from datetime import datetime, timedelta, timezone

D2R = math.pi / 180
SITE_LAT, SITE_LON = 14.0917, 303.2299
TT_MINUS_UTC = 69.2  # seconds (approx, 2030)

LR = [  # D M M' F  sum_l(1e-6 deg) sum_r(1e-3 km)
    (0, 0, 1, 0, 6288774, -20905355), (2, 0, -1, 0, 1274027, -3699111), (2, 0, 0, 0, 658314, -2955968),
    (0, 0, 2, 0, 213618, -569925), (0, 1, 0, 0, -185116, 48888), (0, 0, 0, 2, -114332, -3149),
    (2, 0, -2, 0, 58793, 246158), (2, -1, -1, 0, 57066, -152138), (2, 0, 1, 0, 53322, -170733),
    (2, -1, 0, 0, 45758, -204586), (0, 1, -1, 0, -40923, -129620), (1, 0, 0, 0, -34720, 108743),
    (0, 1, 1, 0, -30383, 104755), (2, 0, 0, -2, 15327, 10321), (0, 0, 1, 2, -12528, 0),
    (0, 0, 1, -2, 10980, 79661), (4, 0, -1, 0, 10675, -34782), (0, 0, 3, 0, 10034, -23210),
    (4, 0, -2, 0, 8548, -21636), (2, 1, -1, 0, -7888, 24208), (2, 1, 0, 0, -6766, 30824),
    (1, 0, -1, 0, -5163, -8379), (1, 1, 0, 0, 4987, -16675), (2, -1, 1, 0, 4036, -12831),
    (2, 0, 2, 0, 3994, -10445), (4, 0, 0, 0, 3861, -11650), (2, 0, -3, 0, 3665, 14403),
    (0, 1, -2, 0, -2689, -7003), (2, 0, -1, 2, -2602, 0), (2, -1, -2, 0, 2390, 10056),
    (1, 0, 1, 0, -2348, 6322), (2, -2, 0, 0, 2236, -9884),
]
B = [
    (0, 0, 0, 1, 5128122), (0, 0, 1, 1, 280602), (0, 0, 1, -1, 277693), (2, 0, 0, -1, 173237),
    (2, 0, -1, 1, 55413), (2, 0, -1, -1, 46271), (2, 0, 0, 1, 32573), (0, 0, 2, 1, 17198),
    (2, 0, 1, -1, 9266), (0, 0, 2, -1, 8822), (2, -1, 0, -1, 8216), (2, 0, -2, -1, 4324),
    (2, 0, 1, 1, 4200), (2, 1, 0, -1, -3359), (2, -1, -1, 1, 2463), (2, -1, 0, 1, 2211),
    (2, -1, -1, -1, 2065), (0, 1, -1, -1, -1870), (4, 0, -1, -1, 1828), (0, 1, 0, 1, -1794),
]


def jd_tt(dt):
    jd = dt.timestamp() / 86400.0 + 2440587.5
    return jd + TT_MINUS_UTC / 86400.0


def ecl_to_icrf(lam, bet, dist, T):
    lam = lam - 1.396971 * T  # precess ecliptic-of-date longitude to J2000 (approx)
    eps = 23.4392911 * D2R
    l, b = lam * D2R, bet * D2R
    x = dist * math.cos(b) * math.cos(l)
    y = dist * math.cos(b) * math.sin(l)
    z = dist * math.sin(b)
    return (x, y * math.cos(eps) - z * math.sin(eps), y * math.sin(eps) + z * math.cos(eps))


def sun_geo(T):  # km
    L0 = 280.46646 + 36000.76983 * T
    M = 357.52911 + 35999.05029 * T
    e = 0.016708634 - 0.000042037 * T
    Mr = M * D2R
    C = ((1.914602 - 0.004817 * T) * math.sin(Mr) + (0.019993 - 0.000101 * T) * math.sin(2 * Mr)
         + 0.000289 * math.sin(3 * Mr))
    nu = (M + C) * D2R
    R = 1.000001018 * (1 - e * e) / (1 + e * math.cos(nu)) * 149597870.7
    return ecl_to_icrf(L0 + C, 0.0, R, T)


def moon_geo(T):  # km
    Lp = 218.3164477 + 481267.88123421 * T
    D = 297.8501921 + 445267.1114034 * T
    M = 357.5291092 + 35999.0502909 * T
    Mp = 134.9633964 + 477198.8675055 * T
    F = 93.2720950 + 483202.0175233 * T
    E = 1 - 0.002516 * T
    A1 = 119.75 + 131.849 * T
    A2 = 53.09 + 479264.290 * T
    A3 = 313.45 + 481266.484 * T
    sl = sr = sb = 0.0
    for d, m, mp, f, l, r in LR:
        arg = (d * D + m * M + mp * Mp + f * F) * D2R
        k = E ** abs(m)
        sl += l * k * math.sin(arg)
        sr += r * k * math.cos(arg)
    for d, m, mp, f, b in B:
        arg = (d * D + m * M + mp * Mp + f * F) * D2R
        sb += b * (E ** abs(m)) * math.sin(arg)
    s = math.sin
    sl += 3958 * s(A1 * D2R) + 1962 * s((Lp - F) * D2R) + 318 * s(A2 * D2R)
    sb += (-2235 * s(Lp * D2R) + 382 * s(A3 * D2R) + 175 * s((A1 - F) * D2R) + 175 * s((A1 + F) * D2R)
           + 127 * s((Lp - Mp) * D2R) - 115 * s((Lp + Mp) * D2R))
    return ecl_to_icrf(Lp + sl / 1e6, sb / 1e6, 385000.56 + sr / 1000.0, T)


def moon_rot(d):
    """IAU 2009 lunar orientation: returns 3x3 matrix ICRF -> Moon body-fixed."""
    T = d / 36525.0
    E = [0, 125.045 - 0.0529921 * d, 250.089 - 0.1059842 * d, 260.008 + 13.0120009 * d,
         176.625 + 13.3407154 * d, 357.529 + 0.9856003 * d, 311.589 + 26.4057084 * d,
         134.963 + 13.0649930 * d, 276.617 + 0.3287146 * d, 34.226 + 1.7484877 * d,
         15.134 - 0.1589763 * d, 119.743 + 0.0036096 * d, 239.961 + 0.1643573 * d,
         25.053 + 12.9590088 * d]
    sn = lambda i: math.sin(E[i] * D2R)
    cs = lambda i: math.cos(E[i] * D2R)
    a0 = (269.9949 + 0.0031 * T - 3.8787 * sn(1) - 0.1204 * sn(2) + 0.0700 * sn(3) - 0.0172 * sn(4)
          + 0.0072 * sn(6) - 0.0052 * sn(10) + 0.0043 * sn(13))
    d0 = (66.5392 + 0.0130 * T + 1.5419 * cs(1) + 0.0239 * cs(2) - 0.0278 * cs(3) + 0.0068 * cs(4)
          - 0.0029 * cs(6) + 0.0009 * cs(7) + 0.0008 * cs(10) - 0.0009 * cs(13))
    W = (38.3213 + 13.17635815 * d - 1.4e-12 * d * d + 3.5610 * sn(1) + 0.1208 * sn(2) - 0.0642 * sn(3)
         + 0.0158 * sn(4) + 0.0252 * sn(5) - 0.0066 * sn(6) - 0.0047 * sn(7) - 0.0046 * sn(8)
         + 0.0028 * sn(9) + 0.0052 * sn(10) + 0.0040 * sn(11) + 0.0019 * sn(12) - 0.0044 * sn(13))

    def rz(t):
        c, s_ = math.cos(t * D2R), math.sin(t * D2R)
        return [[c, s_, 0], [-s_, c, 0], [0, 0, 1]]

    def rx(t):
        c, s_ = math.cos(t * D2R), math.sin(t * D2R)
        return [[1, 0, 0], [0, c, s_], [0, -s_, c]]

    def mm(a, b):
        return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

    return mm(rz(W), mm(rx(90 - d0), rz(90 + a0)))


def mv(m, v):
    return tuple(sum(m[i][k] * v[k] for k in range(3)) for i in range(3))


def enu_basis():
    p, l = SITE_LAT * D2R, SITE_LON * D2R
    up = (math.cos(p) * math.cos(l), math.cos(p) * math.sin(l), math.sin(p))
    east = (-math.sin(l), math.cos(l), 0.0)
    north = (-math.sin(p) * math.cos(l), -math.sin(p) * math.sin(l), math.cos(p))
    return east, north, up


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def sky(dt):
    jd = jd_tt(dt)
    d = jd - 2451545.0
    T = d / 36525.0
    moon = moon_geo(T)
    sun = sun_geo(T)
    R = moon_rot(d)
    east, north, up = enu_basis()
    site_body = tuple(1737.4 * c for c in up)
    out = {}
    for name, geo in (("sun", sun), ("earth", (0.0, 0.0, 0.0))):
        rel = tuple(g - m for g, m in zip(geo, moon))          # from Moon centre, ICRF
        b = mv(R, rel)                                           # body-fixed
        b = tuple(x - s for x, s in zip(b, site_body))           # topocentric
        n = math.sqrt(dot(b, b))
        e, nn, u = dot(b, east) / n, dot(b, north) / n, dot(b, up) / n
        out[name] = (math.degrees(math.atan2(e, nn)) % 360, math.degrees(math.asin(u)), n)
    # Earth phase: angle Sun-Earth-Moon
    em = moon                                  # Earth -> Moon
    es = sun
    cosa = dot(em, es) / math.sqrt(dot(em, em) * dot(es, es))
    alpha = math.acos(cosa)   # phase angle at Earth (0 = full Earth seen from Moon)
    out["earth_phase_lit"] = (1 + math.cos(alpha)) / 2
    out["earth_diam_deg"] = math.degrees(2 * math.asin(6371.0 / out["earth"][2]))
    return out


def main():
    year = int(sys.argv[1]) if len(sys.argv) > 1 else 2030
    t = datetime(year, 1, 1, tzinfo=timezone.utc)
    end = datetime(year + 1, 1, 1, tzinfo=timezone.utc)
    prev = sky(t)["sun"][1]
    found = []
    while t < end:
        t2 = t + timedelta(hours=1)
        el = sky(t2)["sun"][1]
        if prev < 20.0 <= el:
            found.append(t2)
        prev, t = el, t2
    for f in found:
        s = sky(f)
        print(f.isoformat(), "sun az %.1f el %.1f | earth az %.1f el %.1f diam %.2f deg lit %.0f%%" % (
            s["sun"][0], s["sun"][1], s["earth"][0], s["earth"][1], s["earth_diam_deg"], 100 * s["earth_phase_lit"]))
    # Earth wobble over one month
    t0 = found[0] if found else datetime(year, 1, 1, tzinfo=timezone.utc)
    az = [sky(t0 + timedelta(days=i))["earth"] for i in range(30)]
    print("earth az range %.1f..%.1f, el range %.1f..%.1f" % (
        min(a[0] for a in az), max(a[0] for a in az), min(a[1] for a in az), max(a[1] for a in az)))


if __name__ == "__main__":
    main()
