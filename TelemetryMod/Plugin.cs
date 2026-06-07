using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TelemetryMod
{
    // Server-side Puck mod that records per-tick performance metrics to
    // a CSV file under <server-cwd>/telemetry/. One file per server run,
    // named with the run start timestamp. Designed to be left running
    // during every stress-test profiling pass — overhead is one CSV row
    // per server tick (a few hundred bytes appended per tick) plus a
    // small Stopwatch.
    //
    // Columns:
    //   t_ms           wall-clock ms since the run started
    //   frame_ms       UnityEngine.Time.deltaTime * 1000
    //   tick_idx       monotonically increasing tick counter
    //   connected      NetworkManager.Singleton.ConnectedClientsList.Count
    //   game_phase     GameManager phase string (Warmup/FaceOff/Playing/...)
    //   gc_gen0        cumulative GC count for gen 0
    //   gc_gen1        cumulative GC count for gen 1
    //   gc_gen2        cumulative GC count for gen 2
    //   total_alloc_b  GC.GetTotalMemory snapshot (bytes)
    //
    // Two artifacts per run:
    //   telemetry/<timestamp>_metrics.csv   (the per-tick rows)
    //   telemetry/<timestamp>_summary.txt   (run header — config, mod list,
    //                                        start time — written on enable;
    //                                        appended with run duration on
    //                                        disable)
    // B323 renamed Puck's plugin interface IPuckMod → IPuckPlugin (same
    // shape: bool OnEnable(); bool OnDisable();). Source:
    // Puck_B323/Puck/IPuckPlugin.cs.
    public class Plugin : IPuckPlugin
    {
        public const string Name = "TelemetryMod";
        public const string Guid = "com.puckstresstest.telemetry";

        // Sampling cadence. Smaller = more rows = bigger files. A real
        // 240 Hz server tick at 1.0 means ~240 rows/s. Set to 0.05 for
        // 20 Hz (50 ms) sampling — usually plenty for stress tests.
        private const float SampleIntervalSeconds = 0.05f;

        private static StreamWriter s_metricsWriter;
        private static StreamWriter s_summaryWriter;
        internal static StreamWriter s_eventsWriter;
        private static GameObject   s_host;
        internal static Stopwatch   s_stopwatch;
        private static long         s_tickIdx;

        private static readonly Harmony s_harmony = new Harmony(Guid);

        public bool OnEnable()
        {
            try
            {
                StartRun();
                // B323: connection-approval handler moved off ServerManager
                // (Server_ConnectionApproval is gone) and Event_OnClient*
                // handlers moved off ServerManagerController. Subscribe to
                // the underlying EventManager events directly — cleaner
                // than chasing renamed Harmony targets ([[feedback_event-
                // manager_over_harmony]]).
                EventManager.AddEventListener("Event_OnClientConnected",            Handler_OnClientConnected);
                EventManager.AddEventListener("Event_OnClientDisconnected",         Handler_OnClientDisconnected);
                EventManager.AddEventListener("Event_Server_OnConnectionApproved",  Handler_OnConnectionApproved);
                EventManager.AddEventListener("Event_Server_OnConnectionRejected",  Handler_OnConnectionRejected);
                Log($"Enabled — writing CSV every {SampleIntervalSeconds:F3}s; EventManager subscriptions active.");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Failed to enable: " + ex);
                return false;
            }
        }

        public bool OnDisable()
        {
            try
            {
                EventManager.RemoveEventListener("Event_OnClientConnected",            Handler_OnClientConnected);
                EventManager.RemoveEventListener("Event_OnClientDisconnected",         Handler_OnClientDisconnected);
                EventManager.RemoveEventListener("Event_Server_OnConnectionApproved",  Handler_OnConnectionApproved);
                EventManager.RemoveEventListener("Event_Server_OnConnectionRejected",  Handler_OnConnectionRejected);
                EndRun();
                Log("Disabled.");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Failed to disable: " + ex);
                return false;
            }
        }

        private static void Handler_OnClientConnected(System.Collections.Generic.Dictionary<string, object> message)
        {
            ulong clientId = 0;
            if (message != null && message.TryGetValue("clientId", out var v) && v is ulong u) clientId = u;
            RecordEvent("connected", clientId, "");
        }

        private static void Handler_OnClientDisconnected(System.Collections.Generic.Dictionary<string, object> message)
        {
            ulong clientId = 0;
            if (message != null && message.TryGetValue("clientId", out var v) && v is ulong u) clientId = u;
            RecordEvent("disconnected", clientId, "");
        }

        private static void Handler_OnConnectionApproved(System.Collections.Generic.Dictionary<string, object> message)
        {
            ulong clientId = 0;
            if (message != null && message.TryGetValue("clientId", out var v) && v is ulong u) clientId = u;
            RecordEvent("approval", clientId, "approved=true");
        }

        private static void Handler_OnConnectionRejected(System.Collections.Generic.Dictionary<string, object> message)
        {
            ulong clientId = 0;
            string reason = "";
            if (message != null)
            {
                if (message.TryGetValue("clientId", out var v) && v is ulong u) clientId = u;
                if (message.TryGetValue("rejectionCode", out var rc)) reason = $"reason={rc}";
            }
            RecordEvent("approval", clientId, "approved=false;" + reason);
        }

        // Event-based row appended whenever a meaningful network event
        // happens, independent of the 50 ms sampling cadence. Lets us
        // see brief bot connections that fall between samples.
        internal static void RecordEvent(string evt, ulong clientId, string detail = "")
        {
            if (s_eventsWriter == null) return;
            try
            {
                long t = s_stopwatch?.ElapsedMilliseconds ?? 0;
                s_eventsWriter.Write(t); s_eventsWriter.Write(',');
                s_eventsWriter.Write(evt); s_eventsWriter.Write(',');
                s_eventsWriter.Write(clientId); s_eventsWriter.Write(',');
                // Quote detail to keep commas/newlines safe
                s_eventsWriter.Write('"');
                if (detail != null) s_eventsWriter.Write(detail.Replace("\"", "\"\""));
                s_eventsWriter.WriteLine('"');
                s_eventsWriter.Flush();
            }
            catch { /* swallow — telemetry must never crash the server */ }
        }

        private static void StartRun()
        {
            string outDir = Path.Combine(Path.GetFullPath("."), "telemetry");
            Directory.CreateDirectory(outDir);
            string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            string metricsPath = Path.Combine(outDir, $"{ts}_metrics.csv");
            string summaryPath = Path.Combine(outDir, $"{ts}_summary.txt");
            string eventsPath  = Path.Combine(outDir, $"{ts}_events.csv");

            s_metricsWriter = new StreamWriter(metricsPath, append: false, Encoding.ASCII)
                { AutoFlush = false };
            s_metricsWriter.WriteLine(
                "t_ms,frame_ms,tick_idx,connected,game_phase,gc_gen0,gc_gen1,gc_gen2,total_alloc_b");

            s_eventsWriter = new StreamWriter(eventsPath, append: false, Encoding.ASCII)
                { AutoFlush = false };
            s_eventsWriter.WriteLine("t_ms,event,client_id,detail");

            s_summaryWriter = new StreamWriter(summaryPath, append: false, Encoding.ASCII)
                { AutoFlush = true };
            s_summaryWriter.WriteLine($"# {Name} run summary");
            s_summaryWriter.WriteLine($"start_utc={DateTime.UtcNow:o}");
            s_summaryWriter.WriteLine($"host_name={Environment.MachineName}");
            s_summaryWriter.WriteLine($"sample_interval_s={SampleIntervalSeconds}");
            s_summaryWriter.WriteLine($"unity_version={Application.unityVersion}");
            s_summaryWriter.WriteLine($"target_frame_rate={Application.targetFrameRate}");
            s_summaryWriter.WriteLine($"is_batch_mode={Application.isBatchMode}");

            s_stopwatch = Stopwatch.StartNew();
            s_tickIdx = 0;

            s_host = new GameObject($"{Name}_Host");
            UnityEngine.Object.DontDestroyOnLoad(s_host);
            var driver = s_host.AddComponent<TelemetryDriver>();
            driver.StartCoroutine(driver.SampleLoop());

            Log($"writing -> {metricsPath}");
        }

        private static void EndRun()
        {
            try
            {
                if (s_summaryWriter != null)
                {
                    s_summaryWriter.WriteLine($"end_utc={DateTime.UtcNow:o}");
                    if (s_stopwatch != null)
                        s_summaryWriter.WriteLine($"duration_ms={s_stopwatch.ElapsedMilliseconds}");
                    s_summaryWriter.WriteLine($"total_ticks={s_tickIdx}");
                    s_summaryWriter.Flush();
                    s_summaryWriter.Dispose();
                    s_summaryWriter = null;
                }
                if (s_metricsWriter != null)
                {
                    s_metricsWriter.Flush();
                    s_metricsWriter.Dispose();
                    s_metricsWriter = null;
                }
                if (s_eventsWriter != null)
                {
                    s_eventsWriter.Flush();
                    s_eventsWriter.Dispose();
                    s_eventsWriter = null;
                }
                if (s_host != null)
                {
                    UnityEngine.Object.Destroy(s_host);
                    s_host = null;
                }
                s_stopwatch?.Stop();
            }
            catch (Exception ex)
            {
                LogError("EndRun failed: " + ex);
            }
        }

        internal static void Sample()
        {
            if (s_metricsWriter == null) return;
            try
            {
                s_tickIdx++;
                long t = s_stopwatch.ElapsedMilliseconds;
                float frameMs = Time.deltaTime * 1000f;
                int connected = 0;
                string phase = "?";

                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer && nm.ConnectedClientsList != null)
                    connected = nm.ConnectedClientsList.Count;

                try
                {
                    var gm = NetworkBehaviourSingleton<GameManager>.Instance;
                    if (gm != null) phase = gm.Phase.ToString();
                }
                catch { /* GameManager singleton may not be ready early */ }

                int g0 = GC.CollectionCount(0);
                int g1 = GC.CollectionCount(1);
                int g2 = GC.CollectionCount(2);
                long totalAlloc = GC.GetTotalMemory(forceFullCollection: false);

                s_metricsWriter.Write(t);     s_metricsWriter.Write(',');
                s_metricsWriter.Write(frameMs.ToString("F3")); s_metricsWriter.Write(',');
                s_metricsWriter.Write(s_tickIdx); s_metricsWriter.Write(',');
                s_metricsWriter.Write(connected); s_metricsWriter.Write(',');
                s_metricsWriter.Write(phase); s_metricsWriter.Write(',');
                s_metricsWriter.Write(g0);   s_metricsWriter.Write(',');
                s_metricsWriter.Write(g1);   s_metricsWriter.Write(',');
                s_metricsWriter.Write(g2);   s_metricsWriter.Write(',');
                s_metricsWriter.Write(totalAlloc);
                s_metricsWriter.WriteLine();

                // Flush every ~1s of samples so a crashed run still has
                // most data on disk.
                if (s_tickIdx % 20 == 0) s_metricsWriter.Flush();
            }
            catch (Exception ex)
            {
                LogError("Sample failed: " + ex.Message);
            }
        }

        public static void Log(string msg)      => Debug.Log($"[{Name}] {msg}");
        public static void LogError(string msg) => Debug.LogError($"[{Name}] {msg}");
    }

    internal class TelemetryDriver : MonoBehaviour
    {
        public IEnumerator SampleLoop()
        {
            var wait = new WaitForSecondsRealtime(0.05f);
            while (true)
            {
                Plugin.Sample();
                yield return wait;
            }
        }
    }

    // (Connection lifecycle now wired via EventManager subscriptions in
    // Plugin.OnEnable — see Handler_OnClient{Connected,Disconnected} and
    // Handler_OnConnection{Approved,Rejected}. The previous Harmony
    // patches targeted ServerManager.Server_ConnectionApproval and
    // ServerManagerController.Event_OnClient*, both of which were
    // removed/relocated in B323's connection-approval rewrite.)
}
