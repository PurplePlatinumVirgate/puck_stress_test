using System;
using System.IO;
using UnityEngine;
using PuckStressTest.Mirror;

namespace PuckStressTest.Logging
{
    // Per-tick (obs, action, reward) emitter. Sits on the same
    // GameObject as MirrorPlayer + BotBrain. Reads world state from
    // MirrorSynchronizedObjectManager.LatestPositions + Mirror NVs
    // (the same chokepoint BotBrain uses), reads the latest action
    // off MirrorPlayer.GetLastSentAction (the action chokepoint),
    // packs everything into a fixed-size record and writes it to a
    // per-process .bin file. The binary layout is defined by the
    // constants below.
    //
    // NOTE: the reader side (ml/snapshot_format.py + the tools/ scripts) is
    // part of the OPTIONAL ML training pipeline, which is not shipped in this
    // release. If you use it, keep the layout here in sync with
    // ml/snapshot_format.py and bump SCHEMA_VERSION in both.
    //
    // The reward field is filled from a server-emitted reward channel
    // (M2). For M1 we write float.NaN as a placeholder so the loader
    // can still parse records and the BC trainer (M4) can ignore the
    // field entirely.
    public class SnapshotLogger : MonoBehaviour
    {
        // ======== File format constants (mirror ml/snapshot_format.py) ========
        private const uint   MAGIC = 0x50554B53u;           // "PUKS"
        // Bumped to 2 on 2026-05-13 for Puck B323 cutover (Player NV
        // rewrite, PlayerTeam ordinal reorder, byte-Stamina, PositionSelect
        // collapse). Old .bin files become historical-only; regenerate
        // training data after first successful 4-bot smoke on B323.
        private const ushort SCHEMA_VERSION = 2;
        private const ushort FLAG_EPISODE_BOUNDARY = 0x0001;
        private const int    OBS_DIM    = 256;
        private const int    ACTION_DIM = 16;
        private const int    HEADER_SIZE = 28;
        private const int    RECORD_BYTES = HEADER_SIZE + OBS_DIM * 4 + ACTION_DIM * 4;
        private const int    PADDED_RECORD_BYTES = ((RECORD_BYTES + 15) / 16) * 16;

        // ======== Action slot indices (mirror ml/action.py Slots) ========
        private const int A_MOVE_X = 0, A_MOVE_Y = 1;
        private const int A_LOOK_PITCH = 2, A_LOOK_YAW = 3;
        private const int A_STICK_PITCH = 4, A_STICK_YAW = 5;
        private const int A_BLADE_ANGLE = 6;
        private const int A_SLIDE = 7, A_SPRINT = 8, A_STOP = 9;
        private const int A_EXTEND_LEFT = 10, A_EXTEND_RIGHT = 11;
        private const int A_DASH_LEFT = 12, A_DASH_RIGHT = 13;

        // ======== Action range constants (mirror ml/action.py) ========
        private const float LOOK_PITCH_MIN = -25f, LOOK_PITCH_MAX = 75f;
        private const float LOOK_YAW_MIN   = -135f, LOOK_YAW_MAX   = 135f;
        private const float STICK_PITCH_MIN = -25f, STICK_PITCH_MAX = 80f;
        private const float STICK_YAW_MIN   = -92.5f, STICK_YAW_MAX   = 92.5f;

        // ======== Per-instance state ========
        public int    BotIndex;
        public string OutputDirectory = "Logs/snapshots";

        private MirrorPlayer       _player;
        private MirrorPlayerInput  _input;
        private MirrorPlayerBodyV2 _myBody;
        private FileStream         _file;
        private byte[]             _scratch;
        private float[]            _obs;
        private float[]            _action;
        private uint               _tickIndex;
        private bool               _emittedFirstRecordThisEpisode;

        public void Bind(MirrorPlayer player, MirrorPlayerInput input, MirrorPlayerBodyV2 myBody)
        {
            _player = player;
            _input = input;
            _myBody = myBody;
        }

        private void Awake()
        {
            _scratch = new byte[PADDED_RECORD_BYTES];
            _obs    = new float[OBS_DIM];
            _action = new float[ACTION_DIM];
        }

