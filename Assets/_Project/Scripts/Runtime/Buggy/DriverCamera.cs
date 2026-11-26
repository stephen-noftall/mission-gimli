using UnityEngine;

namespace MoonBaseAlpha.Buggy
{
    /// <summary>
    /// First-person view from the driver's helmet. Mouse look is limited to what a suited driver can
    /// turn (the helmet turns with the torso: ~100 deg each way, -55..+60 deg pitch).
    /// The head sits on a very soft spring so bumps are felt (in 1/6 g the crew float in their seats).
    /// </summary>
    public class DriverCamera : MonoBehaviour
    {
        public Transform seatEye;          // anchor on the buggy
        public const string HelmetPivot = "DriverHelmet";
        Transform helmet;
        public float sensitivity = 2.0f;
        public float yawLimit = 100f;
        public float pitchMin = -55f, pitchMax = 60f;
        [Tooltip("Head bob spring (Hz) and damping. Low g: slow, floaty.")]
        public float headFreq = 2.2f, headDamping = 0.5f;
        public float maxHeadOffset = 0.12f;

        float yaw, pitch;
        Vector3 headOffset, headVel;
        Vector3 lastAnchorVel, lastAnchorPos;
        bool first = true;

        void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void LateUpdate()
        {
            if (seatEye == null) return;
            if (Input.GetMouseButtonDown(0) && !MoonBaseAlpha.UI.PauseMenu.AnyMenuOpen) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            {
                var look = Controls.LookDelta(Time.unscaledDeltaTime);   // mouse (when captured) + right stick
                yaw = Mathf.Clamp(yaw + look.x * sensitivity, -yawLimit, yawLimit);
                pitch = Mathf.Clamp(pitch - look.y * sensitivity, pitchMin, pitchMax);
            }

            // head inertia: the anchor accelerates, the head lags on a spring (in the buggy frame)
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector3 pos = seatEye.position;
            if (first) { lastAnchorPos = pos; lastAnchorVel = Vector3.zero; first = false; }
            Vector3 vel = (pos - lastAnchorPos) / dt;
            Vector3 acc = (vel - lastAnchorVel) / dt;
            lastAnchorPos = pos; lastAnchorVel = vel;
            if (acc.sqrMagnitude > 400f) acc = acc.normalized * 20f;   // ignore origin shifts / teleports
            float w = 2f * Mathf.PI * headFreq;
            Vector3 a = -acc * 0.25f - w * w * headOffset - 2f * headDamping * w * headVel;
            headVel += a * dt;
            headOffset += headVel * dt;
            headOffset = Vector3.ClampMagnitude(headOffset, maxHeadOffset);

            transform.position = pos + headOffset + CameraShake.Sample(dt);
            transform.rotation = seatEye.rotation * Quaternion.Euler(pitch, yaw, 0f);
            // the driver's helmet lamps turn with the look
            if (helmet == null || helmet.parent != seatEye) helmet = seatEye.Find(HelmetPivot);
            if (helmet != null) helmet.localRotation = Quaternion.Euler(pitch, yaw, 0f);
        }
    }
}
