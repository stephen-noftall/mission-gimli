using UnityEngine;
using MoonBaseAlpha.Buggy;
using MoonBaseAlpha.Rendering;
using MoonBaseAlpha.Terrain;

namespace MoonBaseAlpha.Crew
{
    /// <summary>
    /// Who the player is controlling and from where we look.
    /// E: get out of / into the rover. C: first / third person. Mouse wheel / d-pad: visor zoom 1/2/4/8x
    /// (first person; in third person the wheel is the camera distance).
    /// On foot: WASD/arrows move, Shift lope, Space jump, H helmet lamps, mouse look.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class PlayerControl : MonoBehaviour
    {
        public enum Mode { Driving, OnFoot }
        /// <summary>V cycles: first person -> rover mast -> base -> crane (only while it moves) -> first person.</summary>
        public enum View { FirstPerson, RoverMast, Base, Crane }
        public Base.CraneController crane;
        public Transform baseCam;
        public View CurrentView { get; private set; } = View.FirstPerson;
        public bool FirstPersonView => !ThirdPerson && CurrentView == View.FirstPerson;
        bool ShowSelf => !FirstPersonView;
        public static string Label(View v) => v switch
        {
            View.RoverMast => "ROVER MAST CAM", View.Base => "BASE CAM", View.Crane => "CRANE CAM", _ => "FIRST PERSON",
        };
        public string ViewLabel => ThirdPerson ? "THIRD PERSON" : Label(CurrentView);

        public Camera cam;
        public BuggyController rover;
        public TerrainStreamer streamer;
        public Cave.CaveStreamer tunnels;
        public ExposureController exposure;
        public WorldOrigin origin;
        public LunarPhysics physics;

        public Mode State { get; private set; } = Mode.Driving;
        public bool ThirdPerson { get; private set; }
        public AstronautController Walker { get; private set; }
        public float NearRoverDistance { get; private set; } = 999f;

        DriverCamera driverCam;
        float pitch, orbitYaw, orbitPitch = 15f, orbitDist = 8f;
        public const float BaseFov = 75f;
        static readonly int[] ZoomSteps = { 1, 2, 4, 8 };
        int zoomIndex;
        float dpadPrev;
        bool dpadOk = true;
        /// <summary>Visor zoom factor (speculative ~2035 helmet display), first person only.</summary>
        public int Zoom => FirstPersonView ? ZoomSteps[zoomIndex] : 1;
        public static float FovFor(int zoom) => 2f * Mathf.Atan(Mathf.Tan(BaseFov * 0.5f * Mathf.Deg2Rad) / zoom) * Mathf.Rad2Deg;
        GameObject driverModel;

        public void Init()
        {
            driverCam = cam.GetComponent<DriverCamera>();
            driverModel = BuggyBuilder.DriverModel(rover);
            if (driverModel) driverModel.GetComponent<AstronautRig>()?.SetVisible(false, false);
            SetDriving();
        }

        public Transform Focus => State == Mode.Driving ? rover.transform : Walker.transform;

        void SetDriving()
        {
            State = Mode.Driving;
            rover.GetComponent<BuggyDriver>().enabled = true;
            rover.brake = 0f; rover.parkingBrake = false; rover.throttle = 0f;
            if (Walker) Walker.gameObject.SetActive(false);
            if (driverModel) driverModel.GetComponent<AstronautRig>()?.SetVisible(ShowSelf, ShowSelf);
            streamer.physicsFocus2 = null;
            if (tunnels) tunnels.physicsFocus2 = null;
            origin.target = rover.transform;
            physics.focus = rover.transform;
            driverCam.enabled = FirstPersonView;
        }

        /// <summary>[V] next camera view (the crane camera only while the crane moves).</summary>
        public void CycleView()
        {
            if (ThirdPerson) { ThirdPerson = false; CurrentView = View.FirstPerson; }
            var next = CurrentView;
            do next = (View)(((int)next + 1) % 4);
            while ((next == View.Crane && !(crane != null && crane.Moving)) || (next == View.Base && baseCam == null));
            SetView(next);
        }

        public void SetView(View v)
        {
            CurrentView = v;
            if (driverCam) driverCam.enabled = State == Mode.Driving && FirstPersonView;
            if (State == Mode.Driving) { if (driverModel) driverModel.GetComponent<AstronautRig>()?.SetVisible(ShowSelf, ShowSelf); }
            else if (Walker) Walker.rig.SetVisible(ShowSelf, ShowSelf);
        }

        /// <summary>Place the camera for the mast / base / crane views (called every LateUpdate; tests call it directly).</summary>
        public void UpdateViewCamera()
        {
            if (ThirdPerson || CurrentView == View.FirstPerson) return;
            switch (CurrentView)
            {
                case View.RoverMast:
                {
                    var m = BuggyBuilder.MastCam(rover);
                    if (m != null) cam.transform.SetPositionAndRotation(m.position, m.rotation);
                    break;
                }
                case View.Base:
                    if (baseCam != null) { var vp = baseCam.Find("View") ?? baseCam; cam.transform.SetPositionAndRotation(vp.position, baseCam.rotation); }
                    break;
                case View.Crane:
                {
                    if (crane == null || !crane.Moving) { SetView(View.FirstPerson); return; }   // the ride is over
                    var up = -TerrainFrame.DownAt(crane.TipWorld);
                    var tip = crane.TipWorld - up * 1.2f;
                    var target = crane.Platform.position;
                    var fwdHint = Vector3.ProjectOnPlane(crane.Platform.transform.forward, up);
                    cam.transform.SetPositionAndRotation(tip, Quaternion.LookRotation((target - tip).normalized, fwdHint.sqrMagnitude > 1e-4f ? fwdHint.normalized : Vector3.forward));
                    break;
                }
            }
        }

        public void ExitRover()
        {
            if (State != Mode.Driving) return;
            if (Mathf.Abs(rover.Speed) > 1.0f) return;   // stop first
            rover.throttle = 0; rover.brake = 1; rover.parkingBrake = true;
            rover.GetComponent<BuggyDriver>().enabled = false;
            // step out on the driver's (left) side, onto the ground
            var up = -TerrainFrame.DownAt(rover.transform.position);
            var p = rover.transform.TransformPoint(new Vector3(-1.75f, 0.5f, 0.35f));
            if (Physics.Raycast(p + up * 2f, -up, out var hit, 6f, ~(1 << 2), QueryTriggerInteraction.Ignore)) p = hit.point + up * 0.05f;
            var fwd = Vector3.ProjectOnPlane(rover.transform.forward, up).normalized;
            if (Walker == null) Walker = AstronautController.Create(p, Quaternion.LookRotation(fwd, up));
            else { Walker.gameObject.SetActive(true); Walker.transform.SetPositionAndRotation(p, Quaternion.LookRotation(fwd, up)); Walker.Body.position = p; Walker.Body.linearVelocity = rover.Body.linearVelocity; }
            Walker.SetLamps(HeadlightsOn);
            if (driverModel) driverModel.SetActive(false);
            State = Mode.OnFoot;
            streamer.physicsFocus2 = Walker.transform;
            if (tunnels) tunnels.physicsFocus2 = Walker.transform;
            origin.target = Walker.transform;
            physics.focus = Walker.transform;
            driverCam.enabled = false;
            pitch = 0;
            Walker.rig.SetVisible(ShowSelf, ShowSelf);
        }

        public void EnterRover()
        {
            if (State != Mode.OnFoot || NearRoverDistance > 2.8f) return;
            if (driverModel) driverModel.SetActive(true);
            SetDriving();
        }

        /// <summary>Back into the driver seat regardless of distance (teleports / restart).</summary>
        public void EnterRoverForced()
        {
            if (State != Mode.OnFoot) return;
            if (driverModel) driverModel.SetActive(true);
            SetDriving();
        }

        bool HeadlightsOn
        {
            get { var d = rover.GetComponent<BuggyDriver>(); return d.headlights.Length > 0 && d.headlights[0].enabled; }
        }

        void Update()
        {
            if (rover == null) return;
            if (State == Mode.OnFoot && Walker != null)
                NearRoverDistance = Vector3.Distance(Walker.transform.position, rover.transform.TransformPoint(new Vector3(-0.42f, 0.0f, 0.35f)));
            else NearRoverDistance = 999f;

            if (Controls.Down(Controls.Btn.Board)) { if (State == Mode.Driving) ExitRover(); else EnterRover(); }
            if (Controls.Down(Controls.Btn.View))
            {
                ThirdPerson = !ThirdPerson;
                CurrentView = View.FirstPerson;
                SetView(View.FirstPerson);
                orbitYaw = 0;
            }
            if (Input.GetKeyDown(KeyCode.V)) CycleView();
            // visor zoom: mouse wheel (first person) or gamepad d-pad up/down
            if (FirstPersonView)
            {
                float wheel = Input.mouseScrollDelta.y;
                if (wheel > 0.01f) zoomIndex = Mathf.Min(zoomIndex + 1, ZoomSteps.Length - 1);
                if (wheel < -0.01f) zoomIndex = Mathf.Max(zoomIndex - 1, 0);
            }
            float dpad = dpadOk ? Controls.DPadY : 0f;
            if (FirstPersonView && dpad > 0.5f && dpadPrev <= 0.5f) zoomIndex = Mathf.Min(zoomIndex + 1, ZoomSteps.Length - 1);
            if (FirstPersonView && dpad < -0.5f && dpadPrev >= -0.5f) zoomIndex = Mathf.Max(zoomIndex - 1, 0);
            dpadPrev = dpad;
            // FOV eases to the zoom step in ~0.3 s
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, FovFor(Zoom), 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.1f));