        // Open the per-episode .bin file. Caller must set BotIndex and
        // OutputDirectory BEFORE calling this. Cannot live in OnEnable
        // because Unity's AddComponent calls OnEnable before the caller
        // can set fields, leading to all 12 child processes opening
        // _b00.bin and racing on the same path.
        public void BeginEpisode()
        {
            try
            {
                Directory.CreateDirectory(OutputDirectory);
                string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(OutputDirectory, $"{ts}_b{BotIndex:D2}.bin");
                _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                                       bufferSize: 1 << 16, useAsync: false);
                _emittedFirstRecordThisEpisode = false;

                // Sidecar JSON pairs the snapshot file's bot_index with the
                // server-assigned NGO client id, so tools/join_rewards.py can
                // join RewardMod's per-tick CSV onto these snapshots without
                // an embedded id slot in the binary schema.
                ulong clientId = _player != null ? _player.OwnerClientId : 0UL;
                string sidecarPath = Path.Combine(OutputDirectory, $"{ts}_b{BotIndex:D2}.json");
                File.WriteAllText(sidecarPath,
                    $"{{\"bot_index\":{BotIndex},\"client_id\":{clientId}}}\n");

                Debug.Log($"[SnapshotLogger b={BotIndex}] writing → {path} (client_id={clientId})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SnapshotLogger b={BotIndex}] open failed: {ex.Message}");
            }
        }

        private void OnDisable()
        {
            try { _file?.Flush(); _file?.Dispose(); } catch { }
            _file = null;
        }

        // Called by BotBrain at the end of each Tick(), after the
        // brain has emitted its actions for the tick. Writes one
        // record. State pulled from mirror chokepoints; action pulled
        // from MirrorPlayer.GetLastSentAction.
        public void EmitTick()
        {
            if (_file == null || _player == null || _input == null) return;

            // ---- ACTION ----
            Array.Clear(_action, 0, ACTION_DIM);
            var act = _input.GetLastSentAction();
            _action[A_MOVE_X]      = act.MoveX / 32767f;
            _action[A_MOVE_Y]      = act.MoveY / 32767f;
            _action[A_LOOK_PITCH]  = NormCentered(ShortToDeg(act.LookPitch), LOOK_PITCH_MIN, LOOK_PITCH_MAX);
            _action[A_LOOK_YAW]    = NormCentered(ShortToDeg(act.LookYaw),   LOOK_YAW_MIN,   LOOK_YAW_MAX);
            _action[A_STICK_PITCH] = NormCentered(ShortToDeg(act.StickPitch), STICK_PITCH_MIN, STICK_PITCH_MAX);
            _action[A_STICK_YAW]   = NormCentered(ShortToDeg(act.StickYaw),   STICK_YAW_MIN,   STICK_YAW_MAX);
            _action[A_BLADE_ANGLE] = act.BladeAngle;
            _action[A_SLIDE]        = act.Slide        ? 1f : 0f;
            _action[A_SPRINT]       = act.Sprint       ? 1f : 0f;
            _action[A_STOP]         = act.Stop         ? 1f : 0f;
            _action[A_EXTEND_LEFT]  = act.ExtendLeft   ? 1f : 0f;
            _action[A_EXTEND_RIGHT] = act.ExtendRight  ? 1f : 0f;
            _action[A_DASH_LEFT]    = act.DashLeft     ? 1f : 0f;
            _action[A_DASH_RIGHT]   = act.DashRight    ? 1f : 0f;
            _input.ClearEdgeFlags();

            // ---- OBSERVATION ----
            Array.Clear(_obs, 0, OBS_DIM);

            Vector3 myPos = Vector3.zero;
            Quaternion myRot = Quaternion.identity;
            if (TryGetMyXform(out myPos, out myRot))
            {
                Vector3 fwd = myRot * Vector3.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);

                // Velocity from snapshot delta. Skip on the first tick
                // of the episode — _lastMyPos defaults to zero, so the
                // very first frame would record a huge phantom velocity
                // pointing from the world origin to wherever the bot
                // spawned. _havePrevPos guards that.
                if (_havePrevPos)
                {
                    Vector3 velWorld = (myPos - _lastMyPos) / Mathf.Max(Time.deltaTime, 1e-3f);
                    Vector3 velBody = WorldToBody(velWorld, fwd, right);
                    _obs[0] = velBody.x; _obs[1] = velBody.y; _obs[2] = velBody.z;
                    float yawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    float yawRate = Mathf.DeltaAngle(_lastMyYawDeg, yawDeg) / Mathf.Max(Time.deltaTime, 1e-3f);
                    _obs[3] = yawRate * Mathf.Deg2Rad;
                    _lastMyYawDeg = yawDeg;
                }
                else
                {
                    _lastMyYawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    _havePrevPos = true;
                }
                _lastMyPos = myPos;

                if (_player != null && TryGetStickXform(out Vector3 stickPos, out Quaternion stickRot))
                {
                    Vector3 d = stickPos - myPos; d.y = 0f;
                    Vector3 dBody = WorldToBody(d, fwd, right);
                    _obs[4] = dBody.x; _obs[5] = dBody.z;
                    Vector3 sFwd = stickRot * Vector3.forward; sFwd.y = 0f;
                    if (sFwd.sqrMagnitude > 1e-4f)
                    {
                        sFwd.Normalize();
                        float sYaw = Mathf.Atan2(sFwd.x, sFwd.z) - Mathf.Atan2(fwd.x, fwd.z);
                        _obs[6] = Mathf.Repeat(sYaw + Mathf.PI, 2 * Mathf.PI) - Mathf.PI;
                    }
                }

                if (_myBody != null)
                {
                    _obs[8] = _myBody.StaminaCompressed.Value / 255f;
                    _obs[9]  = _myBody.IsSliding.Value   ? 1f : 0f;
                    _obs[10] = _myBody.IsSprinting.Value ? 1f : 0f;
                }
                int role = (int)(_player != null ? _player.Role : PlayerRole.None);
                // Role slots: [11]=Goalie, [12]=Defender (reserved, Puck has
                // no Defender role today), [13]=Attacker, [14]=Other/None.
                if      (role == (int)PlayerRole.Goalie)   _obs[11] = 1f;
                else if (role == (int)PlayerRole.Attacker) _obs[13] = 1f;
                else                                       _obs[14] = 1f;
                int team = (int)(_player != null ? _player.Team : PlayerTeam.None);
                if      (team == (int)PlayerTeam.Blue) _obs[15] = 1f;
                else if (team == (int)PlayerTeam.Red)  _obs[16] = 1f;

                EmitPucksToObs(myPos, fwd, right);
                EmitOtherPlayersToObs(myPos, fwd, right, team);

                float ownZ = (team == (int)PlayerTeam.Blue) ? -40.23f : +40.23f;
                Vector3 ownGoal = new Vector3(0f, 0f, ownZ);
                Vector3 oppGoal = new Vector3(0f, 0f, -ownZ);
                Vector3 ownD = WorldToBody(ownGoal - myPos, fwd, right);
                Vector3 oppD = WorldToBody(oppGoal - myPos, fwd, right);
                _obs[105] = ownD.x; _obs[106] = ownD.z;
                _obs[107] = oppD.x; _obs[108] = oppD.z;
                _obs[109] = Mathf.Atan2(oppD.x, oppD.z);
            }

