#if PUCKBOT_ONNX_AVAILABLE
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using UnityEngine;
using PuckStressTest.Logging;
using PuckStressTest.Mirror;

namespace PuckStressTest.Brain
{
    // ONNX-driven brain. Reads obs from the same chokepoint
    // SnapshotLogger uses (via ObsBuilder), runs inference, decodes
    // the 16-float action vector, and emits via MirrorPlayerInput.Send*.
    //
    // Lifecycle mirror BotBrain so MirrorPlayer can swap between them
    // by component type:
    //   - AddComponent + set TickHz + BotIndex + Snapshot, then Bind()
    //   - Update() drives Tick() at TickHz via the standard accumulator
    //
    // Compile guard: PUCKBOT_ONNX_AVAILABLE must be defined in Unity
    // Player Settings > Scripting Define Symbols once Microsoft.ML.OnnxRuntime
    // is installed (NuGetForUnity, manual DLL drop, or via UPM scoped registry).
    // See README in this directory for installation steps.
    //
    // Action decoding mirrors ml/action.py decode_to_rpc — keep the
    // constants in sync if you bump ACTION_SCHEMA_VERSION.
    public class OnnxBrain : MonoBehaviour, IBrain
    {
        public string PolicyPath;
        public float  TickHz = 30f;
        public int    BotIndex;
        public SnapshotLogger Snapshot;
        // Mirror BotBrain.VoteStart: when true, bot sends /vs in chat
        // during Warmup so the game progresses to FaceOff/Playing
        // without a 60-second wait. Required for the policy to actually
        // get to act — the per-tick gate skips Send* until Play state.
        public bool   VoteStart = false;

        // Action range constants — must match ml/action.py exactly.
        private const float LOOK_PITCH_MIN  = -25f, LOOK_PITCH_MAX  = 75f;
        private const float LOOK_YAW_MIN    = -135f, LOOK_YAW_MAX   = 135f;
        private const float STICK_PITCH_MIN = -25f, STICK_PITCH_MAX = 80f;
        private const float STICK_YAW_MIN   = -92.5f, STICK_YAW_MAX = 92.5f;
        private const sbyte BLADE_ANGLE_MIN = -4, BLADE_ANGLE_MAX = 4;

        // Action slot indices — must match ml/action.py Slots.
        private const int A_MOVE_X = 0, A_MOVE_Y = 1;
        private const int A_LOOK_PITCH = 2, A_LOOK_YAW = 3;
        private const int A_STICK_PITCH = 4, A_STICK_YAW = 5;
        private const int A_BLADE_ANGLE = 6;
        private const int A_SLIDE = 7, A_SPRINT = 8, A_STOP = 9;
        private const int A_EXTEND_LEFT = 10, A_EXTEND_RIGHT = 11;
        private const int A_DASH_LEFT = 12, A_DASH_RIGHT = 13;

        private MirrorPlayer       _player;
        private MirrorPlayerInput  _input;
        private MirrorPlayerBodyV2 _myBody;

        private InferenceSession   _session;
        private float[]            _obs;
        private float[]            _action;
        private DenseTensor<float> _inputTensor;
        private List<NamedOnnxValue> _inputs;
        private ObsBuilder.Cache   _obsCache;

        private float _accumulator;
        private bool  _ready;
        private int   _refreshCountdown;

        // Vote-start state. Mirrors BotBrain.cs constants.
        private const float VsResendIntervalSec = 3f;
        private const float VoteWindowSec       = 60f;
        private float _vsFirstSeenTime;
        private float _lastVsSendTime;
        private bool  _voteDone;

        // Edge-tracking for Bernoulli flags so we only Send* on change.
        private bool _prevSlide, _prevSprint, _prevStop, _prevExtL, _prevExtR;
        private sbyte _prevBladeAngle = 0;

        public bool IsReady => _ready;

        public void Bind(MirrorPlayer player, MirrorPlayerInput input, MirrorPlayerBodyV2 myBody)
        {
            _player = player;
            _input  = input;
            _myBody = myBody;
        }

        public void SetMyBody(MirrorPlayerBodyV2 myBody) => _myBody = myBody;

        private void Awake()
        {
            _obs        = new float[ObsBuilder.OBS_DIM];
            _action     = new float[14];
            _inputTensor = new DenseTensor<float>(new[] { 1, ObsBuilder.OBS_DIM });
            _inputs     = new List<NamedOnnxValue>(1) {
                NamedOnnxValue.CreateFromTensor("obs", _inputTensor)
            };
            _obsCache   = new ObsBuilder.Cache();
        }

