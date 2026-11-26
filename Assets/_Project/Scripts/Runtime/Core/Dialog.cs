using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MoonBaseAlpha
{
    /// <summary>
    /// Every spoken line, by ID: <c>StreamingAssets/Dialog/dialog.csv</c> (id, speaker, text, notes). It lives in
    /// StreamingAssets so exhibit staff can fix text or drop in audio without a rebuild. A recording named
    /// <c>&lt;id&gt;.mp3</c> or <c>&lt;id&gt;.wav</c> next to the CSV is played by the radio; without one the line is
    /// subtitles only. WAV (PCM 16-bit) is read directly; MP3 is decoded by Unity at start (async).
    /// </summary>
    public static class Dialog
    {
        public struct Entry { public string id, speaker, text, notes; }

        static Dictionary<string, Entry> entries;
        static readonly Dictionary<string, AudioClip> clips = new();
        /// <summary>Tests point this at a scratch folder.</summary>
        public static string DirOverride;
        public static string Dir => DirOverride ?? Path.Combine(Application.streamingAssetsPath, "Dialog");
        public static string CsvPath => Path.Combine(Dir, "dialog.csv");
        public static IReadOnlyDictionary<string, Entry> Entries { get { EnsureLoaded(); return entries; } }

        public static void EnsureLoaded()
        {
            if (entries != null) return;
            entries = new Dictionary<string, Entry>();
            try
            {
                if (File.Exists(CsvPath)) foreach (var e in Parse(File.ReadAllText(CsvPath))) entries[e.id] = e;
                else Debug.LogWarning($"[Dialog] no table at {CsvPath}");
            }
            catch (Exception ex) { Debug.LogWarning($"[Dialog] could not read {CsvPath}: {ex.Message}"); }
        }

        /// <summary>Forget the table and clips (tests, or after staff edited the CSV).</summary>
        public static void Reload() { entries = null; clips.Clear(); EnsureLoaded(); }

        public static bool Has(string id) { EnsureLoaded(); return id != null && entries.ContainsKey(id); }

        /// <summary>The entry; an unknown ID returns itself as the text (visible, so it gets fixed).</summary>
        public static Entry Get(string id)
        {
            EnsureLoaded();
            if (id != null && entries.TryGetValue(id, out var e)) return e;
            return new Entry { id = id, speaker = "CREW", text = $"[{id}]" };
        }

        public static void Add(Entry e) { EnsureLoaded(); entries[e.id] = e; }

        /// <summary>RFC-4180-style CSV: quoted fields may hold commas, doubled quotes and newlines.</summary>
        public static List<Entry> Parse(string csv)
        {
            var rows = new List<List<string>>();
            var row = new List<string>(); var f = new StringBuilder(); bool q = false;
            for (int i = 0; i < csv.Length; i++)
            {
                char c = csv[i];
                if (q)
                {
                    if (c == '"') { if (i + 1 < csv.Length && csv[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                    else f.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { row.Add(f.ToString()); f.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    row.Add(f.ToString()); f.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                }
                else f.Append(c);
            }
            if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); rows.Add(row); }
            var list = new List<Entry>();
            for (int r = 1; r < rows.Count; r++)   // row 0 = header
            {
                var x = rows[r];
                if (x.Count < 3 || string.IsNullOrWhiteSpace(x[0])) continue;
                list.Add(new Entry { id = x[0].Trim(), speaker = x[1].Trim(), text = x[2].Trim(), notes = x.Count > 3 ? x[3].Trim() : "" });
            }
            return list;
        }

        // ------------------------------------------------------------------ audio

        /// <summary>Recording for a line (.mp3 preferred, then .wav), or null.</summary>
        public static string AudioPath(string id)
        {
            foreach (var ext in new[] { ".mp3", ".wav" })
            {
                var p = Path.Combine(Dir, id + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static AudioClip Clip(string id)
        {
            if (id == null) return null;
            if (clips.TryGetValue(id, out var c)) return c;
            // a WAV can be read on demand (fast); MP3s come from LoadClips at start
            var p = AudioPath(id);
            if (p != null && p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) { c = LoadWav(p); clips[id] = c; return c; }
            return null;
        }

        /// <summary>Decode every MP3 recording of the table (player start; WAVs load on demand).</summary>
        public static IEnumerator LoadClips()
        {
            EnsureLoaded();
            var ids = new List<string>(entries.Keys);
            int n = 0;
            foreach (var id in ids)
            {
                if (clips.ContainsKey(id)) continue;
                var p = AudioPath(id);
                if (p == null || !p.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                using var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(p).AbsoluteUri, AudioType.MPEG);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) { clips[id] = DownloadHandlerAudioClip.GetContent(req); n++; }
                else Debug.LogWarning($"[Dialog] {id}.mp3: {req.error}");
            }
            if (n > 0) Debug.Log($"[Dialog] {n} mp3 recordings loaded");
        }

        /// <summary>PCM WAV (8/16-bit, any rate, mono or stereo) -> AudioClip. Null if unreadable.</summary>
        public static AudioClip LoadWav(string path)
        {
            try
            {
                var b = File.ReadAllBytes(path);
                if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
                int ch = 1, rate = 44100, bits = 16, pos = 12;
                while (pos + 8 <= b.Length)
                {
                    string tag = Encoding.ASCII.GetString(b, pos, 4); int len = BitConverter.ToInt32(b, pos + 4);
                    if (tag == "fmt ") { ch = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
                    else if (tag == "data")
                    {
                        len = Math.Min(len, b.Length - pos - 8);
                        int bps = bits / 8, frames = len / (bps * ch);
                        var data = new float[frames * ch];
                        for (int k = 0; k < data.Length; k++)
                        {
                            int o = pos + 8 + k * bps;
                            data[k] = bits == 16 ? BitConverter.ToInt16(b, o) / 32768f : (b[o] - 128) / 128f;
                        }
                        var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), frames, ch, rate, false);
                        clip.SetData(data, 0);
                        return clip;
                    }
                    pos += 8 + len + (len & 1);
                }
            }
            catch (Exception e) { Debug.LogWarning($"[Dialog] {path}: {e.Message}"); }
            return null;
        }

        /// <summary>Write a mono 16-bit PCM WAV (tests).</summary>
        public static void WriteWav(string path, float[] samples, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples.Length * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples.Length * 2);
            foreach (var s in samples) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
        }

        /// <summary>Markdown recording script from the table (voice actors / TTS).</summary>
        public static string RecordingScript()
        {
            EnsureLoaded();
            var sb = new StringBuilder();
            sb.AppendLine("# Artemis-Marius - dialogue recording script\n");
            sb.AppendLine("Generated from `Assets/StreamingAssets/Dialog/dialog.csv` (the source of truth: edit the CSV, then regenerate).\n");
            sb.AppendLine("**Files:** one per line, mono, 44.1 kHz, MP3 (or 16-bit WAV), named `<ID>.mp3`, placed in");
            sb.AppendLine("`<game>_Data/StreamingAssets/Dialog/` (or `Assets/StreamingAssets/Dialog/` in the project). Lines without a");
            sb.AppendLine("file stay subtitles only. The game adds the radio band-pass and the Quindar tones on HOUSTON lines: record dry.\n");
            sb.AppendLine("**Speakers:** CMDR = commander (the driver); SPEC = mission specialist / geologist; HOUSTON = CAPCOM in");
            sb.AppendLine("Mission Control (arrives after the 2.6 s light delay); CREW = any crew member (generic rover lines).\n");
            var bySpeaker = new Dictionary<string, int>();
            foreach (var e in entries.Values) bySpeaker[e.speaker] = bySpeaker.TryGetValue(e.speaker, out var k) ? k + 1 : 1;
            sb.AppendLine("**Counts:** " + string.Join(", ", new List<string>(Map(bySpeaker))) + $"; {entries.Count} lines.\n");
            sb.AppendLine("| ID | Speaker | Line | Context |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var e in entries.Values) sb.AppendLine($"| `{e.id}` | {e.speaker} | {e.text.Replace("|", "/")} | {e.notes.Replace("|", "/")} |");
            return sb.ToString();
            static IEnumerable<string> Map(Dictionary<string, int> d) { foreach (var kv in d) yield return $"{kv.Key} {kv.Value}"; }
        }
    }
}