            int phaseSlot = ResolvePhaseSlotIndex();
            if (phaseSlot >= 0) _obs[115 + phaseSlot] = 1f;
            float timeNorm, scoreDiff10, periodNorm;
            ResolveGameStats(out timeNorm, out scoreDiff10, out periodNorm);
            _obs[120] = timeNorm;
            _obs[121] = scoreDiff10;
            _obs[122] = periodNorm;

            // ---- PACK + WRITE ----
            ushort flags = 0;
            if (!_emittedFirstRecordThisEpisode)
            {
                flags |= FLAG_EPISODE_BOUNDARY;
                _emittedFirstRecordThisEpisode = true;
            }
            long utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            // Header (28 bytes).
            WriteUInt32(_scratch, 0,  MAGIC);
            WriteUInt16(_scratch, 4,  SCHEMA_VERSION);
            WriteUInt16(_scratch, 6,  flags);
            WriteUInt64(_scratch, 8,  (ulong)utc);
            WriteUInt32(_scratch, 16, (uint)BotIndex);
            WriteUInt32(_scratch, 20, _tickIndex++);
            // Reward placeholder (M2 fills this server-side then bot
            // consumes via NV). Bit pattern 0x7FC00000 = canonical IEEE
            // 754 quiet NaN; loaders treat NaN as "not yet computed".
            WriteUInt32(_scratch, 24, 0x7FC00000u);
            // Bulk float copy: float[] → byte[] via Buffer.BlockCopy. No
            // per-float allocation, no unsafe code in the hot path.
            Buffer.BlockCopy(_obs,    0, _scratch, HEADER_SIZE,                 OBS_DIM    * 4);
            Buffer.BlockCopy(_action, 0, _scratch, HEADER_SIZE + OBS_DIM * 4,   ACTION_DIM * 4);
            // Tail padding zeros.
            for (int i = HEADER_SIZE + OBS_DIM * 4 + ACTION_DIM * 4; i < PADDED_RECORD_BYTES; i++) _scratch[i] = 0;