        // Model load is deferred to the first Tick. Unity calls OnEnable
        // immediately after AddComponent, before the caller has a chance
        // to assign PolicyPath, so loading there always failed.
        private bool TryLazyLoad()
        {
            if (_ready) return true;
            if (string.IsNullOrEmpty(PolicyPath))
            {
                if (!_loadFailureLogged)
                {
                    Debug.LogError($"[OnnxBrain b={BotIndex}] policy path empty");
                    _loadFailureLogged = true;
                }
                return false;
            }
            if (!File.Exists(PolicyPath))
            {
                if (!_loadFailureLogged)
                {
                    Debug.LogError($"[OnnxBrain b={BotIndex}] policy file not found: '{PolicyPath}'");
                    _loadFailureLogged = true;
                }
                return false;
            }
            try
            {
                _session = new InferenceSession(PolicyPath);
                _ready = true;
                Debug.Log($"[OnnxBrain b={BotIndex}] loaded policy {PolicyPath}");
                return true;
            }
            catch (Exception ex)
            {
                if (!_loadFailureLogged)
                {
                    Debug.LogError($"[OnnxBrain b={BotIndex}] load failed: {ex.Message}");
                    _loadFailureLogged = true;
                }
                return false;
            }
        }
        private bool _loadFailureLogged;

        private void OnDisable()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            _ready = false;
        }

        private void Update()
        {
            if (_player == null || _input == null) return;
            // Mirror BotBrain.Update gating: don't tick until our
            // PlayerInput NB is spawned. Pre-spawn Send*s travel
            // through a half-wired NGO state and can poison server
            // input handling for this client.
            if (!_input.IsSpawned) return;
            if (!TryLazyLoad()) return;
            _accumulator += Time.deltaTime * TickHz;
            while (_accumulator >= 1f)
            {
                _accumulator -= 1f;
                Tick();
            }
        }

        // Walks spawned objects for the MirrorGameManager phase.
        private GamePhase CurrentGamePhase()
        {
            if (_player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null)
                return 0;
            foreach (var no in _player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var gm = no.GetComponent<MirrorGameManager>();
                if (gm != null) return gm.GameState.Value.Phase;
            }
            return 0;
        }

        public void Tick()
        {
            if (!TryLazyLoad()) return;

            // Periodic /vs (mirror BotBrain): without this the warmup
            // never expires (12 OnnxBrain bots all silent → server waits
            // the full 60s warmup) and the per-tick state-gate never
            // releases the action loop.
            float vsNow = Time.realtimeSinceStartup;
            if (_vsFirstSeenTime == 0f) _vsFirstSeenTime = vsNow;
            if (VoteStart
                && !_voteDone
                && vsNow - _vsFirstSeenTime < VoteWindowSec
                && vsNow - _lastVsSendTime >= VsResendIntervalSec
                && _player?.NetworkManager != null
                && _player.NetworkManager.IsConnectedClient)
            {
                if (CurrentGamePhase() != GamePhase.Warmup) _voteDone = true;
                else
                {
                    _lastVsSendTime = vsNow;
                    try { ChatSender.TrySend(_player.NetworkManager, "/vs"); }
                    catch (Exception ex) { Debug.LogWarning($"[OnnxBrain b={BotIndex}] /vs failed: {ex.Message}"); }
                }
            }

            // Real-client gate (mirror BotBrain.Tick line ~582-604):
            // PlayerInput.cs:108-141 only ticks inputs while the
            // PlayerInput NB has spawned AND the bot is in Play state.
            // Sending Move/Look/Stick during Warmup/FaceOff/Replay
            // saturates UTP's reliable window with spawn-burst RPCs
            // and can mark our position stale on the server side.
            PlayerState curState = PlayerState.None;
            try { curState = _player.State; } catch { }
            if (curState != PlayerState.Play)
            {
                // Clear any sticky bool flags so the server doesn't
                // think we're still sliding/sprinting from last episode.
                if (_prevSlide)  { try { _input.SendSlide(false);  } catch { } _prevSlide  = false; }
                if (_prevSprint) { try { _input.SendSprint(false); } catch { } _prevSprint = false; }
                if (_prevStop)   { try { _input.SendStop(false);   } catch { } _prevStop   = false; }
                if (_prevExtL)   { try { _input.SendExtendLeft(false);  } catch { } _prevExtL = false; }
                if (_prevExtR)   { try { _input.SendExtendRight(false); } catch { } _prevExtR = false; }
                if (_prevBladeAngle != 0) { try { _input.SendBladeAngle(0); } catch { } _prevBladeAngle = 0; }
                return;
            }
            // Lazy body resolution. BotBrain does this on its own ticker;
            // we replicate so OnnxBrain works standalone.
            if (--_refreshCountdown <= 0)
            {
                _refreshCountdown = Mathf.Max(1, Mathf.RoundToInt(TickHz));  // ~once per second
                if (_myBody == null) _myBody = FindMyBody();
                if (Snapshot != null && _myBody != null) Snapshot.Bind(_player, _input, _myBody);
            }
            // Build obs into our scratch buffer.
            if (!ObsBuilder.Build(_obs, _player, _input, _myBody, _obsCache, Time.deltaTime))
                return;

            // Copy into the pre-allocated input tensor.
            for (int i = 0; i < ObsBuilder.OBS_DIM; i++) _inputTensor[0, i] = _obs[i];

            try
            {
                using var results = _session.Run(_inputs);
                var enumerator = results.GetEnumerator();
                if (!enumerator.MoveNext()) return;
                var tensor = enumerator.Current.AsTensor<float>();
                for (int i = 0; i < 14; i++) _action[i] = tensor[0, i];
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OnnxBrain b={BotIndex}] inference failed: {ex.Message}");
                return;
            }

