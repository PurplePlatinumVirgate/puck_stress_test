using PuckStressTest.Mirror;
using Unity.Netcode;
using UnityEngine;

namespace PuckStressTest
{
    // Once the bot's own Player NetworkObject spawns, this brain ticks
    // at 30 Hz and steers the bot toward the nearest puck.
    //
    // World position source: MirrorSynchronizedObjectManager parses
    // the per-tick `Server_SynchronizeObjectsRpc` snapshot and
    // populates a static (NID → world position) dict. We look up
    //   - bot's own body position (PlayerBodyV2 NID, found via
    //     PlayerReference matching our Player NID)
    //   - nearest MirrorPuck NID and its position
    // and convert the heading to a world-space yaw (LookAngle), which
    // makes the body face the puck. Then we hold +Y on Move so the
    // bot skates forward in its facing direction. Stick raycast also
    // points at the puck so a poke check can connect when close.
    //
    // Falls back to the old fixed-input pattern when world positions
    // aren't yet available (early in the connection lifecycle).
    public class BotBrain : MonoBehaviour
    {
        public MirrorPlayerInput Input;
        public MirrorPlayer Player;
        public int BotIndex;
        // Tick rate in Hz. MirrorPlayer.OnNetworkSpawn reads this from
        // BotConfig (set via --tick-hz CLI flag or playbook
        // input_tick_hz field). 30 Hz is the legacy default; 200 Hz
        // matches Puck's `clientTickRate` for max realism / load.
        public float TickHz = 30f;
        // Whether to send /vs during Warmup. Wired up in
        // MirrorPlayer.OnNetworkSpawn from BotConfig.VoteStart;
        // defaults false so a bot run against community servers
        // doesn't spam vote-start. Set --vote-start (CLI) or
        // vote_start: true (playbook) to enable.
        public bool VoteStart = false;

        // Whether to send /vw outside Warmup to flip the game back to
        // Warmup mid-run. Companion to VoteStart — together they let
        // bots cycle Play↔Warmup so we can repro the Warmup-RTT spike
        // on demand. Wired up in MirrorPlayer.OnNetworkSpawn from
        // BotConfig.VoteWarmup. Defaults false for community-server
        // safety.
        public bool VoteWarmup = false;
        // Wall-clock seconds after the first Playing phase before
        // /vw is allowed to fire. Lets us collect a clean Play
        // baseline first.
        public float VoteWarmupAfterSeconds = 30f;

        // Mod-interaction: periodic carving for CompetitiveSkating.
        // Server-side carve detection = Slide && LateralLeft &&
        // LateralRight && grounded (FixedUpdate_Patch); the modded
        // client also zeroes the forward move component. We replicate
        // that input signature in bursts. Set from the playbook's
        // behavior.comp_carve by MirrorPlayer when wiring the brain.
        public bool CompCarve = false;
        // Playbook behavior toggles. MirrorPlayer sets the real one; the default
        // instance keeps today's behavior for a null playbook. Only the
        // IMPLEMENTED behaviors are gated in the output block (skate_to_puck,
        // rotate_stick_to_puck, rotate_head_to_puck, push_to_opposing_goal) and
        // the pass decision (pass_to_teammate). attempt_poke / respect_faceoff /
        // line_change are not modeled by the bot, so toggling them does nothing.
        public BehaviorToggles Behaviors = new BehaviorToggles();
        private bool _carving;
        private int _carveTicksLeft;
        private int _carveCooldownTicks;

        // ML data emitter (M1). Set by MirrorPlayer.OnNetworkSpawn
        // when BotBrain is attached. Receives EmitTick() at the end
        // of every Tick(), capturing (obs, action) pairs for BC
        // training and PPO rollouts. Null-safe — when unset, brain
        // behaves identically to pre-M1 (no logging).
        public PuckStressTest.Logging.SnapshotLogger Snapshot;

        // Constants below are tuned at the 30 Hz baseline (50 ms per
        // tick). At higher tick rates the same `int Ticks` constant
        // means much less wall-clock time, which shrinks state
        // machine durations to nothing. Scaled() converts a 30 Hz
        // tick count to whatever the current TickHz is.
        private int Scaled(int ticksAt30Hz)
            => Mathf.Max(1, Mathf.RoundToInt(ticksAt30Hz * (TickHz / 30f)));

        private float _accumulator;
        private float _phase;

        // Per-bot RNG, seeded by BotIndex so each bot has a stable
        // "personality" but bots differ from each other. Used for
        // decision noise that prevents identical-state soft-locks
        // (multiple bots circling the same puck, all backing off in
        // the same direction when stuck, all firing on the same tick,
        // etc.). The seed is captured once at Tick startup; resets
        // on episode boundary so a fresh run gets a deterministic
        // (BotIndex-keyed) seed.
        private System.Random _rng;
        private System.Random Rng => _rng ??= new System.Random(0x53A1F09D ^ BotIndex);

        // Personality: pass-reach field (used by TryFindPassTarget).
        // Default median = 18 m; overwritten in EnsurePersonality.
        private float PassReachM = 18f;

        // Personality vector. Sampled once when Player.Username is
        // first populated, then frozen for the bot's lifetime. Seeded
        // by username.GetHashCode() so the same bot name produces the
        // same personality across runs. Names come from
        // BotAuthBypassMod.FabricatePlayerData (server-side), which
        // itself derives names deterministically from SteamId — so
        // the full chain (BotIndex → SteamId → name → personality)
        // is stable.
        //
        // Each axis is a previously-global constant; default values
        // above match the previous compile-time constant so pre-init
        // behavior is unchanged. EnsurePersonality overwrites with
        // a uniform sample inside the axis range listed in the plan.
        private bool _personalityInit;
        private PlayerHandedness _personalityHandedness; // mirror of the picked hand

        private void EnsurePersonality()
        {
            if (_personalityInit) return;
            if (Player == null) return;
            string name;
            try { name = Player.Username.Value.ToString(); } catch { return; }
            if (string.IsNullOrEmpty(name)) return;  // Username NV not populated yet

            var rng = new System.Random(name.GetHashCode() ^ 0x6E3F2A05);
            float U(double lo, double hi) => (float)(lo + rng.NextDouble() * (hi - lo));

            ShotAimToleranceDeg = U(16,   34);   // unused today; reserved for stretch wiring
            FlailEnterSecs      = U(2.1,  3.9);
            CarryEnterRange     = U(2.55, 3.45);
            CarryExitRange      = U(4.25, 5.75);
            StuckEnterDispM     = U(0.40, 0.60);
            StuckBackoffTicks   = (int)U(36, 54);
            SprintStartStamina  = U(0.32, 0.48);
            SprintStopStamina   = U(0.12, 0.18);
            DriftRadius         = U(0.42, 0.90);
            PassReachM          = U(15.3, 20.7);
            ChatVerbosity       = U(0.20, 0.90);
            _personalityHandedness = rng.Next(2) == 0 ? PlayerHandedness.Left : PlayerHandedness.Right;
            _personalityInit = true;

            // Handedness: request the picked hand via the existing
            // Client_RequestHandednessRpc (hash 744616166u). Server
            // updates Player.Handedness NV; PlayerMesh re-applies the
            // L/R stick mesh. One-shot — no re-send.
            try { Player.SendRequestHandedness(_personalityHandedness); } catch { }

            Debug.Log($"[BotBrain bot={BotIndex}] personality '{name}': " +
                      $"hand={_personalityHandedness} " +
                      $"aim={ShotAimToleranceDeg:F1}° flail={FlailEnterSecs:F2}s " +
                      $"carry={CarryEnterRange:F2}/{CarryExitRange:F2} " +
                      $"stuck={StuckEnterDispM:F2}m/{StuckBackoffTicks}t " +
                      $"sprint={SprintStartStamina:F2}-{SprintStopStamina:F2} " +
                      $"drift={DriftRadius:F2}m pass={PassReachM:F1}m " +
                      $"chatty={ChatVerbosity:F2}");
        }

        // ── BotChatter: situational quick-chats ──────────────────
        // Phrases captured from Puck B323's ChatManager.quickChats
        // (canonical inspector data) via ConfigCaptureMod's
        // [QC] dump. Each trigger maps to one or more phrases drawn
        // from the matching category, plus the player's RNG picks one.
        // All triggers send all-chat (isTeamChat=false) for now —
        // the Information category is canonically team-only and not
        // used in this first cut.
        private enum ChatTrigger
        {
            BumpedTeammate, BumpedOpponent,
            OwnTeamGoal,    ConcededGoal,
            SuccessfulStrike,
            StuckBacking,   BoardPinned,
            FlailingStarted,
        }

        private static readonly System.Collections.Generic.Dictionary<ChatTrigger, string[]> s_ChatPool
            = new System.Collections.Generic.Dictionary<ChatTrigger, string[]>
        {
            { ChatTrigger.BumpedTeammate,   new[] { "Sorry!", "Whoops...", "No problem." } },
            { ChatTrigger.BumpedOpponent,   new[] { "$#@%!", "Whoops..." } },
            { ChatTrigger.OwnTeamGoal,      new[] { "Nice shot!", "Great pass!", "🔥", "💯" } },
            { ChatTrigger.ConcededGoal,     new[] { "Nooo!", "OMG!", "😭", "$#@%!" } },
            { ChatTrigger.SuccessfulStrike, new[] { "Nice shot!", "🔥", "💯" } },
            { ChatTrigger.StuckBacking,     new[] { "Whoops...", "$#@%!" } },
            { ChatTrigger.BoardPinned,      new[] { "Whoops...", "$#@%!" } },
            { ChatTrigger.FlailingStarted,  new[] { "OMG!", "Wow!" } },
        };

        private float ChatVerbosity = 0.5f;           // sampled in EnsurePersonality
        private float _lastChatTime;                  // 0 = never sent
        private const float ChatCooldownSec = 6.0f;   // per-bot floor; under Puck's 3/sec ticket budget

        private void TryChat(ChatTrigger t)
        {
            if (Player?.NetworkManager == null) return;
            if (Time.realtimeSinceStartup - _lastChatTime < ChatCooldownSec) return;
            if (Rng.NextDouble() > ChatVerbosity) return;
            if (!s_ChatPool.TryGetValue(t, out var pool) || pool.Length == 0) return;
            string content = pool[Rng.Next(pool.Length)];
            try
            {
                if (PuckStressTest.Mirror.ChatSender.TrySendQuickChat(Player.NetworkManager, content))
                {
                    _lastChatTime = Time.realtimeSinceStartup;
                    Debug.Log($"[BotChatter bot={BotIndex}] {t} → \"{content}\"");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BotChatter bot={BotIndex}] send failed: {ex.Message}");
            }
        }

        // Per-tick edge-detection state for chat triggers. Each *Prev
        // field caches the previous tick's value; we only fire on
        // false→true transitions (or, for scores, on increment).
        private bool _prevIsStuck;
        private bool _prevIsFlailing;
        private bool _prevBoardPinned;
        private int  _prevBlueScore = -1;
        private int  _prevRedScore  = -1;
        private int  _prevCollisionBufferCount = -1;

        // Poll the game manager's score NV for goals. Increment on
        // either side relative to the bot's Team selects OwnTeamGoal
        // vs ConcededGoal.
        private void PollScoreForChat()
        {
            var gm = FindMyGameManager();
            if (gm == null) return;
            int blue, red;
            try
            {
                var gs = gm.GameState.Value;
                blue = gs.BlueScore;
                red  = gs.RedScore;
            }
            catch { return; }
            if (_prevBlueScore < 0) { _prevBlueScore = blue; _prevRedScore = red; return; }

            bool blueScored = blue > _prevBlueScore;
            bool redScored  = red  > _prevRedScore;
            if (blueScored || redScored)
            {
                PuckStressTest.Mirror.PlayerTeam myTeam;
                try { myTeam = Player.Team; } catch { myTeam = PuckStressTest.Mirror.PlayerTeam.None; }
                bool ownTeamGoal =
                    (blueScored && myTeam == PuckStressTest.Mirror.PlayerTeam.Blue) ||
                    (redScored  && myTeam == PuckStressTest.Mirror.PlayerTeam.Red);
                TryChat(ownTeamGoal ? ChatTrigger.OwnTeamGoal : ChatTrigger.ConcededGoal);
            }
            _prevBlueScore = blue;
            _prevRedScore  = red;
        }

        private MirrorGameManager FindMyGameManager()
        {
            if (Player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return null;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var gm = no.GetComponent<MirrorGameManager>();
                if (gm != null) return gm;
            }
            return null;
        }

        // Proximity-based bump detection. Player Body has no
        // CollisionRecorder NB (only Puck and Stick do), so the
        // server doesn't broadcast body-body collisions to us. Cheap
        // workaround: each tick, scan other MirrorPlayerBodyV2 mirrors
        // and check distance to ours; if a body comes within
        // BumpRange of us AND we're past a per-event cooldown, fire
        // the chat with team classification.
        private float _bumpCooldownUntil;
        private const float BumpRange     = 1.1f;   // metres; player radius ~0.5 each
        private const float BumpCooldown  = 2.0f;   // seconds; avoid retriggering inside one collision

        private void PollCollisionsForChat()
        {
            if (_myBodyNid == 0) return;
            if (Time.realtimeSinceStartup < _bumpCooldownUntil) return;
            if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(_myBodyNid, out var myX)) return;
            PuckStressTest.Mirror.PlayerTeam myTeam;
            try { myTeam = Player.Team; } catch { return; }

