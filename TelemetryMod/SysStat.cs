using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TelemetryMod
{
    // Always-on SYSTEM (whole-host) usage: total CPU, total RAM, load average.
    // Distinct from ProcStat (the Puck process alone). On this rig the bots are
    // colocated on the same box, so host usage = Puck + load generators + OS —
    // it surfaces host saturation / memory pressure / contention that per-process
    // stats can't.
    //
    // Linux/Mono reads system-wide /proc (same proven mechanism as ProcStat /
    // ThreadSampler). Available=false off Linux -> the caller writes empty CSV
    // fields -> NaN downstream (graceful, like the legacy proc_* absence).
    internal static class SysStat
    {
        public struct Sample
        {
            public bool   Available;
            public double CpuBusySeconds;   // cumulative
            public double CpuTotalSeconds;  // cumulative
            public long   MemUsedBytes;
            public long   MemTotalBytes;
            public double Load1;
        }

        private static bool s_useProc;
        private static long s_clkTck = 100;
        private static bool s_init;

        private static void EnsureInit()
        {
            if (s_init) return;
            s_init = true;
            try { s_useProc = File.Exists("/proc/stat"); } catch { s_useProc = false; }
            if (s_useProc)
                try { long hz = sysconf(2 /*_SC_CLK_TCK*/); if (hz > 0) s_clkTck = hz; } catch { }
        }

        public static Sample Read()
        {
            EnsureInit();
            if (!s_useProc) return default;          // Available=false
            var s = new Sample { Available = true };
            try
            {
                // /proc/stat: "cpu  user nice system idle iowait irq softirq steal ..."
                string first = ReadFirstLine("/proc/stat");
                if (first != null && first.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    var f = first.Substring(4).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    long total = 0, idle = 0;
                    for (int i = 0; i < f.Length; i++)
                    {
                        if (long.TryParse(f[i], out var v))
                        {
                            total += v;
                            if (i == 3 || i == 4) idle += v;   // idle + iowait
                        }
                    }
                    s.CpuTotalSeconds = (double)total / s_clkTck;
                    s.CpuBusySeconds = (double)(total - idle) / s_clkTck;
                }
            }
            catch { }
            try
            {
                long memTotalKb = 0, memAvailKb = 0;
                foreach (var line in File.ReadAllLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) memTotalKb = FirstLong(line);
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) memAvailKb = FirstLong(line);
                    if (memTotalKb != 0 && memAvailKb != 0) break;
                }
                s.MemTotalBytes = memTotalKb * 1024L;
                s.MemUsedBytes = Math.Max(0, memTotalKb - memAvailKb) * 1024L;
            }
            catch { }
            try
            {
                string la = ReadFirstLine("/proc/loadavg");          // "5.21 4.80 ..."
                if (la != null)
                {
                    var sp = la.IndexOf(' ');
                    var tok = sp > 0 ? la.Substring(0, sp) : la;
                    double.TryParse(tok, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out s.Load1);
                }
            }
            catch { }
            return s;
        }

        private static string ReadFirstLine(string path)
        {
            try { using (var r = new StreamReader(path)) return r.ReadLine(); }
            catch { return null; }
        }

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
