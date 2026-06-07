using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PuckStressTest.Mirror
{
    // Bot-side mirror of Puck's `Player` NetworkBehaviour. Declares the
    // same 14 NetworkVariables in the same order with the same generic
    // types so NGO's SceneObject deserializer reads exactly the bytes
    // the server wrote.
    //
    // CRITICAL invariants (changing any of these silently breaks sync):
    //   1. Field DECLARATION ORDER must match Player.cs in the decompile.
    //   2. Each NetworkVariable<T> generic argument T must match exactly.
    //      Enum types must have the same underlying integer width.
    //   3. The class name doesn't matter (NGO doesn't bind by name), but
    //      the count and order of NetworkVariables does.
    //
    // The bot does not write to these — they are read-only mirrors of
    // server state. Read them in bot behavior code via `Value`.
    //
    // Source for transcription (build B323):
    //   the decompiled Puck assembly, Puck/Player.cs
    //   lines 42 .. 81 (14 fields).
    //
    // B323 collapsed B202's 36 flat NVs into 14: State+Team+Role merged
    // into PlayerGameState; 23 cosmetic FixedString NVs collapsed into
    // PlayerCustomizationState (22 int IDs); SetPlayer{State,Team,...}
    // RPCs replaced by Client_Request* state-machine RPCs; PlayerData
    // (cosmetics, username, number) now arrives via the connection-
    // approval backend, NOT via a Subscription RPC.
    public class MirrorPlayer : NetworkBehaviour
    {
        public NetworkVariable<PlayerGameState>          GameState              = new();
        public NetworkVariable<PlayerCustomizationState> CustomizationState     = new();
        public NetworkVariable<PlayerHandedness>         Handedness             = new();
        public NetworkVariable<FixedString32Bytes>       SteamId                = new();
        public NetworkVariable<FixedString32Bytes>       Username               = new();
        public NetworkVariable<int>                      Number                 = new();
        public NetworkVariable<int>                      PatreonLevel           = new();
        public NetworkVariable<int>                      AdminLevel             = new();
        public NetworkVariable<int>                      Goals                  = new();
        public NetworkVariable<int>                      Assists                = new();
        public NetworkVariable<ulong>                    Ping                   = new();  // B323: was int in B202
        public NetworkVariable<NetworkObjectReference>   PlayerPositionReference = new();
        public NetworkVariable<bool>                     IsMuted                = new();
        public NetworkVariable<bool>                     IsReplay               = new();

        // Computed accessors for legacy consumers that read .State / .Team / .Role
        // as plain enums (PostB323: read these directly without `.Value`).
        public PlayerState State => GameState.Value.Phase;
        public PlayerTeam  Team  => GameState.Value.Team;
        public PlayerRole  Role  => GameState.Value.Role;

        // RPC method-IDs for own-Player request RPCs. Source: B323
        // Player.cs lines 1173-1177 (registration block).
        private const uint Id_Client_RequestTeamRpc           = 2620210071u;  // (PlayerTeam team)
        private const uint Id_Client_RequestClaimPositionRpc  = 949682089u;   // (NetworkObjectReference)
        private const uint Id_Client_RequestTeamSelectRpc     = 4280154797u;  // ()
        private const uint Id_Client_RequestPositionSelectRpc = 3454979199u;  // ()
        private const uint Id_Client_RequestHandednessRpc     = 744616166u;   // (PlayerHandedness)

        // Reflection handles for NGO's protected RPC plumbing — see
        // MirrorPlayerInput for the rationale (RPC method-id hash
        // includes the assembly module name, so we can't use weaver-
        // generated calls; reflection on __beginSendRpc / __endSendRpc
        // is the cheapest workaround).
        private static readonly System.Reflection.MethodInfo s_BeginSendRpc =
            typeof(NetworkBehaviour).GetMethod(
                "__beginSendRpc",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.MethodInfo s_EndSendRpc =
            typeof(NetworkBehaviour).GetMethod(
                "__endSendRpc",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

        public void SendRequestTeamSelect()
        {
            if (!Ready()) return;
            var (writer, attr, rpc) = Begin(Id_Client_RequestTeamSelectRpc, RpcDelivery.Reliable);
            End(ref writer, Id_Client_RequestTeamSelectRpc, attr, rpc, RpcDelivery.Reliable);
        }

        public void SendRequestTeam(PlayerTeam team)
        {
            if (!Ready()) return;
            var (writer, attr, rpc) = Begin(Id_Client_RequestTeamRpc, RpcDelivery.Reliable);
            writer.WriteValueSafe(team, default(FastBufferWriter.ForEnums));
            End(ref writer, Id_Client_RequestTeamRpc, attr, rpc, RpcDelivery.Reliable);
        }

        public void SendRequestPositionSelect()
        {
            if (!Ready()) return;
            var (writer, attr, rpc) = Begin(Id_Client_RequestPositionSelectRpc, RpcDelivery.Reliable);
            End(ref writer, Id_Client_RequestPositionSelectRpc, attr, rpc, RpcDelivery.Reliable);
        }

        public void SendRequestClaimPosition(NetworkObjectReference posRef)
        {
            if (!Ready()) return;
            var (writer, attr, rpc) = Begin(Id_Client_RequestClaimPositionRpc, RpcDelivery.Reliable);
            writer.WriteValueSafe(posRef, default(FastBufferWriter.ForNetworkSerializable));
            End(ref writer, Id_Client_RequestClaimPositionRpc, attr, rpc, RpcDelivery.Reliable);
        }

        public void SendRequestHandedness(PlayerHandedness handedness)
        {
            if (!Ready()) return;
            var (writer, attr, rpc) = Begin(Id_Client_RequestHandednessRpc, RpcDelivery.Reliable);
            writer.WriteValueSafe(handedness, default(FastBufferWriter.ForEnums));
            End(ref writer, Id_Client_RequestHandednessRpc, attr, rpc, RpcDelivery.Reliable);
        }

        private bool Ready()
        {
            if (NetworkManager == null || !NetworkManager.IsListening) return false;
            if (s_BeginSendRpc == null || s_EndSendRpc == null) return false;
            return true;
        }

        private (FastBufferWriter, RpcAttribute.RpcAttributeParams, RpcParams) Begin(uint id, RpcDelivery delivery)
        {
            var attr = new RpcAttribute.RpcAttributeParams { Delivery = delivery };
            var rpc = default(RpcParams);
            object[] args = { id, rpc, attr, SendTo.Server, delivery };
            var writer = (FastBufferWriter)s_BeginSendRpc.Invoke(this, args);
            return (writer, attr, rpc);
        }

        private void End(ref FastBufferWriter writer, uint id,
                         RpcAttribute.RpcAttributeParams attr, RpcParams rpc, RpcDelivery delivery)
        {
            object[] args = { writer, id, rpc, attr, SendTo.Server, delivery };
            s_EndSendRpc.Invoke(this, args);
        }

        // Drive the player state machine. The bot does NOT send a
        // subscription RPC any more (B323 removed it — PlayerData
        // arrives via the connection-approval backend, which our
        // BotAuthBypassMod fabricates server-side). We just need to
        // drive Phase transitions:
        //   None       → RequestTeamSelect    → TeamSelect
        //   TeamSelect → RequestTeam(Red/Blue) → (server stays in TS until valid)
        //              → RequestPositionSelect → PositionSelect
        //   PositionSelect → RequestClaimPosition(NOref) → Play
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            string tag = $"[MirrorPlayer NID={NetworkObjectId} owner={OwnerClientId}]";
            // Only log spawn for our own bot's Player. Across 12 bots ×
            // 12 mirrors × N cycles, owner-only spawn logging keeps the
            // log thread sane.
            if (IsOwner)
                Debug.Log($"{tag} spawned. initial: phase={State} team={Team} role={Role} number={Number.Value} username='{Username.Value}' steamId='{SteamId.Value}'");

            if (IsOwner)
            {
                var input = GetComponent<MirrorPlayerInput>();
                if (input != null)
                {
                    var bi = NetworkManager?.gameObject?.GetComponent<BotInstance>();
                    string brainKind = bi?.Config?.Brain ?? "heuristic";
                    int    botIndex  = bi?.Index ?? 0;
                    float  tickHz    = bi?.Config?.InputTickHz ?? 30f;

                    var snap = gameObject.AddComponent<PuckStressTest.Logging.SnapshotLogger>();
                    snap.BotIndex = botIndex;
                    snap.OutputDirectory = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? ".",
                        "Logs", "snapshots");
                    snap.Bind(this, input, null);
                    snap.BeginEpisode();

                    if (brainKind == "onnx")
                    {
                        var ob = gameObject.AddComponent<PuckStressTest.Brain.OnnxBrain>();
                        ob.PolicyPath = bi?.Config?.PolicyPath ?? "";
                        ob.TickHz     = tickHz;
                        ob.BotIndex   = botIndex;
                        ob.Snapshot   = snap;
                        ob.VoteStart  = bi?.Config?.VoteStart ?? false;
                        ob.Bind(this, input, null);
                        Debug.Log($"{tag} wired OnnxBrain + SnapshotLogger b={botIndex} — tick={tickHz} Hz, policy={ob.PolicyPath}, vs={ob.VoteStart}, snapshots → {snap.OutputDirectory}");
                    }
                    else
                    {
                        var brain = gameObject.AddComponent<BotBrain>();
                        brain.Input = input;
                        brain.Player = this;
                        brain.TickHz = tickHz;
                        brain.BotIndex = botIndex;
                        brain.VoteStart = bi?.Config?.VoteStart ?? false;
                        brain.VoteWarmup = bi?.Config?.VoteWarmup ?? false;
                        brain.VoteWarmupAfterSeconds = bi?.Config?.VoteWarmupAfterSeconds ?? 30f;
                        brain.Snapshot = snap;
                        Debug.Log($"{tag} wired BotBrain + SnapshotLogger b={botIndex} — tick={tickHz} Hz, snapshots → {snap.OutputDirectory}");
                    }
                }
                else
                {
                    Debug.LogWarning($"{tag} owned but no MirrorPlayerInput sibling found");
                }

                // Kick off the state-machine driver. B323 server starts
                // each player in phase=None; we have to explicitly
                // request the TeamSelect transition.
                System.Action<PlayerState> handleState = (newPhase) =>
                {
                    if (newPhase == PlayerState.None)
                    {
                        Debug.Log($"{tag} requesting TeamSelect");
                        SendRequestTeamSelect();
                    }
                    else if (newPhase == PlayerState.TeamSelect)
                    {
                        var team = (OwnerClientId % 2 == 0) ? PlayerTeam.Red : PlayerTeam.Blue;
                        Debug.Log($"{tag} requesting team={team}");
                        SendRequestTeam(team);
                        Debug.Log($"{tag} requesting PositionSelect");
                        SendRequestPositionSelect();
                    }
                    else if (newPhase == PlayerState.PositionSelect)
                    {
                        if (_claimPositionCo == null)
                            _claimPositionCo = StartCoroutine(ClaimPositionLoop(tag));
                    }
                    else if (_claimPositionCo != null)
                    {
                        // Left PositionSelect (Play, Spectate, …); cancel
                        // any in-flight claim loop so it doesn't keep
                        // churning.
                        StopCoroutine(_claimPositionCo);
                        _claimPositionCo = null;
                    }
                };

                GameState.OnValueChanged += (oldGs, newGs) =>
                {
                    if (oldGs.Phase != newGs.Phase)
                    {
                        Debug.Log($"{tag} phase {oldGs.Phase} → {newGs.Phase}");
                        handleState(newGs.Phase);
                    }
                    if (oldGs.Team != newGs.Team)
                        Debug.Log($"{tag} team {oldGs.Team} → {newGs.Team}");
                    if (oldGs.Role != newGs.Role)
                        Debug.Log($"{tag} role {oldGs.Role} → {newGs.Role}");
                };

                // OnValueChanged only fires on CHANGE. If the server
                // already placed us in TeamSelect before our subscription
                // registers, the change-event never fires. Drive once
                // from the current value as well.
                Debug.Log($"{tag} initial phase on spawn: {State}");
                handleState(State);
            }
        }

        // Tracks the in-flight ClaimPositionLoop so phase cycles don't
        // stack overlapping coroutines.
        private Coroutine _claimPositionCo;

        // Find an unclaimed PlayerPosition and send
        // Client_RequestClaimPositionRpc on our OWN Player (B323 moved
        // this RPC off PlayerPositionManager and onto Player itself).
        // The server silently rejects wrong-team claims, so we cycle
        // through positions until one of OUR claims lands — detected via
        // the position's ClaimedByPlayerReference NV pointing at our
        // Player NID, NOT via our Phase: when joining mid-match the
        // server defers PositionSelect → Play until the next FaceOff
        // (StandardGameMode.PreparePlayerForGamePhase no-ops during
        // BlueScore/RedScore/Replay), so the phase can lag a claim by
        // many seconds.
        //
        // Once seated we HOLD: StandardGameMode.OnPlayerRequestPosition
        // releases a player's previous seat on every new claim (and
        // re-claiming your own seat toggles it off), so claiming while
        // seated is what caused the 2026-06-04 musical-chairs bug where
        // bots stole each other's seats forever and some never spawned.
        // The loop never bails permanently — a seatless bot keeps trying
        // (rate-limited) for as long as it stays in PositionSelect; the
        // handleState watcher cancels us once the server promotes us.
        private System.Collections.IEnumerator ClaimPositionLoop(string tag)
        {
            try
            {
                yield return new WaitForSeconds(0.1f);

                var positions = FindAllSpawnedNB<MirrorPlayerPosition>();
                Debug.Log($"{tag} ClaimPositionLoop: {positions.Count} PlayerPosition objects visible");

                int attempts = 0;
                int idx = 0;
                bool announcedSeated = false;
                while (State != PlayerState.Play)
                {
                    if (positions.Count == 0)
                    {
                        // Scene objects not all spawned yet — refetch.
                        yield return new WaitForSeconds(1f);
                        positions = FindAllSpawnedNB<MirrorPlayerPosition>();
                        continue;
                    }

                    var mine = MyClaimedPosition(positions);
                    if (mine != null)
                    {
                        // Seated. Do NOT claim anything else — just wait
                        // for the next FaceOff/Play game phase to promote
                        // us to Play.
                        if (!announcedSeated)
                        {
                            announcedSeated = true;
                            Debug.Log($"{tag} ClaimPositionLoop seated at PlayerPosition NID={mine.NetworkObjectId} after {attempts} attempt(s); holding until server promotes us to Play");
                        }
                        yield return new WaitForSeconds(0.5f);
                        continue;
                    }
                    announcedSeated = false;  // lost the seat somehow — resume claiming

                    var pp = positions[idx % positions.Count];
                    idx++;
                    if (pp == null || pp.IsClaimed) { yield return null; continue; }
                    attempts++;
                    var posRef = new NetworkObjectReference(pp.NetworkObject);
                    if (attempts <= 30 || attempts % 10 == 0)
                        Debug.Log($"{tag} claim attempt #{attempts} → PlayerPosition NID={pp.NetworkObjectId}");
                    SendRequestClaimPosition(posRef);
                    yield return new WaitForSeconds(0.6f);
                }

                Debug.Log($"{tag} ClaimPositionLoop succeeded after {attempts} attempt(s)");
            }
            finally
            {
                _claimPositionCo = null;
            }
        }

        // The PlayerPosition currently claimed by OUR Player object, or
        // null. The server writes ClaimedByPlayerReference =
        // NetworkObjectReference(player.NetworkObject) on a successful
        // claim, so comparing its NID against our own NetworkObjectId is
        // exact.
        private MirrorPlayerPosition MyClaimedPosition(
            System.Collections.Generic.List<MirrorPlayerPosition> positions)
        {
            ulong myNid = NetworkObjectId;
            foreach (var pp in positions)
            {
                if (pp == null) continue;
                try
                {
                    if (pp.ClaimedByPlayerReference.Value.NetworkObjectId == myNid)
                        return pp;
                }
                catch { /* NV not readable yet — treat as not ours */ }
            }
            return null;
        }

        private T FindFirstSpawnedNB<T>() where T : NetworkBehaviour
        {
            if (NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return null;
            foreach (var no in NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var c = no.GetComponent<T>();
                if (c != null) return c;
            }
            return null;
        }

        private System.Collections.Generic.List<T> FindAllSpawnedNB<T>() where T : NetworkBehaviour
        {
            var result = new System.Collections.Generic.List<T>();
            if (NetworkManager?.SpawnManager?.SpawnedObjectsList == null) return result;
            foreach (var no in NetworkManager.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                var c = no.GetComponent<T>();
                if (c != null) result.Add(c);
            }
            return result;
        }
    }

    // The other 5 NetworkBehaviours on Puck's Player prefab
    // (PlayerController, PlayerInput, PlayerInputController,
    // PlayerVoiceRecorder, PlayerVoiceRecorderController) declare zero
    // NetworkVariables, so empty stubs suffice for byte alignment.
    //
    // MirrorPlayerInput is the only one with custom code: it adds
    // outbound input RPC senders (Move, LookAngle, RaycastOriginAngle,
    // etc.) keyed to the EXACT method-IDs Puck's PlayerInput uses on
    // the wire. We can't let NGO's weaver generate IDs because the
    // hash is `XXHash32($"{ModuleName} / {FullName}")` and our module
    // (BotHost.dll) differs from Puck.dll. So we call NGO's protected
    // __beginSendRpc / __endSendRpc directly with hard-coded IDs from
    // the decompile (B323 PlayerInput.cs registration block lines
    // 1380-1415).
    public class MirrorPlayerController       : NetworkBehaviour { }
    public class MirrorPlayerInputController  : NetworkBehaviour { }
    public class MirrorPlayerVoiceRecorder    : NetworkBehaviour { }
    public class MirrorPlayerVoiceRecCtrl     : NetworkBehaviour { }

    public class MirrorPlayerInput : NetworkBehaviour
    {
        // RPC method-IDs harvested from B323 PlayerInput.cs registration
        // block (lines 1380-1415). All hashes differ from B202.
        private const uint Id_Client_MoveInputRpc               = 2880114289u;  // (short x, short y) BitPacked; Reliable
        private const uint Id_Client_RaycastOriginAngleInputRpc = 4145643342u;  // (short x=pitch, short y=yaw) BitPacked; Unreliable
        private const uint Id_Client_LookAngleInputRpc          = 2301322626u;  // (short x=pitch, short y=yaw) BitPacked; Unreliable
        private const uint Id_Client_BladeAngleInputRpc         = 4018011136u;  // (sbyte) ForPrimitives; Reliable
        private const uint Id_Client_SlideInputRpc              = 3775351339u;  // (bool); Reliable
        private const uint Id_Client_SprintInputRpc             = 3297803930u;  // (bool); Reliable
        private const uint Id_Client_StopInputRpc               = 212770831u;   // (bool); Reliable
        private const uint Id_Client_ExtendLeftInputRpc         = 537498773u;   // (bool); Reliable
        private const uint Id_Client_ExtendRightInputRpc        = 4044541524u;  // (bool); Reliable
        private const uint Id_Client_DashLeftInputRpc           = 1929006103u;  // (); Reliable
        private const uint Id_Client_DashRightInputRpc          = 3135613427u;  // (); Reliable

        // NGO marks __beginSendRpc / __endSendRpc as `internal` in C#
        // source. At post-build IL time, RuntimeAccessModifiersILPP
        // patches them to `protected`, but Roslyn at compile time still
        // sees them as inaccessible from our derived class. Reflection
        // is the cheapest workaround; the calls happen at most 30/sec
        // per bot, so reflection cost is irrelevant.
        private static readonly System.Reflection.MethodInfo s_BeginSendRpc =
            typeof(NetworkBehaviour).GetMethod(
                "__beginSendRpc",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.MethodInfo s_EndSendRpc =
            typeof(NetworkBehaviour).GetMethod(
                "__endSendRpc",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

        // Real-client HasChanged gates: MoveInput fires on any value
        // change (predicate null); StickRaycastOrigin / LookAngle fire
        // when Vector2.Distance(lastSent, current) > 0.1 degrees (in
        // short space: ~9 short units, threshold² = 81).
        private bool _haveLastMove;
        private short _lastMoveX, _lastMoveY;
        private bool _haveLastStickAngle;
        private short _lastStickX, _lastStickY;
        private bool _haveLastLookAngle;
        private short _lastLookX, _lastLookY;
        private const int AngleDistSquaredThresholdShorts = 81;

        public struct LastSentAction
        {
            public short MoveX, MoveY;
            public short StickPitch, StickYaw;
            public short LookPitch, LookYaw;
            public sbyte BladeAngle;
            public bool  Slide, Sprint, Stop;
            public bool  ExtendLeft, ExtendRight;
            public bool  DashLeft, DashRight;
        }
        private LastSentAction _last;
        public LastSentAction GetLastSentAction() => _last;
        public void ClearEdgeFlags() { _last.DashLeft = false; _last.DashRight = false; }

        public void SendMove(short x, short y)
        {
            _last.MoveX = x; _last.MoveY = y;
            if (_haveLastMove && x == _lastMoveX && y == _lastMoveY) return;
            _haveLastMove = true;
            _lastMoveX = x; _lastMoveY = y;
            SendBitPackedShortPair(Id_Client_MoveInputRpc, x, y, RpcDelivery.Reliable);
        }

        public void SendRaycastOriginAngle(short x, short y)
        {
            _last.StickPitch = x; _last.StickYaw = y;
            if (_haveLastStickAngle)
            {
                int dx = x - _lastStickX;
                int dy = y - _lastStickY;
                if (dx * dx + dy * dy <= AngleDistSquaredThresholdShorts) return;
            }
            _haveLastStickAngle = true;
            _lastStickX = x; _lastStickY = y;
            SendBitPackedShortPair(Id_Client_RaycastOriginAngleInputRpc, x, y, RpcDelivery.Unreliable);
        }

        public void SendLookAngle(short x, short y)
        {
            _last.LookPitch = x; _last.LookYaw = y;
            if (_haveLastLookAngle)
            {
                int dx = x - _lastLookX;
                int dy = y - _lastLookY;
                if (dx * dx + dy * dy <= AngleDistSquaredThresholdShorts) return;
            }
            _haveLastLookAngle = true;
            _lastLookX = x; _lastLookY = y;
            SendBitPackedShortPair(Id_Client_LookAngleInputRpc, x, y, RpcDelivery.Unreliable);
        }

        public void SendSlide(bool value)        { _last.Slide = value;        SendBoolRpc(Id_Client_SlideInputRpc,        value, RpcDelivery.Reliable); }
        public void SendSprint(bool value)       { _last.Sprint = value;       SendBoolRpc(Id_Client_SprintInputRpc,       value, RpcDelivery.Reliable); }
        public void SendStop(bool value)         { _last.Stop = value;         SendBoolRpc(Id_Client_StopInputRpc,         value, RpcDelivery.Reliable); }
        public void SendExtendLeft(bool value)   { _last.ExtendLeft = value;   SendBoolRpc(Id_Client_ExtendLeftInputRpc,   value, RpcDelivery.Reliable); }
        public void SendExtendRight(bool value)  { _last.ExtendRight = value;  SendBoolRpc(Id_Client_ExtendRightInputRpc,  value, RpcDelivery.Reliable); }

        public void SendDashLeft()  { _last.DashLeft = true;  SendNoArgRpc(Id_Client_DashLeftInputRpc,  RpcDelivery.Reliable); }
        public void SendDashRight() { _last.DashRight = true; SendNoArgRpc(Id_Client_DashRightInputRpc, RpcDelivery.Reliable); }

        public void SendBladeAngle(sbyte value)
        {
            _last.BladeAngle = value;
            if (NetworkManager == null || !NetworkManager.IsListening) return;
            if (s_BeginSendRpc == null || s_EndSendRpc == null) return;
            var rpcParams  = default(RpcParams);
            var attrParams = new RpcAttribute.RpcAttributeParams { Delivery = RpcDelivery.Reliable };
            object[] beginArgs = { Id_Client_BladeAngleInputRpc, rpcParams, attrParams, SendTo.Server, RpcDelivery.Reliable };
            var writer = (FastBufferWriter)s_BeginSendRpc.Invoke(this, beginArgs);
            writer.WriteValueSafe(value, default(FastBufferWriter.ForPrimitives));
            object[] endArgs = { writer, Id_Client_BladeAngleInputRpc, rpcParams, attrParams, SendTo.Server, RpcDelivery.Reliable };
            s_EndSendRpc.Invoke(this, endArgs);
        }

        private void SendNoArgRpc(uint rpcId, RpcDelivery delivery)
        {
            if (NetworkManager == null || !NetworkManager.IsListening) return;
            if (s_BeginSendRpc == null || s_EndSendRpc == null) return;
            var rpcParams  = default(RpcParams);
            var attrParams = new RpcAttribute.RpcAttributeParams { Delivery = delivery };
            object[] beginArgs = { rpcId, rpcParams, attrParams, SendTo.Server, delivery };
            var writer = (FastBufferWriter)s_BeginSendRpc.Invoke(this, beginArgs);
            object[] endArgs = { writer, rpcId, rpcParams, attrParams, SendTo.Server, delivery };
            s_EndSendRpc.Invoke(this, endArgs);
        }

        private void SendBoolRpc(uint rpcId, bool value, RpcDelivery delivery)
        {
            if (NetworkManager == null || !NetworkManager.IsListening) return;
            if (s_BeginSendRpc == null || s_EndSendRpc == null) return;
            var rpcParams  = default(RpcParams);
            var attrParams = new RpcAttribute.RpcAttributeParams { Delivery = delivery };
            object[] beginArgs = { rpcId, rpcParams, attrParams, SendTo.Server, delivery };
            var writer = (FastBufferWriter)s_BeginSendRpc.Invoke(this, beginArgs);
            writer.WriteValueSafe(value, default(FastBufferWriter.ForPrimitives));
            object[] endArgs = { writer, rpcId, rpcParams, attrParams, SendTo.Server, delivery };
            s_EndSendRpc.Invoke(this, endArgs);
        }

        private void SendBitPackedShortPair(uint rpcId, short a, short b, RpcDelivery delivery)
        {
            if (NetworkManager == null || !NetworkManager.IsListening) return;
            if (s_BeginSendRpc == null || s_EndSendRpc == null)
            {
                Debug.LogError("[MirrorPlayerInput] NGO __beginSendRpc / __endSendRpc not resolvable; check NGO version.");
                return;
            }
            var rpcParams = default(RpcParams);
            var attrParams = new RpcAttribute.RpcAttributeParams { Delivery = delivery };
            object[] beginArgs = { rpcId, rpcParams, attrParams, SendTo.Server, delivery };
            var writer = (FastBufferWriter)s_BeginSendRpc.Invoke(this, beginArgs);
            BytePacker.WriteValueBitPacked(writer, a);
            BytePacker.WriteValueBitPacked(writer, b);
            object[] endArgs = { writer, rpcId, rpcParams, attrParams, SendTo.Server, delivery };
            s_EndSendRpc.Invoke(this, endArgs);
        }
    }
}
