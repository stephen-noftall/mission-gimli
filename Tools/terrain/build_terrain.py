"""
Moon Base Alpha - terrain bake.

Downloads (if missing) real LRO elevation data around the Marius Hills Pit and bakes
it into heightmaps for Unity, in a local tangent-plane frame centred on the pit:

    +X = east, +Z = north, +Y = up (local vertical at the pit), metres.
    Y already includes the Moon's curvature (Y = r*cos(c) - R0), so the terrain is a
    true piece of a 1737 km sphere. Gravity in-game points at (0, -R0, 0).

Sources
  - SLDEM2015 (LOLA + Kaguya TC, 512 px/deg ~59 m)   wide area
  - LROC NAC_DTM_MARIUSPIT01 (4 m post spacing)       high-res strip containing the pit

Outputs (Assets/StreamingAssets/Terrain/)
  near.r16  uint16 heights, NEAR_N x NEAR_N, NEAR_SPACING m, row-major, row 0 = south
  far.r16   uint16 heights, FAR_N x FAR_N, FAR_SPACING m
  nacmask.r8 uint8 NAC data weight on the near grid (255 = real 4 m data)
  terrain.json  metadata (scales, offsets, pit geometry, rille direction, tube path)

Usage:  python Tools/terrain/build_terrain.py   (from the project root)
Requires: numpy, scipy, pillow
"""
import json, math, os, sys, urllib.request
import numpy as np
from PIL import Image
from scipy import ndimage

Image.MAX_IMAGE_PIXELS = None

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
RAW = os.path.join(ROOT, "TerrainSource", "raw")
OUT = os.path.join(ROOT, "Assets", "StreamingAssets", "Terrain")
PREVIEW = os.path.join(ROOT, "TerrainSource", "preview")

R_REF = 1737400.0                       # reference sphere radius (both datasets)
PIT_LAT, PIT_LON = 14.0917, 303.2299    # LROC Pits Atlas
NEAR_N, NEAR_SPACING = 6145, 4.0        # 24.576 km square
FAR_N, FAR_SPACING = 2049, 32.0         # 65.536 km square

SLDEM_URL = ("https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/data/"
             "sldem2015/tiles/float_img/sldem2015_512_00n_30n_270_315_float.img")
SLDEM_RECORD = 23040 * 4
SLDEM_LINES = 15360
NAC_URL = ("https://pds.lroc.im-ldi.com/data/LRO-L-LROC-5-RDR-V1.0/LROLRC_2001/DATA/SDP/"
           "NAC_DTM/MARIUSPIT01/NAC_DTM_MARIUSPIT01.TIF")


def log(*a):
    print(*a, flush=True)


# ---------------------------------------------------------------- download

