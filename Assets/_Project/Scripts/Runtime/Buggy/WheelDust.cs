using UnityEngine;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// Regolith thrown by the wheels. With no air there is no billowing cloud: every grain flies a clean
    /// ballistic arc under 1/6 g (particle gravity = Physics.gravity, no drag) and dies on landing.
    /// Launch speed ~ the wheel rim speed, mostly backwards and up off the top of the tread, so at
    /// full speed the "rooster tail" rises several metres (the fenders catch most of it - realistic).
    /// </summary>
    [RequireComponent(typeof(BuggyController))]
    public class WheelDust : MonoBehaviour
    {
        public float particlesPerMetre = 900f;
        BuggyController buggy;
        ParticleSystem ps;
        float[] carry;

        void Start()
        {
            buggy = GetComponent<BuggyController>();
            carry = new float[buggy.wheels.Length];
            var go = new GameObject("WheelDust");
            go.AddComponent<WorldOriginIgnore>();   // shifted explicitly by WorldOrigin with the rest of world-space particles
            ps = go.AddComponent<ParticleSystem>();
            ps.Stop();
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 6f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.003f, 0.01f);   // 3-10 mm clods/grains (finer dust is invisible)
            main.gravityModifier = 1f;
            main.maxParticles = 60000;
            main.playOnAwake = false;
            main.startColor = new Color(1f, 0.97f, 0.93f, 0.9f);
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var col = ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.quality = ParticleSystemCollisionQuality.Medium;
            col.lifetimeLoss = 1f;     // regolith doesn't bounce
            col.bounce = 0f;
            col.collidesWith = ~(1 << BuggyBuilder.VehicleLayer);
            var r = go.GetComponent<ParticleSystemRenderer>();
            var mat = new Material(Shader.Find("MoonBaseAlpha/Dust"));
            mat.SetTexture("_MainTex", Rendering.ProcTex.SoftDot());
            r.sharedMaterial = mat;
            r.minParticleSize = 0.0008f;     // keep distant grains at least ~1 px so the spray reads
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ps.Play();
        }

        void FixedUpdate()
        {
            if (ps == null) return;
            var rb = buggy.Body;
            for (int i = 0; i < buggy.wheels.Length; i++)
            {
                var w = buggy.wheels[i];
                if (!w.grounded || w.soil.rigid) continue;
                float rim = Mathf.Abs(w.omega) * buggy.wheelRadius;
                if (rim < 0.15f) continue;
                carry[i] += particlesPerMetre * rim * Time.fixedDeltaTime * (1f + 3f * Mathf.Abs(w.slip));
                int count = (int)carry[i];
                carry[i] -= count;
                if (count == 0) continue;
                var up = transform.up;
                var fwd = Quaternion.AngleAxis(w.steer * Mathf.Rad2Deg, up) * transform.forward;
                var right = Vector3.Cross(up, fwd);
                var vBody = rb.GetPointVelocity(w.contactPoint);
                float dir = Mathf.Sign(w.omega);
                for (int k = 0; k < count; k++)
                {
                    // grains leave the tread at angles behind the contact patch, slowed by the fender
                    float a = Random.Range(10f, 70f) * Mathf.Deg2Rad;
                    float speed = rim * Random.Range(0.35f, 1.0f);
                    var v = vBody + (-fwd * dir * Mathf.Cos(a) + up * Mathf.Sin(a)) * speed + right * Random.Range(-0.25f, 0.25f) * speed;
                    var p = w.contactPoint + up * 0.03f - fwd * dir * 0.1f + right * Random.Range(-0.12f, 0.12f);
                    var ep = new ParticleSystem.EmitParams { position = p, velocity = v, applyShapeToPosition = false };
                    ps.Emit(ep, 1);
                }
            }
        }

        void OnDestroy() { if (ps) Util.Kill(ps.gameObject); }
    }
}
