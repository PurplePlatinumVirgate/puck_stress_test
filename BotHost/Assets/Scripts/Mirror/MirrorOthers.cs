using System;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PuckStressTest.Mirror
{
    // Mirror of Puck's NetworkObjectCollision struct
    // (puckdecompile/Puck_B323/Puck/NetworkObjectCollision.cs). Layout
    // must match exactly because NetworkObjectCollisionBuffer declares
    // NetworkList<NetworkObjectCollision>, and NetworkList<T> uses
    // T's NetworkSerialize for per-element wire format.
    public struct MirrorNetworkObjectCollision : INetworkSerializable, System.IEquatable<MirrorNetworkObjectCollision>
    {
        public NetworkObjectReference NetworkObjectReference;
        public float Time;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsReader)
            {
                var r = serializer.GetFastBufferReader();
                r.ReadValueSafe(out NetworkObjectReference, default(FastBufferWriter.ForNetworkSerializable));
                r.ReadValueSafe(out Time, default(FastBufferWriter.ForPrimitives));
                return;
            }
            var w = serializer.GetFastBufferWriter();
            w.WriteValueSafe(NetworkObjectReference, default(FastBufferWriter.ForNetworkSerializable));
            w.WriteValueSafe(Time, default(FastBufferWriter.ForPrimitives));
        }

        public bool Equals(MirrorNetworkObjectCollision other)
            => NetworkObjectReference.Equals(other.NetworkObjectReference) && Time == other.Time;
    }

    // Bot-side mirrors of every other Puck NetworkBehaviour referenced
    // by the networked prefabs. Each class declares its NetworkVariables
    // in the SAME ORDER as the server's class so SceneObject
    // deserialization byte-aligns.
    //
    // Source: B323 decompile,
    //   PlayerBody.cs:154         (8 NVs; was PlayerBodyV2.cs in B202.
    //                              Stamina/Speed: short → byte via
    //                              CompressedNetworkVariable<float,byte>)
    //   Stick.cs:73               (1 NV)
    //   Puck.cs:94                (1 NV — IsReplay only; was multi-NV in B202)
    //   StickPositioner.cs:104    (1 NV)
    //   PlayerCamera.cs:9         (1 NV)
    //   SpectatorCamera.cs:24     (1 NV)
    //   ReplayCamera.cs           (0 NVs)
    //   *Controller.cs            (0 NVs)
    //   PlayerPosition.cs:18      (1 NV — ClaimedByPlayerReference NOref)

    // B323's Puck class renamed PlayerBodyV2 → PlayerBody but kept the
    // 8 NV order intact. We keep the C# identifier MirrorPlayerBodyV2
    // for source-compat with bot consumers (BotBrain, ObsBuilder, etc.);
    // the wire layout is what matters and that stays in declaration
    // order. Stamina/Speed wire type changed short→byte (8-bit
    // compressed instead of 16-bit).
    public class MirrorPlayerBodyV2 : NetworkBehaviour
    {
        public NetworkVariable<NetworkObjectReference> PlayerReference   = new();
        public NetworkVariable<byte>                   StaminaCompressed = new();
        public NetworkVariable<byte>                   SpeedCompressed   = new();
        public NetworkVariable<bool>                   IsSprinting       = new();
        public NetworkVariable<bool>                   IsSliding         = new();
        public NetworkVariable<bool>                   IsStopping        = new();
        public NetworkVariable<bool>                   IsExtendedLeft    = new();
        public NetworkVariable<bool>                   IsExtendedRight   = new();

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            string tag = $"[MirrorPlayerBody NID={NetworkObjectId} owner={OwnerClientId}]";
            Debug.Log($"{tag} spawned. PlayerRef={PlayerReference.Value.NetworkObjectId} stamina={StaminaCompressed.Value} speed={SpeedCompressed.Value}");
        }
    }

    public class MirrorStick : NetworkBehaviour
    {
        public NetworkVariable<NetworkObjectReference> PlayerReference = new();
    }

    public class MirrorPuck : NetworkBehaviour
    {
        public NetworkVariable<bool> IsReplay = new();

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            string tag = $"[MirrorPuck NID={NetworkObjectId}]";
            Debug.Log($"{tag} spawned. initial: IsReplay={IsReplay.Value}");
            IsReplay.OnValueChanged += (a, b) => Debug.Log($"{tag} IsReplay {a} -> {b}");
        }
    }

    public class MirrorStickPositioner : NetworkBehaviour
    {
        public NetworkVariable<NetworkObjectReference> PlayerReference = new();
    }

    public class MirrorPlayerCamera : NetworkBehaviour
    {
        public NetworkVariable<NetworkObjectReference> PlayerReference = new();
    }

    public class MirrorSpectatorCamera : NetworkBehaviour
    {
        public NetworkVariable<NetworkObjectReference> PlayerReference = new();
    }

    public class MirrorReplayCamera : NetworkBehaviour { }

    // Scene-placed objects that DO have NetworkVariables.
    public class MirrorGameManager : NetworkBehaviour
    {
        public NetworkVariable<GameState> GameState = new();

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            string tag = $"[MirrorGameManager NID={NetworkObjectId}]";
            Debug.Log($"{tag} spawned. phase={GameState.Value.Phase} tick={GameState.Value.Tick} period={GameState.Value.Period}");
            // Log only PHASE transitions (rare events) so 12 bots × 12
            // manager mirrors don't pump the log thread on every tick
            // bump.
            GameState.OnValueChanged += (a, b) =>
            {
                if (a.Phase != b.Phase || a.Period != b.Period)
                    Debug.Log($"{tag} phase {a.Phase}→{b.Phase} period {a.Period}→{b.Period} blue={b.BlueScore} red={b.RedScore} OT={b.IsOvertime}");
            };
        }
    }

    // Mirror of Puck's `Server` struct (Puck_B323/Puck/Server.cs).
    // Carried by ServerManager.Server NetworkVariable. We don't read it
    // beyond byte alignment — the bot doesn't act on the broadcast
    // server identity — but the layout must match for NGO scene-sync to
    // consume the right number of bytes.
    public struct MirrorServer : INetworkSerializable, System.IEquatable<MirrorServer>
    {
        public FixedString32Bytes IpAddress;
        public ushort Port;
        public FixedString128Bytes Name;
        public int MaxPlayers;
        public int TickRate;
        public bool UseVoip;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsReader)
            {
                var r = serializer.GetFastBufferReader();
                r.ReadValueSafe(out IpAddress,  default(FastBufferWriter.ForFixedStrings));
                r.ReadValueSafe(out Port,       default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out Name,       default(FastBufferWriter.ForFixedStrings));
                r.ReadValueSafe(out MaxPlayers, default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out TickRate,   default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out UseVoip,    default(FastBufferWriter.ForPrimitives));
            }
            else
            {
                var w = serializer.GetFastBufferWriter();
                w.WriteValueSafe(IpAddress,  default(FastBufferWriter.ForFixedStrings));
                w.WriteValueSafe(Port,       default(FastBufferWriter.ForPrimitives));
                w.WriteValueSafe(Name,       default(FastBufferWriter.ForFixedStrings));
                w.WriteValueSafe(MaxPlayers, default(FastBufferWriter.ForPrimitives));
                w.WriteValueSafe(TickRate,   default(FastBufferWriter.ForPrimitives));
                w.WriteValueSafe(UseVoip,    default(FastBufferWriter.ForPrimitives));
            }
        }

        public bool Equals(MirrorServer o) =>
            IpAddress.Equals(o.IpAddress) && Port == o.Port && Name.Equals(o.Name) &&
            MaxPlayers == o.MaxPlayers && TickRate == o.TickRate && UseVoip == o.UseVoip;
    }

    // Scene-placed `Server Manager` NB. B323 dump shows one NV (a
    // Server struct). Bot doesn't use the value, but the byte slot
    // has to be consumed.
    public class MirrorServerManager : NetworkBehaviour
    {
        public NetworkVariable<MirrorServer> Server = new();
    }

    public class MirrorPlayerPosition : NetworkBehaviour
    {
        // B323: renamed from ClaimedByReference → ClaimedByPlayerReference.
        // Name doesn't matter for NGO sync (positional), but tracking the
        // current name in code makes greps against the decompile easier.
        public NetworkVariable<NetworkObjectReference> ClaimedByPlayerReference = new();

        public bool IsClaimed
        {
            get
            {
                try { return ClaimedByPlayerReference.Value.NetworkObjectId != 0; }
                catch { return false; }
            }
        }
    }

    // Scene-placed NetworkBehaviour for PlayerPositionManager. In B202
    // it carried Client_ClaimPositionRpc; in B323 that RPC moved to
    // Player.Client_RequestClaimPositionRpc (sent by the player's own
    // NetworkObject, not the manager). We keep this class as an empty
    // NB so prefab component-count alignment is preserved.
    public class MirrorPlayerPositionManager : NetworkBehaviour { }

    // Empty stubs for *Controller and the Synchronized* / collision
    // buffer behaviours (no NetworkVariables on any of these).
    public struct MirrorSynchronizedObjectData : INetworkSerializable, System.IEquatable<MirrorSynchronizedObjectData>
    {
        public ushort NetworkObjectId;
        public short X, Y, Z, Rx, Ry, Rz, Rw;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsReader)
            {
                var r = serializer.GetFastBufferReader();
                r.ReadValueSafe(out NetworkObjectId, default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out X,  default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out Y,  default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out Z,  default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out Rx, default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out Ry, default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out Rz, default(FastBufferWriter.ForPrimitives));
                r.ReadValueSafe(out Rw, default(FastBufferWriter.ForPrimitives));
                return;
            }
            var w = serializer.GetFastBufferWriter();
            w.WriteValueSafe(NetworkObjectId, default(FastBufferWriter.ForPrimitives));
            w.WriteValueSafe(X,  default(FastBufferWriter.ForPrimitives));
            w.WriteValueSafe(Y,  default(FastBufferWriter.ForPrimitives));
            w.WriteValueSafe(Z,  default(FastBufferWriter.ForPrimitives));
            w.WriteValueSafe(Rx, default(FastBufferWriter.ForPrimitives));
            w.WriteValueSafe(Ry, default(FastBufferWriter.ForPrimitives));
            w.WriteValueSafe(Rz, default(FastBufferWriter.ForPrimitives));
            w.WriteValueSafe(Rw, default(FastBufferWriter.ForPrimitives));
        }

        public bool Equals(MirrorSynchronizedObjectData o) =>
            NetworkObjectId == o.NetworkObjectId && X == o.X && Y == o.Y && Z == o.Z &&
            Rx == o.Rx && Ry == o.Ry && Rz == o.Rz && Rw == o.Rw;

        public Vector3 DecodePosition() => new Vector3(X / 655f, Y / 655f, Z / 655f);
    }

    // Receives Puck's Server_SynchronizeObjectsRpc (id 1738927239 — hash
    // UNCHANGED from B202 per SynchronizedObjectManager.cs:345), parses
    // the per-tick batch of (NID, position, rotation) snapshots, and
    // stores the latest position in a process-wide static dict indexed
    // by NID. BotBrain reads from `LatestPositions` to know where the
    // puck and other synced objects are.
    public class MirrorSynchronizedObjectManager : NetworkBehaviour
    {
        public const uint Id_Server_SynchronizeObjectsRpc = 1738927239u;

        public struct WorldXform { public Vector3 Position; public Quaternion Rotation; }
        public static readonly System.Collections.Generic.Dictionary<ulong, WorldXform> LatestPositions
            = new System.Collections.Generic.Dictionary<ulong, WorldXform>(64);

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            Debug.Log($"[MirrorSynchronizedObjectManager] spawned NID={NetworkObjectId}; snapshot RPC handler is registered. If LatestPositions stays empty during play, the server isn't including this client in synchronizedClientIds — see NOTES_null_bot_progress.md.");
        }

        private void Awake()
        {
            try
            {
                var nbType = typeof(NetworkBehaviour);
                var tableField = nbType.GetField("__rpc_func_table",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var outer = (System.Collections.IDictionary)tableField.GetValue(null);

                Type handlerType = null;
                foreach (var nt in nbType.GetNestedTypes(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic))
                {
                    if (nt.Name == "RpcReceiveHandler") { handlerType = nt; break; }
                }
                if (handlerType == null)
                {
                    Debug.LogError("[MirrorSynchronizedObjectManager] RpcReceiveHandler nested type not found on NetworkBehaviour");
                    return;
                }

                var ourType = GetType();
                System.Collections.IDictionary inner;
                if (!outer.Contains(ourType))
                {
                    var innerType = typeof(System.Collections.Generic.Dictionary<,>)
                        .MakeGenericType(typeof(uint), handlerType);
                    inner = (System.Collections.IDictionary)System.Activator.CreateInstance(innerType);
                    outer.Add(ourType, inner);
                }
                else
                {
                    inner = (System.Collections.IDictionary)outer[ourType];
                }
                if (!inner.Contains(Id_Server_SynchronizeObjectsRpc))
                {
                    var invoke = handlerType.GetMethod("Invoke");
                    var paramTypes = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
                    var forwardTo = typeof(MirrorSynchronizedObjectManager).GetMethod(
                        nameof(Static_HandleSnapshotRpc_Forward),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                    var dm = new System.Reflection.Emit.DynamicMethod(
                        "MirrorSyncObjMgr_RpcShim_" + Id_Server_SynchronizeObjectsRpc,
                        typeof(void),
                        paramTypes,
                        typeof(MirrorSynchronizedObjectManager).Module,
                        skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                    il.Emit(System.Reflection.Emit.OpCodes.Call, forwardTo);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var del = dm.CreateDelegate(handlerType);
                    inner.Add(Id_Server_SynchronizeObjectsRpc, del);

                    var nameTableField = nbType.GetField("__rpc_name_table",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (nameTableField != null)
                    {
                        var nameOuter = (System.Collections.IDictionary)nameTableField.GetValue(null);
                        if (!nameOuter.Contains(ourType))
                        {
                            var nameInnerType = typeof(System.Collections.Generic.Dictionary<,>)
                                .MakeGenericType(typeof(uint), typeof(string));
                            nameOuter.Add(ourType, System.Activator.CreateInstance(nameInnerType));
                        }
                        var nameInner = (System.Collections.IDictionary)nameOuter[ourType];
                        if (!nameInner.Contains(Id_Server_SynchronizeObjectsRpc))
                            nameInner.Add(Id_Server_SynchronizeObjectsRpc, "Server_SynchronizeObjectsRpc");
                    }

                    Debug.Log($"[MirrorSynchronizedObjectManager] registered snapshot RPC handler (id={Id_Server_SynchronizeObjectsRpc}) on {ourType.Name} via DynamicMethod shim");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[MirrorSynchronizedObjectManager] RPC handler registration failed: " + ex);
            }
        }

        private static void Static_HandleSnapshotRpc_Forward(NetworkBehaviour behaviour, FastBufferReader reader)
        {
            try
            {
                ByteUnpacker.ReadValueBitPacked(reader, out ushort tickId);
                reader.ReadValueSafe(out double serverTime, default(FastBufferWriter.ForPrimitives));
                reader.ReadValueSafe(out bool hasArray, default(FastBufferWriter.ForPrimitives));
                if (!hasArray) return;
                reader.ReadValueSafe(out int len);
                for (int i = 0; i < len; i++)
                {
                    reader.ReadValueSafe(out ushort nid, default(FastBufferWriter.ForPrimitives));
                    reader.ReadValueSafe(out short x,  default(FastBufferWriter.ForPrimitives));
                    reader.ReadValueSafe(out short y,  default(FastBufferWriter.ForPrimitives));
                    reader.ReadValueSafe(out short z,  default(FastBufferWriter.ForPrimitives));
                    reader.ReadValueSafe(out short rx, default(FastBufferWriter.ForPrimitives));
                    reader.ReadValueSafe(out short ry, default(FastBufferWriter.ForPrimitives));
                    reader.ReadValueSafe(out short rz, default(FastBufferWriter.ForPrimitives));
                    reader.ReadValueSafe(out short rw, default(FastBufferWriter.ForPrimitives));
                    LatestPositions[nid] = new WorldXform
                    {
                        Position = new Vector3(x / 655f, y / 655f, z / 655f),
                        Rotation = new Quaternion(rx / 32767f, ry / 32767f, rz / 32767f, rw / 32767f),
                    };
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[MirrorSynchronizedObjectManager] snapshot RPC parse failed: " + ex.Message);
            }
        }
    }

    public class MirrorPlayerBodyV2Controller     : NetworkBehaviour { }
    public class MirrorStickController            : NetworkBehaviour { }
    public class MirrorStickPositionerController  : NetworkBehaviour { }
    public class MirrorPlayerCameraController     : NetworkBehaviour { }
    public class MirrorSpectatorCameraController  : NetworkBehaviour { }
    public class MirrorReplayCameraController     : NetworkBehaviour { }
    public class MirrorSynchronizedObject         : NetworkBehaviour { }
    public class MirrorSynchronizedObjectCtrl     : NetworkBehaviour { }
    // SynchronizedAudio declares 2 NetworkVariable<byte> NVs (Volume,
    // Pitch). Order matters; layout unchanged in B323.
    public class MirrorSynchronizedAudio : NetworkBehaviour
    {
        public NetworkVariable<byte> Volume = new();
        public NetworkVariable<byte> Pitch  = new();
    }
    public class MirrorSynchronizedAudioCtrl      : NetworkBehaviour { }

    public class MirrorNetworkObjectCollisionBuffer : NetworkBehaviour
    {
        public NetworkList<MirrorNetworkObjectCollision> Buffer;

        private void Awake()
        {
            Buffer = new NetworkList<MirrorNetworkObjectCollision>(
                null,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);
        }
    }

    public class MirrorEmpty : NetworkBehaviour { }

    // Sends a chat message via B323's ChatManager singleton. RPC is
    // Client_SendChatMessageRpc (hash 3638797367u) on the ChatManager
    // NetworkBehaviour, three args:
    //   string content, bool isQuickChat, bool isTeamChat
    // Previously (B202) chat was on UIChat with a different schema
    // (hasMessage/message/useTeamChat/isMuted) and host hash. Bots use
    // this to vote-start ("/vs") and vote-warmup ("/vw"). Finds
    // ChatManager by iterating spawned NetworkBehaviours and matching
    // type name (we don't have a typed reference because ChatManager
    // is server-only by class identity — we look it up by name on the
    // spawned scene-placed NB list).
    public static class ChatSender
    {
        private const uint Id_Client_SendChatMessageRpc = 3638797367u;

        private static readonly System.Reflection.MethodInfo s_BeginSendRpc =
            typeof(NetworkBehaviour).GetMethod(
                "__beginSendRpc",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.MethodInfo s_EndSendRpc =
            typeof(NetworkBehaviour).GetMethod(
                "__endSendRpc",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Plain chat (default: not a quick chat, not team-only). Used
        // for /vs / /vw commands.
        public static bool TrySend(NetworkManager nm, string message)
            => TrySend(nm, message, isQuickChat: false, isTeamChat: false);

        // Quick chat (renders in the receiving client's QuickChat UI
        // instead of the chat scroll). Used by BotChatter for
        // situational triggers.
        public static bool TrySendQuickChat(NetworkManager nm, string content, bool teamOnly = false)
            => TrySend(nm, content, isQuickChat: true, isTeamChat: teamOnly);

        private static bool TrySend(NetworkManager nm, string message, bool isQuickChat, bool isTeamChat)
        {
            if (nm?.SpawnManager?.SpawnedObjectsList == null) return false;
            if (s_BeginSendRpc == null || s_EndSendRpc == null) return false;

            // Find any NetworkBehaviour whose runtime type implements
            // the ChatManager singleton — we don't have Puck.dll linked,
            // so match by short type name across spawned NB components.
            // Server-side, ChatManager is a NetworkBehaviourSingleton;
            // it lives as a child of a scene-placed root. Iterate all
            // spawned objects and pick the first NB whose type name is
            // "ChatManager".
            NetworkBehaviour chat = null;
            foreach (var no in nm.SpawnManager.SpawnedObjectsList)
            {
                if (no == null) continue;
                foreach (var nb in no.GetComponentsInChildren<NetworkBehaviour>(true))
                {
                    if (nb != null && nb.GetType().Name == "ChatManager")
                    {
                        chat = nb;
                        break;
                    }
                }
                if (chat != null) break;
            }
            if (chat == null)
            {
                // Fall back to broadcasting from all NBs in case our
                // type-name match misses (different host class, mod
                // rename, etc.). This is the same belt-and-suspenders
                // pattern the B202 ChatSender used against UIManager.
                int sent = 0;
                foreach (var no in nm.SpawnManager.SpawnedObjectsList)
                {
                    if (no == null) continue;
                    foreach (var nb in no.GetComponentsInChildren<NetworkBehaviour>(true))
                    {
                        if (nb == null) continue;
                        try { SendOne(nb, message, isQuickChat, isTeamChat); sent++; }
                        catch { /* slots that don't host the RPC silently drop */ }
                    }
                }
                return sent > 0;
            }

            try { SendOne(chat, message, isQuickChat, isTeamChat); return true; }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChatSender] SendOne on ChatManager failed: " + ex.Message);
                return false;
            }
        }

        private static void SendOne(NetworkBehaviour nb, string message, bool isQuickChat, bool isTeamChat)
        {
            var rpcParams  = default(RpcParams);
            var attrParams = new RpcAttribute.RpcAttributeParams { Delivery = RpcDelivery.Reliable };
            object[] beginArgs = { Id_Client_SendChatMessageRpc, rpcParams, attrParams, SendTo.Server, RpcDelivery.Reliable };
            var writer = (FastBufferWriter)s_BeginSendRpc.Invoke(nb, beginArgs);
            // B323 wire format per ChatManager.__rpc_handler_3638797367:
            //   bool hasContent
            //   IF hasContent: string content (UTF-16, oneByteChars=false)
            //   bool isQuickChat
            //   bool isTeamChat
            // The leading hasContent bool is what previously caused
            // OverflowException server-side — without it the reader
            // interpreted the first byte of the string length as a
            // bool, then tried to ReadValueSafe<string> against the
            // remaining bytes which underflowed.
            bool hasContent = !string.IsNullOrEmpty(message);
            writer.WriteValueSafe(hasContent, default(FastBufferWriter.ForPrimitives));
            if (hasContent) writer.WriteValueSafe(message);
            writer.WriteValueSafe(isQuickChat, default(FastBufferWriter.ForPrimitives));
            writer.WriteValueSafe(isTeamChat,  default(FastBufferWriter.ForPrimitives));
            object[] endArgs = { writer, Id_Client_SendChatMessageRpc, rpcParams, attrParams, SendTo.Server, RpcDelivery.Reliable };
            s_EndSendRpc.Invoke(nb, endArgs);
        }
    }
}
