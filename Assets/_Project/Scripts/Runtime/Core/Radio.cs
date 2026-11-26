using System.Collections.Generic;
using UnityEngine;

namespace MoonBaseAlpha
{
    /// <summary>
    /// The radio: one queue of lines by dialogue ID (<see cref="Dialog"/>), spoken one at a time, never over each
    /// other. Anything from Earth arrives after the real Earth-Moon round trip (~2.56 s light time) plus a human
    /// response time. A line with a recording plays it (band-passed like a radio; Houston lines framed by
    /// Quindar tones) and its subtitle stays up for the clip length + 1 s; without one it is subtitles only.
    /// Static so any system can talk; <see cref="Tick"/> is driven by the component (play) or by tests.
    /// </summary>
    public class Radio : MonoBehaviour
    {
        public const float RoundTrip = 2.6f;     // s: 2 x 384,400 km / c = 2.56 s
        public const float ShowFor = 7f;         // s on screen (text-only lines)
        public const float Gap = 0.35f;          // s of silence between two lines
        public const float QuindarLen = 0.25f;   // s: Apollo "intro" 2525 Hz / "outro" 2475 Hz tones

        public struct Line
        {
            public string id, who, text;
            public float at;          // queued: earliest start; shown: start time
            public float dur;         // s the line occupies the channel
            public float show;        // s the subtitle stays up
            public bool earth;        // Houston (Quindar tones)
            public bool urgent;       // a reaction to something happening now: said before story lines that wait
            public AudioClip clip;
            public int order;
        }

        static readonly List<Line> queue = new();
        static readonly List<Line> shown = new();
        public static readonly List<string> Log = new();
        /// <summary>Every ID that was spoken, in order (tests, mission state).</summary>
        public static readonly List<string> Spoken = new();
        /// <summary>Every line that started: ID, start time and how long it held the channel (tests: no overlaps).</summary>
        public static readonly List<(string id, float start, float dur)> History = new();
        public static float Now { get; private set; }
        public static IReadOnlyList<Line> Shown => shown;
        static float busyUntil; static int orderCounter;
        /// <summary>Something queued or still being spoken.</summary>
        public static bool Busy => queue.Count > 0 || Now < busyUntil;
        /// <summary>Seconds since the channel went quiet (0 while busy).</summary>
        public static float QuietFor => Busy ? 0f : Now - busyUntil;
        public static int Pending => queue.Count;
        /// <summary>When the line being spoken (if any) ends.</summary>
        public static float BusyUntil => busyUntil;

        /// <summary>Dialogue volume 0..1 and mute (Esc menu; saved).</summary>
        public static float Volume { get => PlayerPrefs.GetFloat("mba.dialogVolume", 1f); set => PlayerPrefs.SetFloat("mba.dialogVolume", Mathf.Clamp01(value)); }
        public static bool Muted { get => PlayerPrefs.GetInt("mba.dialogMute", 0) == 1; set => PlayerPrefs.SetInt("mba.dialogMute", value ? 1 : 0); }
        public static bool QuindarOn = true;

        /// <summary>Queue line <paramref name="id"/> to be heard no earlier than <paramref name="delay"/> s from now.</summary>
        public static void Say(string id, float delay = 0f, bool urgent = false) => Enqueue(id, delay, false, urgent);

        /// <summary>Mission Control reacting to something that happens now: light time both ways + a response time.</summary>
        public static void FromEarth(string id, float responseDelay = 1.2f, float extraDelay = 0f, bool urgent = false) => Enqueue(id, extraDelay + RoundTrip + responseDelay, true, urgent);

        static void Enqueue(string id, float delay, bool earth, bool urgent = false)
        {
            var e = Dialog.Get(id);
            if (!Dialog.Has(id)) Debug.LogWarning($"[Radio] unknown dialogue ID '{id}'");
            earth |= e.speaker == "HOUSTON";
            queue.Add(new Line { id = id, who = e.speaker, text = e.text, at = Now + delay, earth = earth, urgent = urgent, order = orderCounter++ });
        }

        /// <summary>Is <paramref name="id"/> queued or on screen?</summary>
        public static bool Has(string id) => queue.Exists(l => l.id == id) || shown.Exists(l => l.id == id);

        public static void Clear() { queue.Clear(); shown.Clear(); Log.Clear(); Spoken.Clear(); History.Clear(); Now = 0f; busyUntil = 0f; }

        /// <summary>Time a text-only line takes to say (~3 words/s).</summary>
        public static float TextDuration(string text)
        {
            int words = 1; foreach (char c in text) if (c == ' ') words++;
            return 0.8f + 0.3f * words;
        }

        public static void Tick(float dt)
        {
            Now += dt;
            if (Now >= busyUntil && queue.Count > 0)
            {
                int pick = -1;
                for (int i = 0; i < queue.Count; i++)
                {
                    if (queue[i].at > Now) continue;
                    // urgent reactions first, then first queued, first said
                    if (pick < 0 || (queue[i].urgent && !queue[pick].urgent) || (queue[i].urgent == queue[pick].urgent && queue[i].order < queue[pick].order)) pick = i;
                }
                if (pick >= 0)
                {
                    var l = queue[pick]; queue.RemoveAt(pick);
                    l.at = Now;
                    l.clip = Dialog.Clip(l.id);
                    float lead = l.earth && QuindarOn ? QuindarLen : 0f;
                    if (l.clip != null) { l.dur = 2f * lead + l.clip.length; l.show = l.dur + 1f; }
                    else { l.dur = TextDuration(l.text); l.show = Mathf.Max(ShowFor, l.dur + 1f); }
                    busyUntil = Now + l.dur + Gap;
                    shown.Add(l);
                    Spoken.Add(l.id);
                    History.Add((l.id, Now, l.dur));
                    var s = $"[Radio] {Now:0.0} s {l.who}: {l.text}";
                    Log.Add(s);
                    Debug.Log(s);
                    Started?.Invoke(l);
                    MessageLog.Add(l.text, l.earth ? MessageLog.Kind.Houston : MessageLog.Kind.Crew, l.who);   // round 12 (P5)
                }
            }
            shown.RemoveAll(l => Now - l.at > l.show);
        }