            // Decode and Send. Mirrors ml/action.py decode_to_rpc.
            short moveX = (short)Mathf.Clamp(_action[A_MOVE_X] * 32767f, -32767f, 32767f);
            short moveY = (short)Mathf.Clamp(_action[A_MOVE_Y] * 32767f, -32767f, 32767f);
            _input.SendMove(moveX, moveY);

            short lookPitch = DegToShort(DenormCentered(_action[A_LOOK_PITCH], LOOK_PITCH_MIN, LOOK_PITCH_MAX));
            short lookYaw   = DegToShort(DenormCentered(_action[A_LOOK_YAW],   LOOK_YAW_MIN,   LOOK_YAW_MAX));
            _input.SendLookAngle(lookPitch, lookYaw);

            short stickPitch = DegToShort(DenormCentered(_action[A_STICK_PITCH], STICK_PITCH_MIN, STICK_PITCH_MAX));
            short stickYaw   = DegToShort(DenormCentered(_action[A_STICK_YAW],   STICK_YAW_MIN,   STICK_YAW_MAX));
            _input.SendRaycastOriginAngle(stickPitch, stickYaw);

            sbyte blade = (sbyte)Mathf.Clamp(Mathf.RoundToInt(_action[A_BLADE_ANGLE]), BLADE_ANGLE_MIN, BLADE_ANGLE_MAX);
            if (blade != _prevBladeAngle)
            {
                _input.SendBladeAngle(blade);
                _prevBladeAngle = blade;
            }

            bool slide  = _action[A_SLIDE]        >= 0.5f;
            bool sprint = _action[A_SPRINT]       >= 0.5f;
            bool stop   = _action[A_STOP]         >= 0.5f;
            bool extL   = _action[A_EXTEND_LEFT]  >= 0.5f;
            bool extR   = _action[A_EXTEND_RIGHT] >= 0.5f;
            if (slide  != _prevSlide)  { _input.SendSlide(slide);    _prevSlide  = slide;  }
            if (sprint != _prevSprint) { _input.SendSprint(sprint);  _prevSprint = sprint; }
            if (stop   != _prevStop)   { _input.SendStop(stop);      _prevStop   = stop;   }
            if (extL   != _prevExtL)   { _input.SendExtendLeft(extL); _prevExtL  = extL;   }
            if (extR   != _prevExtR)   { _input.SendExtendRight(extR);_prevExtR  = extR;   }

            // Dashes are edge-triggered RPCs — fire once per positive prediction.
            if (_action[A_DASH_LEFT]  >= 0.5f) _input.SendDashLeft();
            if (_action[A_DASH_RIGHT] >= 0.5f) _input.SendDashRight();

            // Snapshot at end of tick: same chokepoint contract as BotBrain.
            try { Snapshot?.EmitTick(); } catch { }
        }

        private static float DenormCentered(float u, float lo, float hi)
        {
            float mid  = 0.5f * (lo + hi);
            float half = 0.5f * (hi - lo);
            return Mathf.Clamp(mid + Mathf.Clamp(u, -1f, 1f) * half, lo, hi);
        }

        private static short DegToShort(float deg)
        {
            return (short)Mathf.Clamp(deg / 360f * 32767f, -32767f, 32767f);
        }

        private MirrorPlayerBodyV2 FindMyBody()
        {
            if (_player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return null;
            foreach (var no in _player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var b = no.GetComponent<MirrorPlayerBodyV2>();
                if (b == null) continue;
                ulong refNid = 0;
                try { refNid = b.PlayerReference.Value.NetworkObjectId; } catch { }
                if (refNid == _player.NetworkObjectId) return b;
            }
            return null;
        }
    }
}
#else
using UnityEngine;
namespace PuckStressTest.Brain
{
    // Stub when Microsoft.ML.OnnxRuntime isn't installed. Logs a clear
    // error if anyone tries to use --brain onnx without the runtime.
    public class OnnxBrain : MonoBehaviour, IBrain
    {
        public string PolicyPath;
        public float  TickHz;
        public int    BotIndex;
        public PuckStressTest.Logging.SnapshotLogger Snapshot;
        // Mirror the real implementation's public surface so callers
        // (MirrorPlayer) compile whether or not ONNX is enabled.
        public bool   VoteStart;
        public bool IsReady => false;
        public void Bind(PuckStressTest.Mirror.MirrorPlayer p,
                         PuckStressTest.Mirror.MirrorPlayerInput i,
                         PuckStressTest.Mirror.MirrorPlayerBodyV2 b)
        { _ = p; _ = i; _ = b; }
        public void SetMyBody(PuckStressTest.Mirror.MirrorPlayerBodyV2 b) { _ = b; }
        public void Tick() { }
        private void OnEnable()
        {
            Debug.LogError(
                "[OnnxBrain] PUCKBOT_ONNX_AVAILABLE not defined — Microsoft.ML.OnnxRuntime " +
                "is not installed. See BotHost/Assets/Scripts/Brain/README.md.");
        }
    }
}
#endif
