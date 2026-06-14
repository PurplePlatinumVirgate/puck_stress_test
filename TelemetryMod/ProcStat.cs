using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TelemetryMod
{
    // Always-on process-level stats for the Puck process: resident memory (RSS,
    // the REAL OS RAM — not the managed heap that GC.GetTotalMemory reports),
    // cumulative CPU time, and thread count.
    //
    // On the production Linux/Mono dedicated server the System.Diagnostics
    // process memory/CPU APIs are unreliable (see ThreadSampler — ProcessThread
    // timings throw), so we read /proc/self directly. This reuses ThreadSampler's
    // proven primitives: File.ReadAllText on /proc, the /proc/.../stat field
    // layout (fields 14/15 = utime/stime after the last ')'), and sysconf for
    // USER_HZ. The .NET Process API is the Windows-only fallback.
    //
    // Cost: two small /proc reads per 20 Hz sample (~µs) — passive, negligible.
    internal static class ProcStat
    {
        public struct Sample
        {
            public long   RssBytes;    // resident set size
            public double CpuSeconds;  // cumulative (utime + stime)
            public int    Threads;
        }

        private static bool s_useProc;
        private static long s_clkTck = 100;  // Linux USER_HZ fallback
        private static bool s_init;

        private static void EnsureInit()
        {
            if (s_init) return;
            s_init = true;
            try { s_useProc = Directory.Exists("/proc/self"); } catch { s_useProc = false; }
            if (s_useProc)
                try { long hz = sysconf(2 /*_SC_CLK_TCK*/); if (hz > 0) s_clkTck = hz; } catch { }
        }

        public static Sample Read()
        {
            EnsureInit();
            try { return s_useProc ? ReadProc() : ReadDotnet(); }
            catch { return default; }
        }

        // ---- Linux /proc/self ---------------------------------------------
        private static Sample ReadProc()
        {
            var s = new Sample();
            try
            {
                foreach (var line in File.ReadAllLines("/proc/self/status"))
                {
                    if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                        s.RssBytes = FirstLong(line) * 1024L;          // kB -> bytes
                    else if (line.StartsWith("Threads:", StringComparison.Ordinal))
                        s.Threads = (int)FirstLong(line);
                    if (s.RssBytes != 0 && s.Threads != 0) break;
                }
            }
            catch { }
            // CPU: utime (field 14) + stime (field 15) from /proc/self/stat —
            // same parse as ThreadSampler's per-thread stat (fields after last ')').
            try
            {
                string stat = File.ReadAllText("/proc/self/stat");
                int rp = stat.LastIndexOf(')');
                if (rp >= 0)
                {
                    var f = stat.Substring(rp + 2).Split(' ');
                    if (f.Length >= 13)
                        s.CpuSeconds = (double)(long.Parse(f[11]) + long.Parse(f[12])) / s_clkTck;
                }
            }
            catch { }
            return s;
        }

        // ---- Windows fallback ---------------------------------------------
        private static Sample ReadDotnet()
        {
            var s = new Sample();
            try
            {
                var p = Process.GetCurrentProcess();
                s.RssBytes   = p.WorkingSet64;
                s.Threads    = p.Threads.Count;
                s.CpuSeconds = p.TotalProcessorTime.TotalSeconds;
            }
            catch { }
            return s;
        }

        // First contiguous run of digits in a line (e.g. "VmRSS:\t 123456 kB").
        private static long FirstLong(string line)
        {
            var sb = new StringBuilder();
            foreach (char c in line)
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (sb.Length > 0) break;
            }
            return sb.Length > 0 && long.TryParse(sb.ToString(), out var v) ? v : 0L;
        }

        [DllImport("libc", EntryPoint = "sysconf")]
        private static extern long sysconf(int name);
    }
}
