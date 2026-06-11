using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TelemetryMod
{
    // Module B — per-OS-thread CPU sampling.
    //
    // The only thing that sees OFF-main-thread work (a mod serializing on a
    // background thread, WebSocket/TCP receive threads, GC/JIT helpers). The
    // main-thread frame timing in metrics.csv structurally cannot show it.
    //
    // The production server is Linux/Mono, where ProcessThread.TotalProcessorTime
    // throws (unimplemented) — so we read /proc/self/task/<tid>/stat directly
    // (utime+stime in clock ticks; also /comm for the thread NAME, which is what
    // makes the output actionable). ProcessThread is kept as a Windows fallback.
    internal static class ThreadSampler
    {
        public static bool Enabled;

        private struct Snap { public long User, Sys; }
        private static readonly Dictionary<int, Snap> s_last = new Dictionary<int, Snap>();
        private static long s_mainOsThreadId = -1;
        private static bool s_useProc;
        private static long s_clkTck = 100; // Linux USER_HZ; us = ticks * 1_000_000 / clk

        public static long MainOsThreadId => s_mainOsThreadId;

        public static void Init()
        {
            s_useProc = Directory.Exists("/proc/self/task");
            s_mainOsThreadId = TryGetCurrentOsThreadId();
            try { long hz = sysconf(2 /*_SC_CLK_TCK*/); if (hz > 0) s_clkTck = hz; } catch { }
            Enabled = true;
            Plugin.Log($"[threads] enabled; mode={(s_useProc ? "proc" : "processthread")} " +
                       $"main_os_thread_id={s_mainOsThreadId} clk_tck={s_clkTck}");
        }

        public static void Stop() => Enabled = false;

        public static void FlushWindow(StreamWriter w, long tMs, int windowMs)
        {
            if (w == null) return;
            if (s_mainOsThreadId < 0) s_mainOsThreadId = TryGetCurrentOsThreadId();
            if (s_useProc) FlushProc(w, tMs, windowMs);
            else           FlushProcessThread(w, tMs, windowMs);
            w.Flush();
        }

        // ---- Linux /proc/self/task ----------------------------------------
        private static void FlushProc(StreamWriter w, long tMs, int windowMs)
        {
            string[] tids;
            try { tids = Directory.GetFileSystemEntries("/proc/self/task"); }
            catch (Exception ex) { Plugin.LogError("[threads] /proc enum failed: " + ex.Message); return; }

            var seen = new HashSet<int>();
            foreach (var dir in tids)
            {
                int id;
                long utime, stime;
                string name;
                try
                {
                    string tidStr = Path.GetFileName(dir);
                    if (!int.TryParse(tidStr, out id)) continue;
                    string stat = File.ReadAllText(dir + "/stat");
                    // comm (field 2) is wrapped in parens and may contain spaces;
                    // everything after the LAST ')' is space-delimited from field 3.
                    int rp = stat.LastIndexOf(')');
                    if (rp < 0) continue;
                    var f = stat.Substring(rp + 2).Split(' ');
                    // f[0]=state(field3) ... utime=field14 -> f[11], stime=field15 -> f[12]
                    if (f.Length < 13) continue;
                    utime = long.Parse(f[11]);
                    stime = long.Parse(f[12]);
                    try { name = File.ReadAllText(dir + "/comm").Trim(); } catch { name = "?"; }
                }
                catch { continue; }
                seen.Add(id);

                var cur = new Snap { User = utime, Sys = stime };
                if (s_last.TryGetValue(id, out var prev))
                {
                    long du = Math.Max(0, utime - prev.User);
                    long ds = Math.Max(0, stime - prev.Sys);
                    if (du + ds > 0)
                    {
                        long uUs = du * 1_000_000 / s_clkTck;
                        long sUs = ds * 1_000_000 / s_clkTck;
                        int isMain = s_mainOsThreadId < 0 ? -1 : (id == s_mainOsThreadId ? 1 : 0);
                        w.WriteLine($"{tMs},{windowMs},{id},{Csv(name)},{isMain},{uUs + sUs},{uUs},{sUs}");
                    }
                }
                s_last[id] = cur;
            }
            Prune(seen);
        }

        // ---- Windows fallback ---------------------------------------------
        private static void FlushProcessThread(StreamWriter w, long tMs, int windowMs)
        {
            ProcessThreadCollection threads;
            try { threads = Process.GetCurrentProcess().Threads; }
            catch (Exception ex) { Plugin.LogError("[threads] enum failed: " + ex.Message); return; }

            var seen = new HashSet<int>();
            foreach (ProcessThread t in threads)
            {
                int id; long user, sys;
                try { id = t.Id; user = t.UserProcessorTime.Ticks; sys = t.PrivilegedProcessorTime.Ticks; }
                catch { continue; }
                seen.Add(id);
                var cur = new Snap { User = user, Sys = sys };
                if (s_last.TryGetValue(id, out var prev))
                {
                    long du = Math.Max(0, user - prev.User), ds = Math.Max(0, sys - prev.Sys);
                    if (du + ds > 0)
                    {
                        int isMain = s_mainOsThreadId < 0 ? -1 : (id == s_mainOsThreadId ? 1 : 0);
                        w.WriteLine($"{tMs},{windowMs},{id},?,{isMain},{(du + ds) / 10},{du / 10},{ds / 10}");
                    }
                }
                s_last[id] = cur;
            }
            Prune(seen);
        }

        private static void Prune(HashSet<int> seen)
        {
            if (s_last.Count <= seen.Count) return;
            var dead = new List<int>();
            foreach (var k in s_last.Keys) if (!seen.Contains(k)) dead.Add(k);
            foreach (var k in dead) s_last.Remove(k);
        }

        private static long TryGetCurrentOsThreadId()
        {
            try { return gettid(); } catch { }
            try { return GetCurrentThreadId(); } catch { }
            return -1;
        }

        private static string Csv(string s) => s == null ? "" : s.Replace(',', ';');

        [DllImport("libc", EntryPoint = "gettid")]
        private static extern int gettid();

        [DllImport("libc", EntryPoint = "sysconf")]
        private static extern long sysconf(int name);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static extern uint GetCurrentThreadId();
    }
}
