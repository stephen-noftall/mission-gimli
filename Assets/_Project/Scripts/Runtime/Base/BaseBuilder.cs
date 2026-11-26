using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Base
{
    /// <summary>
    /// Procedural surface hardware for the ~2035 expedition (generic designs, no real brands):
    ///  - crew lander: Artemis-era large vertical lander (~24 m), descent stage with tanks and engines,
    ///    pressurised crew cabin with windows, elevator + ladder, solar wings, radiators, antennas;
    ///  - cargo lander: uncrewed ~7 m lander with an open deck - it delivered the crane;
    ///  - fission surface power unit: ~40 kW reactor with radiator wings (NASA FSP class);
    ///  - scout rover: the small robotic rover that found "something" deep in the tubes.
    /// All are static colliders (box approximations).
    /// </summary>
    public static class BaseBuilder
    {
        class M
        {
            public Material white = Mats.Make("HullWhite", new Color(0.9f, 0.9f, 0.88f), 0.1f, 0.4f);
            public Material grey = Mats.Make("HullGrey", new Color(0.55f, 0.56f, 0.58f), 0.6f, 0.45f, Mats.Brushed, 0.3f);
            public Material dark = Mats.Make("HullDark", new Color(0.08f, 0.08f, 0.09f), 0.4f, 0.4f);
            public Material gold = Mats.Make("LanderMLI", new Color(0.85f, 0.62f, 0.26f), 1f, 0.65f, Mats.Foil, 0.8f);
            public Material silver = Mats.Make("LanderSilverMLI", new Color(0.8f, 0.8f, 0.82f), 1f, 0.65f, Mats.Foil, 0.8f);
            public Material solar = Mats.Make("SolarCells", new Color(0.05f, 0.07f, 0.16f), 0.8f, 0.85f);
            public Material window = Mats.Emissive(Mats.Make("CabinWindow", new Color(0.02f, 0.02f, 0.03f), 0.2f, 0.95f), new Color(0.25f, 0.22f, 0.16f));
            public Material nozzle = Mats.Make("Nozzle", new Color(0.25f, 0.22f, 0.2f), 0.9f, 0.4f);
            public Material radiator = Mats.Make("Radiator", new Color(0.92f, 0.92f, 0.9f), 0.0f, 0.6f);
            public Material yellow = Mats.Make("SafetyYellow", new Color(0.85f, 0.7f, 0.1f), 0.1f, 0.4f);
        }
        static M m;
        static M Mt { get { if (m == null || m.white == null) m = new M(); return m; } }

        static readonly Dictionary<PrimitiveType, Mesh> prims = new();
        static Mesh Prim(PrimitiveType t)
        {
            if (prims.TryGetValue(t, out var mm) && mm != null) return mm;
            var go = GameObject.CreatePrimitive(t);
            mm = go.GetComponent<MeshFilter>().sharedMesh;
            Util.Kill(go);
            prims[t] = mm;
            return mm;
        }

        public static GameObject Obj(Transform p, string n, Mesh mesh, Material mat, Vector3 pos, Quaternion? rot = null, Vector3? scale = null, bool shadows = true)
        {
            var go = new GameObject(n);
            go.transform.SetParent(p, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = scale ?? Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }
        public static GameObject Box(Transform p, string n, Vector3 pos, Vector3 size, Material mat, Vector3? euler = null) =>
            Obj(p, n, Prim(PrimitiveType.Cube), mat, pos, Quaternion.Euler(euler ?? Vector3.zero), size);
        public static GameObject Cyl(Transform p, string n, Vector3 pos, float dia, float len, Material mat, Vector3? euler = null) =>
            Obj(p, n, Prim(PrimitiveType.Cylinder), mat, pos, Quaternion.Euler(euler ?? Vector3.zero), new Vector3(dia, len * 0.5f, dia));
        public static void Tube(Transform p, string n, float r, Material mat, params Vector3[] pts) =>
            Obj(p, n, ProcMesh.SweepTube(pts, r, 8), mat, Vector3.zero);
        static void Collider(GameObject root, Vector3 c, Vector3 s)
        {
            var b = root.AddComponent<BoxCollider>(); b.center = c; b.size = s;
        }

        /// <summary>
        /// A surveillance-camera mast for the work site (BASE CAM), origin on the ground; returns the camera anchor
        /// at the top (aim it with its rotation). Real: fixed site cameras are standard on any crane job.
        /// </summary>
        public static Transform CameraMast(Transform parent, float height)
        {
            var mt = Mt;
            var root = new GameObject("BaseCamMast").transform;
            root.SetParent(parent, false);
            Tube(root, "Mast", 0.06f, mt.grey, Vector3.zero, new Vector3(0, height, 0));
            foreach (float a in new[] { 0f, 120f, 240f })
            {
                var d = Quaternion.Euler(0, a, 0) * Vector3.forward;
                Tube(root, "Leg", 0.03f, mt.grey, d * 1.6f, new Vector3(0, 1.8f, 0));
            }
            Box(root, "PanTilt", new Vector3(0, height + 0.12f, 0), new Vector3(0.22f, 0.2f, 0.22f), mt.dark);
            var cam = new GameObject("BaseCam").transform;
            cam.SetParent(root, false);
            cam.localPosition = new Vector3(0, height + 0.32f, 0);
            Box(cam, "CameraBody", new Vector3(0, 0, -0.1f), new Vector3(0.18f, 0.16f, 0.32f), mt.white);
            Cyl(cam, "Lens", new Vector3(0, 0, 0.1f), 0.1f, 0.08f, mt.dark, new Vector3(90, 0, 0));
            var view = new GameObject("View").transform;   // the view point, just in front of the lens
            view.SetParent(cam, false);
            view.localPosition = new Vector3(0, 0, 0.2f);
            return cam;
        }

        /// <summary>Crew lander, origin at the ground under its centre, +Z faces the elevator side.</summary>
        public static GameObject CrewLander(Transform parent)
        {
            var mt = Mt;
            var root = new GameObject("CrewLander (2035)").transform;
            root.SetParent(parent, false);
            // descent stage: octagonal skirt + tanks (lathe), legs, engines
            var skirt = new List<Vector2> { new(4.6f, 2.2f), new(4.6f, 9.5f), new(4.2f, 10.3f) };
            Obj(root, "DescentStage", ProcMesh.Lathe(skirt, 8, false), mt.gold, Vector3.zero);
            Obj(root, "DescentDeck", ProcMesh.Lathe(new List<Vector2> { new(0.01f, 2.2f), new(4.6f, 2.2f) }, 8), mt.silver, Vector3.zero);
            for (int i = 0; i < 4; i++)
            {
                float a = i * 90f + 45f;
                var q = Quaternion.Euler(0, a, 0);
                var hip = q * new Vector3(0, 5.5f, 4.4f);
                var foot = q * new Vector3(0, 0.35f, 8.2f);
                var knee = q * new Vector3(0, 2.8f, 6.8f);
                Tube(root, "LegStrut", 0.22f, mt.grey, hip, knee, foot);
                Tube(root, "LegBrace", 0.12f, mt.grey, q * new Vector3(0, 2.6f, 4.5f), knee);
                Cyl(root, "Footpad", foot - new Vector3(0, 0.2f, 0), 1.6f, 0.25f, mt.grey);
            }
            for (int i = 0; i < 4; i++)
            {
                var q = Quaternion.Euler(0, i * 90f, 0);
                var bell = new List<Vector2> { new(0.35f, 2.2f), new(0.4f, 1.6f), new(0.75f, 0.6f) };
                Obj(root, "Engine", ProcMesh.Lathe(bell, 16), mt.nozzle, q * new Vector3(1.6f, 0, 0));
            }
            // crew/ascent cabin
            var cabin = new List<Vector2> { new(3.4f, 10.3f), new(3.4f, 18.5f), new(2.6f, 21.0f), new(1.2f, 22.4f), new(0.01f, 22.6f) };
            Obj(root, "CrewCabin", ProcMesh.Lathe(cabin, 32), mt.white, Vector3.zero);
            for (int i = 0; i < 6; i++)
            {
                var q = Quaternion.Euler(0, -30 + i * 12, 0);
                Obj(root, "Window", Prim(PrimitiveType.Quad), mt.window, q * new Vector3(0, 19.4f, 3.36f), q * Quaternion.Euler(-12, 180, 0), new Vector3(0.7f, 0.5f, 1));
            }
            Box(root, "Hatch", new Vector3(0, 12.4f, 3.42f), new Vector3(1.3f, 2.1f, 0.1f), mt.grey);
            // elevator tower + cage, ladder
            Tube(root, "ElevatorRailL", 0.08f, mt.grey, new Vector3(-0.9f, 0.1f, 4.9f), new Vector3(-0.9f, 13.6f, 3.6f));
            Tube(root, "ElevatorRailR", 0.08f, mt.grey, new Vector3(0.9f, 0.1f, 4.9f), new Vector3(0.9f, 13.6f, 3.6f));
            Box(root, "ElevatorCage", new Vector3(0, 0.8f, 5.0f), new Vector3(2.0f, 0.12f, 1.6f), mt.yellow);
            Tube(root, "CageRail", 0.04f, mt.yellow, new Vector3(-1f, 0.8f, 4.2f), new Vector3(-1f, 1.9f, 4.2f), new Vector3(-1f, 1.9f, 5.8f), new Vector3(1f, 1.9f, 5.8f), new Vector3(1f, 1.9f, 4.2f), new Vector3(1f, 0.8f, 4.2f));
            for (int i = 0; i < 26; i++)
                Tube(root, "Rung", 0.025f, mt.grey, new Vector3(1.4f, 0.4f + i * 0.45f, 4.9f - i * 0.05f), new Vector3(1.9f, 0.4f + i * 0.45f, 4.9f - i * 0.05f));
            // solar wings + radiators + antennas
            foreach (float s in new[] { -1f, 1f })
            {
                Tube(root, "SolarBoom", 0.1f, mt.grey, new Vector3(s * 3.4f, 17f, 0), new Vector3(s * 6.0f, 17.5f, 0));
                Box(root, "SolarWing", new Vector3(s * 9.5f, 17.5f, 0), new Vector3(7f, 0.06f, 3.2f), mt.solar, new Vector3(0, 0, s * -8f));
                Box(root, "Radiator", new Vector3(s * 3.45f, 14.5f, -1.8f), new Vector3(0.05f, 4f, 2.6f), mt.radiator, new Vector3(0, s * 25f, 0));
            }
            Tube(root, "AntennaMast", 0.06f, mt.grey, new Vector3(0, 22.5f, 0), new Vector3(0, 25.5f, 0));
            Obj(root, "HGA", ProcMesh.Paraboloid(0.9f, 0.3f), mt.white, new Vector3(0, 25.6f, 0.2f), Quaternion.Euler(-40, 0, 0));
            Box(root, "LandingLight", new Vector3(0, 9.9f, 4.5f), new Vector3(0.6f, 0.3f, 0.2f), mt.dark);
            Collider(root.gameObject, new Vector3(0, 11.5f, 0), new Vector3(8.4f, 18.5f, 8.4f));
            return root.gameObject;
        }

        public static GameObject CargoLander(Transform parent)
        {
            var mt = Mt;
            var root = new GameObject("CargoLander (crane delivery)").transform;
            root.SetParent(parent, false);
            Obj(root, "Body", ProcMesh.Lathe(new List<Vector2> { new(2.4f, 1.6f), new(2.4f, 4.6f), new(2.0f, 5.0f) }, 6, false), mt.gold, Vector3.zero);
            Box(root, "Deck", new Vector3(0, 5.05f, 0), new Vector3(5.2f, 0.12f, 5.2f), mt.grey);
            for (int i = 0; i < 4; i++)
            {
                var q = Quaternion.Euler(0, i * 90f + 45f, 0);
                Tube(root, "Leg", 0.14f, mt.grey, q * new Vector3(0, 3.4f, 2.3f), q * new Vector3(0, 1.4f, 4.4f), q * new Vector3(0, 0.25f, 5.0f));
                Cyl(root, "Foot", q * new Vector3(0, 0.12f, 5.0f), 1.0f, 0.18f, mt.grey);
            }
            Obj(root, "Engine", ProcMesh.Lathe(new List<Vector2> { new(0.3f, 1.6f), new(0.55f, 0.5f) }, 16), mt.nozzle, Vector3.zero);
            // empty crane cradles + tie-downs on the deck (the crane is now at the pit)
            foreach (float x in new[] { -1.6f, 0f, 1.6f })
                Box(root, "Cradle", new Vector3(x, 5.4f, 0), new Vector3(0.25f, 0.6f, 4.4f), mt.yellow);
            Tube(root, "Ramp", 0.06f, mt.grey, new Vector3(2.4f, 5.05f, -1f), new Vector3(6.5f, 0.1f, -1f));
            Tube(root, "Ramp", 0.06f, mt.grey, new Vector3(2.4f, 5.05f, 1f), new Vector3(6.5f, 0.1f, 1f));
            Box(root, "Radiator", new Vector3(0, 3.2f, -2.45f), new Vector3(3.0f, 2.2f, 0.05f), mt.radiator);
            Collider(root.gameObject, new Vector3(0, 3.3f, 0), new Vector3(5.2f, 3.6f, 5.2f));
            return root.gameObject;
        }

        public static GameObject Reactor(Transform parent)
        {
            var mt = Mt;
            var root = new GameObject("FissionSurfacePower (40 kW)").transform;
            root.SetParent(parent, false);
            Obj(root, "Core", ProcMesh.Lathe(new List<Vector2> { new(1.1f, 0f), new(1.1f, 2.2f), new(0.9f, 2.6f), new(0.5f, 4.6f), new(0.01f, 4.8f) }, 20), mt.grey, Vector3.zero);
            Obj(root, "Shield", ProcMesh.Lathe(new List<Vector2> { new(1.3f, 0f), new(1.3f, 1.4f), new(1.1f, 1.5f) }, 20), mt.dark, Vector3.zero);
            for (int i = 0; i < 4; i++)
            {
                var q = Quaternion.Euler(0, i * 90f, 0);
                Obj(root, "RadiatorWing", Prim(PrimitiveType.Cube), mt.radiator, q * new Vector3(0, 3.2f, 2.6f), q, new Vector3(0.05f, 2.4f, 3.8f));
            }
            Box(root, "ControlBox", new Vector3(2.4f, 0.4f, 0), new Vector3(1.0f, 0.8f, 0.8f), mt.white);
            Collider(root.gameObject, new Vector3(0, 2.4f, 0), new Vector3(2.4f, 4.8f, 2.4f));
            return root.gameObject;
        }

        /// <summary>The robotic scout (~1.8 m): six wheels, mast camera, RTG at the back, ground radar antenna.</summary>
        public static GameObject Scout(Transform parent)
        {
            var mt = Mt;
            var root = new GameObject("Scout rover").transform;
            root.SetParent(parent, false);
            Box(root, "Chassis", new Vector3(0, 0.55f, 0), new Vector3(1.0f, 0.35f, 1.6f), mt.white);
            Box(root, "MLITop", new Vector3(0, 0.74f, 0), new Vector3(0.95f, 0.04f, 1.5f), mt.gold);
            for (int i = 0; i < 3; i++)
            foreach (float s in new[] { -1f, 1f })
                Cyl(root, "Wheel", new Vector3(s * 0.62f, 0.25f, -0.6f + i * 0.6f), 0.5f, 0.2f, mt.dark, new Vector3(0, 0, 90));
            Tube(root, "Mast", 0.03f, mt.grey, new Vector3(0.3f, 0.75f, 0.6f), new Vector3(0.3f, 1.7f, 0.6f));
            Box(root, "MastCam", new Vector3(0.3f, 1.78f, 0.65f), new Vector3(0.28f, 0.14f, 0.14f), mt.grey);
            Cyl(root, "RTG", new Vector3(0, 0.75f, -0.95f), 0.3f, 0.6f, mt.dark, new Vector3(90, 0, 0));
            Box(root, "GPRAntenna", new Vector3(0, 0.25f, 0.95f), new Vector3(0.9f, 0.08f, 0.3f), mt.grey);
            var lamp = new GameObject("BeaconLamp").AddComponent<Light>();
            lamp.transform.SetParent(root, false);
            lamp.transform.localPosition = new Vector3(0.3f, 1.9f, 0.65f);
            lamp.type = LightType.Point; lamp.range = 25f; lamp.color = new Color(1f, 0.55f, 0.2f);
            lamp.gameObject.AddComponent<PhysicalLight>().physicalIntensity = 60f;
            Collider(root.gameObject, new Vector3(0, 0.6f, 0), new Vector3(1.4f, 1.0f, 2.0f));
            return root.gameObject;
        }
    }
}