        /// <summary>A line starts (the component plays its audio).</summary>
        public static event System.Action<Line> Started;

        // ------------------------------------------------------------------ audio (play mode)

        AudioSource voice, tones;
        AudioClip quindarIn, quindarOut;
        readonly List<(float t, AudioClip c, AudioSource s)> sched = new();

        void Awake()
        {
            Dialog.EnsureLoaded();
            voice = gameObject.AddComponent<AudioSource>();
            tones = gameObject.AddComponent<AudioSource>();
            foreach (var s in new[] { voice, tones }) { s.playOnAwake = false; s.spatialBlend = 0f; s.ignoreListenerPause = true; }
            // radio voice: band-pass ~300-3400 Hz (the voice band of a VHF / telephone link) + a little grit
            if (GetComponent<AudioHighPassFilter>() == null) gameObject.AddComponent<AudioHighPassFilter>().cutoffFrequency = 300f;
            if (GetComponent<AudioLowPassFilter>() == null) gameObject.AddComponent<AudioLowPassFilter>().cutoffFrequency = 3400f;
            if (GetComponent<AudioDistortionFilter>() == null) gameObject.AddComponent<AudioDistortionFilter>().distortionLevel = 0.12f;
            quindarIn = Tone("QuindarIntro", 2525f); quindarOut = Tone("QuindarOutro", 2475f);
            Started += OnStarted;
            if (Application.isPlaying) StartCoroutine(Dialog.LoadClips());
        }

        void OnDestroy() { Started -= OnStarted; }

        static AudioClip Tone(string name, float hz)
        {
            int rate = 44100, n = (int)(rate * QuindarLen);
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = 0.35f * Mathf.Sin(2 * Mathf.PI * hz * i / rate) * Mathf.Clamp01(Mathf.Min(i, n - i) / 200f);
            var c = AudioClip.Create(name, n, 1, rate, false); c.SetData(d, 0); return c;
        }

        void OnStarted(Line l)
        {
            if (Muted || Volume <= 0f) return;
            float lead = l.earth && QuindarOn ? QuindarLen : 0f;
            float t0 = Time.unscaledTime;
            if (lead > 0) sched.Add((t0, quindarIn, tones));
            if (l.clip != null)
            {
                sched.Add((t0 + lead, l.clip, voice));
                if (lead > 0) sched.Add((t0 + lead + l.clip.length, quindarOut, tones));
            }
            else if (lead > 0) sched.Add((t0 + Mathf.Max(0.5f, l.dur - QuindarLen), quindarOut, tones));
        }

        void Update()
        {
            Tick(Time.unscaledDeltaTime);
            for (int i = 0; i < sched.Count; i++)
            {
                if (sched[i].t > Time.unscaledTime) continue;
                if (!Muted) sched[i].s.PlayOneShot(sched[i].c, Volume);
                sched.RemoveAt(i--);
            }
        }

        GUIStyle style;
        void OnGUI()
        {
            if (shown.Count == 0) return;
            if (!MissionDirector.HideHud) return;   // round 12 (P5): the helmet HUD's log panel shows the lines; subtitles only on the probe camera / debrief
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = true };
            float W = Screen.width, H = Screen.height;
            int n = Mathf.Min(3, shown.Count);
            for (int k = 0; k < n; k++)
            {
                var l = shown[shown.Count - n + k];
                float age = Now - l.at, a = Mathf.Clamp01((l.show - age) / 1.0f);
                var r = new Rect(W * 0.5f - 430, H - 180 + (k - n + 1) * 46, 860, 44);
                style.normal.textColor = new Color(0, 0, 0, a * 0.8f);
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), $"<b>{l.who}:</b> {l.text}", style);
                style.normal.textColor = l.earth ? new Color(0.75f, 0.9f, 1f, a) : new Color(1f, 0.95f, 0.8f, a);
                GUI.Label(r, $"<b>{l.who}:</b> {l.text}", style);
            }
        }
    }

    /// <summary>Camera shake impulses (decaying), sampled by the cameras.</summary>
    public static class CameraShake
    {
        static float amp, decay = 3f, phase;
        public static void Add(float amplitude, float duration) { amp = Mathf.Max(amp, amplitude); decay = 3f / Mathf.Max(0.1f, duration); }
        public static float Amplitude => amp;
        public static Vector3 Sample(float dt)
        {
            if (amp < 1e-4f) { amp = 0; return Vector3.zero; }
            phase += dt;
            var o = amp * new Vector3(Mathf.PerlinNoise(phase * 18f, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, phase * 21f) - 0.5f, Mathf.PerlinNoise(phase * 15f, 5.1f) - 0.5f) * 2f;
            amp *= Mathf.Exp(-decay * dt);
            return o;
        }
    }
}
