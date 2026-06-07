using System;

namespace PuckStressTest
{
    [Serializable]
    public class BotConfig
    {
        public string ServerAddress = "127.0.0.1";
        public ushort ServerPort = 30609;
        public string ServerPassword = "";
        public int BotCount = 1;
        public float RunDurationSeconds = 30f;
        public int Seed = 1;
        public string DisplayNamePrefix = "Bot";
        public string PlaybookPath = "";
        public Playbook Playbook = new Playbook();

        // Bot input tick rate (Hz). 30 is the conservative default
        // (matches earlier behavior); set to 200 to match Puck's
        // configured `clientTickRate` for max realism. Higher rates
        // also exercise the server's per-tick input processing more
        // aggressively, useful when stress-testing input handling.
        public float InputTickHz = 30f;

        // Whether bots send /vs in chat during Warmup to start the
        // game on a test server. Defaults OFF so connecting to live
        // community servers doesn't spam vote-start. Enable with
        // --vote-start (CLI) or vote_start: true (playbook) when
        // testing against our own dedicated server.
        public bool VoteStart = false;

        // Whether bots send /vw (vote-warmup) outside Warmup phase to
        // force the server back to Warmup mid-run. The /vs symmetric
        // companion: lets us reproduce the Warmup-RTT spike on demand
        // by ping-ponging the game between Play and Warmup. Combined
        // with --vote-start the bots will cycle phases automatically.
        // Defaults OFF for the same reason VoteStart does.
        public bool VoteWarmup = false;

        // Delay before /vw fires after the first time we see Playing.
        // Avoids triggering Warmup before we've captured a clean Play
        // baseline. Defaults to 30s.
        public float VoteWarmupAfterSeconds = 30f;

        // Multi-process forking. With BotsPerProcess > 0 (default 1)
        // and ChildMode=false, the BotHost.exe process is the LAUNCHER:
        // it spawns ceil(BotCount/BotsPerProcess) child BotHost.exe
        // processes, each with --child-mode and --bot-index-offset N,
        // then waits for them all to exit. Each child process owns its
        // own Unity main thread, NetworkManagers, and UTP drivers, so
        // server spawn-burst processing parallelizes across cores
        // instead of serializing on one main thread (which produced
        // the multi-second Warmup-RTT spike at 12 bots/process).
        // BotsPerProcess=1 = strict one-NetworkManager-per-process,
        // matching real-game architecture exactly.
        public int  BotsPerProcess = 1;
        public bool ChildMode      = false;
        public int  BotIndexOffset = 0;

        // Which decision logic drives each bot. "heuristic" runs the
        // hand-coded BotBrain. "onnx" loads PolicyPath as an ONNX
        // model and routes its outputs through MirrorPlayerInput.
        // Both write to the snapshot logger identically.
        public string Brain      = "heuristic";
        public string PolicyPath = "";

        public static BotConfig FromCommandLine()
        {
            var c = new BotConfig();
            // Track which flags the operator set explicitly so we can
            // honor them over playbook values.
            bool botsExplicit = false;
            bool durationExplicit = false;
            bool seedExplicit = false;
            bool tickHzExplicit = false;
            bool voteStartExplicit = false;
            bool voteWarmupExplicit = false;
            bool voteWarmupAfterExplicit = false;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : "";
                switch (a)
                {
                    case "--server":      c.ServerAddress = Next(); break;
                    case "--port":        c.ServerPort = ushort.Parse(Next()); break;
                    case "--password":    c.ServerPassword = Next(); break;
                    case "--bots":        c.BotCount = int.Parse(Next()); botsExplicit = true; break;
                    case "--duration":    c.RunDurationSeconds = float.Parse(Next()); durationExplicit = true; break;
                    case "--seed":        c.Seed = int.Parse(Next()); seedExplicit = true; break;
                    case "--name-prefix": c.DisplayNamePrefix = Next(); break;
                    case "--playbook":    c.PlaybookPath = Next(); break;
                    case "--tick-hz":     c.InputTickHz = float.Parse(Next()); tickHzExplicit = true; break;
                    case "--vote-start":  c.VoteStart = true; voteStartExplicit = true; break;
                    case "--no-vote-start": c.VoteStart = false; voteStartExplicit = true; break;
                    case "--vote-warmup":   c.VoteWarmup = true;  voteWarmupExplicit = true; break;
                    case "--no-vote-warmup":c.VoteWarmup = false; voteWarmupExplicit = true; break;
                    case "--vote-warmup-after-seconds":
                        c.VoteWarmupAfterSeconds = float.Parse(Next()); voteWarmupAfterExplicit = true; break;
                    case "--bots-per-process":   c.BotsPerProcess = int.Parse(Next()); break;
                    case "--child-mode":         c.ChildMode = true; break;
                    case "--bot-index-offset":   c.BotIndexOffset = int.Parse(Next()); break;
                    case "--brain":              c.Brain = Next(); break;
                    case "--policy-path":        c.PolicyPath = Next(); break;
                }
            }

            // Playbook fills in scalar values when the operator did not
            // pass the flag explicitly. Explicit CLI flags always win.
            if (!string.IsNullOrEmpty(c.PlaybookPath))
            {
                c.Playbook = Playbook.LoadOrDefault(c.PlaybookPath);
                if (!botsExplicit) c.BotCount = c.Playbook.BotCount;
                if (!durationExplicit) c.RunDurationSeconds = c.Playbook.DurationSeconds;
                if (!seedExplicit) c.Seed = c.Playbook.Seed;
                if (!tickHzExplicit) c.InputTickHz = c.Playbook.InputTickHz;
                if (!voteStartExplicit) c.VoteStart = c.Playbook.VoteStart;
                if (!voteWarmupExplicit) c.VoteWarmup = c.Playbook.VoteWarmup;
                if (!voteWarmupAfterExplicit) c.VoteWarmupAfterSeconds = c.Playbook.VoteWarmupAfterSeconds;
            }
            return c;
        }
    }
}
