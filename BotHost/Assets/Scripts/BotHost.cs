using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace PuckStressTest
{
    // Entry point. Two modes:
    //   LAUNCHER (default): if BotsPerProcess < BotCount, spawn
    //     ceil(BotCount/BotsPerProcess) child BotHost.exe processes
    //     in --child-mode, each with --bots BotsPerProcess and a
    //     unique --bot-index-offset, then wait for them all to exit.
    //     This puts each NetworkManager on its own OS process / main
    //     thread, eliminating the Warmup-RTT spike that comes from
    //     serializing 12 NMs on one Unity main thread.
    //   CHILD (--child-mode): single Unity process owns BotsPerProcess
    //     BotInstance children. Behaves the same as the legacy
    //     single-process mode.
    //
    // Real games have one NetworkManager per OS process. Forking the
    // bots that way matches real-game architecture and removes a class
    // of bugs (spawn-burst stalls, main-thread contention) that would
    // otherwise contaminate any ML training data we collect.
    public class BotHost : MonoBehaviour
    {
        public BotConfig Config = new BotConfig();

        private readonly List<BotInstance> _bots = new();
        private readonly List<Process> _children = new();
        private float _shutdownAt;
        private bool _launcherMode;

        private void Awake()
        {
            // Match the server's serverTickRate (360 from
            // testserver/server_configuration.json). NGO transport
            // polls receive once per Update via NetworkUpdateLoop;
            // running below the server's tick rate means each
            // server-tick state push waits up to (1/clientFrameRate)
            // before the bot processes it, which UnityTransport then
            // attributes as RTT in NetworkTransport.GetCurrentRtt
            // (Player.cs:1152). 360 fps + 360 server tick = single-
            // tick budget on each side ≈ 2.8 ms. Real game client
            // defaults to FpsLimit=240 (SettingsManager.cs:51), but
            // matching the server is strictly better for stress
            // testing since it removes one of the latency stages.
            Application.targetFrameRate = 360;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            // Match server fixedDeltaTime so Unity's PhysicsManager
            // doesn't run multiple fixed steps per Update (which it
            // would at the default 50 Hz when targetFrameRate >> 50).
            // None of our mirrors simulate physics, but Unity still
            // schedules the FixedUpdate phase based on fixedDeltaTime.
            Time.fixedDeltaTime = 1f / 360f;
        }

        private void Start()
        {
            if (Application.isBatchMode)
            {
                Config = BotConfig.FromCommandLine();
            }

            // LAUNCHER vs CHILD branch. We're a launcher iff:
            //   - --child-mode was NOT passed, AND
            //   - BotsPerProcess < BotCount (otherwise one process is
            //     enough — no point forking).
            // Strict default BotsPerProcess=1 matches one
            // NetworkManager per OS process (real-game architecture).
            int slice = Math.Max(1, Config.BotsPerProcess);
            _launcherMode = !Config.ChildMode && Config.BotCount > slice;

            if (_launcherMode)
            {
                StartChildren(slice);
                _shutdownAt = Time.realtimeSinceStartup + Config.RunDurationSeconds + 30f;
                return;
            }

            // Child / single-process mode.
            int n = Config.ChildMode ? Math.Min(Config.BotCount, slice) : Config.BotCount;
            int offset = Config.BotIndexOffset;
            Debug.Log(
                $"[BotHost] starting count={n} indexOffset={offset} " +
                $"server={Config.ServerAddress}:{Config.ServerPort} " +
                $"duration={Config.RunDurationSeconds}s seed={Config.Seed} " +
                $"tickHz={Config.InputTickHz} " +
                $"playbook='{Config.Playbook.Name}' " +
                $"actions={Config.Playbook.ScriptedActions.Count} " +
                $"childMode={Config.ChildMode}");

            for (int i = 0; i < n; i++)
            {
                int globalIdx = offset + i;
                var go = new GameObject($"Bot[{globalIdx:D2}]");
                Object.DontDestroyOnLoad(go);
                var bot = go.AddComponent<BotInstance>();
                bot.Init(Config, globalIdx);
                var runner = go.AddComponent<ScriptedActionsRunner>();
                runner.Init(Config.Playbook, globalIdx, bot);
                _bots.Add(bot);
            }

            _shutdownAt = Time.realtimeSinceStartup + Config.RunDurationSeconds;
        }

        private void StartChildren(int slice)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int total = Config.BotCount;
            int procs = (total + slice - 1) / slice;
            string logsDir = Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Logs", "children");
            Directory.CreateDirectory(logsDir);
            string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            Debug.Log(
                $"[BotHost LAUNCHER] forking {procs} child processes " +
                $"× {slice} bots/proc = {total} total. logs={logsDir}");

            for (int p = 0; p < procs; p++)
            {
                int offset = p * slice;
                int count  = Math.Min(slice, total - offset);
                var args = BuildChildArgs(count, offset);
                string logPath = Path.Combine(logsDir, $"{ts}_p{p:D2}.log");
                args = $"{args} -logFile \"{logPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                var proc = Process.Start(psi);
                _children.Add(proc);
                Debug.Log($"[BotHost LAUNCHER] spawned child p={p} pid={proc.Id} offset={offset} count={count} log={logPath}");
            }
        }

        private string BuildChildArgs(int count, int offset)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("-batchmode -nographics ");
            sb.Append("--child-mode ");
            sb.Append($"--bots {count} ");
            sb.Append($"--bot-index-offset {offset} ");
            sb.Append($"--server {Config.ServerAddress} ");
            sb.Append($"--port {Config.ServerPort} ");
            sb.Append($"--duration {Config.RunDurationSeconds} ");
            sb.Append($"--seed {Config.Seed + offset} ");
            sb.Append($"--tick-hz {Config.InputTickHz} ");
            if (Config.VoteStart) sb.Append("--vote-start ");
            if (Config.VoteWarmup) sb.Append("--vote-warmup ");
            sb.Append($"--vote-warmup-after-seconds {Config.VoteWarmupAfterSeconds} ");
            if (!string.IsNullOrEmpty(Config.PlaybookPath))
                sb.Append($"--playbook \"{Config.PlaybookPath}\" ");
            if (!string.IsNullOrEmpty(Config.Brain) && Config.Brain != "heuristic")
                sb.Append($"--brain {Config.Brain} ");
            if (!string.IsNullOrEmpty(Config.PolicyPath))
                sb.Append($"--policy-path \"{Config.PolicyPath}\" ");
            return sb.ToString().Trim();
        }

        private void Update()
        {
            if (_launcherMode)
            {
                bool allExited = _children.Count > 0;
                foreach (var c in _children) if (!c.HasExited) { allExited = false; break; }
                if (allExited || Time.realtimeSinceStartup >= _shutdownAt)
                {
                    Debug.Log("[BotHost LAUNCHER] all children exited (or timeout) — quitting");
                    KillChildren();
                    if (Application.isBatchMode) Application.Quit(0);
                    else enabled = false;
                }
                return;
            }
            if (Time.realtimeSinceStartup >= _shutdownAt)
            {
                Debug.Log("[BotHost] run duration elapsed — shutting down");
                foreach (var bot in _bots) bot.Shutdown();
                if (Application.isBatchMode) Application.Quit(0);
                else enabled = false;
            }
        }

        private void KillChildren()
        {
            foreach (var c in _children)
            {
                try { if (!c.HasExited) c.Kill(); } catch { }
            }
        }

        private void OnApplicationQuit()
        {
            if (_launcherMode) KillChildren();
            foreach (var bot in _bots) bot.Shutdown();
        }
    }
}
