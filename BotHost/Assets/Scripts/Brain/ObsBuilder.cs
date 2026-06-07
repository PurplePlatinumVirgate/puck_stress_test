using System;
using UnityEngine;
using PuckStressTest.Mirror;

namespace PuckStressTest.Brain
{
    // Builds the 256-float observation vector consumed by ONNX inference
    // (and, eventually, by SnapshotLogger after a planned dedup).
    //
    // Byte layout MUST match SnapshotLogger.cs (and ml/obs.py, which is part
    // of the optional ML pipeline, not shipped here). If you change one,
    // change the others and bump OBS_SCHEMA_VERSION.
    //
    // Why a standalone class: OnnxBrain runs at inference time and needs
    // the same obs the BC trainer learned on. Replicating SnapshotLogger's
    // logic in a sibling component invites drift; lifting it to a shared
    // builder lets both call into one source of truth.
    //
    // *** This file currently mirrors SnapshotLogger.cs lines 134-217 +
    //     helpers. The SnapshotLogger has NOT YET been refactored to
    //     call into here (preserving the validated byte layout).
    //     The dedup is a follow-up change. ***
    public static class ObsBuilder
    {
        public const int OBS_DIM = 256;
        public const int N_PUCKS = 4;
        public const int N_TEAMMATES = 9;
        public const int N_OPPONENTS = 9;

        // State the per-bot caller passes in. Lets us hold per-tick velocity
        // estimates (snapshot-delta) without ObsBuilder owning lifecycle.
        public class Cache
        {
            public Vector3 LastMyPos;
            public float   LastMyYawDeg;
            public bool    HavePrevPos;
            public readonly (Vector3 d, Quaternion rot, int team)[] OtherCache
                = new (Vector3, Quaternion, int)[24];
        }

        // Build into a caller-provided float[OBS_DIM]. Returns true on
        // success, false if the bot's body transform isn't resolvable yet
        // (caller should leave the obs vector at its previous value).
        public static bool Build(
            float[] obs,
            MirrorPlayer player,
            MirrorPlayerInput input,
            MirrorPlayerBodyV2 myBody,
            Cache cache,
            float deltaTime)
        {
            if (obs == null || obs.Length < OBS_DIM) return false;
            Array.Clear(obs, 0, OBS_DIM);

            if (player == null || myBody == null) return false;
            if (!TryGetMyXform(myBody, out Vector3 myPos, out Quaternion myRot))
                return false;

            Vector3 fwd = myRot * Vector3.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);

            // Self body velocity (snapshot-delta). Skip on the first tick of
            // an episode — cache.LastMyPos defaults to zero, which would
            // record a phantom velocity from the world origin.
            if (cache.HavePrevPos)
            {
                Vector3 velWorld = (myPos - cache.LastMyPos) / Mathf.Max(deltaTime, 1e-3f);
                Vector3 velBody  = WorldToBody(velWorld, fwd, right);
                obs[0] = velBody.x; obs[1] = velBody.y; obs[2] = velBody.z;
                float yawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                float yawRate = Mathf.DeltaAngle(cache.LastMyYawDeg, yawDeg) / Mathf.Max(deltaTime, 1e-3f);
                obs[3] = yawRate * Mathf.Deg2Rad;
                cache.LastMyYawDeg = yawDeg;
            }
            else
            {
                cache.LastMyYawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                cache.HavePrevPos  = true;
            }
            cache.LastMyPos = myPos;