            float bumpSq = BumpRange * BumpRange;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var otherBody = no.GetComponent<MirrorPlayerBodyV2>();
                if (otherBody == null) continue;
                if (no.NetworkObjectId == _myBodyNid) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var ox)) continue;
                if ((ox.Position - myX.Position).sqrMagnitude > bumpSq) continue;

                ulong otherPlayerNid;
                try { otherPlayerNid = otherBody.PlayerReference.Value.NetworkObjectId; } catch { continue; }
                if (!Player.NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(otherPlayerNid, out var otherPlayerNo))
                    continue;
                var otherPlayer = otherPlayerNo.GetComponent<MirrorPlayer>();
                if (otherPlayer == null) continue;
                PuckStressTest.Mirror.PlayerTeam otherTeam;
                try { otherTeam = otherPlayer.Team; } catch { continue; }
                bool sameTeam = otherTeam == myTeam && myTeam != PuckStressTest.Mirror.PlayerTeam.None;
                TryChat(sameTeam ? ChatTrigger.BumpedTeammate : ChatTrigger.BumpedOpponent);
                _bumpCooldownUntil = Time.realtimeSinceStartup + BumpCooldown;
                return;
            }
        }

        // Legacy stub — Player Body lacks NetworkObjectCollisionRecorder
        // in B323 so we don't read its buffer. Kept commented for
        // reference; PollCollisionsForChat above is the active path.
        private Unity.Netcode.NetworkList<MirrorNetworkObjectCollision> FindCollisionBufferFor_unused(ulong bodyNid)
        {
            if (Player?.NetworkManager?.SpawnManager?.SpawnedObjects == null) return null;
            if (!Player.NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(bodyNid, out var bodyNo)) return null;
            var buf = bodyNo.GetComponent<MirrorNetworkObjectCollisionBuffer>();
            return buf?.Buffer;
        }


        // Drift offset on the chase steer target. Random-walks at
        // DriftStepHz over ±DriftRadius m, applied as an xy offset
        // when steering toward the puck. Causes converging bots to
        // approach the puck along slightly different vectors instead
        // of stacking on the exact same line.
        private Vector3 _driftOffset;
        private float   _driftNextStepTime;
        private const float DriftStepHz   = 0.8f;   // re-roll ~every 1.25 s
        // Per-bot personality field (overwritten in EnsurePersonality
        // from a name-keyed RNG). Default = median of the sampled
        // range so pre-init behavior is unchanged.
        private float       DriftRadius   = 0.6f;   // metres
        private const float DriftLerpRate = 0.6f;   // per-second blend toward new target

        // Drift offset state. Returns current xy drift to add onto
        // steerTarget when chasing the puck. Skips during goalie /
        // stuck / cradle states (those have their own positioning).
        private Vector3 CurrentDriftOffset()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= _driftNextStepTime)
            {
                _driftNextStepTime = now + 1f / DriftStepHz;
                // Pick a new random target on the disk of radius DriftRadius.
                float ang = (float)(Rng.NextDouble() * 2.0 * System.Math.PI);
                float r   = (float)(Rng.NextDouble()) * DriftRadius;
                _driftTarget = new Vector3(r * Mathf.Cos(ang), 0f, r * Mathf.Sin(ang));
            }
            // Lerp toward the target so the offset moves smoothly,
            // never snapping. Approximates an OU random walk.
            float dt = 1f / Mathf.Max(1f, TickHz);
            _driftOffset = Vector3.Lerp(_driftOffset, _driftTarget, DriftLerpRate * dt);
            return _driftOffset;
        }
        private Vector3 _driftTarget;

        // Cached lookups — refreshed every ~30 ticks since spawned NID
        // sets are stable once the connection settles.
        private ulong _myBodyNid;
        private MirrorPlayerBodyV2 _myBody;
        // Bot's own stick NetworkObjectId — found by walking spawned
        // MirrorStick components and matching PlayerReference back to
        // our MirrorPlayer. Stick world (pos, rot) lives in
        // MirrorSynchronizedObjectManager.LatestPositions.
        private ulong _myStickNid;
        private int _refreshTickCounter;

        // Last-sent state for the bool RPCs. Slide and Sprint go over
        // RELIABLE delivery, so we only resend on transition to keep
        // bandwidth + server input churn down.
        private bool _slideSent;
        private bool _sprintSent;
        // Last sbyte sent for BladeAngleInput. Reliable; resend only
        // on change.
        private sbyte _bladeAngleSent;
        // Range from PlayerInput.cs:2077/2081: -4 .. +4. One unit =
        // bladeAngleStep (12.5°) rotation of the blade around the
        // shaft axis (Stick.cs:155-156). Used during shot strike to
        // open the blade face toward the offensive goal — adds a
        // small follow-through and helps the puck release off the
        // blade.
        private const sbyte BladeAngleStrike = 3;
        // Modest blade-tilt during cradle carry. 1 step = 12.5° of
        // face rotation. Real players cup with this scroll-wheel
        // input to keep the puck on the broadside; we hold it
        // continuously while inCradle. Going higher than 1-2 starts
        // to look like a slap-shot windup, so keep it subtle.
        private const sbyte BladeAngleCradleCup = 1;
        // Stamina policy hysteresis: start sprinting only when above
        // 0.4 (server requires >0.25 to actually engage); stop when
        // it drops below 0.15. Avoids flicker while regenerating.
        // Personality fields (see EnsurePersonality).
        private float SprintStartStamina = 0.40f;
        private float SprintStopStamina  = 0.15f;
        // Pulse-turn tactic. A long crouch-turn kills all forward
        // momentum (Skate.cs lateral force vs body rotation), so real
        // players tap-crouch-turn-release-recover repeatedly. The
        // state machine below cycles SLIDE (tight turn, brief) and
        // RECOVER (release, regain momentum, re-evaluate) until the
        // bot is roughly aimed at the puck.
        //
        // Tuned for 30 Hz tick: SlideTicks=5 → ~165 ms crouch-turn.
        // RecoverTicks=6 → ~200 ms straight-skate recovery. With
        // PuckTickInterval ≈ 33 ms, that's roughly 6 frames per
        // half-cycle — close to what a human input feels like.
        private const float PulseEnterAngle = 45f; // start pulsing if |delta| > this
        private const float PulseExitAngle  = 15f; // stop pulsing once |delta| < this
        private const int   PulseSlideTicks   = 5;
        private const int   PulseRecoverTicks = 6;

        // Distance below which sprint is wasteful — bot is about to
        // arrive at the puck.
        private const float SprintMinTargetDistance = 4f;

        // Goal positions (hardcoded constants from PuckAIPractice mod
        // 3543744568 line 341/343). Red goal at -Z, Blue goal at +Z.
        // Bots attack the OPPOSING goal — Red team attacks Blue, Blue
        // team attacks Red. Y stays near 0 (ice plane).
        private static readonly Vector3 GoalRedWorld  = new Vector3(0f, 0f, -40.23f);
        private static readonly Vector3 GoalBlueWorld = new Vector3(0f, 0f,  40.23f);

        // Carry mode: switch from chasing the puck to skating at the
        // goal once close. Body forward then aligns with goal direction
        // and the puck rides in front of the body where the stick can
        // push it. Hysteresis avoids flicker at the boundary.
        // Personality fields (see EnsurePersonality).
        private float CarryEnterRange = 3.0f;
        private float CarryExitRange  = 5.0f;

        // Returns the world position of the goal this bot should be
        // attacking, based on its current Team NV. Vector3.zero if
        // team isn't yet known (bot is in TeamSelect or earlier).
        public Vector3 OffensiveGoal()
        {
            if (Player == null) return Vector3.zero;
            try
            {
                switch (Player.Team)
                {
                    case PuckStressTest.Mirror.PlayerTeam.Red:  return GoalBlueWorld;
                    case PuckStressTest.Mirror.PlayerTeam.Blue: return GoalRedWorld;
                    default: return Vector3.zero;
                }
            }
            catch { return Vector3.zero; }
        }

        // True if the server has assigned this bot to the Goalie
        // role. Goalies must defend the net, never chase the puck
        // beyond the crease — handled with a separate target in the
        // tick path.
        public bool IsGoalie()
        {
            if (Player == null) return false;
            try { return Player.Role == PuckStressTest.Mirror.PlayerRole.Goalie; }
            catch { return false; }
        }

        // Crease constants (rink geometry):
        //   x ∈ [-2.5, +2.5], z anchored just inside own goal line.
        // Anchor sits 1.5 m inside the goal line on the rink side
        // (so the goalie can step forward to push out a puck near
        // the crease, but stays clearly inside the paint).
        private const float CreaseHalfWidth   = 2.5f;
        private const float CreaseAnchorInset = 1.5f;

        // Goalie target: track the puck's X clamped to the crease,
        // anchored 1.5 m in front of own goal line. Defaults to
        // crease center when no puck is visible. ML brain reads the
        // same Role NV; this stays a pure policy choice in BotBrain.
        public Vector3 GoalieTarget(bool havePuck, Vector3 puckPos)
        {
            Vector3 ownGoal = DefensiveGoal();
            if (ownGoal == Vector3.zero) return Vector3.zero;
            float anchorZ = ownGoal.z - Mathf.Sign(ownGoal.z) * CreaseAnchorInset;
            float trackX  = havePuck
                ? Mathf.Clamp(puckPos.x, -CreaseHalfWidth, CreaseHalfWidth)
                : 0f;
            return new Vector3(trackX, 0f, anchorZ);
        }

        // The goal we DEFEND. Used to derive support / rest positions
        // for non-carrier bots so they don't crowd the puck.
        public Vector3 DefensiveGoal()
        {
            if (Player == null) return Vector3.zero;
            try
            {
                switch (Player.Team)
                {
                    case PuckStressTest.Mirror.PlayerTeam.Red:  return GoalRedWorld;
                    case PuckStressTest.Mirror.PlayerTeam.Blue: return GoalBlueWorld;
                    default: return Vector3.zero;
                }
            }
            catch { return Vector3.zero; }
        }

        private enum TurnState { Normal, PulseSlide, PulseRecover }
        private TurnState _turnState = TurnState.Normal;
        private int _turnStateTicksLeft;

        // Carry mode latch — once we engage carry within
        // CarryEnterRange we hold it until we drift past
        // CarryExitRange. Avoids flipping in/out at the boundary.
        private bool _carrying;

        // Puck-velocity tracker: previous puckPos + the real time it
        // was sampled. Used by EstimatePuckVelocity() to lead the aim.
        private Vector3 _lastPuckPos;
        private float   _lastPuckSampleTime;
        private Vector3 _lastPuckVelocity;
        private bool    _havePuckHistory;

        // Maximum lead time (sec). Cap so a momentarily wrong puck
        // velocity estimate doesn't fling the aim metres ahead.
        private const float MaxLeadSeconds = 0.20f;

        // Fixed end-to-end input-actuation latency we model when leading
        // the puck. Player.Ping is unusable as a lead source: it's set
        // server-side from UnityTransport.GetCurrentRtt and only resampled
        // every 10 s (PlayerController.cs:61-67). UTP's pipeline RTT is a
        // slow-converging EWMA with a non-zero default, so on a localhost
        // link it floats around 30-50 ms even though real UDP RTT is sub-ms
        // — using it as the lead horizon over-leads aim by ~30 ms (≈15 cm
        // at a 5 m/s puck), which is enough to systematically miss the
        // blade's broadside.
        //
        // Real budget at 360 Hz: bot tick 2.8 ms + transport flush ~2 ms
        // + server PlayerInput tick 2.8 ms + Stick FixedUpdate 2.8 ms +
        // snapshot back ~2.8 ms ≈ 13 ms. Round to 15 ms; can be tuned
        // later from per-strike diagnostics.
        private const float ProcessingLatencySeconds = 0.015f;

        // Shot trigger FSM. Decompile (Stick.cs:144-189, StickPositioner.cs:142-189):
        //   - StickRaycastOriginAngleInput rotates a raycast origin.
        //   - Raycast forward → BladeTargetPosition in world space.
        //   - Two PID controllers drag the blade rigidbody to that
        //     target each FixedUpdate. PointVelocity from PlayerBody
        //     is also injected into the stick rigidbody.
        // To "shoot" without any new RPC: snap the raycast Y across
        // the puck toward the goal. The blade target jumps; the PID
        // sweeps the blade through the puck; the puck takes an
        // impulse roughly in the strike direction. A real player
        // does the same — backswing, then forward snap.
        //
        // FSM: Idle → WindUp → Strike → Cooldown → Idle.
        //   WindUp:   raycast yaw BEHIND puck (away from goal). ~3
        //             ticks (~100 ms at 30 Hz). Pulls blade backward.
        //   Strike:   raycast yaw PAST puck toward goal. ~4 ticks.
        //             Blade sweeps across puck toward goal.
        //   Cooldown: lockout ~1 s before another shot can fire so
        //             we don't whiff in a tight loop.
        private enum ShotState { Idle, WindUp, Strike, Cooldown }
        private ShotState _shotState = ShotState.Idle;
        private int _shotTicksLeft;

        // Engage thresholds. The blade is a SEGMENT, not a point —
        // contact happens on its broadside. Per Stick.cs the stick
        // rigidbody pivots at the shaft handle and the blade's broad
        // face lies along a line of length ≈ BladeSegmentLengthWorld
        // (~0.3 m, per project_shot_mechanics.md) extending from
        //   heel = stickPos + stickFwd * StickHeelDistWorld
        // to
        //   toe  = stickPos + stickFwd * StickToeDistWorld
        // We measure the closest distance from the puck (XZ) to that
        // segment, not to the toe point alone. ShotMaxBladeToPuckRange
        // is the segment-to-puck threshold (smaller than before because
        // segment-distance is intrinsically tighter than toe-distance —
        // a 0.8 m point-distance often means 0.0 m segment-distance).
        //
        // Body-range gate is 3.0 m so a Carrier can wind up while
        // still approaching. Aim tolerance unchanged.
        private const float ShotMaxBodyToPuckRange   = 3.0f;
        private const float ShotMaxBladeToPuckRange  = 0.35f;
        private const float StickLengthWorld         = 2.0f;
        // Blade broadside extents along stick forward. The toe is at
        // ~StickLengthWorld; the heel sits ~0.30 m closer to body.
        private const float StickToeDistWorld        = 2.0f;
        private const float StickHeelDistWorld       = 1.70f;
        // Personality field (see EnsurePersonality). Not yet wired —
        // sampled but unused. Stretch task slot.
        private float ShotAimToleranceDeg      = 25f;
        private const int   ShotWindUpTicks   = 3;   // ~100 ms backswing
        private const int   ShotStrikeTicks   = 2;
        private const int   ShotCooldownTicks = 30;
        // Cradle zone in BODY-LOCAL coords (meters). Derived from
        // analysis of 8860 human shots across 46 replays:
        //   FWD:  median 1.43, p10 0.69, p90 2.14   → use [0.9, 2.0]
        //   SIDE: median 0.10, p10 -1.24, p90 1.01  → use [-0.5, 1.0]
        //         (handedness offsets puck slightly to the right)
        //   UP:   median 0.21, p90 0.50              → use ≤ 0.5
        // The shot FSM only enters WindUp when the puck has been in
        // this zone for ≥ CradleSettleTicks. Real hockey: you cradle
        // the puck before flicking, you don't slap at moving pucks.
        private const float CradleFwdMin  = 0.9f;
        private const float CradleFwdMax  = 2.0f;
        private const float CradleSideMin = -0.5f;
        private const float CradleSideMax =  1.0f;
        private const float CradleUpMax   =  0.5f;
        private const int   CradleSettleTicks = 2;
        // Raycast-yaw offsets relative to body forward, in degrees.
        // Backswing: rotate AWAY from goal direction (small loading).
        // Strike: rotate PAST goal direction — this is the FLICK.
        // The flick is what generates blade tip rotational velocity;
        // body forward speed contributes only a baseline impulse
        // (replay p10 body speed at shot = 0.7 m/s — humans shoot
        // from a stop). Per real wrist-shot mechanics: short backswing,
        // explosive forward rotation, follow-through.
        private const float ShotBackswingDeg = 20f;
        private const float ShotStrikeDeg    = 45f;

        // Throttle debug logs to once per ~10 s (300 ticks at 30 Hz)
        // — enough to confirm steering activity in long runs without
        // flooding the log.
        private int _logTickCounter;
        private bool _everSawWorldPosition;

        // Diagnostics — answer "did the blade ever get close to the
        // puck?" and "did the strike actually move the puck?":
        //   _minBladePuckDistThisPeriod: smallest XZ distance from
        //     predicted blade impact (stickPos + stickFwd * 2.0) to
        //     the assigned puck during the current ~10 s log window.
        //   _shotsAttemptedThisPeriod: count of strike entries.
        //   _puckSpeedAtStrike / _puckSpeedAfterStrike: speed
        //     samples around a strike — diff tells us whether the
        //     swing actually transferred momentum to the puck.
        private float _minBladePuckDistThisPeriod = float.MaxValue;

        // Flail mode: when the puck has been sitting near-still for a
        // few seconds and we're close to it, force the shot FSM to
        // engage every chance it gets AND oscillate the blade angle.
        // Resolves "two bots circle a stagnant puck forever" — wild
        // swinging eventually connects via dumb luck and re-injects
        // puck velocity, breaking the standoff.
        private float _puckStagnantStartTime;          // 0 = not currently stagnant
        // Per-tick flag, set in the shot-trigger block, read again in
        // the blade-angle block to inject oscillation. Class field so
        // both sites see it across the {} scope boundary.
        private bool  _isFlailingThisTick;
        private const float FlailEnterSpeedMps  = 0.30f;
        private const float FlailExitSpeedMps   = 0.80f;
        // Personality field (see EnsurePersonality).
        private float FlailEnterSecs      = 3.0f;
        private const float FlailEnterRangeM    = 3.5f;
        private int   _shotsAttemptedThisPeriod;
        private float _puckSpeedAtStrike;
        private float _puckSpeedAfterStrike;
        // Peak puck speed during the strike outcome window. Tracks
        // the impulse delivery rather than the post-collision residue
        // — see STRIKE outcome log site for rationale.
        private float _puckPeakSpeedThisStrike;
        private int   _strikeOutcomeTicksLeft;
        private Vector3 _strikePuckPos;
        // Number of consecutive ticks the puck has been in the cradle
        // zone. Engages shot FSM only after CradleSettleTicks.
        private int _cradleTicks;
        private int _maxCradleTicksThisPeriod;

        // /vs vote-start: when bots join during warmup, each bot
        // periodically chats "/vs" so the vote count clears the
        // threshold and the server transitions Warmup → FaceOff →
        // Playing. Only fires for the first VoteWindowSec after
        // first spawn — outside warmup the chat is harmless but
        // unnecessary. Server config must have allowVoting=true.
        private float _lastVsSendTime;
        private float _vsFirstSeenTime;
        private bool  _voteDone;
        private const float VsResendIntervalSec = 3f;
        private const float VoteWindowSec       = 60f;

        // /vw vote-warmup state — symmetric to /vs but fires outside
        // Warmup phase. Pairs with VoteStart for repeatable
        // Play↔Warmup ping-pong testing.
        private float _vwFirstPlayingTime;
        private float _lastVwSendTime;
        private bool  _vwArmed;
        private const float VwResendIntervalSec = 3f;

        // Minimal phase-tagged Ping sampler for warmup-spike repro.
        // Logs once per second from BotIndex==0 only. Per-process logs
        // mean every child's bot 0 logs; with --bots-per-process 1 the
        // global bot is index 0 in its child, so all 12 children log.
        // Each line includes BotIndex (global) so segmentation is easy.
        private float _lastPingLogTime;
        private PuckStressTest.Mirror.GamePhase _lastLoggedPhase;
        private const float PingLogIntervalSec = 1f;

        // Walks SpawnedObjects looking for the MirrorGameManager and
        // returns its current phase. Returns 0 (Unknown) if not found.
        private PuckStressTest.Mirror.GamePhase CurrentGamePhase()
        {
            if (Player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null)
                return 0;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var gm = no.GetComponent<PuckStressTest.Mirror.MirrorGameManager>();
                if (gm != null) return gm.GameState.Value.Phase;
            }
            return 0;
        }

        // Stuck detection: bots wedged on each other or on the goal
        // frame stop moving but keep chasing. Track displacement
        // over a rolling window; flag stuck when low + far from
        // target. Stuck bots back off (moving away from the puck)
        // which surrenders the closest-claim to a teammate, who
        // steps in via the standard rank-based assignment logic.
        private Vector3 _stuckCheckPos;
        private float   _stuckCheckTime;
        private bool    _isStuck;
        private int     _stuckBackoffTicksLeft;
        private int     _stuckEpisodesThisPeriod;
        private const float StuckCheckIntervalSec = 1.0f;
        // Personality fields (see EnsurePersonality).
        private float StuckEnterDispM       = 0.5f;
        private const float StuckExitDispM        = 1.5f;
        private int   StuckBackoffTicks     = 45; // ~1.5 s at 30 Hz

        // Stuck-backoff rotation: ±90° applied to the away-from-puck
        // vector when backing off. Rolled once per stuck episode so the
        // bot doesn't oscillate. Sign=0 means "not currently held",
        // re-roll on next entry.
        private float _stuckBackoffRotDeg;
        private int   _stuckBackoffRotSign;

        // Goal crease no-go zone: don't drive into our own or the
        // opposing goal frame. Goals at z = ±40.23 (PuckAIPractice
        // mod). Steer targets that fall inside this box get
        // deflected to the rink-side edge.
        private const float GoalAvoidHalfWidth = 2.5f;
        private const float GoalAvoidNearZ     = 38.5f;
        private const float GoalAvoidFarZ      = 42.5f;

        // Stick-angle slew model. Mirrors StickPositioner.RotateRaycastOrigin
        // from the Puck decompile: the server PID-tracks the
        // raycastOriginAngle toward stickRaycastOriginAngleInput and
        // CLAMPS the angular velocity at ±15 deg/s. So a 30° yaw step
        // takes ~2 s to settle, which is why our "snap to input"
        // assumption produced lots of "blade was 4 cm from puck but
        // never struck" telemetry — the input target was right but
        // the actual blade was slewing toward it.
        //
        // We model the actual angle here so we can:
        //   1. lead the input by the slew time (predict where the
        //      puck will be when the stick actually arrives), and
        //   2. tell the shot FSM "you're not actually aimed yet —
        //      hold the windup another tick".
        private const float StickSlewMaxDegPerSec = 15f;       // PID outputMax
        private const float StickPidProportional  = 0.75f;     // PID p-gain
        private const float StickPitchMin = -25f,  StickPitchMax = 80f;
        private const float StickYawMin   = -92.5f, StickYawMax  = 92.5f;
        // initialStickRaycastOriginAngle = (40, 80) per PlayerInput.cs:2055.
        private Vector2 _estStickAngleDeg  = new Vector2(40f, 80f);
        private Vector2 _lastStickInputDeg = new Vector2(40f, 80f);
        private float   _maxSlewErrThisPeriod;

        // Body-yaw slew model — mirror of Movement.Turn() from the
        // decompile (Movement.cs:230-266). Server applies torque from
        // the sign of MoveInput.x with these constants:
        //   turnAcceleration       = 1.625  rad/s² ≈ 93.1°/s²
        //   turnBrakeAcceleration  = 3.25   rad/s² ≈ 186.2°/s²
        //   turnMaxSpeed           = 1.375  rad/s  ≈ 78.8°/s (×TurnMultiplier)
        //   turnDrag               = 3.0    /s (no input, below max)
        //   turnOverspeedDrag      = 2.25   /s (above max)
        //   TurnMultiplier         = 1 | 2 (sliding) | 5 (jumping)
        // PlayerBodyV2.cs:421-422 only uses sign(MoveInput.x), not magnitude.
        // We reseed _estBodyYawWorldDeg from the mirror snapshot each
        // tick (FEEDBACK — mirror is server-authoritative, just RTT-old)
        // and forward-project for shot-direction decisions (FEEDFORWARD —
        // see ProjectBodyYawWorldDeg). Server has no rollback, so an
        // off-by-one-tick body yaw at strike-landing time = a missed
        // shot or a wide one. The projection horizon is
        // RttSeconds() + StrikeWindowSec, the time between "we decide
        // to flick" and "blade impacts puck on the server."
        private const float BodyTurnAccelDegPerSec2     = 93.1f;
        private const float BodyTurnBrakeDegPerSec2     = 186.2f;
        private const float BodyTurnMaxRateDegPerSec    = 78.8f;
        private const float BodyTurnDragPerSec          = 3.0f;
        private const float BodyTurnOverspeedDragPerSec = 2.25f;

        private float _estBodyYawWorldDeg;
        private float _estBodyYawRateDegPerSec;
        private float _measuredBodyYawWorldDegPrev;
        private bool  _haveMeasuredBodyYaw;

        private void Update()
        {
            if (Input == null || !Input.IsSpawned) return;

            _accumulator += Time.deltaTime * TickHz;
            while (_accumulator >= 1f)
            {
                _accumulator -= 1f;
                Tick();
            }
        }

        private void Tick()
        {
            float tickDt = 1f / TickHz;
            _phase += tickDt;

            // Lazy personality init. No-op once the bot has its name
            // and the personality vector is sampled.
            EnsurePersonality();

            // Per-tick BotChatter polls. Edge-triggered chats fire
            // from their own existing detection sites further down.
            PollScoreForChat();
            PollCollisionsForChat();

            // Step our model of the engine's stick-angle PID first, so
            // _estStickAngleDeg reflects "where the blade actually is
            // right now" before we compute a new input.
            IntegrateStickSlew(tickDt);

            // Periodic /vs vote-start during warmup. Stops as soon as
            // phase leaves Warmup. Gated by VoteStart so we don't spam
            // vote-start on community servers — only fires when
            // explicitly enabled via --vote-start CLI flag or
            // playbook vote_start: true.
            float vsNow = Time.realtimeSinceStartup;
            if (_vsFirstSeenTime == 0f) _vsFirstSeenTime = vsNow;
            if (VoteStart
                && !_voteDone
                && vsNow - _vsFirstSeenTime < VoteWindowSec
                && vsNow - _lastVsSendTime >= VsResendIntervalSec
                && Player != null && Player.NetworkManager != null
                && Player.NetworkManager.IsConnectedClient)
            {
                if (CurrentGamePhase() != PuckStressTest.Mirror.GamePhase.Warmup)
                {
                    _voteDone = true;
                }
                else
                {
                    _lastVsSendTime = vsNow;
                    try { PuckStressTest.Mirror.ChatSender.TrySend(Player.NetworkManager, "/vs"); }
                    catch (System.Exception ex) { Debug.LogWarning($"[BotBrain bot={BotIndex}] /vs send failed: {ex.Message}"); }
                }
            }

            // Periodic /vw vote-warmup during Playing — symmetric
            // companion to /vs. Forces server back to Warmup mid-run
            // so we can cycle Play↔Warmup repeatably. Arms after
            // VoteWarmupAfterSeconds of Playing; resets on Warmup
            // entry so the bot keeps cycling.
            if (VoteWarmup
                && Player != null && Player.NetworkManager != null
                && Player.NetworkManager.IsConnectedClient)
            {
                var phase = CurrentGamePhase();
                if (phase == PuckStressTest.Mirror.GamePhase.Playing)
                {
                    if (_vwFirstPlayingTime == 0f) _vwFirstPlayingTime = vsNow;
                    if (!_vwArmed && vsNow - _vwFirstPlayingTime >= VoteWarmupAfterSeconds)
                        _vwArmed = true;
                    if (_vwArmed && vsNow - _lastVwSendTime >= VwResendIntervalSec)
                    {
                        _lastVwSendTime = vsNow;
                        try { PuckStressTest.Mirror.ChatSender.TrySend(Player.NetworkManager, "/vw"); }
                        catch (System.Exception ex) { Debug.LogWarning($"[BotBrain bot={BotIndex}] /vw send failed: {ex.Message}"); }
                    }
                }
                else if (phase == PuckStressTest.Mirror.GamePhase.Warmup)
                {
                    _vwFirstPlayingTime = 0f;
                    _vwArmed = false;
                }
            }

            // Phase-tagged ping log. With --bots-per-process 1 each
            // child has a single bot, so log unconditionally — every
            // child's log file gets its own ping trace, segmentable
            // by BotIndex (global).
            if (Player != null)
            {
                var nowPhase = CurrentGamePhase();
                bool phaseChanged = nowPhase != _lastLoggedPhase;
                if (phaseChanged || vsNow - _lastPingLogTime >= PingLogIntervalSec)
                {
                    // Player.Ping is NetworkVariable<ulong> in B323
                    // (was int in B202). Cast back to int for the log
                    // — ms values stay well under int range.
                    int ping = -1;
                    try { ping = (int)Player.Ping.Value; } catch { }
                    Debug.Log($"[PING] phase={nowPhase} ping={ping}ms (botIdx={BotIndex})");
                    _lastPingLogTime  = vsNow;
                    _lastLoggedPhase  = nowPhase;
                }
            }

            // Real-client gate: PlayerInput.cs:108-141 only ticks
            // inputs while the PlayerInput NB IsOwner AND has spawned;
            // OnNetworkDespawn flips shouldTickInputs=false. The Player
            // NetworkObject persists across phases but the BodyV2 +
            // Stick + StickPositioner + PlayerCamera get despawned on
            // Play→Replay→Warmup→FaceOff cycles, and a real client
            // stops sending Move/Stick/Look during that window. We
            // mirror that here: when our State isn't Play, drop any
            // in-flight FSM state and skip the input-send block.
            // Without this, BotBrain sprays Move/Stick/Look at 360 Hz
            // to a Player NB whose body is gone; combined with the
            // server's reliable spawn burst on phase transitions
            // (~48 spawn messages × 12 clients), UTP's reliable
            // window saturates and measured RTT spikes to 1000 ms+.
            PlayerState curState = PlayerState.None;
            try { curState = Player.State; } catch { }
            if (curState != PlayerState.Play)
            {
                // Reset transient state so we don't resume a
                // half-completed shot or pulse-turn after respawn.
                _shotState = ShotState.Idle;
                _shotTicksLeft = 0;
                _strikeOutcomeTicksLeft = 0;
                _turnState = TurnState.Normal;
                _turnStateTicksLeft = 0;
                _carrying = false;
                _isStuck = false;
                _stuckBackoffTicksLeft = 0;
                _cradleTicks = 0;
                // Bool RPCs were last-sent-on-change; if they were
                // true while we were playing, server still thinks
                // we're sliding/sprinting. Clear once on transition.
                if (_slideSent)  { try { Input.SendSlide(false);  } catch { } _slideSent = false; }
                if (_sprintSent) { try { Input.SendSprint(false); } catch { } _sprintSent = false; }
                if (_bladeAngleSent != 0) { try { Input.SendBladeAngle(0); } catch { } _bladeAngleSent = 0; }
                if (_carving)
                {
                    try { Input.SendLateralLeft(false); Input.SendLateralRight(false); } catch { }
                    _carving = false; _carveTicksLeft = 0;
                }
                return;
            }

            // Refresh cached body / stick NIDs periodically; server
            // may respawn body / stick between rounds.
            if (--_refreshTickCounter <= 0)
            {
                _refreshTickCounter = Scaled(30);
                _myBodyNid  = FindMyBodyNid();
                _myBody     = FindMyBody();
                _myStickNid = FindMyStickNid();
                if (Snapshot != null) Snapshot.Bind(Player, Input, _myBody);
            }

            Vector3 myPos = Vector3.zero;
            Quaternion myRot = Quaternion.identity;
            Vector3 puckPos = Vector3.zero;
            bool haveMe   = TryGetMyXform(out myPos, out myRot);

            // FEEDBACK: refresh body-yaw model from the mirror snapshot
            // (authoritative, RTT-old). Sliding doubles turnMaxSpeed
            // server-side, so pass the current sent slide state.
            ReseedBodyYawFromMirror(tickDt, myRot, haveMe, _slideSent);
            bool goalie   = IsGoalie();
            // Goalies never claim pucks — they defend the net. The
            // assigned-puck claim logic on the team SKIPS the goalie
            // (rank-based assignment doesn't filter by role yet, but
            // the goalie's puckPos = nearest puck for tracking, NOT
            // for chasing). Skaters do the carry / shot work.
            // Stuck bots drop their carrier claim and fall through to
            // support behavior — that surrenders the puck to the next
            // teammate by rank-distance, so a fresh skater steps in.
            bool isCarrier = !goalie && !_isStuck && haveMe && TryGetAssignedPuck(myPos, out puckPos);
            bool haveWorld = isCarrier;

            // Cradle membership computed once at the top of Tick so
            // it's visible to (a) carry-mode entry, (b) stickhandle
            // oscillation in the stick-engage block, (c) the
            // BladeAngle cup logic, and (d) the slide-suppression in
            // the wantSlide block — all of which fire in different
            // scopes lower down. Body-local puck position needs myRot
            // and puckPos which are now both valid.
            Vector3 puckBodyLocal = haveMe && isCarrier
                ? Quaternion.Inverse(myRot) * (puckPos - myPos)
                : Vector3.zero;
            bool inCradle = isCarrier
                            && puckBodyLocal.z >= CradleFwdMin && puckBodyLocal.z <= CradleFwdMax
                            && puckBodyLocal.x >= CradleSideMin && puckBodyLocal.x <= CradleSideMax
                            && puckBodyLocal.y <= CradleUpMax;

            // Strike aim target. Set inside the haveWorld block when
            // engageShot fires; defaults to OffensiveGoal() so the
            // WindUp/Strike blade-angle block (outside haveWorld
            // scope) can read it without a null check. Updated to a
            // teammate position when we elect to pass instead of
            // shoot.
            Vector3 strikeAimTarget = OffensiveGoal();

            // Update puck-velocity history when we have a real puck
            // assignment (Support's "puckPos" is a static rest point,
            // not a real puck — skip in that case). Use realtime
            // delta so it survives variable tick rates.
            if (isCarrier)
            {
                float now = Time.realtimeSinceStartup;
                if (_havePuckHistory)
                {
                    float dt = Mathf.Max(0.001f, now - _lastPuckSampleTime);
                    _lastPuckVelocity = (puckPos - _lastPuckPos) / dt;
                }
                _lastPuckPos = puckPos;
                _lastPuckSampleTime = now;
                _havePuckHistory = true;
            }
            else
            {
                _havePuckHistory = false;
                _lastPuckVelocity = Vector3.zero;
            }

            if (haveMe && !isCarrier)
            {
                Vector3 rest;
                if (goalie)
                {
                    bool seePuck = TryGetNearestPuck(myPos, out var trackPuck);
                    rest = GoalieTarget(seePuck, trackPuck);
                }
                else
                {
                    rest = SupportRestPosition();
                }
                if (rest != Vector3.zero)
                {
                    puckPos = rest;
                    haveWorld = true;
                }
            }

            // CARRY MODE: chase vs carry switch on body→puck distance.
            //   chase  (far)  → steer toward the puck.
            //   carry  (near) → steer toward the GOAL itself. Body
            //                   forward = goal direction. The puck
            //                   already sits in front of us (we got
            //                   here by chasing it), so skating
            //                   forward pushes the puck toward the
            //                   goal via the stick blade. This also
            //                   satisfies the shot trigger's
            //                   "aimed at goal" gate naturally.
            //
            // An earlier version targeted a waypoint 0.6 m BEHIND the
            // puck on the puck→goal line. That positioned the bot
            // correctly while approaching but, once close, body
            // forward pointed at the waypoint (often perpendicular
            // to puck→goal), so the shot FSM never triggered.
            //
            // Hysteresis: enter at CarryEnterRange, drop at
            // CarryExitRange. Carry is also dropped if no offensive
            // goal is known yet (team unset / mid-spawn).
            // STUCK DETECTION: track displacement over a rolling 1 s
            // window. If we've moved less than StuckEnterDispM while
            // a steer target sits far away, flag stuck. Stuck bots
            // back off (skate AWAY from the puck for ~1.5 s), which
            // physically gives up the closest-claim so a teammate
            // takes over via the normal rank-based assignment.
            float stuckNow = Time.realtimeSinceStartup;
            if (_stuckCheckTime == 0f) { _stuckCheckTime = stuckNow; _stuckCheckPos = myPos; }
            if (haveMe && stuckNow - _stuckCheckTime >= StuckCheckIntervalSec)
            {
                float disp = Vector3.Distance(myPos, _stuckCheckPos);
                if (!_isStuck && disp < StuckEnterDispM && haveWorld
                    && Vector3.Distance(myPos, puckPos) > 1.5f)
                {
                    _isStuck = true;
                    TryChat(ChatTrigger.StuckBacking);
                    _stuckBackoffTicksLeft = Scaled(StuckBackoffTicks);
                    _stuckEpisodesThisPeriod++;
                }
                else if (_isStuck && disp > StuckExitDispM)
                {
                    _isStuck = false;
                }
                _stuckCheckTime = stuckNow;
                _stuckCheckPos = myPos;
            }
            if (_stuckBackoffTicksLeft > 0) _stuckBackoffTicksLeft--;
            else if (_isStuck && _stuckBackoffTicksLeft == 0) _isStuck = false;

            // FEEDFORWARD: in chase, steer toward where the puck will
            // be when we arrive, not where it is now. Intercept time
            // ≈ distToPuck / bodySkateSpeed. Real hockey: skate to
            // the puck's future position, don't chase its current one.
            const float BodySkateSpeed = 6.0f;
            const float MaxLeadTime    = 1.0f; // s — ignore far-future predictions
            Vector3 steerTarget = puckPos;
            if (isCarrier && haveWorld)
            {
                float distNow = Vector3.Distance(myPos, puckPos);
                float leadTime = Mathf.Min(MaxLeadTime, distNow / BodySkateSpeed);
                steerTarget = puckPos + EstimatePuckVelocity() * leadTime;
                // Decision noise: per-bot xy drift breaks the symmetry
                // that produces "multiple bots circling the same puck".
                // Drift fades out as we close in (<2 m) so it doesn't
                // ruin the precise approach for the actual carrier.
                if (!IsGoalie() && !_isStuck && _cradleTicks == 0)
                {
                    float distFade = Mathf.Clamp01((distNow - 2f) / 3f);
                    steerTarget += CurrentDriftOffset() * distFade;
                }
            }
            Vector3 offGoal = OffensiveGoal();
            bool haveOffGoal = offGoal != Vector3.zero;
            float distToPuck = (haveWorld && isCarrier) ? Vector3.Distance(myPos, puckPos) : float.MaxValue;

            // STUCK BACKOFF: override target to skate AWAY from the
            // puck (or current target). Releases the closest-claim
            // so a teammate steps in. Carry mode is disabled while
            // stuck so we don't accidentally re-engage.
            if (_isStuck && _stuckBackoffTicksLeft > 0 && haveWorld)
            {
                Vector3 awayDir = (myPos - puckPos);
                awayDir.y = 0f;
                if (awayDir.sqrMagnitude < 0.01f)
                {
                    Vector3 fwdFallback = myRot * Vector3.forward;
                    fwdFallback.y = 0f;
                    awayDir = -fwdFallback;
                }
                // Decision noise: rotate the away-from-puck vector by
                // a random ±90° per stuck episode. Without this, two
                // bots wedged together both back off along exactly
                // opposite-and-equivalent puck-radial vectors, which
                // for symmetric wedges still leaves them blocking
                // each other. Latched per backoff window (re-rolled
                // on entry) so the bot doesn't oscillate within a
                // single backoff.
                if (_stuckBackoffRotSign == 0)
                {
                    _stuckBackoffRotDeg = ((float)Rng.NextDouble() * 2f - 1f) * 90f;
                    _stuckBackoffRotSign = 1;
                }
                Quaternion rot = Quaternion.AngleAxis(_stuckBackoffRotDeg, Vector3.up);
                awayDir = rot * awayDir.normalized;
                steerTarget = myPos + awayDir * 6f;
                _carrying = false;
            }
            else if (_stuckBackoffTicksLeft == 0 && _stuckBackoffRotSign != 0)
            {
                _stuckBackoffRotSign = 0; // re-roll next time
            }

            // GOAL CREASE AVOID: if steerTarget falls inside either
            // goal frame (|x| < 2.5, |z| 38.5–42.5), pull it back to
            // the rink-side edge. Pucks resting in the net are dead
            // and not worth chasing; teammates wedging the goal
            // frame stop moving entirely.
            if (Mathf.Abs(steerTarget.x) < GoalAvoidHalfWidth
                && Mathf.Abs(steerTarget.z) > GoalAvoidNearZ
                && Mathf.Abs(steerTarget.z) < GoalAvoidFarZ)
            {
                steerTarget = new Vector3(
                    Mathf.Sign(steerTarget.x) * (GoalAvoidHalfWidth + 0.5f),
                    steerTarget.y,
                    Mathf.Sign(steerTarget.z) * GoalAvoidNearZ);
            }

            // BOARD-PINNED TURNAROUND: if we're physically against the
            // boards AND the current steer target sits on the other
            // side of the board (i.e. we'd have to turn INTO the wall
            // to follow it), redirect first toward the nearest patch
            // of open ice so the body rotates clear. The game's
            // physics doesn't let the body rotate into a wall, so a
            // naive "turn toward target" steers into a no-op and the
            // bot stalls glued to the boards.
            //
            // Rink bounds (rink geometry): boards at
            // approx |x| ≈ 13.0 (side) and |z| ≈ 42.5 (end). "Pinned"
            // means within BoardPinM of a board. Open-ice direction
            // is +/-x or +/-z toward rink centre.
            if (haveMe)
            {
                // Empirical rink bounds (observed bot myPos extremes
                // 2026-05-14: x reached -17.5, z reached 43.1). The
                // playable area in the Puck level extends past the
                // simple rink rectangle (notably behind the nets and
                // in the corners), so trigger only when the bot has
                // pushed FAR out — otherwise we'd over-trigger every
                // time a bot runs along the wall normally.
                const float BoardPinM      = 0.6f;    // distance from board considered "pinned"
                const float BoardSideX     = 18.0f;
                const float BoardEndZ      = 44.0f;
                const float ClearPushM     = 6.0f;    // how far inward the clear waypoint sits

                Vector3 clearDir = Vector3.zero;
                if      (myPos.x >  BoardSideX - BoardPinM) clearDir += Vector3.left;
                else if (myPos.x < -BoardSideX + BoardPinM) clearDir += Vector3.right;
                if      (myPos.z >  BoardEndZ  - BoardPinM) clearDir += Vector3.back;
                else if (myPos.z < -BoardEndZ  + BoardPinM) clearDir += Vector3.forward;

                if (clearDir != Vector3.zero)
                {
                    // Only override if the current target would push
                    // us deeper into the wall (steerDir · clearDir < 0).
                    Vector3 steerDir = (steerTarget - myPos);
                    steerDir.y = 0f;
                    if (steerDir.sqrMagnitude > 0.01f && Vector3.Dot(steerDir.normalized, clearDir.normalized) < 0f)
                    {
                        steerTarget = myPos + clearDir.normalized * ClearPushM;
                        if (!_prevBoardPinned) TryChat(ChatTrigger.BoardPinned);
                        _prevBoardPinned = true;
                        // Also temporarily drop carry — we're recovering,
                        // not driving the puck.
                        _carrying = false;
                    }
                    else _prevBoardPinned = false;
                }
                else _prevBoardPinned = false;
            }
            if (isCarrier && haveOffGoal)
            {
                // Carry mode pivots body forward to face the offensive
                // goal so skating forward pushes the puck onward via the
                // blade. But this must NOT happen until the puck is
                // actually on the broadside — otherwise the pivot
                // rotates AWAY from a puck the bot is still chasing
                // (user-reported "bots turn away from puck" symptom).
                //
                // Gates (all in body-local + world coordinates):
                //   - puck in body's front hemisphere (z > 0)
                //   - puck not too far laterally for cradle (|x| < 1.5)
                //   - body forward vector and (myPos→goal) point in
                //     the same XZ hemisphere; otherwise carrying =
                //     180° pivot away from the puck while we're still
                //     fetching it.
                Vector3 puckBL_carry = Quaternion.Inverse(myRot) * (puckPos - myPos);
                Vector3 myPosToGoalXZ = new Vector3(offGoal.x - myPos.x, 0f, offGoal.z - myPos.z);
                Vector3 bodyFwdXZ = (myRot * Vector3.forward); bodyFwdXZ.y = 0f;
                bool puckInFront     = puckBL_carry.z > 0.4f && Mathf.Abs(puckBL_carry.x) < 1.5f;
                bool goalInFront     = Vector3.Dot(myPosToGoalXZ, bodyFwdXZ) > 0f;
                bool carryEligible   = puckInFront && goalInFront && distToPuck < CarryEnterRange;
                bool carryStillValid = puckInFront && distToPuck < CarryExitRange;
                if (_carrying)
                {
                    if (!carryStillValid) _carrying = false;
                }
                else
                {
                    if (carryEligible) _carrying = true;
                }
                if (_carrying) steerTarget = offGoal;
            }
            else
            {
                _carrying = false;
            }

            short moveX, moveY, lookX, lookY, stickX, stickY;
            if (haveWorld)
            {
                // World-space heading from bot to STEERING TARGET
                // (puck during chase, carry waypoint during carry).
                Vector3 toTarget = steerTarget - myPos;
                toTarget.y = 0f;
                float targetYawWorld = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;

                // For LookAngle and stick aim, always look at the
                // PUCK itself, not the carry waypoint. Body chases
                // the waypoint, head + stick track the puck.
                Vector3 toPuck = puckPos - myPos;
                toPuck.y = 0f;
                float puckYawWorld = Mathf.Atan2(toPuck.x, toPuck.z) * Mathf.Rad2Deg;

                // Bot's current world yaw from its body rotation.
                Vector3 bodyForward = myRot * Vector3.forward;
                bodyForward.y = 0f;
                float currentYawWorld = Mathf.Atan2(bodyForward.x, bodyForward.z) * Mathf.Rad2Deg;

                // Body-relative angles. Positive = target is to the
                // RIGHT of body forward; negative = LEFT. Wrapped to
                // [-180, 180]. Two separate deltas:
                //   deltaYaw     — body steers toward STEER TARGET
                //                  (puck during chase, carry waypoint
                //                   during carry).
                //   deltaYawPuck — head + stick aim AT THE PUCK
                //                  itself (matches what a real player
                //                  does — eyes on the puck while
                //                  positioning the body).
                float deltaYaw     = Mathf.DeltaAngle(currentYawWorld, targetYawWorld);
                float deltaYawPuck = Mathf.DeltaAngle(currentYawWorld, puckYawWorld);

                // Move axes per Puck's PlayerBodyV2.cs:419-422:
                //   y > 0 → MoveForwards, y < 0 → MoveBackwards
                //   x > 0 → TurnRight,    x < 0 → TurnLeft
                // Encode short = ratio * 32767. Skate full forward,
                // steer with x proportional to angle delta (saturate
                // at 30° for crisp turns). Support bots stop pushing
                // forward once they're inside SupportArrivedRange of
                // their rest spot — otherwise they overshoot and
                // oscillate, which reads as "huddling".
                bool atRest = !isCarrier && Vector3.Distance(myPos, steerTarget) < SupportArrivedRange;
                moveY = atRest ? (short)0 : (short)32767;
                float turn = Mathf.Clamp(deltaYaw / 30f, -1f, 1f);
                moveX = (short)Mathf.RoundToInt(turn * 32767f);

                // LookAngle is body-relative head turn. Format per
                // PlayerInput.cs:680: Vector2(x, y) where x = pitch
                // (clamped -25..75), y = yaw (clamped -135..135).
                // Aim head toward PUCK (eyes on puck) with a slight
                // downward pitch to look at the ice.
                float headPitch = Mathf.Clamp(-10f, -25f, 75f);
                float headYaw   = Mathf.Clamp(deltaYawPuck, -135f, 135f);
                lookX  = AngleToShort(headPitch);
                lookY  = AngleToShort(headYaw);

                // Aim raycast at the LEAD-PREDICTED puck position
                // from the actual STICK POSITION (not body center).
                //
                // Why stick, not body: the raycastOrigin GameObject
                // is parented to the stick, so its world position is
                // (stickPos + stickRot * raycastOriginLocalOffset).
                // We don't know the local offset exactly, but the
                // stick's transform is much closer to the true
                // origin than the body — this picks up both:
                //   - Lever 1 (origin HEIGHT): stickPos.y is the
                //     real shaft midpoint, no need to guess "1.0 m".
                //   - Lever 2 (HANDEDNESS offset): stick sits to the
                //     right of body; aiming from the stick removes
                //     the body→stick lateral bias that was causing
                //     the yaw to be a few degrees off when the puck
                //     was close.
                //
                // Lever 3 (lead the puck): predict where the puck
                // will be when the action lands. Use the most
                // recently sampled puck velocity scaled by the
                // server-reported RTT NV.
                Vector3 originXyz;
                bool haveStickOrigin = TryGetMyStickXform(out originXyz, out _);
                if (!haveStickOrigin)
                {
                    // Pre-stick fallback: use body center + rough
                    // 1.0 m height so we still get sensible angles
                    // before the stick mirror binds.
                    originXyz = myPos + Vector3.up * 1.0f;
                }

                Vector3 bodyFwdVec   = myRot * Vector3.forward;
                Vector3 bodyRightVec = myRot * Vector3.right;
                bodyFwdVec.y = 0f;   bodyFwdVec   = bodyFwdVec.normalized;
                bodyRightVec.y = 0f; bodyRightVec = bodyRightVec.normalized;

                // Solve for the input angles that would put the BLADE
                // at puckLead. Two-pass: first pass uses RTT-only lead
                // to get a rough desired angle; we measure the slew
                // gap to that rough angle, convert to a slew-time
                // estimate, and re-lead by that horizon. Result: the
                // input target is aimed at where the puck WILL be
                // when the engine PID has actually slewed the stick
                // there.
                // GATE: only actively aim when this bot can plausibly
                // engage the puck. For Support bots and far Carriers,
                // a 10-m-distant puck means the geometry-derived yaw
                // is huge and changes fast as the bot moves — sending
                // that to the engine PID just saturates its integral
                // term and leaves the stick out of position when the
                // bot DOES get close. Park at the engine's neutral
                // pose (40°, 0°) until within engagement range.
                const float StickEngageRange = 4.0f;
                bool stickEngage = isCarrier && distToPuck < StickEngageRange;
                float pitchDeg, stickYawDeg;
                if (stickEngage)
                {
                    // FEEDFORWARD stick aim: aim at where the puck
                    // WILL be in (RTT + stickSlewTime), not where it
                    // is now. Two natural consequences:
                    //   1. Incoming pass — puck moving toward us →
                    //      puckLead is CLOSER than puckPos → blade
                    //      gets pulled IN toward body (the "bulldozer
                    //      scoop" the user described).
                    //   2. Cradled puck — puckVel ≈ bodyVel → puckLead
                    //      shifts by the same amount as the body, so
                    //      relative position stays in cradle.
                    //   3. Loose puck moving away — puckLead is
                    //      farther → blade extends to chase.
                    // BROADSIDE OFFSET: the contact zone is the blade's
                    // long broad face, not the toe point. ComputeStickAimDeg
                    // lands the raycast hit (= toe) at aimPoint. To put
                    // the BROADSIDE on the puck, we overshoot the puck
                    // along the origin→puck direction by half the blade
                    // segment, so the toe is past the puck and the
                    // broadside (heel..toe midpoint) sits over it.
                    // Per project_shot_mechanics.md the puck rides the
                    // broadside, not the needlepoint at the toe.
                    Vector3 puckVel = EstimatePuckVelocity();
                    float baseLead = RttSeconds();
                    const float BroadsideBias = 0.15f; // (toe - heel)/2
                    Vector3 puckLead0 = puckPos + puckVel * baseLead;
                    Vector3 toPuckXZ0 = new Vector3(puckLead0.x - originXyz.x, 0f, puckLead0.z - originXyz.z);
                    Vector3 broadsideDir0 = toPuckXZ0.sqrMagnitude > 1e-4f ? toPuckXZ0.normalized : bodyFwdVec;
                    Vector3 aimPoint0 = puckLead0 + broadsideDir0 * BroadsideBias;
                    ComputeStickAimDeg(originXyz, aimPoint0,
                                       bodyFwdVec, bodyRightVec,
                                       out float rawPitchDeg, out float rawYawDeg);
                    float pitchSlewErr = Mathf.Abs(Mathf.DeltaAngle(_estStickAngleDeg.x, rawPitchDeg));
                    float yawSlewErr   = Mathf.Abs(Mathf.DeltaAngle(_estStickAngleDeg.y, rawYawDeg));
                    float slewTime = Mathf.Max(pitchSlewErr, yawSlewErr) / StickSlewMaxDegPerSec;
                    slewTime = Mathf.Clamp(slewTime, 0f, 0.6f);
                    Vector3 puckLead = puckPos + puckVel * (baseLead + slewTime);
                    Vector3 toPuckXZ = new Vector3(puckLead.x - originXyz.x, 0f, puckLead.z - originXyz.z);
                    Vector3 broadsideDir = toPuckXZ.sqrMagnitude > 1e-4f ? toPuckXZ.normalized : bodyFwdVec;
                    Vector3 aimPoint = puckLead + broadsideDir * BroadsideBias;
                    ComputeStickAimDeg(originXyz, aimPoint,
                                       bodyFwdVec, bodyRightVec,
                                       out pitchDeg, out stickYawDeg);
                }
                else
                {
                    // Engine neutral pose: pitch 40°, yaw 0° (body
                    // forward). Stick stays parked, ready to engage.
                    pitchDeg = 40f;
                    stickYawDeg = 0f;
                }

                // Stickhandle oscillation while the puck is in the
                // cradle. Real players "tap left and right" by swinging
                // the stick yaw across the puck (forehand → over the
                // top → backhand → back). We add a low-freq sin
                // around the tracked aim. The engine's PID slews at
                // ≤15°/s, so amplitude+frequency must stay within
                // what the actuator can follow without saturating
                // (and HasChanged RPC gating means smaller swings cut
                // RPC volume). 8° at 1.2 Hz → peak 60°/s → engine
                // tracks ~25% of that, producing a real left/right
                // tap motion across the broadside without losing the
                // puck. Skip during shot FSM states (the
                // backswing/strike code below overrides yawClamped
                // anyway, but cleaner to gate here too).
                if (inCradle && _shotState == ShotState.Idle)
                {
                    const float StickhandleAmpDeg = 8f;
                    const float StickhandleHz     = 1.2f;
                    float osc = StickhandleAmpDeg * Mathf.Sin(2f * Mathf.PI * StickhandleHz * _phase);
                    stickYawDeg += osc;
                }

                // RATE LIMIT: cap input delta from modeled current
                // angle. Engine PID slews at ≤15°/s anyway; sending
                // 80° steps just winds the integral up and leaves
                // the stick over-shooting once the input settles.
                // 25° per tick at 30 Hz = 750°/s upper-bound input
                // velocity, well above the 15°/s actuator limit.
                const float MaxInputDeltaPerTick = 25f;
                float pitchClamped = _estStickAngleDeg.x + Mathf.Clamp(
                    Mathf.DeltaAngle(_estStickAngleDeg.x, pitchDeg),
                    -MaxInputDeltaPerTick, MaxInputDeltaPerTick);
                float yawClamped = _estStickAngleDeg.y + Mathf.Clamp(
                    Mathf.DeltaAngle(_estStickAngleDeg.y, stickYawDeg),
                    -MaxInputDeltaPerTick, MaxInputDeltaPerTick);
                pitchClamped = Mathf.Clamp(pitchClamped, StickPitchMin, StickPitchMax);
                yawClamped   = Mathf.Clamp(yawClamped,   StickYawMin,   StickYawMax);

                // Shot trigger. Body-relative angle to the offensive
                // goal — used to choose the backswing/strike direction
                // and decide whether we're aimed.
                float goalYawWorld   = Mathf.Atan2((offGoal - myPos).x, (offGoal - myPos).z) * Mathf.Rad2Deg;
                float deltaYawGoal   = Mathf.DeltaAngle(currentYawWorld, goalYawWorld);
                bool bladeClose = false;
                float bladePuckDist = float.MaxValue;
                Vector3 bladeHeelXZ = Vector3.zero, bladeToeXZ = Vector3.zero;
                bool haveBladeSeg = false;
                if (TryGetMyStickXform(out var stickPos, out var stickRot))
                {
                    Vector3 stickFwd = stickRot * Vector3.forward;
                    stickFwd.y = 0f;
                    if (stickFwd.sqrMagnitude > 1e-4f) stickFwd = stickFwd.normalized;
                    Vector3 heel = stickPos + stickFwd * StickHeelDistWorld;
                    Vector3 toe  = stickPos + stickFwd * StickToeDistWorld;
                    bladeHeelXZ  = new Vector3(heel.x, 0f, heel.z);
                    bladeToeXZ   = new Vector3(toe.x,  0f, toe.z);
                    Vector3 puckXZShot = new Vector3(puckPos.x, 0f, puckPos.z);
                    bladePuckDist = ClosestDistancePointToSegmentXZ(puckXZShot, bladeHeelXZ, bladeToeXZ);
                    bladeClose = bladePuckDist < ShotMaxBladeToPuckRange;
                    haveBladeSeg = true;
                }
                if (isCarrier && bladePuckDist < _minBladePuckDistThisPeriod)
                    _minBladePuckDistThisPeriod = bladePuckDist;

                // Cradle gate — body-local puck position must be in
                // the human-shot envelope (replay distributions). This
                // replaces the loose "bladeClose && aimed at goal"
                // gate that triggered shots on glancing contact.
                // (Both inCradle and puckBodyLocal were hoisted earlier
                // in Tick so they're visible to the stickhandle
                // oscillation, blade-cup, and slide-suppression
                // blocks too.)
                if (inCradle) _cradleTicks++;
                else _cradleTicks = 0;
                if (_cradleTicks > _maxCradleTicksThisPeriod)
                    _maxCradleTicksThisPeriod = _cradleTicks;

                // Flail-on-stagnant detection. Track how long the puck
                // has been below FlailEnterSpeedMps; once it crosses
                // FlailEnterSecs AND we're within FlailEnterRangeM,
                // enter flail mode. Exit when the puck speed crosses
                // FlailExitSpeedMps (hysteresis prevents the timer
                // bouncing while a teammate barely nudges the puck).
                float puckSpeedNow = haveWorld ? EstimatePuckVelocity().magnitude : 0f;
                float wallNow = Time.realtimeSinceStartup;
                if (haveWorld && puckSpeedNow < FlailEnterSpeedMps)
                {
                    if (_puckStagnantStartTime == 0f) _puckStagnantStartTime = wallNow;
                }
                else if (puckSpeedNow > FlailExitSpeedMps || !haveWorld)
                {
                    _puckStagnantStartTime = 0f;
                }
                bool puckStagnant = _puckStagnantStartTime > 0f
                                    && (wallNow - _puckStagnantStartTime) >= FlailEnterSecs;
                _isFlailingThisTick = isCarrier && puckStagnant
                                      && distToPuck < FlailEnterRangeM
                                      && !goalie;
                if (_isFlailingThisTick && !_prevIsFlailing) TryChat(ChatTrigger.FlailingStarted);
                _prevIsFlailing = _isFlailingThisTick;

                // Strike trigger. Fire whenever puck is on the
                // broadside (bladeClose) OR in the cradle envelope
                // (inCradle) — gives more shot opportunities than
                // bladeClose alone. Replay analysis showed real shots
                // come from puckBodyLocal.fwd ~ 1.4-3.5m, which spans
                // both gates. ALSO fire in flail mode — when a stagnant
                // puck has been sitting nearby for FlailEnterSecs the
                // bot ignores aim quality and just keeps swinging until
                // something connects.
                bool engageShot = isCarrier
                                  && haveOffGoal
                                  && (bladeClose || inCradle || _isFlailingThisTick);

                // Decide aim target: shoot at goal if we have a clear
                // lane, otherwise look for an open teammate to pass
                // to. Scoring: a teammate is a good pass target if
                //   (a) they're within reasonable pass distance,
                //   (b) they're in the offensive direction (closer to
                //       offGoal than us, on the puck→goal axis),
                //   (c) the lane to them is at least as open as our
                //       lane to goal.
                // The strike sweep direction below uses the body-rel
                // yaw to whichever target we pick.
                bool isPass = false;
                strikeAimTarget = offGoal;
                // pass_to_teammate behavior toggle: when off, never pass — always
                // take the shot at goal.
                if (engageShot && Behaviors != null && Behaviors.PassToTeammate)
                {
                    bool laneToGoalClear = IsLaneClear(myPos, offGoal, ignoreNid: Player.NetworkObjectId, blockerRadius: 0.8f);
                    if (!laneToGoalClear)
                    {
                        // Try a pass instead. Scan teammates for the
                        // best-positioned one with an open lane to
                        // goal AND an open lane from us to them.
                        if (TryFindPassTarget(myPos, offGoal, out var passTo))
                        {
                            strikeAimTarget = passTo;
                            isPass = true;
                        }
                    }
                }

                ShotState prevShotState = _shotState;
                AdvanceShotState(engageShot);
                if (prevShotState == ShotState.Idle && _shotState == ShotState.WindUp)
                {
                    // Strike-entry geometry snapshot. Lets us see WHERE
                    // the blade actually was relative to the puck the
                    // moment we committed to a shot — independently of
                    // whether the strike succeeded. Compare modeled
                    // blade segment, stick mirror pose, body-local
                    // puck position, and modeled stick angles.
                    Vector3 puckBL = puckBodyLocal;
                    string heelStr = haveBladeSeg ? $"{bladeHeelXZ.x:F2},{bladeHeelXZ.z:F2}" : "n/a";
                    string toeStr  = haveBladeSeg ? $"{bladeToeXZ.x:F2},{bladeToeXZ.z:F2}" : "n/a";
                    string targetTag = isPass ? "PASS" : "SHOT";
                    Debug.Log(
                        $"[BotBrain bot={BotIndex}] STRIKE-ENTRY {targetTag} " +
                        $"aimXZ=({strikeAimTarget.x:F2},{strikeAimTarget.z:F2}) " +
                        $"puckXZ=({puckPos.x:F2},{puckPos.z:F2}) " +
                        $"bladeHeelXZ=({heelStr}) bladeToeXZ=({toeStr}) " +
                        $"segDist={bladePuckDist:F2}m " +
                        $"puckBodyLocal=(fwd={puckBL.z:F2},side={puckBL.x:F2},up={puckBL.y:F2}) " +
                        $"estStickDeg=({_estStickAngleDeg.x:F1},{_estStickAngleDeg.y:F1}) " +
                        $"inputDeg=({_lastStickInputDeg.x:F1},{_lastStickInputDeg.y:F1}) " +
                        $"puckVel={EstimatePuckVelocity().magnitude:F2}m/s");
                }

                // FLICK: during Strike, override the stick yaw input
                // to swing PAST the goal direction. This is what
                // generates blade-tip rotational velocity (the
                // dominant impulse on a wrist shot per replay
                // analysis — body forward speed is only a baseline).
                // WindUp = pull yaw AWAY from goal (small backswing),
                // Strike = swing PAST goal direction. The cradle
                // settle ensures the puck is against the broadside
                // before the flick fires.
                // Aim yaw: goal direction by default, teammate direction
                // when passing. Computed here so the WindUp/Strike
                // sweep below can swing toward whichever target was
                // chosen by the lane-clear / pass-target logic above.
                float aimYawWorld = Mathf.Atan2(
                    (strikeAimTarget - myPos).x, (strikeAimTarget - myPos).z) * Mathf.Rad2Deg;
                // FEEDFORWARD body yaw at strike-impact time. The
                // mirror snapshot is RTT-old; our input also takes
                // ~RTT to land; and the strike window itself spans
                // ShotStrikeTicks. Predict the body yaw at that
                // future moment so the backswing/strike direction
                // signs are picked against where the body actually
                // WILL face, not where it was. With body turn rate
                // up to 78.8°/s, a 100 ms blind spot = 8° error.
                float strikeImpactHorizonSec =
                    RttSeconds()
                    + Scaled(ShotStrikeTicks) * tickDt;
                float predictedBodyYawWorld = ProjectBodyYawWorldDeg(
                    strikeImpactHorizonSec,
                    Mathf.Sign((float)moveX),
                    _slideSent);
                if (_shotState == ShotState.WindUp)
                {
                    // Rotate yaw away from aim by ShotBackswingDeg.
                    // bodyRelAimYaw > 0 ⇒ target is right of fwd ⇒
                    // backswing to the LEFT (negative).
                    float bodyRelAimYaw = Mathf.DeltaAngle(predictedBodyYawWorld, aimYawWorld);
                    float backswingYaw = -Mathf.Sign(bodyRelAimYaw) * ShotBackswingDeg;
                    float backswingClamp = _estStickAngleDeg.y + Mathf.Clamp(
                        Mathf.DeltaAngle(_estStickAngleDeg.y, backswingYaw),
                        -MaxInputDeltaPerTick, MaxInputDeltaPerTick);
                    backswingClamp = Mathf.Clamp(backswingClamp, StickYawMin, StickYawMax);
                    yawClamped = backswingClamp;
                }
                else if (_shotState == ShotState.Strike)
                {
                    // Swing PAST aim direction. Keeps the engine PID
                    // integrator pushing the raycast across the puck
                    // during the strike window — without this push
                    // the blade tracks the puck slowly via the 15°/s
                    // limiter and the BladeAngle flick alone doesn't
                    // produce the strike rate we measured 2026-04-28.
                    float bodyRelAimYaw = Mathf.DeltaAngle(predictedBodyYawWorld, aimYawWorld);
                    float strikeYaw = Mathf.Sign(bodyRelAimYaw) * ShotStrikeDeg;
                    if (Mathf.Abs(bodyRelAimYaw) < 1f) strikeYaw = ShotStrikeDeg; // default right
                    yawClamped = Mathf.Clamp(strikeYaw, StickYawMin, StickYawMax);
                }

                _lastStickInputDeg = new Vector2(pitchClamped, yawClamped);
                stickX = AngleToShort(pitchClamped);
                stickY = AngleToShort(yawClamped);
            }
            else
            {
                // Pre-world fallback: hold forward, sweep look slowly.
                moveX = 0;
                moveY = 32767;
                float sweepYaw = 60f * Mathf.Sin(2f * Mathf.PI * 0.25f * _phase);
                lookX  = AngleToShort(0f);     // pitch
                lookY  = AngleToShort(sweepYaw); // yaw
                stickX = AngleToShort(0f);
                stickY = AngleToShort(sweepYaw * 0.5f);
                _lastStickInputDeg = new Vector2(0f, sweepYaw * 0.5f);
            }

            // Mod-interaction carve bursts (CompetitiveSkating): replicate
            // the modded client's input signature — Slide + both Laterals
            // ON and the forward move component zeroed — for ~1.5s every
            // ~10s of play. Runs on top of whatever the heuristics decided.
            if (CompCarve)
            {
                if (_carving)
                {
                    moveY = 0;                      // forward-only zeroed
                    if (--_carveTicksLeft <= 0)
                    {
                        _carving = false;
                        try { Input.SendLateralLeft(false); Input.SendLateralRight(false); } catch { }
                        _carveCooldownTicks = Scaled(240) + (BotIndex * 17) % Scaled(120);
                    }
                }
                else if (--_carveCooldownTicks <= 0)
                {
                    _carving = true;
                    _carveTicksLeft = Scaled(45);   // ~1.5s at 30Hz-scaled ticks
                    try { Input.SendLateralLeft(true); Input.SendLateralRight(true); } catch { }
                }
            }

            // Honor the IMPLEMENTED playbook behavior toggles. Defaults are
            // all-on (= current behavior), so this is a no-op unless a playbook
            // explicitly turns one off.
            if (Behaviors != null)
            {
                // skate_to_puck off: a skater that shouldn't chase holds position
                // when it doesn't have the puck (goalies keep their net play).
                if (!Behaviors.SkateToPuck && !isCarrier && !goalie) { moveX = 0; moveY = 0; }
                // push_to_opposing_goal off: a carrier stops driving forward.
                if (!Behaviors.PushToOpposingGoal && isCarrier) { moveY = 0; }
                // rotate_stick_to_puck / rotate_head_to_puck off: neutral angle.
                if (!Behaviors.RotateStickToPuck) { stickX = AngleToShort(0f); stickY = AngleToShort(0f); }
                if (!Behaviors.RotateHeadToPuck)  { lookX  = AngleToShort(0f);  lookY  = AngleToShort(0f); }
            }

            Input.SendMove(moveX, moveY);
            Input.SendRaycastOriginAngle(stickX, stickY);
            Input.SendLookAngle(lookX, lookY);

            // Sprint + slide policy via the pulse-turn state machine.
            // Slide and Sprint are RELIABLE bool RPCs — only re-sent
            // on transition. Sprint is incompatible with slide on
            // the server side (PlayerBodyV2.cs:400 / 410: sprint
            // requires `!IsSliding`), so we suppress sprint during
            // PulseSlide.
            bool wantSlide  = false;
            bool wantSprint = false;
            if (haveWorld)
            {
                Vector3 toPuck = puckPos - myPos;
                toPuck.y = 0f;
                float dist = toPuck.magnitude;
                Vector3 fwd = myRot * Vector3.forward;
                fwd.y = 0f;
                float currentYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                float targetYaw = Mathf.Atan2(toPuck.x, toPuck.z) * Mathf.Rad2Deg;
                float absDelta = Mathf.Abs(Mathf.DeltaAngle(currentYaw, targetYaw));

                // Tick the state machine. While carrying the puck (or
                // actively cradling it), suppress PulseSlide entirely:
                // slide flips IsSliding=true and Skate.cs treats it as
                // a hard turn that kills forward momentum, leaving the
                // puck behind. Force Normal state so the body turns
                // only via proportional moveX (which preserves skate
                // momentum and lets the puck stay on the broadside).
                bool puckOnStick = _carrying || inCradle;
                if (puckOnStick && _turnState != TurnState.Normal)
                {
                    _turnState = TurnState.Normal;
                    _turnStateTicksLeft = 0;
                }
                if (puckOnStick)
                {
                    // Don't even tick the FSM toward PulseSlide while
                    // carrying — gentle proportional turn only.
                    wantSlide = false;
                }
                else
                {
                    AdvanceTurnState(absDelta);
                    wantSlide = (_turnState == TurnState.PulseSlide);
                }

                // Sprint policy: must NOT be sliding (server gate),
                // target must be far enough to make sprint worthwhile,
                // bot must be roughly aimed, and stamina must support
                // it (hysteresis around the server's 0.25 start gate).
                float stamina = ReadStamina();
                bool aimedRoughly = absDelta < 30f;
                bool farEnough    = dist > SprintMinTargetDistance;
                bool notSliding   = !wantSlide;
                if (_sprintSent)
                    wantSprint = notSliding && stamina > SprintStopStamina && farEnough;
                else
                    wantSprint = notSliding && stamina > SprintStartStamina && aimedRoughly && farEnough;
            }
            else
            {
                _turnState = TurnState.Normal;
                _turnStateTicksLeft = 0;
                _shotState = ShotState.Idle;
                _shotTicksLeft = 0;
            }

            // Carve overrides the slide policy: the server's carve check is
            // Slide && LateralLeft && LateralRight && grounded.
            if (_carving) wantSlide = true;

            if (wantSlide != _slideSent)
            {
                Input.SendSlide(wantSlide);
                _slideSent = wantSlide;
            }
            if (wantSprint != _sprintSent)
            {
                Input.SendSprint(wantSprint);
                _sprintSent = wantSprint;
            }

            // Blade angle: this is the wrist-shot mechanic for
            // stationary shots. Stick.cs:155-156 sets the blade's
            // localRotation around its shaft axis directly from
            // BladeAngleInput * 12.5° per step — NO PID slew. So a
            // step from -3 to +3 in one frame rotates the blade
            // face 75° instantly, dragging it across whatever it's
            // touching. That's the impulse on a stationary puck.
            //
            // WindUp: cock the blade angle BACK (away from goal).
            // Strike: snap it forward. Idle/Cooldown/no-shot: 0.
            sbyte wantBlade = 0;
            if ((_shotState == ShotState.WindUp || _shotState == ShotState.Strike)
                && haveWorld && isCarrier && strikeAimTarget != Vector3.zero)
            {
                // BladeAngle flick direction matches the chosen
                // strikeAimTarget — goal for shots, teammate for
                // passes. Same wrist-shot mechanic; only the target
                // direction differs.
                Vector3 aimDir = strikeAimTarget - myPos;
                // FEEDFORWARD: pick the flick sign against the body
                // yaw the server will actually have when the flick
                // lands, not the yaw the snapshot reported. Same
                // horizon as the raycast-yaw prediction above
                // (RttSeconds() + strike-window).
                float bladeImpactHorizonSec =
                    RttSeconds()
                    + Scaled(ShotStrikeTicks) * tickDt;
                float bladeBodyYawWorld = ProjectBodyYawWorldDeg(
                    bladeImpactHorizonSec,
                    Mathf.Sign((float)moveX),
                    _slideSent);
                float bodyRelAimYaw = Mathf.DeltaAngle(
                    bladeBodyYawWorld,
                    Mathf.Atan2(aimDir.x, aimDir.z) * Mathf.Rad2Deg);
                sbyte forwardSign = (sbyte)(bodyRelAimYaw >= 0f ? 1 : -1);
                wantBlade = (sbyte)(_shotState == ShotState.WindUp
                    ? -forwardSign * BladeAngleStrike  // cocked
                    :  forwardSign * BladeAngleStrike); // released
            }
            else if (inCradle && haveWorld && isCarrier)
            {
                // Cradle cup. Real players use the scroll wheel
                // (BladeAngleUp/Down → BladeAngleInput, PlayerInput.cs:
                // 341-353) to close the blade face onto the puck.
                // Stick.cs:155 applies BladeAngleInput * 12.5° around
                // Vector3.forward — the blade's local forward, which
                // tilts the face open/closed. A small persistent tilt
                // toward the body cups the puck against the broadside
                // and keeps it from sliding off; without this the
                // blade is straight-faced and pucks bounce out.
                //
                // Sign by handedness: right-handed stick is to the
                // right of body, so the blade's local forward axis
                // points outward (right). Closing the face toward the
                // body means rotating the top of the blade leftward,
                // which is NEGATIVE angle (CCW around local forward
                // when viewed from handle to tip). Mirror for left-
                // handed.
                sbyte cupSign = -1;
                try { if (Player.Handedness.Value == PlayerHandedness.Left) cupSign = +1; } catch { }
                wantBlade = (sbyte)(cupSign * BladeAngleCradleCup);
            }
            // Flail-mode blade chaos: when the puck has been stagnant
            // nearby for a few seconds, override wantBlade with a
            // rapid oscillation that whips the face across the puck.
            // Each bot flips on a tick interval offset by its
            // BotIndex so multiple flailing bots don't all flip in
            // phase. Bypasses the strike/cradle gates above.
            if (_isFlailingThisTick)
            {
                int phase = ((int)(Time.realtimeSinceStartup * 12f) + BotIndex) & 1;
                wantBlade = (sbyte)(phase == 0 ? +BladeAngleStrike : -BladeAngleStrike);
            }

            if (wantBlade != _bladeAngleSent)
            {
                Input.SendBladeAngle(wantBlade);
                _bladeAngleSent = wantBlade;
            }

            if (haveWorld) _everSawWorldPosition = true;

            // Strike outcome: a few ticks after a strike fires, sample
            // puck velocity again. If the strike actually transferred
            // momentum, _puckSpeedAfterStrike >> _puckSpeedAtStrike.
            // Distance the puck travelled in that window is also a
            // good signal (especially for stationary pucks).
            if (_strikeOutcomeTicksLeft > 0)
            {
                // Sample puck velocity each tick during the window
                // and keep the MAX. Single-sample-at-end misses the
                // peak (puck typically hits another bot or boards
                // within 100ms post-strike, killing velocity to 0
                // before our 200ms-post readout). Peak is the actual
                // impulse signal — replay baseline median is 4.34
                // m/s, p90 8.69 m/s.
                float vNow = EstimatePuckVelocity().magnitude;
                if (vNow > _puckPeakSpeedThisStrike) _puckPeakSpeedThisStrike = vNow;
                _strikeOutcomeTicksLeft--;
                if (_strikeOutcomeTicksLeft == 0 && isCarrier)
                {
                    _puckSpeedAfterStrike = vNow;
                    float puckMoved = (puckPos - _strikePuckPos).magnitude;
                    Debug.Log(
                        $"[BotBrain bot={BotIndex}] STRIKE outcome: " +
                        $"puckSpeed before={_puckSpeedAtStrike:F2} peak={_puckPeakSpeedThisStrike:F2} end={_puckSpeedAfterStrike:F2} m/s, " +
                        $"puckMoved={puckMoved:F2}m");
                    // Chat trigger: speed jumped by ≥5 m/s = the bot
                    // actually transferred momentum.
                    if (_puckPeakSpeedThisStrike >= _puckSpeedAtStrike + 5f)
                        TryChat(ChatTrigger.SuccessfulStrike);
                }
            }

            if (--_logTickCounter <= 0)
            {
                _logTickCounter = Scaled(300); // ~10 s
                int snapEntries = MirrorSynchronizedObjectManager.LatestPositions.Count;
                if (haveWorld)
                {
                    string role = goalie ? "Goalie" : (isCarrier ? "Carrier" : "Support");
                    string minStr = _minBladePuckDistThisPeriod < float.MaxValue
                        ? _minBladePuckDistThisPeriod.ToString("F2")
                        : "n/a";
                    Debug.Log(
                        $"[BotBrain bot={BotIndex}] {role}: myPos={myPos} → tgt={puckPos} " +
                        $"carry={_carrying} shot={_shotState} " +
                        $"minBladePuckDist={minStr}m strikes={_shotsAttemptedThisPeriod} " +
                        $"maxCradleTicks={_maxCradleTicksThisPeriod} " +
                        $"stuckEpisodes={_stuckEpisodesThisPeriod} stuck={_isStuck} " +
                        $"slewMaxErr={_maxSlewErrThisPeriod:F1}deg " +
                        $"(snapshotEntries={snapEntries})");
                    _minBladePuckDistThisPeriod = float.MaxValue;
                    _shotsAttemptedThisPeriod = 0;
                    _maxSlewErrThisPeriod = 0f;
                    _maxCradleTicksThisPeriod = 0;
                    _stuckEpisodesThisPeriod = 0;
                }
                else if (!_everSawWorldPosition)
                    Debug.Log($"[BotBrain bot={BotIndex}] no world position yet — sweeping (snapshotEntries={snapEntries}, myBodyNid={_myBodyNid})");
            }

            // M1 ML data emission — runs alongside the heuristic brain,
            // captures (obs, action) per tick. Reward stays NaN until M2.
            try { Snapshot?.EmitTick(); }
            catch (System.Exception ex) { Debug.LogError($"[BotBrain bot={BotIndex}] SnapshotLogger.EmitTick failed: {ex.Message}"); }
        }

        // Walk all spawned PlayerBodyV2 mirrors; return the NID whose
        // PlayerReference points back to our own MirrorPlayer's NID.
        private ulong FindMyBodyNid()
        {
            if (Player == null || Player.NetworkManager?.SpawnManager?.SpawnedObjectsList == null)
                return 0;
            ulong myPlayerNid = Player.NetworkObjectId;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var body = no.GetComponent<MirrorPlayerBodyV2>();
                if (body == null) continue;
                try
                {
                    if (body.PlayerReference.Value.NetworkObjectId == myPlayerNid)
                        return no.NetworkObjectId;
                }
                catch { }
            }
            return 0;
        }

        // Pulse-turn state machine. Called once per BotBrain.Tick.
        //   Normal       → small turns via moveX. If |delta| >= 45°,
        //                  enter PulseSlide.
        //   PulseSlide   → slide=true for ~165 ms, body rotates fast.
        //                  Then enter PulseRecover.
        //   PulseRecover → slide=false for ~200 ms; body skates
        //                  forward to recover momentum lost during
        //                  the slide. After recovery, if |delta|
        //                  still >= 15°, back to PulseSlide; else
        //                  Normal.
        // The exit threshold (15°) is well inside Normal's natural
        // moveX-steering envelope so we don't oscillate.
        private void AdvanceTurnState(float absDelta)
        {
            switch (_turnState)
            {
                case TurnState.Normal:
                    if (absDelta >= PulseEnterAngle)
                    {
                        _turnState = TurnState.PulseSlide;
                        _turnStateTicksLeft = Scaled(PulseSlideTicks);
                    }
                    break;
                case TurnState.PulseSlide:
                    if (--_turnStateTicksLeft <= 0)
                    {
                        _turnState = TurnState.PulseRecover;
                        _turnStateTicksLeft = Scaled(PulseRecoverTicks);
                    }
                    break;
                case TurnState.PulseRecover:
                    if (--_turnStateTicksLeft <= 0)
                    {
                        if (absDelta >= PulseExitAngle)
                        {
                            _turnState = TurnState.PulseSlide;
                            _turnStateTicksLeft = Scaled(PulseSlideTicks);
                        }
                        else
                        {
                            _turnState = TurnState.Normal;
                        }
                    }
                    break;
            }
        }

        // Shot trigger state machine. Called once per Tick from the
        // haveWorld branch with `engage` = true when carrying + in
        // range + aimed at goal + blade close enough to puck.
        //   Idle     → WindUp on engage.
        //   WindUp   → Strike after ShotWindUpTicks regardless of
        //              engage (don't abort mid-backswing — looks
        //              spastic and never actually shoots).
        //   Strike   → Cooldown after ShotStrikeTicks.
        //   Cooldown → Idle after ShotCooldownTicks.
        private void AdvanceShotState(bool engage)
        {
            switch (_shotState)
            {
                case ShotState.Idle:
                    if (engage)
                    {
                        // WindUp first (cocks the blade angle back).
                        // Then Strike snaps it forward — the angle
                        // change rotates the blade face around the
                        // shaft axis, sweeping it through the puck.
                        // Stick.cs:155 sets blade rotation directly
                        // (no PID slew), so the snap happens in a
                        // single physics tick.
                        _shotState = ShotState.WindUp;
                        _shotTicksLeft = Scaled(ShotWindUpTicks);
                        _shotsAttemptedThisPeriod++;
                        _puckSpeedAtStrike = EstimatePuckVelocity().magnitude;
                        _puckPeakSpeedThisStrike = _puckSpeedAtStrike;
                        _strikePuckPos     = _lastPuckPos;
                        _strikeOutcomeTicksLeft = Scaled(ShotWindUpTicks + ShotStrikeTicks + 4);
                    }
                    break;
                case ShotState.WindUp:
                    if (--_shotTicksLeft <= 0)
                    {
                        _shotState = ShotState.Strike;
                        _shotTicksLeft = Scaled(ShotStrikeTicks);
                    }
                    break;
                case ShotState.Strike:
                    if (--_shotTicksLeft <= 0)
                    {
                        _shotState = ShotState.Cooldown;
                        _shotTicksLeft = Scaled(ShotCooldownTicks);
                    }
                    break;
                case ShotState.Cooldown:
                    if (--_shotTicksLeft <= 0)
                        _shotState = ShotState.Idle;
                    break;
            }
        }

        // Resolve our Stick's NetworkObjectId by matching its
        // PlayerReference NV back to our MirrorPlayer. Mirrors what
        // FindMyBodyNid does for the body. Returns 0 if not found
        // yet (early in connection lifecycle).
        private ulong FindMyStickNid()
        {
            if (Player == null || Player.NetworkManager?.SpawnManager?.SpawnedObjectsList == null)
                return 0;
            ulong myPlayerNid = Player.NetworkObjectId;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var stick = no.GetComponent<MirrorStick>();
                if (stick == null) continue;
                try
                {
                    if (stick.PlayerReference.Value.NetworkObjectId == myPlayerNid)
                        return no.NetworkObjectId;
                }
                catch { }
            }
            return 0;
        }

        // Resolve our PlayerBodyV2 mirror (cached). Same lookup
        // strategy as FindMyBodyNid but returns the component.
        private MirrorPlayerBodyV2 FindMyBody()
        {
            if (Player == null || Player.NetworkManager?.SpawnManager?.SpawnedObjectsList == null)
                return null;
            ulong myPlayerNid = Player.NetworkObjectId;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var body = no.GetComponent<MirrorPlayerBodyV2>();
                if (body == null) continue;
                try
                {
                    if (body.PlayerReference.Value.NetworkObjectId == myPlayerNid)
                        return body;
                }
                catch { }
            }
            return null;
        }

        // Puck velocity from successive snapshots. Reset whenever
        // we lose the puck or change assigned puck. Fall back to
        // zero when we don't have history yet.
        private Vector3 EstimatePuckVelocity()
        {
            return _havePuckHistory ? _lastPuckVelocity : Vector3.zero;
        }

        // Effective input-actuation latency to use as the puck-lead
        // horizon. We deliberately do NOT read Player.Ping — see
        // ProcessingLatencySeconds for why that NV is unusable.
        private float RttSeconds() => ProcessingLatencySeconds;

        // Stamina: PlayerBodyV2.cs:21 — Stamina = StaminaCompressed / 16383f.
        // Range 0..1. Default to 1f if mirror not yet bound so the bot
        // doesn't stutter at startup.
        private float ReadStamina()
        {
            if (_myBody == null) return 1f;
            try { return _myBody.StaminaCompressed.Value / 255f; }
            catch { return 1f; }
        }

        private bool TryGetMyXform(out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            if (_myBodyNid == 0) return false;
            if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(_myBodyNid, out var x))
                return false;
            pos = x.Position;
            rot = x.Rotation;
            return true;
        }

        // Bot's own stick world transform. Snapshot updates rotation
        // too, so carry-pose can know which way the blade is facing.
        public bool TryGetMyStickXform(out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            if (_myStickNid == 0) return false;
            if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(_myStickNid, out var x))
                return false;
            pos = x.Position;
            rot = x.Rotation;
            return true;
        }

        // Iterate spawned MirrorPuck NetworkBehaviours; pick the one
        // with the smallest squared distance to `myPos` for which we
        // have a recent snapshot entry.
        private bool TryGetNearestPuck(Vector3 myPos, out Vector3 puckPos)
        {
            puckPos = Vector3.zero;
            float bestDistSq = float.MaxValue;
            bool found = false;
            if (Player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return false;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                if (no.GetComponent<MirrorPuck>() == null) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var x))
                    continue;
                float d2 = (x.Position - myPos).sqrMagnitude;
                if (d2 < bestDistSq)
                {
                    bestDistSq = d2;
                    puckPos = x.Position;
                    found = true;
                }
            }
            return found;
        }

        // Possession-aware puck assignment. For each puck, the closest
        // teammate (including me) "claims" it. Of pucks I claim, pick
        // the one closest to me. Returns false if no puck is claimed
        // by me — caller should treat as Support role.
        //
        // All inputs come from existing chokepoints: positions from
        // MirrorSynchronizedObjectManager.LatestPositions, team from
        // MirrorPlayer.Team NV. No new wire surface. ML brain reading
        // the same state can derive the same assignment.
        //
        // Tie-break by NetworkObjectId so two teammates with identical
        // distances don't both claim the same puck.
        // How many teammates per puck are allowed to chase. 1 = nearest
        // only (old strict behavior); 2+ lets supporting bots also press
        // the puck, eliminating the "one Goalie+one carrier, everyone
        // else idle" sparseness on a 12-bot rink. With 6 teammates per
        // team and CarrierTopK=2, at most 2 chase per puck per team.
        private const int CarrierTopK = 2;

        private bool TryGetAssignedPuck(Vector3 myPos, out Vector3 puckPos)
        {
            puckPos = Vector3.zero;
            if (Player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return false;
            PuckStressTest.Mirror.PlayerTeam myTeam;
            try { myTeam = Player.Team; } catch { return false; }
            if (myTeam != PuckStressTest.Mirror.PlayerTeam.Red &&
                myTeam != PuckStressTest.Mirror.PlayerTeam.Blue) return false;

            ulong myNid = Player.NetworkObjectId;
            float bestMineSq = float.MaxValue;
            bool foundMine = false;

            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                if (no.GetComponent<MirrorPuck>() == null) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var px))
                    continue;

                // Count teammates strictly CLOSER to this puck than I am.
                // If fewer than CarrierTopK are closer, I'm in the top-K
                // and may chase. Ties broken by smaller NetworkObjectId
                // (deterministic; matches the earlier strict-min rule
                // when CarrierTopK = 1).
                float myDistSq = (px.Position - myPos).sqrMagnitude;
                int closerCount = 0;

                foreach (var no2 in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
                {
                    if (no2 == null) continue;
                    var teammateBody = no2.GetComponent<MirrorPlayerBodyV2>();
                    if (teammateBody == null) continue;

                    ulong teammatePlayerNid;
                    try { teammatePlayerNid = teammateBody.PlayerReference.Value.NetworkObjectId; }
                    catch { continue; }
                    if (teammatePlayerNid == myNid) continue;

                    if (!Player.NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(teammatePlayerNid, out var teammatePlayerNo))
                        continue;
                    var teammatePlayer = teammatePlayerNo.GetComponent<MirrorPlayer>();
                    if (teammatePlayer == null) continue;
                    PuckStressTest.Mirror.PlayerTeam teammateTeam;
                    try { teammateTeam = teammatePlayer.Team; } catch { continue; }
                    if (teammateTeam != myTeam) continue;
                    // Exclude goalies — they have their own positioning
                    // and shouldn't compete for the carrier slot. Lets
                    // CarrierTopK reflect actual chasing skaters.
                    PuckStressTest.Mirror.PlayerRole teammateRole;
                    try { teammateRole = teammatePlayer.Role; } catch { continue; }
                    if (teammateRole == PuckStressTest.Mirror.PlayerRole.Goalie) continue;

                    if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no2.NetworkObjectId, out var tx))
                        continue;
                    float tDistSq = (px.Position - tx.Position).sqrMagnitude;
                    if (tDistSq < myDistSq ||
                        (tDistSq == myDistSq && no2.NetworkObjectId < no.NetworkObjectId))
                    {
                        closerCount++;
                        if (closerCount >= CarrierTopK) break; // early-exit; can't be top-K
                    }
                }

                if (closerCount < CarrierTopK && myDistSq < bestMineSq)
                {
                    bestMineSq = myDistSq;
                    puckPos = px.Position;
                    foundMine = true;
                }
            }
            return foundMine;
        }

        private const float SupportArrivedRange = 1.5f;

        // Offside safety buffer. Per the offside zone geometry, the
        // opposing blue line is at z = ±13.07/13.43. Stay 0.5 m back
        // from that on our side so player radius (~0.26) doesn't
        // straddle the line.
        private const float OffsideBufferZ = 12.5f;

        // Hockey position roles. Each PlayerPosition NetworkObject on
        // the rink corresponds to one of these by GlobalObjectIdHash;
        // mapping captured via ConfigCaptureMod [NB-LAYOUT] dump.
        private enum PositionRole { Unknown, C, LW, RW, LD, RD, G }

        // Per-position lateral home offset (x, in metres). Forwards
        // spread wide for puck support across the offensive blue
        // line; defensemen sit narrower so they cover the slot
        // when transitioning to defence.
        // Standard hockey shape (mirrored by team in code below).
        private static readonly System.Collections.Generic.Dictionary<uint, PositionRole> s_HashToRole =
            new System.Collections.Generic.Dictionary<uint, PositionRole>
            {
                // B323 hashes — captured 2026-05-13 from ConfigCaptureMod
                // [NB-LAYOUT] dump (srv_nbdump.log, see project_b323_cutover
                // memory). Order within each role pair is one Blue + one
                // Red; the role mapping is identical for both so we don't
                // need to disambiguate at lookup time.
                //
                // Symptom of stale (B202) hashes here: MyPositionRole()
                // returns Unknown for every bot → s_RoleHomeZ defaults
                // → all non-carriers cluster at the same xz point.
                { 1032755835u, PositionRole.C  },
                {  545915040u, PositionRole.C  },
                { 1131824149u, PositionRole.LW },
                {  684054533u, PositionRole.LW },
                { 1799701982u, PositionRole.RW },
                { 3600366787u, PositionRole.RW },
                { 1628817490u, PositionRole.LD },
                { 2244056922u, PositionRole.LD },
                { 3563399594u, PositionRole.RD },
                { 3964409804u, PositionRole.RD },
                { 1383825887u, PositionRole.G  },
                { 3319866970u, PositionRole.G  },
            };

        // Resolve our PlayerPosition role by walking spawned mirrors
        // for the one whose NID matches Player.PlayerPositionReference,
        // then look up its prefab GlobalObjectIdHash. Returns Unknown
        // if we haven't claimed a position yet.
        // True if no body (player or goalie) sits within blockerRadius
        // metres of the line segment from `from` to `to` (XZ plane).
        // Used to decide whether to shoot at goal vs pass to a
        // teammate — if any opponent is in the lane, treat it as
        // blocked. We ignore our own body via ignoreNid (passed in as
        // our Player NID) and skip the target itself when the target
        // is at a teammate's body.
        private bool IsLaneClear(Vector3 from, Vector3 to, ulong ignoreNid, float blockerRadius)
        {
            if (Player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return true;
            Vector3 a = new Vector3(from.x, 0f, from.z);
            Vector3 b = new Vector3(to.x,   0f, to.z);
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var body = no.GetComponent<MirrorPlayerBodyV2>();
                if (body == null) continue;
                ulong owningPlayerNid;
                try { owningPlayerNid = body.PlayerReference.Value.NetworkObjectId; } catch { continue; }
                if (owningPlayerNid == ignoreNid) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var bx))
                    continue;
                Vector3 p = new Vector3(bx.Position.x, 0f, bx.Position.z);
                float d = ClosestDistancePointToSegmentXZ(p, a, b);
                if (d < blockerRadius) return false;
            }
            return true;
        }

        // Look for a teammate that is (a) closer to offGoal than us,
        // (b) within reasonable pass distance, (c) has an open lane
        // from us to them, AND (d) has an open lane to goal.
        // Returns true with the teammate body's world position via
        // out param when found. Pick the highest-scoring candidate
        // (closer to goal = better) so the puck moves toward the
        // strongest scoring chance.
        private bool TryFindPassTarget(Vector3 myPos, Vector3 offGoal, out Vector3 passTo)
        {
            passTo = Vector3.zero;
            if (Player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return false;
            PuckStressTest.Mirror.PlayerTeam myTeam;
            try { myTeam = Player.Team; } catch { return false; }
            if (myTeam != PuckStressTest.Mirror.PlayerTeam.Red &&
                myTeam != PuckStressTest.Mirror.PlayerTeam.Blue) return false;
            ulong myPlayerNid = Player.NetworkObjectId;
            float myDistToGoalSq = (offGoal - myPos).sqrMagnitude;

            const float MinPassDist  = 3.0f;
            // MaxPassDist is a personality field (PassReachM) set by
            // EnsurePersonality. Local var alias keeps the use sites
            // below readable.
            float MaxPassDist  = PassReachM;
            const float PassBlockerR = 0.8f;
            float bestScore = 0f;
            Vector3 bestPos = Vector3.zero;
            bool found = false;

            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var body = no.GetComponent<MirrorPlayerBodyV2>();
                if (body == null) continue;
                ulong tmPlayerNid;
                try { tmPlayerNid = body.PlayerReference.Value.NetworkObjectId; } catch { continue; }
                if (tmPlayerNid == myPlayerNid) continue;
                if (!Player.NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(tmPlayerNid, out var tmPlayerNo)) continue;
                var tmPlayer = tmPlayerNo.GetComponent<MirrorPlayer>();
                if (tmPlayer == null) continue;
                PuckStressTest.Mirror.PlayerTeam tmTeam;
                try { tmTeam = tmPlayer.Team; } catch { continue; }
                if (tmTeam != myTeam) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var tmBx)) continue;

                Vector3 tmPos = tmBx.Position;
                float passDist = Vector3.Distance(myPos, tmPos);
                if (passDist < MinPassDist || passDist > MaxPassDist) continue;
                float tmDistToGoalSq = (offGoal - tmPos).sqrMagnitude;
                if (tmDistToGoalSq >= myDistToGoalSq) continue;  // not closer

                // Lane from us to teammate must be clear (ignore self
                // and the teammate itself).
                if (!IsLaneClear(myPos, tmPos, myPlayerNid, PassBlockerR)) continue;
                // Teammate's lane to goal must be clearer than ours.
                bool tmLaneToGoal = IsLaneClear(tmPos, offGoal, tmPlayerNid, PassBlockerR);
                if (!tmLaneToGoal) continue;

                // Score: prefer teammate closer to goal (smaller
                // distance) and shorter pass.
                float score = (myDistToGoalSq - tmDistToGoalSq) - 0.05f * passDist;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos   = tmPos;
                    found     = true;
                }
            }

            if (found) passTo = bestPos;
            return found;
        }

        private PositionRole MyPositionRole()
        {
            if (Player == null) return PositionRole.Unknown;
            ulong ppNid;
            try { ppNid = Player.PlayerPositionReference.Value.NetworkObjectId; } catch { return PositionRole.Unknown; }
            if (ppNid == 0) return PositionRole.Unknown;
            if (Player.NetworkManager?.SpawnManager?.SpawnedObjects == null) return PositionRole.Unknown;
            if (!Player.NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(ppNid, out var no) || no == null)
                return PositionRole.Unknown;
            uint hash = 0;
            try
            {
                var f = typeof(NetworkObject).GetField("GlobalObjectIdHash",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null) hash = (uint)f.GetValue(no);
            }
            catch { }
            return s_HashToRole.TryGetValue(hash, out var role) ? role : PositionRole.Unknown;
        }

        // Role-aware support target. The shape moves dynamically with
        // the puck zone:
        //   - Defensive zone (puck deep on our side): D tight at our
        //     net, F at top of zone for breakout outlets.
        //   - Neutral zone: D back at our blue line, F midway between
        //     blue lines spread laterally.
        //   - Offensive zone (puck deep in their end): D at their blue
        //     line as point, F low for net-front + below circles.
        // All positions are mirrored by team (Red defends -z, Blue
        // defends +z). Offside-safe: F can't precede the puck across
        // their blue line.
        private Vector3 SupportRestPosition()
        {
            Vector3 ownGoal = DefensiveGoal();
            if (ownGoal == Vector3.zero) return Vector3.zero;
            float ownSign = Mathf.Sign(ownGoal.z);  // +1 for Blue, -1 for Red

            // Default conservative spot if we don't have puck or role
            // resolved yet — sit halfway in our defensive half.
            Vector3 myPos = Vector3.zero;
            Vector3 puck  = Vector3.zero;
            bool haveMe = TryGetMyXform(out myPos, out _);
            bool havePuck = haveMe && TryGetNearestPuck(myPos, out puck);
            if (!havePuck)
                return new Vector3(0f, 0f, ownGoal.z * 0.5f);

            PositionRole role = MyPositionRole();

            // Zone classification by puck z relative to OUR side.
            // ownSign>0 (Blue): defensive z>OffsideBufferZ, neutral
            // -OffsideBufferZ..+OffsideBufferZ, offensive z<-OffsideBufferZ.
            float puckOnOurSide = puck.z * ownSign;  // > 0 means in our half
            bool puckDefensive  = puckOnOurSide >  OffsideBufferZ;
            bool puckOffensive  = puckOnOurSide < -OffsideBufferZ;

            // Lateral home (x): forwards spread wide, D narrow.
            // C=0, LW=-, RW=+, LD=-, RD=+. Matches standard hockey.
            float laneX;
            switch (role)
            {
                case PositionRole.LW: laneX = -7f;  break;
                case PositionRole.RW: laneX = +7f;  break;
                case PositionRole.C:  laneX =  0f;  break;
                case PositionRole.LD: laneX = -5f;  break;
                case PositionRole.RD: laneX = +5f;  break;
                case PositionRole.G:  return GoalieTarget(true, puck);
                default:              laneX =  0f;  break;
            }

            // Z by role + zone. ownSign mirrors so positive numbers
            // are always toward OUR goal, negative toward THEIR goal.
            // Then multiply by ownSign at the end to flip per-team.
            float zRel;  // distance toward our goal from centre
            bool isDefenseman = (role == PositionRole.LD || role == PositionRole.RD);

            if (puckDefensive)
            {
                // Puck deep in our zone. D tight near the net, F at
                // top of zone for breakout outlet.
                zRel = isDefenseman ? 32f : 18f;
            }
            else if (puckOffensive)
            {
                // Puck in offensive zone. D at the point near their
                // blue line, F low for net-front / below-circles.
                // Express as distance toward our goal (flip sign so
                // moving toward their goal means smaller zRel).
                zRel = isDefenseman ? -12f : -28f;
            }
            else
            {
                // Neutral zone. F midway between blue lines, slightly
                // ahead of puck on the puck→theirGoal axis. D back at
                // our blue line.
                zRel = isDefenseman ? 13f : -8f;
            }

            // Forwards add a pucj-tracking trail offset on the
            // lateral axis so they don't all stack on the home column
            // when the puck drifts cross-ice. Limit tracking to
            // ±4m so we keep our lane structure.
            if (!isDefenseman)
            {
                float puckLateralPull = Mathf.Clamp(puck.x - laneX, -4f, 4f) * 0.5f;
                laneX += puckLateralPull;
            }

            float worldZ = zRel * ownSign;

            // Offside safety: forwards must not cross the opponent
            // blue line before the puck. If the puck hasn't entered
            // their zone yet, clamp our z to our side of the blue
            // line.
            if (!puckOffensive && !isDefenseman)
            {
                if (ownSign > 0f) worldZ = Mathf.Max(worldZ, -OffsideBufferZ);
                else              worldZ = Mathf.Min(worldZ,  OffsideBufferZ);
            }

            return new Vector3(laneX, 0f, worldZ);
        }

        private int ComputeTeamRank()
        {
            if (Player == null) return 0;
            if (Player.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return 0;
            PuckStressTest.Mirror.PlayerTeam myTeam;
            try { myTeam = Player.Team; } catch { return 0; }
            if (myTeam != PuckStressTest.Mirror.PlayerTeam.Red &&
                myTeam != PuckStressTest.Mirror.PlayerTeam.Blue) return 0;

            ulong myNid = Player.NetworkObjectId;
            int rank = 0;
            foreach (var no in Player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var p = no.GetComponent<MirrorPlayer>();
                if (p == null) continue;
                if (no.NetworkObjectId == myNid) continue;
                try { if (p.Team != myTeam) continue; } catch { continue; }
                if (no.NetworkObjectId < myNid) rank++;
            }
            return rank;
        }

        // Closest distance from point p to the line segment (a,b),
        // all assumed to lie in the XZ plane (Y is ignored). Used for
        // blade-segment-vs-puck contact tests. The blade's broadside
        // is a segment, not a point — distance to the toe alone misses
        // the case where the puck is alongside the broad face.
        private static float ClosestDistancePointToSegmentXZ(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = new Vector3(b.x - a.x, 0f, b.z - a.z);
            Vector3 ap = new Vector3(p.x - a.x, 0f, p.z - a.z);
            float ab2 = ab.x * ab.x + ab.z * ab.z;
            if (ab2 < 1e-6f) return Mathf.Sqrt(ap.x * ap.x + ap.z * ap.z);
            float t = (ap.x * ab.x + ap.z * ab.z) / ab2;
            t = Mathf.Clamp01(t);
            float dx = (a.x + ab.x * t) - p.x;
            float dz = (a.z + ab.z * t) - p.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Solve for the (pitch, yaw) input angles that aim the stick
        // raycast at `aimPoint` from `originXyz`. Mirrors the geometry
        // in StickPositioner.ShootRaycast: ray fires from raycastOrigin
        // along its forward and intersects ice at the blade target,
        // capped at maximumReach=2.5m. Pitch is the elevation below
        // horizontal; yaw is body-relative side angle. Output is
        // clamped to the engine's input range.
        private static void ComputeStickAimDeg(
            Vector3 originXyz, Vector3 aimPoint,
            Vector3 bodyFwd, Vector3 bodyRight,
            out float pitchDeg, out float yawDeg)
        {
            Vector3 toAimXZ = new Vector3(aimPoint.x - originXyz.x, 0f, aimPoint.z - originXyz.z);
            float horizDist = toAimXZ.magnitude;

            // Default: aim at ICE level (y=0). Puck radius is ~0.08;
            // anything above ~0.30 means the puck is clearly airborne
            // (deflection / hop) — only then lift the blade.
            float aimY = (aimPoint.y > 0.30f) ? aimPoint.y : 0f;
            float vDrop = Mathf.Max(0.05f, originXyz.y - aimY);

            // Constraint: ray must reach ice within maximumReach=2.5m.
            // sqrt(vDrop² + horizDist²) ≤ 2.5 → clamp horiz.
            const float MaximumReach = 2.5f;
            float maxHoriz = Mathf.Sqrt(Mathf.Max(0.0001f,
                MaximumReach * MaximumReach - vDrop * vDrop));
            float horizClamped = Mathf.Min(horizDist, maxHoriz);
            pitchDeg = Mathf.Atan2(vDrop, Mathf.Max(horizClamped, 0.05f)) * Mathf.Rad2Deg;
            pitchDeg = Mathf.Clamp(pitchDeg, StickPitchMin, StickPitchMax);

            float fwdComp  = Vector3.Dot(toAimXZ, bodyFwd);
            float sideComp = Vector3.Dot(toAimXZ, bodyRight);
            yawDeg = Mathf.Atan2(sideComp, Mathf.Max(fwdComp, 0.1f)) * Mathf.Rad2Deg;
            yawDeg = Mathf.Clamp(yawDeg, StickYawMin, StickYawMax);
        }

        // Step the modelled stick-angle one tick toward the last input
        // we sent. Matches the engine's PID with p=0.75, output clamped
        // to ±15 deg/s. Integral term is ignored — for the transient
        // case (input changes faster than steady-state error builds)
        // proportional + clamp captures the slew rate that matters.
        private void IntegrateStickSlew(float dt)
        {
            float errPitch = _lastStickInputDeg.x - _estStickAngleDeg.x;
            float errYaw   = _lastStickInputDeg.y - _estStickAngleDeg.y;
            float vPitch = Mathf.Clamp(errPitch * StickPidProportional,
                                       -StickSlewMaxDegPerSec, StickSlewMaxDegPerSec);
            float vYaw   = Mathf.Clamp(errYaw   * StickPidProportional,
                                       -StickSlewMaxDegPerSec, StickSlewMaxDegPerSec);
            _estStickAngleDeg.x += vPitch * dt;
            _estStickAngleDeg.y += vYaw   * dt;
            float maxErr = Mathf.Max(Mathf.Abs(errPitch), Mathf.Abs(errYaw));
            if (maxErr > _maxSlewErrThisPeriod) _maxSlewErrThisPeriod = maxErr;
        }

        // Reseed the body-yaw model from the mirror snapshot. The
        // mirror has the authoritative server yaw from ~RTT/2 ms ago;
        // we treat it as ground truth and overwrite the local model.
        // Yaw rate is derived from the snapshot delta (a quasi-tick
        // apart) so we capture mid-turn momentum the engine has
        // already accumulated server-side.
        private void ReseedBodyYawFromMirror(float tickDt, Quaternion myRot, bool haveMe, bool isSliding)
        {
            if (!haveMe)
            {
                _haveMeasuredBodyYaw = false;
                return;
            }
            Vector3 fwd = myRot * Vector3.forward;
            float measuredYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            float multiplier  = isSliding ? 2.0f : 1.0f;
            float maxRate     = BodyTurnMaxRateDegPerSec * multiplier;
            if (_haveMeasuredBodyYaw)
            {
                float dYaw = Mathf.DeltaAngle(_measuredBodyYawWorldDegPrev, measuredYaw);
                float measuredRate = dYaw / Mathf.Max(tickDt, 1e-4f);
                // Allow 1.5× max for transient overshoot (slide release etc.)
                _estBodyYawRateDegPerSec = Mathf.Clamp(measuredRate, -maxRate * 1.5f, maxRate * 1.5f);
            }
            else
            {
                _estBodyYawRateDegPerSec = 0f;
                _haveMeasuredBodyYaw = true;
            }
            _estBodyYawWorldDeg            = measuredYaw;
            _measuredBodyYawWorldDegPrev   = measuredYaw;
        }

        // Forward-project body yaw by `horizonSec` assuming `turnSign`
        // (sign of MoveInput.x we're sending) and `isSliding` are held
        // throughout the horizon. Mirrors Movement.Turn() exactly:
        //   - Same-direction input + below max → accelerate at turnAccel.
        //   - Opposite-direction input → brake at turnBrakeAccel.
        //   - No input + below max → fractional drag at turnDrag.
        //   - Above max (any direction) → overspeed drag at turnOverspeedDrag.
        // Substep at ~60 Hz so a 0.05-0.5 s horizon stays accurate.
        private float ProjectBodyYawWorldDeg(float horizonSec, float turnSign, bool isSliding)
        {
            if (horizonSec <= 0f) return _estBodyYawWorldDeg;
            float multiplier = isSliding ? 2.0f : 1.0f;
            float maxRate    = BodyTurnMaxRateDegPerSec * multiplier;
            float yaw  = _estBodyYawWorldDeg;
            float rate = _estBodyYawRateDegPerSec;
            int steps  = Mathf.Max(1, Mathf.CeilToInt(horizonSec * 60f));
            float dt   = horizonSec / steps;
            float accel = BodyTurnAccelDegPerSec2 * multiplier;
            float brake = BodyTurnBrakeDegPerSec2 * multiplier;
            for (int i = 0; i < steps; i++)
            {
                if (turnSign > 0f)
                {
                    if (rate >= 0f) { if (Mathf.Abs(rate) < maxRate) rate += accel * dt; }
                    else            { rate += brake * dt; }
                }
                else if (turnSign < 0f)
                {
                    if (rate <= 0f) { if (Mathf.Abs(rate) < maxRate) rate -= accel * dt; }
                    else            { rate -= brake * dt; }
                }
                else if (Mathf.Abs(rate) <= maxRate)
                {
                    rate *= 1f - BodyTurnDragPerSec * dt;
                }
                if (Mathf.Abs(rate) > maxRate)
                {
                    rate *= 1f - BodyTurnOverspeedDragPerSec * dt;
                }
                yaw += rate * dt;
            }
            return yaw;
        }

        private static short AngleToShort(float degrees)
        {
            // Mirror PlayerInput.cs:623 — `value = (x * 360) / 32767`.
            // Inverse: short = (degrees / 360) * 32767.
            float clamped = Mathf.Repeat(degrees + 180f, 360f) - 180f;
            int v = Mathf.RoundToInt(clamped / 360f * 32767f);
            return (short)Mathf.Clamp(v, short.MinValue, short.MaxValue);
        }
    }
}
