using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using MoonBaseAlpha.Rendering;

namespace MoonBaseAlpha.Crew
{
    /// <summary>
    /// Procedural suited astronaut (xEMU-like): fabric suit with bearings at the joints, hard upper torso,
    /// PLSS backpack, helmet with white protective shell, gold sun visor and side lamps, gloves, boots.
    /// Crew are told apart by coloured stripes (Artemis-style). Feet at the local origin, facing +Z.
    /// </summary>
    public static class AstronautBuilder
    {
        static readonly Color[] Stripe = { new(0.75f, 0.1f, 0.08f), new(0.1f, 0.25f, 0.7f), new(0.85f, 0.65f, 0.1f), new(0.1f, 0.55f, 0.25f) };

        class M
        {
            public Material suit = Mats.Make("SuitFabric", new Color(0.86f, 0.86f, 0.84f), 0f, 0.15f, Mats.Fabric, 0.6f);
            public Material hard = Mats.Textured(Mats.Make("SuitHard", Color.white, 0f, 0.45f), ProcTex.Paint("SuitHardTex", new Color(0.9f, 0.9f, 0.88f), 21, 0.05f, 0.35f));
            public Material bearing = Mats.Textured(Mats.Make("Bearing", Color.white, 0.85f, 0.5f), ProcTex.Paint("BearingTex", new Color(0.55f, 0.56f, 0.58f), 22, 0.3f, 0.2f));
            public Material visor = Mats.Make("GoldVisor", new Color(0.8f, 0.58f, 0.22f), 0.45f, 0.85f);   // no env reflections on the Moon scene: keep some diffuse gold
            public Material glove = Mats.Textured(Mats.Make("Glove", Color.white, 0f, 0.3f, Mats.Fabric, 0.8f), ProcTex.Paint("GloveTex", new Color(0.35f, 0.38f, 0.45f), 23, 0f, 0.45f));
            public Material boot = Mats.Textured(Mats.Make("Boot", Color.white, 0f, 0.25f, Mats.Fabric, 0.8f), ProcTex.Paint("BootTex", new Color(0.75f, 0.75f, 0.73f), 24, 0f, 0.8f));
            public Material sole = Mats.Textured(Mats.Make("Sole", Color.white, 0f, 0.2f), ProcTex.Paint("SoleTex", new Color(0.12f, 0.12f, 0.12f), 25, 0f, 0.8f));
            public Material lamp = Mats.Emissive(Mats.Make("HelmetLamp", new Color(0.8f, 0.8f, 0.8f), 0f, 0.9f), Color.black);
            public Material[] stripes, huts, tabs;
            public Material plss, patch;
            public M()
            {
                stripes = new Material[Stripe.Length]; huts = new Material[Stripe.Length]; tabs = new Material[Stripe.Length];
                for (int i = 0; i < Stripe.Length; i++)
                {
                    stripes[i] = Mats.Textured(Mats.Make("Stripe" + i, Color.white, 0f, 0.2f), ProcTex.Paint("StripeTex" + i, Stripe[i], 26 + i, 0f, 0.25f));
                    huts[i] = Mats.Make("SuitHard" + i, Color.white, 0f, 0.45f);
                    huts[i].SetTexture("_BaseMap", ProcTex.HardTorso(Stripe[i]));
                    tabs[i] = Mats.Make("CrewTab" + i, Color.white, 0f, 0.3f);
                    tabs[i].SetTexture("_BaseMap", ProcTex.CrewTab(Stripe[i], i + 1));
                }
                // fabric: seams + stitching (round 8); the fabric normal map stays
                suit.SetColor("_BaseColor", Color.white * 0.89f);
                suit.SetTexture("_BaseMap", ProcTex.SuitSeams());
                plss = Mats.Make("PLSS", Color.white, 0f, 0.4f);
                plss.SetTexture("_BaseMap", ProcTex.PlssPanels());
                patch = Mats.Cutout(Mats.Make("MissionPatch", Color.white, 0f, 0.25f), ProcTex.MissionPatch(), Vector2.one);
            }
        }
        static M mats;

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