def fetch(url, path, byte_range=None):
    if os.path.exists(path):
        return
    os.makedirs(os.path.dirname(path), exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": "terrain-bake/1.0"})
    if byte_range:
        req.add_header("Range", "bytes=%d-%d" % byte_range)
    log("downloading", url)
    with urllib.request.urlopen(req) as r, open(path + ".part", "wb") as f:
        while True:
            b = r.read(1 << 20)
            if not b:
                break
            f.write(b)
    os.replace(path + ".part", path)


def sldem_band(lat_min, lat_max):
    """Fetch only the rows we need from the 1.4 GB SLDEM tile via HTTP range."""
    first = max(0, int(15359.5 - 512 * lat_max) - 4)
    last = min(SLDEM_LINES - 1, int(15359.5 - 512 * lat_min) + 5)
    path = os.path.join(RAW, "sldem_band.img")
    meta = os.path.join(RAW, "sldem_band.txt")
    if os.path.exists(meta):
        f0, n0 = map(int, open(meta).read().split())
        if f0 > first or f0 + n0 - 1 < last:
            os.remove(path)
    if not os.path.exists(path):
        fetch(SLDEM_URL, path, (first * SLDEM_RECORD, (last + 1) * SLDEM_RECORD - 1))
        open(meta, "w").write("%d %d" % (first, last - first + 1))
    f0, n0 = map(int, open(meta).read().split())
    a = np.fromfile(path, dtype="<f4").reshape(n0, 23040)
    return a.astype(np.float64) * 1000.0, f0   # km -> m


# ---------------------------------------------------------------- geometry

def tangent_to_latlon(x, z, R):
    """Azimuthal-equidistant inverse around the pit. x east, z north (m)."""
    lat0, lon0 = math.radians(PIT_LAT), math.radians(PIT_LON)
    rho = np.hypot(x, z)
    c = rho / R
    az = np.arctan2(x, z)
    sl = np.sin(lat0) * np.cos(c) + np.cos(lat0) * np.sin(c) * np.cos(az)
    lat = np.arcsin(np.clip(sl, -1, 1))
    lon = lon0 + np.arctan2(np.sin(az) * np.sin(c) * np.cos(lat0),
                            np.cos(c) - np.sin(lat0) * sl)
    return np.degrees(lat), np.degrees(lon), c


class Sampler:
    def __init__(self):
        fetch(NAC_URL, os.path.join(RAW, "NAC_DTM_MARIUSPIT01.TIF"))
        im = Image.open(os.path.join(RAW, "NAC_DTM_MARIUSPIT01.TIF"))
        tie = im.tag_v2[33922]
        scale = im.tag_v2[33550]
        self.nac_x0, self.nac_y0 = tie[3], tie[4]          # corner of pixel (0,0)
        self.nac_dx, self.nac_dy = scale[0], scale[1]
        nac = np.array(im, dtype=np.float32)
        valid = nac > -1e30
        log("NAC", nac.shape, "valid %.1f%%" % (100 * valid.mean()))
        # Erode the valid region so we don't use ragged edge pixels, then feather.
        valid = ndimage.binary_erosion(valid, iterations=6)
        dist = ndimage.distance_transform_edt(valid) * self.nac_dx   # metres to edge
        self.nac_w = np.clip(dist / 400.0, 0, 1).astype(np.float32)   # 400 m feather
        self.nac_w = self.nac_w * self.nac_w * (3 - 2 * self.nac_w)  # smoothstep
        # Fill invalid pixels by nearest valid neighbour so interpolation stays finite.
        idx = ndimage.distance_transform_edt(~valid, return_distances=False, return_indices=True)
        self.nac = nac[tuple(idx)].astype(np.float32)
        self.nac_k = math.cos(math.radians(14.0))  # projection centre latitude

        band, self.sl_first = sldem_band(PIT_LAT - 1.3, PIT_LAT + 1.3)
        # crop to the longitudes we need and pre-filter once for cubic spline sampling
        c0 = int(512.0 * (PIT_LON - 1.6 - 180.0) - 46080.5)
        c1 = int(512.0 * (PIT_LON + 1.6 - 180.0) - 46080.5)
        self.sl_col0 = c0
        self.sldem = ndimage.spline_filter(band[:, c0:c1], order=3)
        log("SLDEM band", band.shape, "cropped", self.sldem.shape, "first line", self.sl_first)

    def sample(self, lat, lon):
        # SLDEM simple cylindrical, 512 ppd; 0-based pixel centres.
        line = 15359.5 - 512.0 * lat - self.sl_first
        samp = 512.0 * (lon - 180.0) - 46080.5 - self.sl_col0
        hs = ndimage.map_coordinates(self.sldem, [line, samp], order=3, mode="nearest", prefilter=False)
        # NAC equirectangular, centre lat 14, centre lon 180.
        X = R_REF * np.radians(lon - 180.0) * self.nac_k
        Y = R_REF * np.radians(lat)
        col = (X - self.nac_x0) / self.nac_dx - 0.5
        row = (self.nac_y0 - Y) / self.nac_dy - 0.5
        hn = ndimage.map_coordinates(self.nac, [row, col], order=1, mode="nearest")
        w = ndimage.map_coordinates(self.nac_w, [row, col], order=1, mode="constant", cval=0.0)
        return hs + w * (hn - hs), w, hs, hn


def bake_grid(s, n, spacing, R0, want_mask=False, tag=""):
    half = (n - 1) * spacing / 2.0
    Y = np.empty((n, n), np.float32)
    M = np.empty((n, n), np.uint8) if want_mask else None
    xs = np.arange(n) * spacing - half
    rows = 256
    for r0 in range(0, n, rows):
        r1 = min(n, r0 + rows)
        zz, xx = np.meshgrid(np.arange(r0, r1) * spacing - half, xs, indexing="ij")
        lat, lon, c = tangent_to_latlon(xx, zz, R0)
        h, w, _, _ = s.sample(lat.ravel(), lon.ravel())
        h = h.reshape(xx.shape)
        r = R_REF + h
        Y[r0:r1] = (r * np.cos(c) - R0).astype(np.float32)
        if want_mask:
            M[r0:r1] = np.round(w.reshape(xx.shape) * 255).astype(np.uint8)
        log("  %s rows %d/%d" % (tag, r1, n))
    return Y, M


def write_r16(path, Y):
    lo, hi = float(Y.min()), float(Y.max())
    q = np.round((Y - lo) / (hi - lo) * 65535).astype("<u2")
    q.tofile(path)
    return lo, (hi - lo) / 65535.0


def hillshade(Y, spacing, az=315, alt=45):
    gz, gx = np.gradient(Y.astype(np.float64), spacing)
    nx, ny, nz = -gx, np.ones_like(gx), -gz
    l = np.sqrt(nx * nx + ny * ny + nz * nz)
    a, b = math.radians(az), math.radians(alt)
    L = (math.sin(a) * math.cos(b), math.sin(b), math.cos(a) * math.cos(b))
    s = (nx * L[0] + ny * L[1] + nz * L[2]) / l
    return np.clip(s * 255, 0, 255).astype(np.uint8)[::-1]   # north up


# ---------------------------------------------------------------- pit analysis

def analyse_pit(s, R0):
    """Measure the pit from the NAC DTM on a 1 m local grid."""
    n, sp = 401, 1.0
    half = 200.0
    zz, xx = np.meshgrid(np.arange(n) * sp - half, np.arange(n) * sp - half, indexing="ij")
    lat, lon, c = tangent_to_latlon(xx, zz, R0)
    _, _, _, hn = s.sample(lat.ravel(), lon.ravel())
    H = hn.reshape(xx.shape)
    rr = np.hypot(xx, zz)
    # deepest point near the atlas position = pit centre
    m = np.where(rr < 40, H, np.inf)
    iz, ix = np.unravel_index(np.argmin(m), m.shape)
    cx, cz = xx[iz, ix], zz[iz, ix]
    # refine: centroid of the lowest region
    low = (H < H[iz, ix] + 8) & (np.hypot(xx - cx, zz - cz) < 45)
    cx, cz = float(xx[low].mean()), float(zz[low].mean())
    rr = np.hypot(xx - cx, zz - cz)
    rim = float(np.median(H[(rr > 44) & (rr < 50)]))
    surround = float(np.median(H[(rr > 80) & (rr < 120)]))
    floor_dtm = float(H[rr < 10].min())

    # Rille axis: elevation profile around rings 150..450 m
    angs = np.radians(np.arange(0, 360, 2))
    prof = np.zeros(len(angs))
    for rad in (150, 225, 300, 375, 450):
        px = cx + rad * np.sin(angs)
        pz = cz + rad * np.cos(angs)
        lat2, lon2, _ = tangent_to_latlon(px, pz, R0)
        _, _, hs2, hn2 = s.sample(lat2, lon2)
        prof += hn2
    prof = ndimage.uniform_filter1d(prof, 9, mode="wrap")
    # two lowest separated directions
    i1 = int(np.argmin(prof))
    sep = np.abs(((np.degrees(angs) - np.degrees(angs[i1]) + 180) % 360) - 180)
    i2 = int(np.argmin(np.where(sep > 90, prof, np.inf)))
    # tube heads down-rille (lower of the two)
    down = i1 if prof[i1] <= prof[i2] else i2
    up = i2 if down == i1 else i1

    # Follow the rille thalweg for the tube path
    def thalweg(start_az, length=600, step=10):
        pts = [(cx, cz)]
        az = start_az
        x, z = cx, cz
        for _ in range(int(length / step)):
            best = None
            for d in np.radians(np.arange(-25, 26, 5)):
                a = az + d
                tx, tz = x + step * np.sin(a), z + step * np.cos(a)
                # sample a small cross-section 30 m either side to find the trough
                hsum = 0
                for off in (-12, 0, 12):
                    ox = tx + off * np.cos(a)
                    oz = tz - off * np.sin(a)
                    la, lo_, _ = tangent_to_latlon(np.array([ox]), np.array([oz]), R0)
                    hsum += s.sample(la, lo_)[0][0]
                if best is None or hsum < best[0]:
                    best = (hsum, a, tx, tz)
            _, a, x, z = best
            az = 0.7 * az + 0.3 * a     # keep it smooth
            pts.append((float(x), float(z)))
        return pts

    path_down = thalweg(angs[down])
    path_up = thalweg(angs[up], length=200)
    return dict(center=[cx, cz], rimHeight=rim, surroundHeight=surround,
                dtmFloorHeight=floor_dtm,
                rilleAzimuthDown=float(np.degrees(angs[down])),
                rilleAzimuthUp=float(np.degrees(angs[up])),
                tubePathDown=path_down, tubePathUp=path_up), H


# ---------------------------------------------------------------- main

def main():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(PREVIEW, exist_ok=True)
    s = Sampler()

    # Pit surface radius defines the frame origin.
    h_pit, _, _, _ = s.sample(np.array([PIT_LAT]), np.array([PIT_LON]))
    lat0, lon0 = PIT_LAT, PIT_LON
    # Use the surrounding plain height (not the pit floor) for R0.
    ring = np.radians(np.arange(0, 360, 10))
    la, lo_, _ = tangent_to_latlon(120 * np.sin(ring), 120 * np.cos(ring), R_REF)
    h0 = float(np.median(s.sample(la, lo_)[0]))
    R0 = R_REF + h0
    log("frame origin height %.1f m (R0 = %.1f m)" % (h0, R0))

    pit, Hpit = analyse_pit(s, R0)
    log("pit", {k: v for k, v in pit.items() if not k.startswith("tube")})

    log("baking near grid")
    Yn, Mn = bake_grid(s, NEAR_N, NEAR_SPACING, R0, want_mask=True, tag="near")
    log("baking far grid")
    Yf, _ = bake_grid(s, FAR_N, FAR_SPACING, R0, tag="far")

    near_off, near_scale = write_r16(os.path.join(OUT, "near.r16"), Yn)
    far_off, far_scale = write_r16(os.path.join(OUT, "far.r16"), Yf)
    Mn.tofile(os.path.join(OUT, "nacmask.r8"))

    meta = dict(
        version=1,
        site=dict(name="Marius Hills Pit", lat=PIT_LAT, lon=PIT_LON,
                  originHeight=h0, R0=R0, referenceRadius=R_REF),
        near=dict(file="near.r16", n=NEAR_N, spacing=NEAR_SPACING,
                  heightOffset=near_off, heightScale=near_scale,
                  maskFile="nacmask.r8"),
        far=dict(file="far.r16", n=FAR_N, spacing=FAR_SPACING,
                 heightOffset=far_off, heightScale=far_scale),
        pit=pit,
        sources=["SLDEM2015 512ppd (LOLA/Kaguya)", "LROC NAC_DTM_MARIUSPIT01 v1.9 (4 m)"],
    )
    json.dump(meta, open(os.path.join(OUT, "terrain.json"), "w"), indent=1)

    # previews for sanity checks
    Image.fromarray(hillshade(Yn[::4, ::4], NEAR_SPACING * 4)).save(os.path.join(PREVIEW, "near_hillshade.png"))
    Image.fromarray(hillshade(Yf[::2, ::2], FAR_SPACING * 2)).save(os.path.join(PREVIEW, "far_hillshade.png"))
    Image.fromarray(Mn[::8, ::8][::-1]).save(os.path.join(PREVIEW, "nac_mask.png"))
    sh = hillshade(Hpit, 1.0)
    Image.fromarray(sh).save(os.path.join(PREVIEW, "pit_hillshade.png"))
    log("near range %.1f..%.1f m, far range %.1f..%.1f m" % (Yn.min(), Yn.max(), Yf.min(), Yf.max()))
    log("done ->", OUT)


if __name__ == "__main__":
    main()