            try { _file.Write(_scratch, 0, PADDED_RECORD_BYTES); }
            catch (Exception ex) { Debug.LogError($"[SnapshotLogger] write failed: {ex.Message}"); }
        }

        // ============== Helpers ==============

        private Vector3 _lastMyPos;
        private float   _lastMyYawDeg;
        private bool    _havePrevPos;

        private bool TryGetMyXform(out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (_myBody == null) return false;
            if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(_myBody.NetworkObjectId, out var x))
                return false;
            pos = x.Position; rot = x.Rotation;
            return true;
        }

        private bool TryGetStickXform(out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (_player == null || _player.NetworkManager == null) return false;
            // Walk spawned stick mirrors and find the one with our PlayerReference.
            foreach (var no in _player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var s = no.GetComponent<MirrorStick>();
                if (s == null) continue;
                ulong refNid = 0;
                try { refNid = s.PlayerReference.Value.NetworkObjectId; } catch { }
                if (refNid != _player.NetworkObjectId) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var x))
                    return false;
                pos = x.Position; rot = x.Rotation;
                return true;
            }
            return false;
        }

        private void EmitPucksToObs(Vector3 myPos, Vector3 fwd, Vector3 right)
        {
            const int N = 4;
            Span<float> dists = stackalloc float[N];
            Span<Vector3> ds = stackalloc Vector3[N];
            for (int i = 0; i < N; i++) { dists[i] = float.MaxValue; ds[i] = Vector3.zero; }
            var spawned = _player?.NetworkManager?.SpawnManager?.SpawnedObjectsList;
            if (spawned == null) return;
            foreach (var no in spawned)
            {
                if (no == null) continue;
                var pk = no.GetComponent<MirrorPuck>();
                if (pk == null) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var x)) continue;
                Vector3 d = x.Position - myPos;
                float r = d.sqrMagnitude;
                for (int i = 0; i < N; i++)
                {
                    if (r < dists[i]) { for (int j = N - 1; j > i; j--) { dists[j] = dists[j-1]; ds[j] = ds[j-1]; } dists[i] = r; ds[i] = d; break; }
                }
            }
            const int Base = 17;
            for (int i = 0; i < N; i++)
            {
                if (dists[i] >= float.MaxValue) break;
                Vector3 dBody = WorldToBody(ds[i], fwd, right);
                _obs[Base + i * 4 + 0] = dBody.x;
                _obs[Base + i * 4 + 1] = dBody.y;
                _obs[Base + i * 4 + 2] = dBody.z;
                // _obs[Base + i*4 + 3] (vx_body) left zero; reserved for future.
            }
        }

        private void EmitOtherPlayersToObs(Vector3 myPos, Vector3 fwd, Vector3 right, int myTeam)
        {
            const int N = 9;
            Span<float> tmDist = stackalloc float[N];
            Span<int>   tmIdx  = stackalloc int[N];
            Span<float> opDist = stackalloc float[N];
            Span<int>   opIdx  = stackalloc int[N];
            for (int i = 0; i < N; i++) { tmDist[i] = opDist[i] = float.MaxValue; tmIdx[i] = opIdx[i] = -1; }

            var spawned = _player?.NetworkManager?.SpawnManager?.SpawnedObjectsList;
            if (spawned == null) return;

            int seen = 0;
            foreach (var no in spawned)
            {
                if (no == null) continue;
                var body = no.GetComponent<MirrorPlayerBodyV2>();
                if (body == null) continue;
                if (_myBody != null && body.NetworkObjectId == _myBody.NetworkObjectId) continue;
                ulong playerNid = 0;
                try { playerNid = body.PlayerReference.Value.NetworkObjectId; } catch { }
                MirrorPlayer pl = null;
                if (playerNid != 0)
                {
                    foreach (var no2 in spawned)
                    {
                        if (no2 == null || no2.NetworkObjectId != playerNid) continue;
                        pl = no2.GetComponent<MirrorPlayer>();
                        break;
                    }
                }
                int team = (int)(pl != null ? pl.Team : PlayerTeam.None);
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(body.NetworkObjectId, out var x)) continue;
                Vector3 d = x.Position - myPos;
                float r = d.sqrMagnitude;
                Span<float> dist = team == myTeam ? tmDist : opDist;
                Span<int>   idx  = team == myTeam ? tmIdx  : opIdx;
                for (int i = 0; i < N; i++)
                {
                    if (r < dist[i]) { for (int j = N - 1; j > i; j--) { dist[j] = dist[j-1]; idx[j] = idx[j-1]; } dist[i] = r; idx[i] = seen; break; }
                }
                _otherCache[seen] = (d, x.Rotation, team);
                seen++;
                if (seen >= _otherCache.Length) break;
            }

            const int TmBase = 33;
            const int OpBase = 69;
            for (int i = 0; i < N; i++)
            {
                if (tmIdx[i] < 0) break;
                var (d, rot, _) = _otherCache[tmIdx[i]];
                Vector3 dBody = WorldToBody(d, fwd, right);
                Vector3 oFwd = rot * Vector3.forward; oFwd.y = 0f;
                float yawRel = oFwd.sqrMagnitude > 1e-4f
                    ? Mathf.Atan2(oFwd.x, oFwd.z) - Mathf.Atan2(fwd.x, fwd.z) : 0f;
                _obs[TmBase + i * 4 + 0] = dBody.x;
                _obs[TmBase + i * 4 + 1] = dBody.z;
                _obs[TmBase + i * 4 + 2] = Mathf.Repeat(yawRel + Mathf.PI, 2 * Mathf.PI) - Mathf.PI;
            }
            for (int i = 0; i < N; i++)
            {
                if (opIdx[i] < 0) break;
                var (d, rot, _) = _otherCache[opIdx[i]];
                Vector3 dBody = WorldToBody(d, fwd, right);
                Vector3 oFwd = rot * Vector3.forward; oFwd.y = 0f;
                float yawRel = oFwd.sqrMagnitude > 1e-4f
                    ? Mathf.Atan2(oFwd.x, oFwd.z) - Mathf.Atan2(fwd.x, fwd.z) : 0f;
                _obs[OpBase + i * 4 + 0] = dBody.x;
                _obs[OpBase + i * 4 + 1] = dBody.z;
                _obs[OpBase + i * 4 + 2] = Mathf.Repeat(yawRel + Mathf.PI, 2 * Mathf.PI) - Mathf.PI;
            }
        }

        private readonly (Vector3 d, Quaternion rot, int team)[] _otherCache = new (Vector3, Quaternion, int)[24];

        private int ResolvePhaseSlotIndex()
        {
            if (_player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return -1;
            foreach (var no in _player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var gm = no.GetComponent<MirrorGameManager>();
                if (gm == null) continue;
                var phase = gm.GameState.Value.Phase;
                switch (phase)
                {
                    case GamePhase.Warmup:  return 0;
                    case GamePhase.FaceOff: return 1;
                    case GamePhase.Playing: return 2;
                    case GamePhase.Replay:  return 3;
                    default:                return 4;
                }
            }
            return -1;
        }

        private void ResolveGameStats(out float timeNorm, out float scoreDiff10, out float periodNorm)
        {
            timeNorm = 0f; scoreDiff10 = 0f; periodNorm = 0f;
            if (_player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return;
            foreach (var no in _player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var gm = no.GetComponent<MirrorGameManager>();
                if (gm == null) continue;
                var gs = gm.GameState.Value;
                int myTeam = (int)(_player != null ? _player.Team : PlayerTeam.None);
                int diff = (myTeam == (int)PlayerTeam.Blue) ? (gs.BlueScore - gs.RedScore) : (gs.RedScore - gs.BlueScore);
                scoreDiff10 = Mathf.Clamp(diff / 10f, -1f, 1f);
                periodNorm = Mathf.Clamp(gs.Period / 3f, 0f, 1f);
                timeNorm = Mathf.Clamp(gs.Time / 600f, 0f, 1f);
                return;
            }
        }

        // ============== Pure helpers (byte writes + math) ==============

        private static void WriteUInt16(byte[] b, int off, ushort v)
        {
            b[off + 0] = (byte)(v);
            b[off + 1] = (byte)(v >> 8);
        }

        private static void WriteUInt32(byte[] b, int off, uint v)
        {
            b[off + 0] = (byte)(v);
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
        }

        private static void WriteUInt64(byte[] b, int off, ulong v)
        {
            b[off + 0] = (byte)(v);
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
            b[off + 4] = (byte)(v >> 32);
            b[off + 5] = (byte)(v >> 40);
            b[off + 6] = (byte)(v >> 48);
            b[off + 7] = (byte)(v >> 56);
        }

        private static float ShortToDeg(short s) => (s / 32767f) * 360f;

        private static float NormCentered(float x, float lo, float hi)
        {
            float mid = 0.5f * (lo + hi);
            float half = 0.5f * (hi - lo);
            return Mathf.Clamp((x - mid) / half, -1f, 1f);
        }

        private static Vector3 WorldToBody(Vector3 v, Vector3 fwd, Vector3 right)
        {
            // Project to body frame: x = right, y = up, z = forward.
            return new Vector3(Vector3.Dot(v, right), v.y, Vector3.Dot(v, fwd));
        }
    }
}