        static GameObject Part(Transform p, string n, PrimitiveType t, Material m, Vector3 pos, Vector3 scale, Vector3? euler = null) => Part(p, n, Prim(t), m, pos, scale, euler);
        static GameObject Part(Transform p, string n, Mesh mesh, Material m, Vector3 pos, Vector3 scale, Vector3? euler = null)
        {
            var go = new GameObject(n);
            go.transform.SetParent(p, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.Euler(euler ?? Vector3.zero);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.On;
            return go;
        }

        static Transform Joint(Transform p, string n, Vector3 pos)
        {
            var t = new GameObject(n).transform;
            t.SetParent(p, false);
            t.localPosition = pos;
            return t;
        }

        /// <summary>Limb segment hanging down from a joint: capsule of given length + bearing ring at the joint.</summary>
        static void Segment(Transform joint, string n, float len, float dia, Material m, M mm, Material stripe = null)
        {
            Part(joint, n, PrimitiveType.Capsule, m, new Vector3(0, -len / 2, 0), new Vector3(dia, len / 2 + dia * 0.25f, dia));
            Part(joint, n + "Bearing", PrimitiveType.Cylinder, mm.bearing, Vector3.zero, new Vector3(dia * 1.08f, 0.02f, dia * 1.08f));
            if (stripe != null) Part(joint, n + "Stripe", PrimitiveType.Cylinder, stripe, new Vector3(0, -len * 0.35f, 0), new Vector3(dia * 1.04f, 0.025f, dia * 1.04f));
        }

        public static AstronautRig Build(Transform parent, int crewIndex)
        {
            if (mats == null || mats.suit == null || mats.visor == null) mats = new M();   // materials may be unloaded with a scene
            var mm = mats;
            var root = new GameObject("Astronaut" + crewIndex).transform;
            root.SetParent(parent, false);
            var rig = root.gameObject.AddComponent<AstronautRig>();
            var stripe = mm.stripes[crewIndex % mm.stripes.Length];

            var pelvis = Joint(root, "Pelvis", new Vector3(0, 0.98f, 0));
            rig.pelvis = pelvis;
            // brief / lower torso
            Part(pelvis, "Brief", PrimitiveType.Sphere, mm.suit, new Vector3(0, 0.02f, 0), new Vector3(0.42f, 0.3f, 0.32f));
            Part(pelvis, "WaistBearing", PrimitiveType.Cylinder, mm.bearing, new Vector3(0, 0.17f, 0), new Vector3(0.4f, 0.02f, 0.3f));
            var chest = Joint(pelvis, "Chest", new Vector3(0, 0.18f, 0));
            rig.chest = chest;
            // hard upper torso
            Part(chest, "HUT", PrimitiveType.Sphere, mm.huts[crewIndex % mm.huts.Length], new Vector3(0, 0.27f, 0.01f), new Vector3(0.54f, 0.58f, 0.4f));
            // mission patch on the right chest (on the HUT surface, facing out)
            {
                var pp = new Vector3(0.15f, 0.36f, 0.168f);
                var n = new Vector3(0.15f / (0.27f * 0.27f), 0.09f / (0.29f * 0.29f), 0.158f / (0.2f * 0.2f)).normalized;
                var pg = Part(chest, "MissionPatch", PrimitiveType.Quad, mm.patch, pp + n * 0.004f, new Vector3(0.085f, 0.085f, 1f));
                pg.transform.localRotation = Quaternion.LookRotation(-n, Vector3.up);
                pg.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            }
            Part(chest, "DCM", PrimitiveType.Cube, mm.hard, new Vector3(0, 0.25f, 0.22f), new Vector3(0.26f, 0.12f, 0.1f), new Vector3(-15, 0, 0));
            Part(chest, "DCMKnobs", PrimitiveType.Cylinder, mm.bearing, new Vector3(0.06f, 0.29f, 0.27f), new Vector3(0.04f, 0.02f, 0.04f), new Vector3(75, 0, 0));
            // PLSS backpack
            Part(chest, "PLSS", PrimitiveType.Cube, mm.plss, new Vector3(0, 0.3f, -0.33f), new Vector3(0.5f, 0.66f, 0.26f));
            Part(chest, "PLSSTop", PrimitiveType.Cube, mm.hard, new Vector3(0, 0.66f, -0.3f), new Vector3(0.42f, 0.08f, 0.2f));
            Part(chest, "PLSSPanel", PrimitiveType.Cube, mm.bearing, new Vector3(0, 0.22f, -0.465f), new Vector3(0.36f, 0.3f, 0.01f));
            // helmet: shell + gold visor (hemisphere facing forward) + lamps
            var head = Joint(chest, "Head", new Vector3(0, 0.66f, 0.04f));
            rig.head = head;
            Part(head, "NeckRing", PrimitiveType.Cylinder, mm.bearing, new Vector3(0, -0.06f, 0), new Vector3(0.3f, 0.025f, 0.3f));
            Part(head, "Shell", PrimitiveType.Sphere, mm.hard, new Vector3(0, 0.12f, -0.02f), new Vector3(0.38f, 0.38f, 0.38f));
            var hemi = new List<Vector2>();
            for (int i = 0; i <= 10; i++) { float a = i * Mathf.PI / 2 / 10; hemi.Add(new Vector2(Mathf.Cos(a) * 0.195f + 0.0001f, Mathf.Sin(a) * 0.195f)); }
            var visorGo = Part(head, "Visor", ProcMesh.Lathe(hemi, 24), mm.visor, new Vector3(0, 0.1f, 0.0f), Vector3.one, new Vector3(90, 0, 0));
            visorGo.transform.localScale = new Vector3(0.98f, 1.0f, 0.9f);
            rig.visor = visorGo.GetComponent<Renderer>();
            foreach (float s in new[] { -1f, 1f })
                Part(head, "LampPod", PrimitiveType.Cube, mm.lamp, new Vector3(s * 0.19f, 0.2f, 0.05f), new Vector3(0.05f, 0.05f, 0.09f));
            Part(head, "StripeHelmet", PrimitiveType.Cube, stripe, new Vector3(0, 0.31f, -0.02f), new Vector3(0.05f, 0.02f, 0.25f));

            // arms
            rig.shoulderL = Joint(chest, "ShoulderL", new Vector3(-0.31f, 0.47f, 0));
            rig.shoulderR = Joint(chest, "ShoulderR", new Vector3(0.31f, 0.47f, 0));
            foreach (var (sh, side) in new[] { (rig.shoulderL, -1), (rig.shoulderR, 1) })
            {
                Segment(sh, "UpperArm", 0.3f, 0.15f, mm.suit, mm, stripe);
                if (side < 0)
                {
                    // crew-number tab on the left upper arm
                    var tab = Part(sh, "CrewTab", PrimitiveType.Quad, mm.tabs[crewIndex % mm.tabs.Length], new Vector3(-0.079f, -0.1f, 0f), new Vector3(0.06f, 0.04f, 1f));
                    tab.transform.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
                    tab.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                }
                var el = Joint(sh, "Elbow", new Vector3(0, -0.3f, 0));
                Segment(el, "Forearm", 0.27f, 0.13f, mm.suit, mm);
                var wr = Joint(el, "Wrist", new Vector3(0, -0.28f, 0));
                Part(wr, "WristRing", PrimitiveType.Cylinder, mm.bearing, Vector3.zero, new Vector3(0.12f, 0.02f, 0.12f));
                Part(wr, "Glove", PrimitiveType.Sphere, mm.glove, new Vector3(0, -0.07f, 0.01f), new Vector3(0.1f, 0.15f, 0.08f));
                Part(wr, "Thumb", PrimitiveType.Capsule, mm.glove, new Vector3(-side * 0.035f, -0.05f, 0.04f), new Vector3(0.03f, 0.035f, 0.03f), new Vector3(40, 0, 0));
                if (side < 0) { rig.elbowL = el; rig.wristL = wr; } else { rig.elbowR = el; rig.wristR = wr; }
            }
            // legs
            rig.hipL = Joint(pelvis, "HipL", new Vector3(-0.12f, -0.05f, 0));
            rig.hipR = Joint(pelvis, "HipR", new Vector3(0.12f, -0.05f, 0));
            foreach (var (hip, side) in new[] { (rig.hipL, -1), (rig.hipR, 1) })
            {
                Segment(hip, "Thigh", 0.44f, 0.18f, mm.suit, mm, stripe);
                var kn = Joint(hip, "Knee", new Vector3(0, -0.44f, 0));
                Segment(kn, "Shin", 0.4f, 0.16f, mm.suit, mm);
                var an = Joint(kn, "Ankle", new Vector3(0, -0.41f, 0));
                Part(an, "Boot", PrimitiveType.Cube, mm.boot, new Vector3(0, -0.03f, 0.05f), new Vector3(0.15f, 0.12f, 0.3f));
                Part(an, "Sole", PrimitiveType.Cube, mm.sole, new Vector3(0, -0.085f, 0.05f), new Vector3(0.16f, 0.025f, 0.31f));
                if (side < 0) { rig.kneeL = kn; rig.ankleL = an; } else { rig.kneeR = kn; rig.ankleR = an; }
            }
            return rig;
        }

        /// <summary>A crew member sitting in a rover seat (seat pan origin at <paramref name="seatPos"/>).</summary>
        public static GameObject BuildSeated(Transform parent, Vector3 seatPos, int crewIndex)
        {
            var rig = Build(parent, crewIndex);
            rig.transform.localPosition = seatPos + new Vector3(0, -0.98f + 0.16f, -0.05f);
            rig.SetSeated();
            return rig.gameObject;
        }
    }

