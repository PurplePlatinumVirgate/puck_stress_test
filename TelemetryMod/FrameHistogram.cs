using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Netcode;

namespace TelemetryMod
{
    // Always-on per-frame frame-time histogram. Fixes the length bias of the
    // 20 Hz metrics sampler: WaitForSecondsRealtime deadlines land in a frame
    // with probability proportional to its duration, so the sampled frame_ms
    // distribution is time-weighted and its tail percentiles are inflated in
    // an arm-dependent way for A/B comparisons. This module instead counts
    // EVERY frame into a preallocated log-binned histogram (zero allocation
    // on the hot path) keyed by game phase, and flushes one CSV row per
    // (window, phase-with-frames) through the same 1 s window channel as
    // FunctionProfiler/Counters.
    //
    // Bin spec: 12 bins per octave (ratio 2^(1/12), ~5.95% relative width),
    // 144 regular bins from 0.5 ms to 2048 ms, plus underflow (<0.5 ms) and
    // overflow (>=2048 ms) = 146 count columns. Columns are named by their
    // upper edge in integer microseconds ("u530" = bin ending at 530 us;
    // "u500" = underflow, "uinf" = overflow), so the file is self-describing
    // and the analysis reconstructs edges from the header alone.
    //
    // Timing uses Stopwatch.GetTimestamp deltas between consecutive
    // LateUpdates rather than Time.deltaTime: deltaTime is clamped by
    // Time.maximumDeltaTime and silently truncates large hitches, so the
    // histogram's max can legitimately exceed the legacy metrics frame_ms.
    internal static class FrameHistogram
    {
        public const int    BinsPerOctave = 12;
        public const int    Octaves       = 12;
        public const long   MinUs         = 500;            // 0.5 ms
        public const int    RegularBins   = BinsPerOctave * Octaves; // 144
        public const int    NumBins       = RegularBins + 2;         // +under/overflow
        private const int   OverflowIdx   = NumBins - 1;
        private const double InvLn2       = 1.4426950408889634; // 1/ln(2)

        private sealed class Slot
        {
            public string Name;
            public long[] Bins = new long[NumBins];
            public long   Count, SumUs, MaxUs;
            public int    ConnMin, ConnMax;
        }

        private static Slot[] s_slots;          // one per GamePhase value + unknown
        private static int[]  s_slotOfPhase;    // enum int value -> slot index
        private static int    s_unknownSlot;
        private static long   s_lastTs;
        private static double s_usPerTick;
        private static long[] s_edgesUs;        // upper edges of the 144 regular bins

        // CSV header, generated from the same edge array used for naming so
        // header and binning cannot drift.
        public static string HeaderLine
        {
            get
            {
                var sb = new StringBuilder(
                    "t_ms,window_ms,game_phase,connected_min,connected_max,count,sum_us,max_us");
                sb.Append(",u").Append(MinUs);                  // underflow (< MinUs)
                for (int k = 0; k < RegularBins; k++)
                    sb.Append(",u").Append(EdgesUs[k]);
                sb.Append(",uinf");                             // overflow
                return sb.ToString();
            }
        }

        public static long[] EdgesUs
        {
            get
            {
                if (s_edgesUs == null)
                {
                    s_edgesUs = new long[RegularBins];
                    for (int k = 1; k <= RegularBins; k++)
                        s_edgesUs[k - 1] = (long)Math.Round(MinUs * Math.Pow(2.0, k / (double)BinsPerOctave));
                }
                return s_edgesUs;
            }
        }

        public static void Init()
        {
            s_usPerTick = 1e6 / Stopwatch.Frequency;
            s_lastTs    = 0;

            // GamePhase is sequential 0..10 on B897 (None..PostGame,
            // Puck_B897/Puck/GamePhase.cs), but map defensively: any value
            // outside the table lands in the unknown slot ("?"), which the
            // analysis phase filter ignores.
            var values = (GamePhase[])Enum.GetValues(typeof(GamePhase));
            int maxVal = 0;
            foreach (var v in values) if ((int)v > maxVal) maxVal = (int)v;

            s_slots       = new Slot[values.Length + 1];
            s_slotOfPhase = new int[maxVal + 1];
            for (int i = 0; i < s_slotOfPhase.Length; i++) s_slotOfPhase[i] = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                s_slots[i] = new Slot { Name = values[i].ToString() };
                s_slotOfPhase[(int)values[i]] = i;
            }
            s_unknownSlot = values.Length;
            s_slots[s_unknownSlot] = new Slot { Name = "?" };
        }

        // Called every frame from TelemetryDriver.LateUpdate. Hot path:
        // no strings, no boxing, no LINQ, no per-frame allocation.
        public static void SampleFrame()
        {
            if (s_slots == null) return;
            long now = Stopwatch.GetTimestamp();
            if (s_lastTs == 0) { s_lastTs = now; return; } // first frame: no delta
            long dtUs = (long)((now - s_lastTs) * s_usPerTick);
            s_lastTs = now;
            if (dtUs <= 0) return;

            int slotIdx = s_unknownSlot;
            int connected = 0;
            try
            {
                var gm = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gm != null)
                {
                    int p = (int)gm.Phase;
                    if (p >= 0 && p < s_slotOfPhase.Length) slotIdx = s_slotOfPhase[p];
                }
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer && nm.ConnectedClientsList != null)
                    connected = nm.ConnectedClientsList.Count;
            }
            catch { /* pre-spawn singleton reads may throw; unknown slot is fine */ }

            int idx;
            if (dtUs < MinUs) idx = 0;
            else
            {
                idx = 1 + (int)(Math.Log(dtUs * (1.0 / MinUs)) * InvLn2 * BinsPerOctave);
                if (idx > OverflowIdx) idx = OverflowIdx;
                else if (idx < 1) idx = 1;
            }

            var s = s_slots[slotIdx];
            s.Bins[idx]++;
            s.SumUs += dtUs;
            if (dtUs > s.MaxUs) s.MaxUs = dtUs;
            if (s.Count == 0) { s.ConnMin = connected; s.ConnMax = connected; }
            else
            {
                if (connected < s.ConnMin) s.ConnMin = connected;
                else if (connected > s.ConnMax) s.ConnMax = connected;
            }
            s.Count++;
        }

        // Called once per window from Plugin.FlushWindow (main thread).
        // Allocation here is fine — once per second.
        public static void FlushWindow(StreamWriter w, long tMs, int windowMs)
        {
            if (w == null || s_slots == null) return;
            for (int i = 0; i < s_slots.Length; i++)
            {
                var s = s_slots[i];
                if (s.Count == 0) continue;
                var sb = new StringBuilder(NumBins * 2 + 64);
                sb.Append(tMs).Append(',').Append(windowMs).Append(',')
                  .Append(s.Name).Append(',')
                  .Append(s.ConnMin).Append(',').Append(s.ConnMax).Append(',')
                  .Append(s.Count).Append(',').Append(s.SumUs).Append(',').Append(s.MaxUs);
                for (int b = 0; b < NumBins; b++) sb.Append(',').Append(s.Bins[b]);
                w.WriteLine(sb.ToString());
                Array.Clear(s.Bins, 0, NumBins);
                s.Count = 0; s.SumUs = 0; s.MaxUs = 0; s.ConnMin = 0; s.ConnMax = 0;
            }
            w.Flush();
        }

        public static void Stop()
        {
            s_slots = null;
            s_slotOfPhase = null;
            s_lastTs = 0;
        }
    }
}
