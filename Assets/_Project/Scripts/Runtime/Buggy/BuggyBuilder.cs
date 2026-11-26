using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Crew;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// Builds the MR-4 rover procedurally, after the Apollo LRV: swept aluminium tube chassis with
    /// rounded bends, woven nylon seat webbing, zinc-coated piano-wire mesh tyres (alpha-cut lattice)
    /// with titanium chevrons and inner bump-stop frames, fibreglass fenders, gold-foil (MLI) covered
    /// avionics and battery boxes with white radiators, a parabolic umbrella high-gain antenna, a
    /// helical low-gain antenna, a TV camera, a console and the T-handle hand controller.
    /// Roll bars and the second seat row are our 4-crew additions. Seated crew are included.
    /// Physics dimensions (wheel mounts, radius, colliders) are unchanged from stage 1.
    /// </summary>
    public static class BuggyBuilder
    {
        public const int VehicleLayer = 2; // "Ignore Raycast": wheel sphere-casts skip our own colliders

        class M
        {
            public Material alu = Mats.Textured(Mats.Make("Aluminium", Color.white, 0.9f, 0.55f, Mats.Brushed, 0.4f), ProcTex.Paint("AluminiumTex", new Color(0.78f, 0.79f, 0.81f), 15, 0.1f, 0.3f));
            public Material aluDark = Mats.Textured(Mats.Make("AluminiumAnodised", Color.white, 0.85f, 0.45f, Mats.Brushed, 0.4f), ProcTex.Paint("AluAnodisedTex", new Color(0.45f, 0.46f, 0.48f), 16, 0.5f, 0.35f));
            public Material white = Mats.Textured(Mats.Make("ThermalWhite", Color.white, 0f, 0.3f), ProcTex.Paint("ThermalWhitePaint", new Color(0.88f, 0.88f, 0.86f), 11, 0.25f, 0.5f));
            public Material gold = Mats.Make("GoldMLI", new Color(0.85f, 0.62f, 0.26f), 1f, 0.7f, Mats.Foil, 0.8f);
            public Material silver = Mats.Make("SilverMLI", new Color(0.82f, 0.82f, 0.84f), 1f, 0.7f, Mats.Foil, 0.8f);
            public Material webbing = Mats.Cutout(Mats.Make("SeatWebbing", Color.white, 0f, 0.2f), ProcTex.Webbing(new Color(0.22f, 0.25f, 0.33f)), new Vector2(2, 2));
            public Material mesh = Mats.Cutout(Mats.Make("WireMesh", Color.white, 0.9f, 0.45f), Mats.Wire, new Vector2(1, 1));
            public Material titanium = Mats.Textured(Mats.Make("Titanium", Color.white, 0.9f, 0.5f), ProcTex.Paint("TitaniumTex", new Color(0.52f, 0.52f, 0.55f), 12, 0.4f, 0.25f));
            public Material dark = Mats.Textured(Mats.Make("BlackAnodised", Color.white, 0.5f, 0.45f), ProcTex.Paint("BlackAnodisedTex", new Color(0.045f, 0.045f, 0.05f), 13, 0.5f, 0.6f));
            public Material fender = Mats.Textured(Mats.Make("Fibreglass", Color.white, 0f, 0.35f), ProcTex.Fibreglass("MR-4", 31));
            public Material lens = Mats.Emissive(Mats.Make("LampLens", new Color(0.9f, 0.9f, 0.9f), 0f, 0.95f), Color.black);
            public Material panel = Mats.Emissive(Mats.Make("Console", Color.white, 0.2f, 0.5f), new Color(0.6f, 0.6f, 0.5f), Mats.PanelTex);
            public Material dustCover = Mats.Textured(Mats.Make("DustCoverFabric", Color.white, 0f, 0.25f), ProcTex.DustCover(41));
            public Material rubber = Mats.Textured(Mats.Make("Rubber", Color.white, 0f, 0.2f), ProcTex.Paint("RubberTex", new Color(0.08f, 0.08f, 0.08f), 14, 0f, 0.7f));
        }

        static GameObject Obj(Transform parent, string name, Mesh mesh, Material m, Vector3 pos, Quaternion? rot = null, Vector3? scale = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = scale ?? Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.On;
            return go;
        }

        static readonly Dictionary<PrimitiveType, Mesh> prims = new();
        static Mesh Prim(PrimitiveType t)
        {
            if (prims.TryGetValue(t, out var m) && m != null) return m;
            var go = GameObject.CreatePrimitive(t);
            m = go.GetComponent<MeshFilter>().sharedMesh;
            Util.Kill(go);
            prims[t] = m;
            return m;
        }

        static GameObject Box(Transform p, string n, Vector3 pos, Vector3 size, Material m, Vector3? euler = null) =>
            Obj(p, n, Prim(PrimitiveType.Cube), m, pos, Quaternion.Euler(euler ?? Vector3.zero), size);

        static GameObject Cyl(Transform p, string n, Vector3 pos, float dia, float len, Material m, Vector3? euler = null) =>
            Obj(p, n, Prim(PrimitiveType.Cylinder), m, pos, Quaternion.Euler(euler ?? Vector3.zero), new Vector3(dia, len * 0.5f, dia));

        static void TubeP(Transform p, string n, float r, Material m, float bend, params Vector3[] pts) =>
            Obj(p, n, ProcMesh.SweepTube(pts, r, 10, bend), m, Vector3.zero);

        public static BuggyController Build(Transform parent = null)
        {
            var mats = new M();
            var root = new GameObject("MR-4 Rover");
            if (parent) root.transform.SetParent(parent, false);
            root.AddComponent<Rigidbody>();
            var ctrl = root.AddComponent<BuggyController>();
            var body = new GameObject("Body").transform;
            body.SetParent(root.transform, false);

            // ---------------- chassis: perimeter frame (rounded), cross members
            float rx = 0.62f, y = 0.16f, zf = 1.85f, zr = -1.85f;
            TubeP(body, "Frame", 0.028f, mats.alu, 0.12f,
                new Vector3(-rx, y, zr), new Vector3(-rx, y, zf), new Vector3(rx, y, zf), new Vector3(rx, y, zr), new Vector3(-rx, y, zr), new Vector3(-rx, y, zr + 0.3f));
            foreach (float z in new[] { -1.45f, -0.6f, 0.2f, 1.0f, 1.45f })
                TubeP(body, "Cross", 0.022f, mats.alu, 0, new Vector3(-rx, y, z), new Vector3(rx, y, z));
            // chassis hinge fittings (LRV folded in three sections)
            foreach (float z in new[] { -0.6f, 1.0f })
            foreach (float x in new[] { -rx, rx })
                Cyl(body, "Hinge", new Vector3(x, y, z), 0.075f, 0.06f, mats.aluDark, new Vector3(0, 0, 90));
            // floor: ribbed aluminium panels
            Box(body, "FloorFront", new Vector3(0, y + 0.03f, 0.6f), new Vector3(1.22f, 0.008f, 1.7f), mats.aluDark);
            Box(body, "FloorRear", new Vector3(0, y + 0.03f, -0.85f), new Vector3(1.22f, 0.008f, 1.2f), mats.aluDark);
            for (int i = 0; i < 9; i++)
                Box(body, "FloorRib", new Vector3(-0.5f + i * 0.125f, y + 0.04f, -0.1f), new Vector3(0.012f, 0.012f, 3.3f), mats.alu);

            // ---------------- suspension (visual): torsion-bar wishbones to each wheel
            foreach (float z in new[] { -1.45f, 1.45f })
            {
                TubeP(body, "SuspBeam", 0.035f, mats.aluDark, 0, new Vector3(-0.78f, 0.22f, z), new Vector3(0.78f, 0.22f, z));
                foreach (float sx in new[] { -1f, 1f })
                {
                    TubeP(body, "UpperArm", 0.018f, mats.alu, 0, new Vector3(sx * 0.6f, 0.27f, z + 0.14f), new Vector3(sx * 0.84f, 0.12f, z), new Vector3(sx * 0.6f, 0.27f, z - 0.14f));
                    TubeP(body, "LowerArm", 0.02f, mats.alu, 0, new Vector3(sx * 0.6f, 0.05f, z + 0.17f), new Vector3(sx * 0.84f, -0.05f, z), new Vector3(sx * 0.6f, 0.05f, z - 0.17f));
                    TubeP(body, "Damper", 0.028f, mats.dark, 0, new Vector3(sx * 0.58f, 0.44f, z), new Vector3(sx * 0.83f, 0.06f, z));
                    TubeP(body, "DamperRod", 0.012f, mats.titanium, 0, new Vector3(sx * 0.7f, 0.26f, z), new Vector3(sx * 0.83f, 0.06f, z));
                    Cyl(body, "TorsionBar", new Vector3(sx * 0.6f, 0.05f, z), 0.04f, 0.4f, mats.aluDark, new Vector3(90, 0, 0));
                }
            }

            // ---------------- seats (tube frames + woven webbing), armrests, belts
            foreach (float z in new[] { 0.35f, -0.85f })
            foreach (float x in new[] { -0.42f, 0.42f })
            {
                var seat = new GameObject("Seat").transform;
                seat.SetParent(body, false);
                seat.localPosition = new Vector3(x, 0.42f, z);
                float w = 0.26f;
                TubeP(seat, "SeatFrame", 0.014f, mats.alu, 0.05f,
                    new Vector3(-w, 0, 0.25f), new Vector3(-w, 0, -0.22f), new Vector3(-w, 0.62f, -0.34f), new Vector3(w, 0.62f, -0.34f), new Vector3(w, 0, -0.22f), new Vector3(w, 0, 0.25f), new Vector3(-w, 0, 0.25f));
                Box(seat, "Pan", new Vector3(0, 0.005f, 0.02f), new Vector3(0.5f, 0.006f, 0.46f), mats.webbing);
                Box(seat, "Back", new Vector3(0, 0.31f, -0.28f), new Vector3(0.5f, 0.6f, 0.006f), mats.webbing, new Vector3(-11, 0, 0));
                foreach (float lx in new[] { -w, w })
                {
                    TubeP(seat, "Leg", 0.013f, mats.alu, 0, new Vector3(lx, -0.26f, 0.2f), new Vector3(lx, 0f, 0.2f));
                    TubeP(seat, "Leg", 0.013f, mats.alu, 0, new Vector3(lx, -0.26f, -0.2f), new Vector3(lx, 0f, -0.2f));
                }
                float ax = x < 0 ? -w - 0.04f : w + 0.04f;
                TubeP(seat, "Armrest", 0.016f, mats.aluDark, 0.04f, new Vector3(ax, 0f, -0.2f), new Vector3(ax, 0.22f, -0.15f), new Vector3(ax, 0.22f, 0.2f), new Vector3(ax, 0.05f, 0.25f));
                Box(seat, "Belt", new Vector3(0, 0.24f, 0.0f), new Vector3(0.5f, 0.04f, 0.004f), mats.webbing, new Vector3(0, 0, 18));
                Box(seat, "Footrest", new Vector3(0, -0.2f, 0.62f), new Vector3(0.44f, 0.015f, 0.16f), mats.aluDark, new Vector3(22, 0, 0));
            }
            // roll bars (added for a crew of four)
            foreach (float z in new[] { -0.2f, -1.4f })
                TubeP(body, "RollBar", 0.028f, mats.alu, 0.18f, new Vector3(-0.85f, y, z), new Vector3(-0.8f, 1.45f, z), new Vector3(0.8f, 1.45f, z), new Vector3(0.85f, y, z));
            TubeP(body, "RollBrace", 0.02f, mats.alu, 0, new Vector3(-0.8f, 1.45f, -0.2f), new Vector3(-0.8f, 1.45f, -1.4f));
            TubeP(body, "RollBrace", 0.02f, mats.alu, 0, new Vector3(0.8f, 1.45f, -0.2f), new Vector3(0.8f, 1.45f, -1.4f));
            // lift cage (round 12): 4 poles rising from the roll-bar corners and bending in to one hook point above the
            // centre (RoverWinch.CageHook), with the rover's powered rope ascender under the hook
            {
                var hookP = RoverWinch.CageHook;
                foreach (var c in new[] { new Vector3(-0.8f, 1.45f, -0.2f), new Vector3(0.8f, 1.45f, -0.2f), new Vector3(-0.8f, 1.45f, -1.4f), new Vector3(0.8f, 1.45f, -1.4f) })
                {
                    var mid = new Vector3(c.x * 0.85f, 2.15f, Mathf.Lerp(c.z, hookP.z, 0.35f));
                    TubeP(body, "CagePole", 0.022f, mats.alu, 0.25f, c, mid, hookP - new Vector3(0, 0.18f, 0));
                }
                Box(body, "CageAscender", hookP - new Vector3(0, 0.26f, 0), new Vector3(0.22f, 0.16f, 0.26f), mats.dark);
                TubeP(body, "CageHookEye", 0.02f, mats.alu, 0.3f, hookP - new Vector3(0.07f, 0.12f, 0), hookP, hookP - new Vector3(-0.07f, 0.12f, 0));
            }

            // ---------------- forward chassis: avionics (MLI blanket + radiator with mirror strips + dust covers)
            Box(body, "AvionicsBay", new Vector3(0, 0.33f, 1.55f), new Vector3(1.1f, 0.3f, 0.55f), mats.gold);
            Box(body, "Radiator", new Vector3(0, 0.485f, 1.55f), new Vector3(1.0f, 0.01f, 0.48f), mats.white);
            for (int i = 0; i < 6; i++)
                Box(body, "OSR", new Vector3(-0.4f + i * 0.16f, 0.492f, 1.55f), new Vector3(0.12f, 0.004f, 0.42f), mats.silver);
            // hinged dust covers over the radiator (round 13, T2: fabric texture; hinge edge on the radiator, was 10 cm inside the bay)
            Box(body, "DustCover", new Vector3(-0.28f, 0.715f, 1.77f), new Vector3(0.5f, 0.01f, 0.5f), mats.dustCover, new Vector3(-62, 0, 0));
            Box(body, "DustCover", new Vector3(0.28f, 0.715f, 1.77f), new Vector3(0.5f, 0.01f, 0.5f), mats.dustCover, new Vector3(-62, 0, 0));
            // console on a post between the front seats
            TubeP(body, "ConsolePost", 0.025f, mats.alu, 0.05f, new Vector3(0, y, 0.85f), new Vector3(0, 0.78f, 0.95f));
            var console = Box(body, "Console", new Vector3(0, 0.88f, 0.98f), new Vector3(0.46f, 0.26f, 0.07f), mats.dark, new Vector3(-22, 0, 0));
            Obj(console.transform, "Face", Prim(PrimitiveType.Quad), mats.panel, new Vector3(0, 0, -0.51f), Quaternion.identity, new Vector3(0.95f, 0.9f, 1));
            // three live screens under the dials: drive / nav / systems
            var screens = root.AddComponent<CockpitScreens>();
            screens.rover = ctrl;
            screens.Build(console.transform);
            // T-handle hand controller
            TubeP(body, "HandCtrl", 0.016f, mats.dark, 0.02f, new Vector3(0, 0.44f, 0.55f), new Vector3(0, 0.66f, 0.58f), new Vector3(0, 0.72f, 0.62f));
            TubeP(body, "TBar", 0.014f, mats.dark, 0, new Vector3(-0.06f, 0.73f, 0.63f), new Vector3(0.06f, 0.73f, 0.63f));
            // helical low-gain antenna
            TubeP(body, "LGAMast", 0.012f, mats.alu, 0, new Vector3(-0.55f, 0.5f, 1.82f), new Vector3(-0.55f, 1.0f, 1.82f));
            Obj(body, "LGAHelix", ProcMesh.Helix(0.035f, 0.045f, 9f, 0.004f), mats.titanium, new Vector3(-0.55f, 1.0f, 1.82f));
            // TV camera (GCTA-like) on a short mast, outboard of the right dust cover (round 13, T2: it went through it):
            // a bracket off the avionics bay's corner, then up
            TubeP(body, "TVMast", 0.015f, mats.alu, 0.03f, new Vector3(0.5f, 0.44f, 1.82f), new Vector3(0.64f, 0.46f, 1.82f), new Vector3(0.64f, 0.78f, 1.82f));
            Box(body, "TVCamera", new Vector3(0.64f, 0.85f, 1.84f), new Vector3(0.12f, 0.1f, 0.22f), mats.white);
            Cyl(body, "TVLens", new Vector3(0.64f, 0.85f, 1.97f), 0.07f, 0.06f, mats.dark, new Vector3(90, 0, 0));
            // rear camera mast (ROVER MAST CAM): looks forward over the whole rover, like a chase view
            // (Apollo's GCTA rode on the LRV; Artemis rover concepts carry mast cameras)
            TubeP(body, "CamMast", 0.02f, mats.alu, 0, new Vector3(0f, 0.5f, -1.98f), new Vector3(0f, 2.42f, -1.98f));
            Box(body, "MastCamera", new Vector3(0f, 2.5f, -1.98f), new Vector3(0.14f, 0.12f, 0.24f), mats.white);
            Cyl(body, "MastCamLens", new Vector3(0f, 2.5f, -1.84f), 0.07f, 0.05f, mats.dark, new Vector3(90, 0, 0));
            var mastCam = new GameObject("MastCam").transform;
            mastCam.SetParent(body, false);
            mastCam.localPosition = new Vector3(0f, 2.52f, -1.76f);   // just in front of the lens
            mastCam.localRotation = Quaternion.Euler(14f, 0f, 0f);
            // rear pallet: Li-ion pack in MLI + radiator, sample bag rack, tool carrier
            Box(body, "BatteryPack", new Vector3(0, 0.36f, -1.65f), new Vector3(1.15f, 0.38f, 0.5f), mats.gold);
            Box(body, "PackRadiator", new Vector3(0, 0.555f, -1.65f), new Vector3(1.1f, 0.01f, 0.45f), mats.white);
            for (int i = 0; i < 7; i++)
                Box(body, "OSR", new Vector3(-0.45f + i * 0.15f, 0.562f, -1.65f), new Vector3(0.11f, 0.004f, 0.4f), mats.silver);
            TubeP(body, "Rack", 0.014f, mats.alu, 0.04f, new Vector3(-0.5f, 0.56f, -1.9f), new Vector3(-0.5f, 1.15f, -1.9f), new Vector3(0.5f, 1.15f, -1.9f), new Vector3(0.5f, 0.56f, -1.9f));
            for (int i = 0; i < 4; i++)
                Box(body, "SampleBag", new Vector3(-0.36f + i * 0.24f, 0.82f, -1.93f), new Vector3(0.2f, 0.3f, 0.05f), mats.white);
            TubeP(body, "Tongs", 0.008f, mats.titanium, 0, new Vector3(0.55f, 0.6f, -1.95f), new Vector3(0.55f, 1.35f, -1.95f));
            TubeP(body, "Scoop", 0.01f, mats.titanium, 0, new Vector3(-0.58f, 0.6f, -1.95f), new Vector3(-0.58f, 1.3f, -1.95f));

            // ---------------- lights (LED, physically based)
            var lights = new List<Light>();
            foreach (float x in new[] { -0.55f, 0.55f })
            {
                Box(body, "LampHousing", new Vector3(x, 0.55f, 1.88f), new Vector3(0.16f, 0.1f, 0.08f), mats.dark);
                Obj(body, "LampLens", Prim(PrimitiveType.Quad), mats.lens, new Vector3(x, 0.55f, 1.925f), Quaternion.identity, new Vector3(0.13f, 0.07f, 1));
                lights.Add(Lamp(body, "HeadlightSpot", new Vector3(x, 0.55f, 1.95f), Quaternion.Euler(3, 0, 0), 60, 36, 200, 30600f, true));   // 3 deg down + taller cone: lights the tube vault too
            }
            foreach (float x in new[] { -0.8f, 0.8f })
            {
                Box(body, "FloodHousing", new Vector3(x, 1.5f, -0.2f), new Vector3(0.12f, 0.08f, 0.1f), mats.dark, new Vector3(30, x < 0 ? -50 : 50, 0));
                lights.Add(Lamp(body, "FloodLight", new Vector3(x, 1.5f, -0.2f), Quaternion.Euler(30, x < 0 ? -50 : 50, 0), 100, 70, 80, 1300f, false));
            }
            // roll-bar lamp: wide flood aimed up-forward (tube roof, side openings), no shadow map
            Box(body, "RollBarLampHousing", new Vector3(0, 1.5f, -0.2f), new Vector3(0.22f, 0.08f, 0.1f), mats.dark, new Vector3(-30, 0, 0));
            lights.Add(Lamp(body, "FloodLightRollBar", new Vector3(0, 1.55f, -0.12f), Quaternion.Euler(-30, 0, 0), 120, 80, 90, 2500f, false));
            foreach (var l in lights) l.enabled = false;

            // ---------------- wheels: wire mesh tyre, chevrons, inner bump-stop frame, hub motor
            var tyreMesh = ProcMesh.TyreBand(0.41f, 0.26f, 56, 0.02f);
            var frameMesh = ProcMesh.TyreBand(0.32f, 0.2f, 40, 0f);
            var hubProfile = new List<Vector2> { new(0.001f, 0.15f), new(0.16f, 0.14f), new(0.17f, 0.08f), new(0.19f, 0.0f), new(0.17f, -0.08f), new(0.16f, -0.14f), new(0.001f, -0.15f) };
            var hubMesh = ProcMesh.Lathe(hubProfile, 24);
            var wheels = new BuggyController.Wheel[4];
            int k = 0;
            foreach (bool frontW in new[] { true, false })
            foreach (bool leftW in new[] { true, false })
            {
                var w = new BuggyController.Wheel
                {
                    name = (frontW ? "F" : "R") + (leftW ? "L" : "R"),
                    front = frontW, left = leftW,
                    mount = new Vector3(leftW ? -1.1f : 1.1f, 0.25f, frontW ? 1.45f : -1.45f),   // 2.2 m track (4 crew: higher CoM than the LRV)
                };
                var pivot = new GameObject("Wheel" + w.name).transform;
                pivot.SetParent(root.transform, false);
                pivot.localPosition = w.mount - Vector3.up * 0.18f;
                var spin = new GameObject("Spin").transform;
                spin.SetParent(pivot, false);
                Obj(spin, "WireTyre", tyreMesh, mats.mesh, Vector3.zero);
                Obj(spin, "BumpStopFrame", frameMesh, mats.titanium, Vector3.zero).GetComponent<MeshRenderer>().sharedMaterial = mats.mesh;
                Obj(spin, "HubMotor", hubMesh, mats.gold, Vector3.zero, Quaternion.Euler(0, 0, 90));
                // outboard hub cover: bolt circle, panel line, stencil, dust (turns with the wheel)
                var capMat = Mats.Cutout(Mats.Make("HubCap" + w.name, Color.white, 0.6f, 0.45f), ProcTex.HubCap("MR-4 " + w.name, k + 3), Vector2.one);
                float sideX = leftW ? -1f : 1f;
                Obj(spin, "HubCover", Prim(PrimitiveType.Quad), capMat, new Vector3(sideX * 0.152f, 0, 0), Quaternion.Euler(0, leftW ? 90f : -90f, 0), new Vector3(0.36f, 0.36f, 1f));
                for (int s = 0; s < 6; s++)   // spokes / rim flanges
                {
                    float a = s * 60f;
                    var q = Quaternion.Euler(a, 0, 0);
                    foreach (float side in new[] { -0.12f, 0.12f })
                        Obj(spin, "Spoke", ProcMesh.SweepTube(new[] { q * new Vector3(side, 0.16f, 0), q * new Vector3(side, 0.4f, 0) }, 0.01f, 6), mats.alu, Vector3.zero);
                }
                for (int c = 0; c < 18; c++)   // titanium chevron treads (V pairs)
                {
                    float a = c * 20f;
                    var q = Quaternion.Euler(a, 0, 0);
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var g = Box(spin, "Chevron", q * new Vector3(side * 0.065f, 0.412f, 0.0f), new Vector3(0.13f, 0.006f, 0.022f), mats.titanium);
                        g.transform.localRotation = q * Quaternion.Euler(0, side * 30f, 0);
                    }
                }
                // fender: fibreglass arc shell over the wheel + extension
                Obj(pivot, "Fender", FenderMesh(0.47f, 0.3f, 130f), mats.fender, Vector3.zero);
                w.steerPivot = pivot;
                w.spinner = spin;
                wheels[k++] = w;
            }
            ctrl.wheels = wheels;

            // ---------------- crew: three seated passengers (driver seat stays empty: that's the player)
            var crew = new GameObject("Crew").transform;
            crew.SetParent(body, false);
            var passengers = new List<AstronautRig>
            {
                AstronautBuilder.BuildSeated(crew, new Vector3(0.42f, 0.42f, 0.35f), 1).GetComponent<AstronautRig>(),
                AstronautBuilder.BuildSeated(crew, new Vector3(-0.42f, 0.42f, -0.85f), 2).GetComponent<AstronautRig>(),
                AstronautBuilder.BuildSeated(crew, new Vector3(0.42f, 0.42f, -0.85f), 3).GetComponent<AstronautRig>(),
            };
            var driverModel = AstronautBuilder.BuildSeated(crew, new Vector3(-0.42f, 0.42f, 0.35f), 0);
            driverModel.name = "DriverModel";

            // ---------------- colliders (body only; wheels are sphere-cast)
            AddBox(root, new Vector3(0, 0.2f, 0), new Vector3(1.9f, 0.22f, 3.9f));
            AddBox(root, new Vector3(0, 0.95f, -0.3f), new Vector3(1.7f, 1.1f, 2.6f));
            AddBox(root, new Vector3(0, 1.45f, -0.8f), new Vector3(1.7f, 0.1f, 1.3f));

            var eye = new GameObject("DriverEye").transform;
            eye.SetParent(root.transform, false);
            eye.localPosition = new Vector3(-0.42f, 1.47f, 0.42f);
            // driver's helmet lamps (two, 3200 cd each): the pivot follows the first-person look (DriverCamera)
            var helmet = new GameObject(DriverCamera.HelmetPivot).transform;
            helmet.SetParent(eye, false);
            foreach (float x in new[] { -0.13f, 0.13f })
            {
                var hl = Lamp(helmet, "HelmetLamp", new Vector3(x, 0.12f, 0.08f), Quaternion.Euler(6, 0, 0), 45, 25, 80, 3200f, false);
                hl.enabled = false;
                lights.Add(hl);
            }

            var driver = root.AddComponent<BuggyDriver>();
            driver.buggy = ctrl;
            driver.headlights = lights.ToArray();
            root.AddComponent<WheelDust>();
            var bounce = root.AddComponent<LampBounce>();
            var heads = root.AddComponent<CrewHeads>();
            heads.driver = driver;
            heads.Init(passengers);
            // one soft bounce source for every lamp on the rover (headlights, flood, driver + crew helmet lamps)
            for (int h = 0; h < heads.Count; h++) lights.Add(heads.Lamp(h));
            bounce.lamps = lights.ToArray();

            SetLayer(root.transform, VehicleLayer);
            ctrl.Setup();
            return ctrl;
        }

        static Light Lamp(Transform p, string n, Vector3 pos, Quaternion rot, float outer, float inner, float range, float candela, bool shadows)
        {
            var l = new GameObject(n).AddComponent<Light>();
            l.transform.SetParent(p, false);
            l.transform.localPosition = pos; l.transform.localRotation = rot;
            l.type = LightType.Spot; l.spotAngle = outer; l.innerSpotAngle = inner; l.range = range;
            l.color = new Color(1f, 0.97f, 0.92f);
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            l.shadowBias = 0.1f; l.shadowNormalBias = 1.5f;     // grazing beams over rough rock: no acne
            l.shadowNearPlane = 0.3f;                           // ignore the lamp housing itself
            l.gameObject.AddComponent<PhysicalLight>().physicalIntensity = candela;
            return l;
        }

        /// <summary>Arc shell (axis X) over the wheel, centred at the top.</summary>
        static Mesh FenderMesh(float radius, float width, float arcDeg)
        {
            var v = new List<Vector3>(); var t = new List<int>(); var uv = new List<Vector2>();
            int seg = 16;
            for (int s = 0; s <= seg; s++)
            {
                float a = Mathf.Deg2Rad * (90f - arcDeg / 2 + arcDeg * s / seg);
                var dir = new Vector3(0, Mathf.Sin(a), Mathf.Cos(a));
                v.Add(new Vector3(-width / 2, 0, 0) + dir * radius); uv.Add(new Vector2(0, s / (float)seg));
                v.Add(new Vector3(width / 2, 0, 0) + dir * radius); uv.Add(new Vector2(1, s / (float)seg));
            }
            for (int s = 0; s < seg; s++)
            {
                int a = s * 2;
                t.Add(a); t.Add(a + 1); t.Add(a + 2); t.Add(a + 2); t.Add(a + 1); t.Add(a + 3);
            }
            var m = new Mesh { name = "Fender" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return ProcMesh.DoubleSided(m);
        }

        static void AddBox(GameObject go, Vector3 c, Vector3 s)
        {
            var b = go.AddComponent<BoxCollider>();
            b.center = c; b.size = s;
            b.sharedMaterial = new PhysicsMaterial("RoverHull") { dynamicFriction = 0.5f, staticFriction = 0.6f, bounciness = 0.1f, bounceCombine = PhysicsMaterialCombine.Minimum };
        }

        public static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t) SetLayer(c, layer);
        }

        public static Transform DriverEye(BuggyController b) => b.transform.Find("DriverEye");
        public static Transform MastCam(BuggyController b) => b.transform.Find("Body/MastCam");
        public static GameObject DriverModel(BuggyController b) => b.transform.Find("Body/Crew/DriverModel")?.gameObject;
    }
}