    /// <summary>Joint references + simple procedural poses.</summary>
    public class AstronautRig : MonoBehaviour
    {
        public Transform pelvis, chest, head, shoulderL, shoulderR, elbowL, elbowR, wristL, wristR, hipL, hipR, kneeL, kneeR, ankleL, ankleR;
        public Renderer visor;

        public void SetSeated()
        {
            hipL.localRotation = hipR.localRotation = Quaternion.Euler(-88, 0, 0);
            kneeL.localRotation = kneeR.localRotation = Quaternion.Euler(80, 0, 0);
            ankleL.localRotation = ankleR.localRotation = Quaternion.Euler(-5, 0, 0);
            shoulderL.localRotation = Quaternion.Euler(-30, 0, -8);
            shoulderR.localRotation = Quaternion.Euler(-30, 0, 8);
            elbowL.localRotation = elbowR.localRotation = Quaternion.Euler(-50, 0, 0);
            chest.localRotation = Quaternion.Euler(-6, 0, 0);
        }

        /// <summary>
        /// Locomotion pose. phase in [0,1) per stride; amount = 0 standing .. 1 full stride;
        /// airborne = lunar "lope" flight phase (legs tucked, arms out for balance).
        /// </summary>
        public void SetLocomotion(float phase, float amount, bool airborne, float lean)
        {
            float s = Mathf.Sin(phase * Mathf.PI * 2);
            float c = Mathf.Cos(phase * Mathf.PI * 2);
            float swing = 28f * amount;
            if (airborne)
            {
                hipL.localRotation = Quaternion.Euler(-20 * amount - 8, 0, 0);
                hipR.localRotation = Quaternion.Euler(-5 * amount, 0, 0);
                kneeL.localRotation = Quaternion.Euler(35 * amount + 10, 0, 0);
                kneeR.localRotation = Quaternion.Euler(25 * amount + 8, 0, 0);
            }
            else
            {
                hipL.localRotation = Quaternion.Euler(-swing * s, 0, 0);
                hipR.localRotation = Quaternion.Euler(swing * s, 0, 0);
                kneeL.localRotation = Quaternion.Euler(Mathf.Max(0, 30 * amount * c) + 4, 0, 0);
                kneeR.localRotation = Quaternion.Euler(Mathf.Max(0, -30 * amount * c) + 4, 0, 0);
            }
            ankleL.localRotation = ankleR.localRotation = Quaternion.identity;
            shoulderL.localRotation = Quaternion.Euler(18 * amount * s, 0, airborne ? -25 : -6);
            shoulderR.localRotation = Quaternion.Euler(-18 * amount * s, 0, airborne ? 25 : 6);
            elbowL.localRotation = elbowR.localRotation = Quaternion.Euler(-25 - 10 * amount, 0, 0);
            chest.localRotation = Quaternion.Euler(lean, 0, 0);
        }

        public void SetVisible(bool body, bool helmet)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                bool isHead = r.transform.IsChildOf(head);
                r.shadowCastingMode = ShadowCastingMode.On;
                r.enabled = isHead ? helmet : body;
                if (!r.enabled) { r.enabled = true; r.shadowCastingMode = ShadowCastingMode.ShadowsOnly; }
            }
        }
    }
}