            if (TryGetStickXform(player, out Vector3 stickPos, out Quaternion stickRot))
            {
                Vector3 d = stickPos - myPos; d.y = 0f;
                Vector3 dBody = WorldToBody(d, fwd, right);
                obs[4] = dBody.x; obs[5] = dBody.z;
                Vector3 sFwd = stickRot * Vector3.forward; sFwd.y = 0f;
                if (sFwd.sqrMagnitude > 1e-4f)
                {
                    sFwd.Normalize();
                    float sYaw = Mathf.Atan2(sFwd.x, sFwd.z) - Mathf.Atan2(fwd.x, fwd.z);
                    obs[6] = Mathf.Repeat(sYaw + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
                }
            }

            obs[8]  = myBody.StaminaCompressed.Value / 255f;
            obs[9]  = myBody.IsSliding.Value   ? 1f : 0f;
            obs[10] = myBody.IsSprinting.Value ? 1f : 0f;
            int role = (int)player.Role;
            if      (role == (int)PlayerRole.Goalie)   obs[11] = 1f;
            else if (role == (int)PlayerRole.Attacker) obs[13] = 1f;
            else                                       obs[14] = 1f;
            int team = (int)player.Team;
            if      (team == (int)PlayerTeam.Blue) obs[15] = 1f;
            else if (team == (int)PlayerTeam.Red)  obs[16] = 1f;

            EmitPucksToObs(obs, player, myPos, fwd, right);
            EmitOtherPlayersToObs(obs, player, myBody, myPos, fwd, right, team, cache);

            float ownZ = (team == (int)PlayerTeam.Blue) ? -40.23f : +40.23f;
            Vector3 ownGoal = new Vector3(0f, 0f, ownZ);
            Vector3 oppGoal = new Vector3(0f, 0f, -ownZ);
            Vector3 ownD = WorldToBody(ownGoal - myPos, fwd, right);
            Vector3 oppD = WorldToBody(oppGoal - myPos, fwd, right);
            obs[105] = ownD.x; obs[106] = ownD.z;
            obs[107] = oppD.x; obs[108] = oppD.z;
            obs[109] = Mathf.Atan2(oppD.x, oppD.z);

            int phaseSlot = ResolvePhaseSlotIndex(player);
            if (phaseSlot >= 0) obs[115 + phaseSlot] = 1f;
            ResolveGameStats(player, out float timeNorm, out float scoreDiff10, out float periodNorm);
            obs[120] = timeNorm;
            obs[121] = scoreDiff10;
            obs[122] = periodNorm;

            return true;
        }

        // ============== Helpers (mirror SnapshotLogger.cs) ==============

        private static bool TryGetMyXform(MirrorPlayerBodyV2 myBody, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (myBody == null) return false;
            if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(myBody.NetworkObjectId, out var x))
                return false;
            pos = x.Position; rot = x.Rotation;
            return true;
        }

