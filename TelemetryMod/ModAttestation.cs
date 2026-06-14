using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TelemetryMod
{
    // Runtime mod attestation — the strongest link in the run's proof chain.
    //
    // Filesystem state (server_config.json, Plugins/ listing) says what SHOULD
    // load; this records what the process ACTUALLY loaded. Two failure modes
    // make that distinction load-bearing (B897 decompile):
    //   - SteamWorkshopManager.OnDownloadItemResult only handles k_EResultOK:
    //     a failed workshop download is silently ignored and the item sits in
    //     Phase=Downloading forever — the server runs "mod on" with no mod.
    //   - ModManagerController auto-enables EVERY Ready mod on a dedicated
    //     server, regardless of config — runtime state is the only truth.
    //
    // Writes telemetry/<ts>_mods.json (schema mods_attest_v1) atomically
    // (tmp + replace) whenever the ModManager state changes: event listeners
    // give immediacy; a change-detected sweep from the ~1 s window flush
    // gives robustness if an event name drifts between Puck builds. The
    // harness polls this file after server start and gates the measurement
    // window on the subject mod being proven ready+enabled.
    internal static class ModAttestation
    {
        private static string s_path;
        private static string s_tmpPath;
        private static System.Diagnostics.Stopwatch s_stopwatch;
        private static string s_lastCanonical;
        private static readonly List<string> s_failures = new List<string>();
        private static bool s_subscribed;

        // Listener delegates kept so Stop() can unsubscribe the same instances.
        private static Action<Dictionary<string, object>> s_onChange;
        private static Action<Dictionary<string, object>> s_onModFailed;
        private static Action<Dictionary<string, object>> s_onPluginFailed;

        public static void Init(string outDir, string ts, System.Diagnostics.Stopwatch sw)
        {
            try
            {
                s_path = Path.Combine(outDir, $"{ts}_mods.json");
                s_tmpPath = s_path + ".tmp";
                s_stopwatch = sw;
                s_lastCanonical = null;
                s_failures.Clear();

                s_onChange = _ => WriteIfChanged();
                s_onModFailed = msg => OnEnableFailed("mod", msg);
                s_onPluginFailed = msg => OnEnableFailed("plugin", msg);

                EventManager.AddEventListener("Event_OnModAdded", s_onChange);
                EventManager.AddEventListener("Event_OnPluginAdded", s_onChange);
                EventManager.AddEventListener("Event_OnModStateChanged", s_onChange);
                EventManager.AddEventListener("Event_OnPluginStateChanged", s_onChange);
                EventManager.AddEventListener("Event_OnModSteamWorkshopItemStateChanged", s_onChange);
                EventManager.AddEventListener("Event_OnModEnableFailed", s_onModFailed);
                EventManager.AddEventListener("Event_OnPluginEnableFailed", s_onPluginFailed);
                s_subscribed = true;

                WriteIfChanged();   // initial snapshot (TelemetryMod itself at least)
            }
            catch (Exception ex)
            {
                Plugin.LogError("[attest] init failed: " + ex.Message);
            }
        }

        // Called from Plugin.FlushWindow() (~1 s). Cheap when nothing changed.
        public static void MaybeWrite(long tMs)
        {
            try { WriteIfChanged(); }
            catch { /* attestation must never crash the server */ }
        }

        public static void Stop()
        {
            try
            {
                WriteIfChanged(force: true);
                if (s_subscribed)
                {
                    EventManager.RemoveEventListener("Event_OnModAdded", s_onChange);
                    EventManager.RemoveEventListener("Event_OnPluginAdded", s_onChange);
                    EventManager.RemoveEventListener("Event_OnModStateChanged", s_onChange);
                    EventManager.RemoveEventListener("Event_OnPluginStateChanged", s_onChange);
                    EventManager.RemoveEventListener("Event_OnModSteamWorkshopItemStateChanged", s_onChange);
                    EventManager.RemoveEventListener("Event_OnModEnableFailed", s_onModFailed);
                    EventManager.RemoveEventListener("Event_OnPluginEnableFailed", s_onPluginFailed);
                    s_subscribed = false;
                }
            }
            catch { }
            s_path = null;
        }

        private static void OnEnableFailed(string kind, Dictionary<string, object> msg)
        {
            try
            {
                string id = "?";
                if (msg != null)
                {
                    if (msg.TryGetValue("mod", out var m) && m is Mod mod) id = mod.Id;
                    else if (msg.TryGetValue("plugin", out var p) && p is global::Plugin pl) id = pl.Id;
                }
                long t = s_stopwatch?.ElapsedMilliseconds ?? 0;
                lock (s_failures)
                {
                    s_failures.Add($"{{\"t_ms\":{t},\"kind\":\"{kind}\",\"id\":\"{Esc(id)}\",\"detail\":\"enable failed\"}}");
                }
                Plugin.RecordEvent("mod_enable_failed", 0, $"kind={kind};id={id}");
                WriteIfChanged(force: true);
            }
            catch { }
        }

        private static void WriteIfChanged(bool force = false)
        {
            if (s_path == null) return;
            try
            {
                string body = BuildBody();        // canonical: no timestamps
                if (!force && body == s_lastCanonical) return;
                s_lastCanonical = body;

                long t = s_stopwatch?.ElapsedMilliseconds ?? 0;
                var sb = new StringBuilder(body.Length + 128);
                sb.Append("{\"schema\":\"mods_attest_v1\",\"t_ms\":").Append(t)
                  .Append(",\"captured_utc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",")
                  .Append(body).Append('}');

                File.WriteAllText(s_tmpPath, sb.ToString(), Encoding.ASCII);
                if (File.Exists(s_path)) File.Replace(s_tmpPath, s_path, null);
                else File.Move(s_tmpPath, s_path);
            }
            catch { /* never crash the server; next change retries */ }
        }

        // The state portion of the JSON, deterministic, timestamp-free —
        // doubles as the change-detection key.
        private static string BuildBody()
        {
            var sb = new StringBuilder(512);
            sb.Append("\"mods\":[");
            bool first = true;
            var mods = ModManager.Mods;
            for (int i = 0; i < mods.Count; i++)
            {
                Mod m;
                try { m = mods[i]; } catch { break; }
                if (m == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(Esc(m.Id))
                  .Append("\",\"ready\":").Append(m.IsReady ? "true" : "false")
                  .Append(",\"enabled\":").Append(m.IsEnabled ? "true" : "false")
                  .Append(",\"path\":\"").Append(Esc(m.Path ?? "")).Append("\"}");
            }
            sb.Append("],\"plugins\":[");
            first = true;
            var plugins = ModManager.Plugins;
            for (int i = 0; i < plugins.Count; i++)
            {
                global::Plugin p;
                try { p = plugins[i]; } catch { break; }
                if (p == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(Esc(p.Id))
                  .Append("\",\"ready\":").Append(p.IsReady ? "true" : "false")
                  .Append(",\"enabled\":").Append(p.IsEnabled ? "true" : "false")
                  .Append(",\"path\":\"").Append(Esc(p.Path ?? "")).Append("\"}");
            }
            sb.Append("],\"failures\":[");
            lock (s_failures)
            {
                sb.Append(string.Join(",", s_failures));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