            if (State == Mode.OnFoot && Walker != null)
            {
                Walker.move = Controls.Move;
                Walker.run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || Controls.Move.magnitude > 0.95f && Input.GetKey(KeyCode.JoystickButton8);
                if (Controls.Down(Controls.Btn.Brake)) Walker.jumpRequest = true;
                if (Controls.Down(Controls.Btn.Lights)) Walker.SetLamps(!Walker.LampsOn);
                if (FirstPersonView)
                {
                    var look = Controls.LookDelta(Time.unscaledDeltaTime);
                    Walker.yawDelta += look.x * 2f;
                    pitch = Mathf.Clamp(pitch - look.y * 2f, -88f, 75f);   // look up to 88 deg (Earth overhead)
                }
            }
            if (Input.GetMouseButtonDown(0) && !MoonBaseAlpha.UI.PauseMenu.AnyMenuOpen) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        void LateUpdate()
        {
            if (rover == null) return;
            if (ThirdPerson)
            {
                {
                    // on foot: the mouse / right stick turns the astronaut and the camera stays behind them;
                    // driving: free orbit around the rover (the mouse can't steer it)
                    var look = Controls.LookDelta(Time.unscaledDeltaTime);
                    if (State == Mode.OnFoot && Walker != null) { Walker.yawDelta += look.x * 3f; orbitYaw = 0f; }
                    else orbitYaw += look.x * 3f;
                    orbitPitch = Mathf.Clamp(orbitPitch - look.y * 2f, -10f, 80f);
                }
                orbitDist = Mathf.Clamp(orbitDist - Input.mouseScrollDelta.y * 0.8f, 2.5f, 40f);
                var t = Focus;
                var up = -TerrainFrame.DownAt(t.position);
                var target = t.position + up * (State == Mode.Driving ? 1.0f : 1.3f);
                var baseFwd = Vector3.ProjectOnPlane(t.forward, up).normalized;
                var rot = Quaternion.AngleAxis(orbitYaw, up) * Quaternion.LookRotation(baseFwd, up) * Quaternion.Euler(orbitPitch, 0, 0);
                var desired = target - rot * Vector3.forward * orbitDist;
                // don't go through the ground / cave walls
                if (Physics.SphereCast(target, 0.3f, (desired - target).normalized, out var hit, orbitDist, ~(1 << 2), QueryTriggerInteraction.Ignore))
                    desired = target + (desired - target).normalized * Mathf.Max(0.5f, hit.distance - 0.2f);
                cam.transform.SetPositionAndRotation(desired, Quaternion.LookRotation(target - desired, up));
            }
            else if (CurrentView != View.FirstPerson) UpdateViewCamera();
            else if (State == Mode.OnFoot && Walker != null)
            {
                cam.transform.SetPositionAndRotation(Walker.eye.position, Walker.transform.rotation * Quaternion.Euler(pitch, 0, 0));
            }
            // the helmet (and its lamps) follow where you look
            if (State == Mode.OnFoot && Walker != null && Walker.rig != null)
                Walker.rig.head.localRotation = Quaternion.Euler(Mathf.Clamp(pitch, -60f, 70f), 0, 0);
        }
    }
}