        private static bool TryGetStickXform(MirrorPlayer player, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (player == null || player.NetworkManager == null) return false;
            foreach (var no in player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var s = no.GetComponent<MirrorStick>();
                if (s == null) continue;
                ulong refNid = 0;
                try { refNid = s.PlayerReference.Value.NetworkObjectId; } catch { }
                if (refNid != player.NetworkObjectId) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var x))
                    return false;
                pos = x.Position; rot = x.Rotation;
                return true;
            }
            return false;
        }

        private static void EmitPucksToObs(
            float[] obs, MirrorPlayer player, Vector3 myPos, Vector3 fwd, Vector3 right)
        {
            Span<float> dists = stackalloc float[N_PUCKS];
            Span<Vector3> ds = stackalloc Vector3[N_PUCKS];
            for (int i = 0; i < N_PUCKS; i++) { dists[i] = float.MaxValue; ds[i] = Vector3.zero; }

            var spawned = player?.NetworkManager?.SpawnManager?.SpawnedObjectsList;
            if (spawned == null) return;
            foreach (var no in spawned)
            {
                if (no == null) continue;
                var pk = no.GetComponent<MirrorPuck>();
                if (pk == null) continue;
                if (!MirrorSynchronizedObjectManager.LatestPositions.TryGetValue(no.NetworkObjectId, out var x)) continue;
                Vector3 d = x.Position - myPos;
                float r = d.sqrMagnitude;
                for (int i = 0; i < N_PUCKS; i++)
                {
                    if (r < dists[i])
                    {
                        for (int j = N_PUCKS - 1; j > i; j--) { dists[j] = dists[j-1]; ds[j] = ds[j-1]; }
                        dists[i] = r; ds[i] = d; break;
                    }
                }
            }
            const int Base = 17;
            for (int i = 0; i < N_PUCKS; i++)
            {
                if (dists[i] >= float.MaxValue) break;
                Vector3 dBody = WorldToBody(ds[i], fwd, right);
                obs[Base + i * 4 + 0] = dBody.x;
                obs[Base + i * 4 + 1] = dBody.y;
                obs[Base + i * 4 + 2] = dBody.z;
            }
        }

        private static void EmitOtherPlayersToObs(
            float[] obs, MirrorPlayer player, MirrorPlayerBodyV2 myBody,
            Vector3 myPos, Vector3 fwd, Vector3 right, int myTeam, Cache cache)
        {
            Span<float> tmDist = stackalloc float[N_TEAMMATES];
            Span<int>   tmIdx  = stackalloc int[N_TEAMMATES];
            Span<float> opDist = stackalloc float[N_OPPONENTS];
            Span<int>   opIdx  = stackalloc int[N_OPPONENTS];
            for (int i = 0; i < N_TEAMMATES; i++) { tmDist[i] = float.MaxValue; tmIdx[i] = -1; }
            for (int i = 0; i < N_OPPONENTS; i++) { opDist[i] = float.MaxValue; opIdx[i] = -1; }

            var spawned = player?.NetworkManager?.SpawnManager?.SpawnedObjectsList;
            if (spawned == null) return;

            int seen = 0;
            foreach (var no in spawned)
            {
                if (no == null) continue;
                var body = no.GetComponent<MirrorPlayerBodyV2>();
                if (body == null) continue;
                if (myBody != null && body.NetworkObjectId == myBody.NetworkObjectId) continue;
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
                if (team == myTeam)
                {
                    for (int i = 0; i < N_TEAMMATES; i++)
                    {
                        if (r < tmDist[i])
                        {
                            for (int j = N_TEAMMATES - 1; j > i; j--) { tmDist[j] = tmDist[j-1]; tmIdx[j] = tmIdx[j-1]; }
                            tmDist[i] = r; tmIdx[i] = seen; break;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < N_OPPONENTS; i++)
                    {
                        if (r < opDist[i])
                        {
                            for (int j = N_OPPONENTS - 1; j > i; j--) { opDist[j] = opDist[j-1]; opIdx[j] = opIdx[j-1]; }
                            opDist[i] = r; opIdx[i] = seen; break;
                        }
                    }
                }
                cache.OtherCache[seen] = (d, x.Rotation, team);
                seen++;
                if (seen >= cache.OtherCache.Length) break;
            }

            const int TmBase = 33;
            const int OpBase = 69;
            for (int i = 0; i < N_TEAMMATES; i++)
            {
                if (tmIdx[i] < 0) break;
                var (d, rot, _) = cache.OtherCache[tmIdx[i]];
                Vector3 dBody = WorldToBody(d, fwd, right);
                Vector3 oFwd = rot * Vector3.forward; oFwd.y = 0f;
                float yawRel = oFwd.sqrMagnitude > 1e-4f
                    ? Mathf.Atan2(oFwd.x, oFwd.z) - Mathf.Atan2(fwd.x, fwd.z) : 0f;
                obs[TmBase + i * 4 + 0] = dBody.x;
                obs[TmBase + i * 4 + 1] = dBody.z;
                obs[TmBase + i * 4 + 2] = Mathf.Repeat(yawRel + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
            }
            for (int i = 0; i < N_OPPONENTS; i++)
            {
                if (opIdx[i] < 0) break;
                var (d, rot, _) = cache.OtherCache[opIdx[i]];
                Vector3 dBody = WorldToBody(d, fwd, right);
                Vector3 oFwd = rot * Vector3.forward; oFwd.y = 0f;
                float yawRel = oFwd.sqrMagnitude > 1e-4f
                    ? Mathf.Atan2(oFwd.x, oFwd.z) - Mathf.Atan2(fwd.x, fwd.z) : 0f;
                obs[OpBase + i * 4 + 0] = dBody.x;
                obs[OpBase + i * 4 + 1] = dBody.z;
                obs[OpBase + i * 4 + 2] = Mathf.Repeat(yawRel + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
            }
        }

        private static int ResolvePhaseSlotIndex(MirrorPlayer player)
        {
            if (player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return -1;
            foreach (var no in player.NetworkManager.SpawnManager.SpawnedObjectsList)
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

        private static void ResolveGameStats(
            MirrorPlayer player, out float timeNorm, out float scoreDiff10, out float periodNorm)
        {
            timeNorm = 0f; scoreDiff10 = 0f; periodNorm = 0f;
            if (player?.NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return;
            foreach (var no in player.NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var gm = no.GetComponent<MirrorGameManager>();
                if (gm == null) continue;
                var gs = gm.GameState.Value;
                int myTeam = (int)player.Team;
                int diff = (myTeam == (int)PlayerTeam.Blue) ? (gs.BlueScore - gs.RedScore) : (gs.RedScore - gs.BlueScore);
                scoreDiff10 = Mathf.Clamp(diff / 10f, -1f, 1f);
                periodNorm  = Mathf.Clamp(gs.Period / 3f, 0f, 1f);
                timeNorm    = Mathf.Clamp(gs.Time / 600f, 0f, 1f);
                return;
            }
        }

        private static Vector3 WorldToBody(Vector3 v, Vector3 fwd, Vector3 right)
        {
            return new Vector3(Vector3.Dot(v, right), v.y, Vector3.Dot(v, fwd));
        }
    }
}
