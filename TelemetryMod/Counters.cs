using System;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace TelemetryMod
{
    // Module C — Unity built-in profiler counters via ProfilerRecorder.
    //
    // Confirmed working on this Release + Mono dedicated-server build (proven by
    // ToasterProfiler). The external Unity Profiler cannot attach (no
    // player-connection-debug) and NGO NetworkMetrics are compiled out
    // (MULTIPLAYER_TOOLS undefined), so this in-process path is the only built-in
    // breakdown available. Gives a cheap, low-overhead frame-level view
    // (PlayerLoop vs scripts vs GC.Alloc bytes/frame) independent of Harmony.
    //
    // Sampled every frame from the driver's LateUpdate (LastValue is per-frame);
    // aggregated per window. Time counters report nanoseconds; count/byte
    // counters report raw values. The counter name disambiguates the unit.
    internal static class Counters
    {
        public static bool Enabled;

        private sealed class Rec
        {
            public string          Name;
            public ProfilerRecorder R;
            public Recorder        Legacy;
            public long            Total;
            public long            Max;
            public long            Count;
            public long[]          Buf;
            public int             BufIdx;
        }

        private static readonly List<Rec> s_recs = new List<Rec>();

        // (category, statName). Render-category counters are skipped on headless.
        private static readonly (ProfilerCategory cat, string name)[] s_defs =
        {
            (ProfilerCategory.Internal, "Main Thread"),
            (ProfilerCategory.Internal, "PlayerLoop"),
            (ProfilerCategory.Memory,   "GC.Alloc.Size"),
            (ProfilerCategory.Memory,   "GC.Alloc.Count"),
            (ProfilerCategory.Memory,   "GC Used Memory"),
            (ProfilerCategory.Memory,   "GC Reserved Memory"),
            (ProfilerCategory.Physics,  "Physics.ContactsCount"),
            (ProfilerCategory.Physics,  "Physics.QueriesPerformedCount"),
            (ProfilerCategory.Scripts,  "BehaviourUpdate"),
            (ProfilerCategory.Scripts,  "FixedBehaviourUpdate"),
            (ProfilerCategory.Scripts,  "LateBehaviourUpdate"),
        };

        public static void Init()
        {
            bool headless = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            int ok = 0;
            foreach (var (cat, name) in s_defs)
            {
                try
                {
                    var r = ProfilerRecorder.StartNew(cat, name, 1, ProfilerRecorderOptions.Default);
                    if (r.Valid) { s_recs.Add(new Rec { Name = name, R = r, Buf = new long[4096] }); ok++; }
                    else { r.Dispose(); Plugin.Log($"[counters] not available: {cat}/{name}"); }
                }
                catch (Exception ex) { Plugin.Log($"[counters] setup failed {cat}/{name}: {ex.Message}"); }
            }
            // Legacy GC.Collect time marker (separate API).
            try
            {
                var leg = Recorder.Get("GC.Collect");
                if (leg != null && leg.isValid) { leg.enabled = true; s_recs.Add(new Rec { Name = "GC.Collect", Legacy = leg, Buf = new long[4096] }); ok++; }
            }
            catch { }

            Enabled = s_recs.Count > 0;
            Plugin.Log($"[counters] enabled {ok} counters (headless={headless})");
        }

        // Called every frame on the main thread.
        public static void SampleFrame()
        {
            for (int i = 0; i < s_recs.Count; i++)
            {
                var rec = s_recs[i];
                long v;
                try { v = rec.Legacy != null ? rec.Legacy.elapsedNanoseconds : rec.R.LastValue; }
                catch { continue; }
                if (v <= 0) continue;
                rec.Total += v;
                rec.Count++;
                if (v > rec.Max) rec.Max = v;
                rec.Buf[rec.BufIdx % rec.Buf.Length] = v;
                rec.BufIdx++;
            }
        }

        public static void FlushWindow(StreamWriter w, long tMs, int windowMs)
        {
            if (w == null) return;
            foreach (var rec in s_recs)
            {
                if (rec.Count == 0) continue;
                int n = Math.Min(rec.BufIdx, rec.Buf.Length);
                double p95 = 0, p99 = 0;
                if (n > 0)
                {
                    var buf = new long[n];
                    Array.Copy(rec.Buf, buf, n);
                    Array.Sort(buf);
                    p95 = buf[Math.Min(n - 1, (int)(n * 0.95))];
                    p99 = buf[Math.Min(n - 1, (int)(n * 0.99))];
                }
                double avg = (double)rec.Total / rec.Count;
                w.WriteLine($"{tMs},{windowMs},{rec.Name.Replace(',', ';')}," +
                            $"{rec.Total},{avg:F1},{p95:F1},{p99:F1},{rec.Max}");
                rec.Total = 0; rec.Max = 0; rec.Count = 0; rec.BufIdx = 0;
            }
            w.Flush();
        }

        public static void Stop()
        {
            Enabled = false;
            foreach (var rec in s_recs)
            {
                try
                {
                    if (rec.Legacy != null) rec.Legacy.enabled = false;
                    else rec.R.Dispose();
                }
                catch { }
            }
            s_recs.Clear();
        }
    }
}
