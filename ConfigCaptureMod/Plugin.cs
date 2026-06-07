using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace ConfigCaptureMod
{
    // Server-side Puck mod whose only job is to print the server's
    // NGO NetworkConfig hash + the inputs that go into it.
    //
    // Used ONCE to capture the hash so the bot harness can hardcode-match it
    // (path A from NOTES_null_bot_progress.md). Disable for actual profiling
    // runs — this mod itself contributes nothing to the system under test.
    //
    // Drop the built DLL at <server-CWD>/Plugins/ConfigCaptureMod/ConfigCaptureMod.dll
    // and run the server normally. Look for "[ConfigCaptureMod]" lines in the
    // server log.
    // B323 renamed Puck's plugin interface IPuckMod → IPuckPlugin.
    public class Plugin : IPuckPlugin
    {
        public const string Name = "ConfigCaptureMod";
        public const string Guid = "com.puckstresstest.configcapture";

        private static Harmony s_harmony;

        public bool OnEnable()
        {
            Log("Enabling — will dump NGO NetworkConfig once the server's NetworkManager is up.");
            try
            {
                s_harmony = new Harmony("com.puckstresstest.configcapture");
                s_harmony.PatchAll(typeof(Plugin).Assembly);
                Log("Harmony patches applied.");
            }
            catch (Exception ex) { LogError("Harmony apply failed: " + ex); }

            // The NetworkManager singleton may not exist yet when mods are
            // loaded (LateStart on ModManagerControllerV2 fires after the
            // first frame, but server transport startup is later). Spin a
            // coroutine that polls until ready.
            //
            // We piggy-back on any active MonoBehaviour by creating our own
            // GameObject — Puck mods don't ship with a host, so we make one.
            var go = new GameObject("ConfigCaptureMod_Host");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var host = go.AddComponent<CoroutineHost>();
            host.StartCoroutine(WaitAndDump());
            host.StartCoroutine(WatchSynchronizedClientIds());
            host.StartCoroutine(WatchAndDumpRuntimeLayouts());
            host.StartCoroutine(WaitAndDumpQuickChats());
            return true;
        }

        // Once players actually spawn (post-warmup), dump the
        // ground-truth ChildNetworkBehaviours array PER PREFAB HASH so
        // we can compare against PrefabRegistrar's BehaviourTypes
        // arrays. Fires once per unique (hash, NB-layout) — if a Puck
        // build adds an NB to a prefab, the layout signature will
        // change and we'll get a fresh dump line.
        //
        // Read-only: just walks the spawned-objects dict via reflection.
        private static IEnumerator WatchAndDumpRuntimeLayouts()
        {
            float deadline = Time.realtimeSinceStartup + 600f;
            var seen = new HashSet<string>();
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(2f);
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.SpawnManager?.SpawnedObjects == null) continue;

                foreach (var kv in nm.SpawnManager.SpawnedObjects)
                {
                    var no = kv.Value;
                    if (no == null) continue;

                    uint prefabHash = ReadField<uint>(no, "GlobalObjectIdHash");

                    // Match NGO's exact ChildNetworkBehaviours filter
                    // (NetworkObject.cs:2611): GetComponentsInChildren
                    // including inactive, where each NB.NetworkObject ==
                    // this. Preserve order; do NOT distinct.
                    var ordered = no.GetComponentsInChildren<NetworkBehaviour>(true)
                        .Where(b => b != null && b.NetworkObject == no)
                        .ToArray();

                    var nbDesc = ordered
                        .Select(b => $"{b.GetType().Name}({CountNVs(b)})")
                        .ToArray();

                    string sig = $"hash={prefabHash}|count={ordered.Length}|nbs=[{string.Join(",", nbDesc)}]";
                    if (seen.Add(sig))
                    {
                        Log($"[NB-LAYOUT] go='{no.gameObject.name}' nid={no.NetworkObjectId} {sig}");
                    }
                }
            }
        }

        // Dev-only diagnostic: every 5 s, dump SyncObjMgr's
        // synchronizedClientIds list so we can see exactly which
        // clients the server is broadcasting puck snapshots to. Used
        // to debug why the bot's MirrorSynchronizedObjectManager
        // never receives Server_SynchronizeObjectsRpc. Disable for
        // profiling runs (the periodic Debug.Log adds noise).
        private static IEnumerator WatchSynchronizedClientIds()
        {
            // Wait for SyncObjMgr to spawn server-side.
            float deadline = Time.realtimeSinceStartup + 30f;
            object syncMgr = null;
            while (Time.realtimeSinceStartup < deadline && syncMgr == null)
            {
                yield return new WaitForSeconds(0.5f);
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.SpawnManager?.SpawnedObjects == null) continue;
                foreach (var kv in nm.SpawnManager.SpawnedObjects)
                {
                    var no = kv.Value;
                    if (no == null) continue;
                    var nbs = no.GetComponentsInChildren<NetworkBehaviour>(true);
                    foreach (var nb in nbs)
                    {
                        if (nb == null) continue;
                        if (nb.GetType().Name == "SynchronizedObjectManager")
                        {
                            syncMgr = nb;
                            Log($"WatchSynchronizedClientIds: found SyncObjMgr (NID={kv.Key}, type={nb.GetType().FullName})");
                            break;
                        }
                    }
                    if (syncMgr != null) break;
                }
            }
            if (syncMgr == null) { LogError("WatchSynchronizedClientIds: SyncObjMgr not found within 30 s"); yield break; }

            var idsField = syncMgr.GetType().GetField("synchronizedClientIds",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (idsField == null) { LogError("WatchSynchronizedClientIds: synchronizedClientIds field not found"); yield break; }

            string lastSig = "";
            while (true)
            {
                yield return new WaitForSeconds(5f);
                try
                {
                    var ids = idsField.GetValue(syncMgr) as System.Collections.IEnumerable;
                    var sb = new System.Text.StringBuilder();
                    int count = 0;
                    if (ids != null)
                    {
                        foreach (var id in ids)
                        {
                            if (count > 0) sb.Append(',');
                            sb.Append(id);
                            count++;
                        }
                    }
                    string sig = $"count={count} ids=[{sb}]";
                    if (sig != lastSig)
                    {
                        Log($"WatchSynchronizedClientIds: {sig}");
                        lastSig = sig;
                    }
                }
                catch (Exception ex) { LogError("WatchSynchronizedClientIds tick failed: " + ex.Message); }
            }
        }

        public bool OnDisable()
        {
            Log("Disabling.");
            return true;
        }

        // One-shot dump of Puck's canonical QuickChat phrase list.
        // ChatManager (B323) has a SerializedDictionary<QuickChat-
        // Category, QuickChat[]> populated from Unity inspector data;
        // the decompile doesn't carry the strings but they sit in
        // memory once the server boots. Reflect them out so we can
        // paste them into BotBrain's chat pool.
        private static IEnumerator WaitAndDumpQuickChats()
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            MonoBehaviour chatManager = null;
            while (Time.realtimeSinceStartup < deadline && chatManager == null)
            {
                yield return new WaitForSeconds(0.5f);
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.SpawnManager?.SpawnedObjects == null) continue;
                foreach (var kv in nm.SpawnManager.SpawnedObjects)
                {
                    var no = kv.Value;
                    if (no == null) continue;
                    foreach (var nb in no.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (nb != null && nb.GetType().Name == "ChatManager") { chatManager = nb; break; }
                    }
                    if (chatManager != null) break;
                }
            }
            if (chatManager == null) { LogError("DumpQuickChats: ChatManager not found within 30 s"); yield break; }

            try
            {
                var t = chatManager.GetType();
                var f = t.GetField("quickChats", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) { LogError("DumpQuickChats: quickChats field not found"); yield break; }
                var dict = f.GetValue(chatManager) as System.Collections.IDictionary;
                if (dict == null) { LogError("DumpQuickChats: quickChats field is not IDictionary"); yield break; }

                Log("=========== QUICKCHAT POOL ===========");
                foreach (var key in dict.Keys)
                {
                    var arr = dict[key] as System.Array;
                    if (arr == null) continue;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var qc = arr.GetValue(i);
                        if (qc == null) continue;
                        var contentF = qc.GetType().GetField("Content");
                        var teamF    = qc.GetType().GetField("IsTeamChat");
                        string content = contentF?.GetValue(qc) as string;
                        bool   isTeam  = teamF != null && (bool)teamF.GetValue(qc);
                        Log($"[QC] {key}[{i}] isTeamChat={isTeam} content={content}");
                    }
                }
                Log("=========== END QUICKCHAT POOL ===========");
            }
            catch (System.Exception ex)
            {
                LogError("DumpQuickChats failed: " + ex);
            }
        }

        private static IEnumerator WaitAndDump()
        {
            // Wait for NetworkManager to be created and (ideally) for the
            // server to have started so prefab registration is finalized.
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer && nm.NetworkConfig != null)
                {
                    DumpConfig(nm.NetworkConfig);
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
            LogError("Timed out waiting for NetworkManager.Singleton to become a server.");
        }

        private static void DumpConfig(NetworkConfig cfg)
        {
            try
            {
                ulong hash = cfg.GetConfig(false);
                Log("=========== NetworkConfig HASH ===========");
                Log($"  GetConfig(): 0x{hash:X16}  ({hash})");
                Log("=========== HASH INPUTS ===========");
                Log($"  ProtocolVersion:                   {cfg.ProtocolVersion}");
                Log($"  TickRate:                          {cfg.TickRate}");
                Log($"  ConnectionApproval:                {cfg.ConnectionApproval}");
                Log($"  ForceSamePrefabs:                  {cfg.ForceSamePrefabs}");
                Log($"  EnableSceneManagement:             {cfg.EnableSceneManagement}");
                Log($"  EnsureNetworkVariableLengthSafety: {cfg.EnsureNetworkVariableLengthSafety}");
                Log($"  RpcHashSize:                       {cfg.RpcHashSize}");
                Log("=========== PLAYER PREFAB (NetworkConfig.PlayerPrefab) ===========");
                DumpPlayerPrefab(cfg);
                Log("=========== PREFAB LIST (NetworkPrefabOverrideLinks) ===========");
                DumpPrefabList(cfg);
                Log("=========== SPAWNED NETWORK OBJECTS (SpawnManager.SpawnedObjects) ===========");
                DumpSpawnedObjects();
                Log("=========== END ===========");
            }
            catch (Exception ex)
            {
                LogError("Failed to dump NetworkConfig: " + ex);
            }
        }

        private static void DumpSpawnedObjects()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm?.SpawnManager?.SpawnedObjects == null)
                {
                    Log("  SpawnManager.SpawnedObjects is null");
                    return;
                }

                // Aggregate by (hash, in-scene flag, components) so a
                // dozen pucks with identical layouts collapse to one row.
                // NB list MUST mirror exactly what NGO's
                // NetworkObject.ChildNetworkBehaviours walks (line 2611
                // of NetworkObject.cs):
                //   GetComponentsInChildren<NetworkBehaviour>(true)
                //     .Where(b => b.NetworkObject == this)
                // and preserve ORDER + DUPLICATES (no .Distinct).
                // The bot needs the exact same count and order to
                // byte-align scene-sync deserialization.
                var groups = new Dictionary<string, (int count, ulong sampleNid, string desc)>();
                int total = 0;

                foreach (var kv in nm.SpawnManager.SpawnedObjects)
                {
                    total++;
                    var no = kv.Value;
                    if (no == null) continue;

                    uint prefabHash = ReadField<uint>(no, "GlobalObjectIdHash");
                    uint inSceneSrc = ReadField<uint>(no, "InScenePlacedSourceGlobalObjectIdHash");
                    bool isInScene  = ReadField<bool>(no, "IsSceneObject") ||
                                      no.IsSceneObject.GetValueOrDefault();

                    // Match NGO's exact ChildNetworkBehaviours filter:
                    // include nested-children NBs whose .NetworkObject
                    // resolves back to THIS NetworkObject. Preserve
                    // declaration order; do NOT Distinct().
                    var ordered = no.GetComponentsInChildren<NetworkBehaviour>(true)
                        .Where(b => b != null && b.NetworkObject == no)
                        .ToArray();

                    // Per-NB NV count (NetworkVariableFields is internal —
                    // walk the public NetworkBehaviour fields ourselves).
                    var nbDesc = ordered
                        .Select(b => $"{b.GetType().Name}({CountNVs(b)})")
                        .ToArray();

                    string key = $"hash={prefabHash}|inSceneSrc={inSceneSrc}|isInScene={isInScene}|nbCount={ordered.Length}|nbsOrdered=[{string.Join(",", nbDesc)}]";
                    if (groups.TryGetValue(key, out var v))
                    {
                        groups[key] = (v.count + 1, v.sampleNid, v.desc);
                    }
                    else
                    {
                        groups[key] = (1, kv.Key, $"name='{no.gameObject.name}'");
                    }
                }

                Log($"  total spawned: {total}");
                int i = 0;
                foreach (var g in groups.OrderBy(g => g.Key))
                {
                    Log($"    [{i++:D2}] x{g.Value.count,3}  {g.Value.desc}  {g.Key}  (sampleNid={g.Value.sampleNid})");
                }
            }
            catch (Exception ex) { LogError("DumpSpawnedObjects failed: " + ex); }
        }

        // Counts NetworkVariable<T> / NetworkList<T> fields on a
        // NetworkBehaviour instance by walking its declared and inherited
        // fields. Mirrors the criterion NGO uses to populate
        // NetworkVariableFields (NetworkBehaviour.cs:InitializeVariables —
        // any field assignable to NetworkVariableBase).
        private static int CountNVs(NetworkBehaviour b)
        {
            try
            {
                int n = 0;
                Type t = b.GetType();
                while (t != null && t != typeof(NetworkBehaviour) && t != typeof(MonoBehaviour))
                {
                    var fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var f in fs)
                    {
                        if (typeof(NetworkVariableBase).IsAssignableFrom(f.FieldType)) n++;
                    }
                    t = t.BaseType;
                }
                return n;
            }
            catch { return -1; }
        }

        private static T ReadField<T>(object instance, string name)
        {
            try
            {
                var f = instance.GetType().GetField(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return default;
                object v = f.GetValue(instance);
                return v is T t ? t : default;
            }
            catch { return default; }
        }

        private static void DumpPlayerPrefab(NetworkConfig cfg)
        {
            try
            {
                var go = cfg.PlayerPrefab;
                if (go == null) { Log("  PlayerPrefab is null"); return; }
                var no = go.GetComponent<NetworkObject>();
                uint hash = no != null ? GetPrefabHash(no) : 0;
                string types = DescribePrefab(new { Prefab = go });
                Log($"  hash={hash}  go.name='{go.name}'");
                // Include NetworkBehaviour layout same as DumpPrefabList does
                LogComponents(go);
            }
            catch (Exception ex) { LogError("DumpPlayerPrefab failed: " + ex); }
        }

        private static uint GetPrefabHash(NetworkObject no)
        {
            var f = typeof(NetworkObject).GetField(
                "GlobalObjectIdHash",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return f != null ? (uint)f.GetValue(no) : 0;
        }

        private static void LogComponents(UnityEngine.GameObject go)
        {
            var comps = go.GetComponentsInChildren<UnityEngine.Component>(true);
            var nbs = comps
                .Where(c => c != null)
                .Where(c => typeof(NetworkBehaviour).IsAssignableFrom(c.GetType()))
                .Select(c => c.GetType().FullName)
                .Distinct()
                .ToList();
            Log($"  NetworkBehaviours: [{string.Join(",", nbs)}]");
        }

        private static void DumpPrefabList(NetworkConfig cfg)
        {
            try
            {
                object prefabs = cfg.GetType().GetField("Prefabs",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(cfg);
                if (prefabs == null) { Log("  (no Prefabs container)"); return; }

                var linksField = prefabs.GetType().GetField("NetworkPrefabOverrideLinks",
                    BindingFlags.Public | BindingFlags.Instance);
                if (linksField == null) { Log("  (no NetworkPrefabOverrideLinks field)"); return; }

                var dict = (IDictionary)linksField.GetValue(prefabs);
                if (dict == null) { Log("  (NetworkPrefabOverrideLinks is null)"); return; }

                Log($"  count: {dict.Count}");

                // Sort by hash key, then for each entry resolve the
                // prefab GameObject and the NetworkBehaviour types on it.
                var entries = new List<KeyValuePair<uint, object>>();
                foreach (DictionaryEntry e in dict)
                {
                    entries.Add(new KeyValuePair<uint, object>(Convert.ToUInt32(e.Key), e.Value));
                }
                entries = entries.OrderBy(e => e.Key).ToList();

                int i = 0;
                foreach (var entry in entries)
                {
                    string types = DescribePrefab(entry.Value);
                    Log($"    [{i++:D3}] hash={entry.Key,10}  {types}");
                }
            }
            catch (Exception ex)
            {
                LogError("Failed to dump prefab list: " + ex);
            }
        }

        private static string DescribePrefab(object networkPrefab)
        {
            if (networkPrefab == null) return "(null entry)";
            try
            {
                // NetworkPrefab.Prefab is the GameObject reference
                var prefabField = networkPrefab.GetType().GetField("Prefab",
                    BindingFlags.Public | BindingFlags.Instance);
                var go = prefabField?.GetValue(networkPrefab) as UnityEngine.GameObject;
                if (go == null) return "(no GameObject)";

                var components = go.GetComponentsInChildren<UnityEngine.Component>(true);
                var nbTypes = components
                    .Where(c => c != null)
                    .Select(c => c.GetType())
                    .Where(t => typeof(NetworkBehaviour).IsAssignableFrom(t))
                    .Select(t => t.FullName)
                    .Distinct()
                    .ToList();

                string allComponentTypes = string.Join(",", components
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .Distinct());

                return $"name='{go.name}' NetworkBehaviours=[{string.Join(",", nbTypes)}]  AllComponents=[{allComponentTypes}]";
            }
            catch (Exception ex)
            {
                return $"(describe failed: {ex.Message})";
            }
        }

        public static void Log(string msg)      => Debug.Log($"[{Name}] {msg}");
        public static void LogError(string msg) => Debug.LogError($"[{Name}] {msg}");
    }

    internal class CoroutineHost : MonoBehaviour { }

    // Counts per-clientId actual `DirectSendRpcTarget.Send` calls
    // for RpcMessage. If snapshot RPC reaches Send for client=1 but
    // bot doesn't see method=1738927239, the issue is in the send
    // queue / wire / receive path. If snapshot never reaches Send,
    // the issue is upstream in RpcTarget routing.
    // Hooks the actual transport layer's Send. If snapshot RPC bytes
    // make it here for client 1, the wire is doing its job and the
    // drop is on the bot's receive side or in OS networking. If they
    // DON'T make it here, NGO is dropping them between RpcTarget and
    // the transport.
    [HarmonyPatch]
    internal static class Patch_UTP_Send
    {
        private static readonly Dictionary<ulong, int> s_byClient = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, long> s_bytesByClient = new Dictionary<ulong, long>();
        private static float s_nextLog;

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Unity.Netcode.Transports.UTP.UnityTransport", false);
                if (t == null) continue;
                return t.GetMethod("Send",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(ulong), typeof(System.ArraySegment<byte>), typeof(Unity.Netcode.NetworkDelivery) },
                    null);
            }
            return null;
        }

        [HarmonyPrefix]
        public static void Prefix(ulong clientId, System.ArraySegment<byte> payload, Unity.Netcode.NetworkDelivery networkDelivery)
        {
            try
            {
                s_byClient.TryGetValue(clientId, out var c);
                s_byClient[clientId] = c + 1;
                s_bytesByClient.TryGetValue(clientId, out var b);
                s_bytesByClient[clientId] = b + payload.Count;
                if (Time.realtimeSinceStartup >= s_nextLog)
                {
                    s_nextLog = Time.realtimeSinceStartup + 5f;
                    string per = string.Join(",", s_byClient.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}({s_bytesByClient[kv.Key]}B)"));
                    Plugin.Log($"Patch_UTP_Send: per-client send count(bytes) = [{per}]");
                }
            }
            catch { }
        }
    }

    [HarmonyPatch]
    internal static class Patch_DirectSend_Send
    {
        // Per-clientId counters: snapshot RPC vs all other RPCs.
        // Lets us answer: "Did DirectSend actually dispatch the
        // snapshot RPC to client X?". If snapshot count is high but
        // bot doesn't see method=1738927239, the issue is downstream
        // of DirectSend (queue, transport, or receive side).
        private static readonly Dictionary<ulong, int> s_snapshotByClient = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, int> s_otherByClient = new Dictionary<ulong, int>();
        private static float s_nextLog;
        private const uint SnapshotRpcId = 1738927239u;

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Unity.Netcode.DirectSendRpcTarget", false);
                if (t == null) continue;
                return t.GetMethod("Send",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            return null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, object behaviour, object message)
        {
            try
            {
                ulong clientId = (ulong)__instance.GetType().GetField("ClientId",
                    BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);

                // RpcMessage.Metadata.NetworkRpcMethodId
                var metaField = message.GetType().GetField("Metadata",
                    BindingFlags.Public | BindingFlags.Instance);
                bool isSnapshot = false;
                if (metaField != null)
                {
                    object meta = metaField.GetValue(message);
                    var idField = meta.GetType().GetField("NetworkRpcMethodId",
                        BindingFlags.Public | BindingFlags.Instance);
                    uint id = (uint)idField.GetValue(meta);
                    isSnapshot = id == SnapshotRpcId;
                }

                var dict = isSnapshot ? s_snapshotByClient : s_otherByClient;
                dict.TryGetValue(clientId, out var c);
                dict[clientId] = c + 1;

                if (Time.realtimeSinceStartup >= s_nextLog)
                {
                    s_nextLog = Time.realtimeSinceStartup + 5f;
                    string snap = string.Join(",", s_snapshotByClient.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
                    string oth  = string.Join(",", s_otherByClient.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
                    int snapPayloadLen = -1;
                    if (isSnapshot)
                    {
                        try
                        {
                            var bufField = message.GetType().GetField("WriteBuffer",
                                BindingFlags.Public | BindingFlags.Instance);
                            object writer = bufField.GetValue(message);
                            var lenProp = writer.GetType().GetProperty("Length",
                                BindingFlags.Public | BindingFlags.Instance);
                            snapPayloadLen = (int)lenProp.GetValue(writer);
                        }
                        catch { }
                    }
                    Plugin.Log($"Patch_DirectSend_Send: snapshot[{snap}] other[{oth}] lastSnapPayload={snapPayloadLen}");
                }
            }
            catch { }
        }
    }

    // After SetVersion finishes adding the bot's known messages,
    // dump what the server has registered for that client. If
    // RpcMessage is missing, the server can't send RPCs to this
    // client (per NetworkMessageManager.SendMessage line 568:
    // skips clients with version<0).
    [HarmonyPatch]
    internal static class Patch_SetVersion_DumpAfter
    {
        private static int s_dumpsLeft = 4;

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Unity.Netcode.NetworkMessageManager", false);
                if (t == null) continue;
                return t.GetMethod("SetVersion",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            return null;
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, ulong clientId, uint messageHash, int version)
        {
            if (s_dumpsLeft <= 0) return;
            try
            {
                // Only dump once per client when the per-client dict
                // first reaches 5+ entries (handshake almost done).
                var field = __instance.GetType().GetField("m_PerClientMessageVersions",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var outer = field?.GetValue(__instance) as System.Collections.IDictionary;
                if (outer == null || !outer.Contains(clientId)) return;
                var inner = outer[clientId] as System.Collections.IDictionary;
                if (inner == null || inner.Count < 20) return;
                s_dumpsLeft--;
                var sb = new System.Text.StringBuilder();
                sb.Append($"PerClientMessageVersions[{clientId}] (count={inner.Count}): ");
                foreach (System.Collections.DictionaryEntry kv in inner)
                {
                    var t = kv.Key as Type;
                    if (t != null) sb.Append(t.Name).Append('=').Append(kv.Value).Append(';');
                }
                Plugin.Log("Patch_SetVersion_DumpAfter: " + sb);
            }
            catch (Exception ex) { Plugin.LogError("Patch_SetVersion_DumpAfter failed: " + ex.Message); }
        }
    }

    // Logs who calls Server_RemoveSynchronizedClientId — to find why
    // the bot drops out of synchronizedClientIds while still connected.
    [HarmonyPatch]
    internal static class Patch_Server_RemoveSynchronizedClientId
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("SynchronizedObjectManager", false);
                if (t == null) continue;
                return t.GetMethod("Server_RemoveSynchronizedClientId",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            return null;
        }

        [HarmonyPrefix]
        public static void Prefix(ulong clientId)
        {
            Plugin.Log($"Patch_Server_RemoveSynchronizedClientId: clientId={clientId} — stack:\n{new System.Diagnostics.StackTrace(true)}");
        }
    }

    // Counts invocations of Server_SynchronizeObjectsRpc to verify
    // whether the server is actually calling the RPC each tick when
    // the bot is in synchronizedClientIds. Logs every 5 s so the
    // periodicity is easy to read.
    [HarmonyPatch]
    internal static class Patch_Server_SynchronizeObjectsRpc
    {
        private static int s_callCount;
        private static float s_nextLogTime;

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            // Server_SynchronizeObjectsRpc is private on
            // SynchronizedObjectManager. Find by name.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("SynchronizedObjectManager", false);
                if (t == null) continue;
                var m = t.GetMethod("Server_SynchronizeObjectsRpc",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                return m;
            }
            return null;
        }

        [HarmonyPrefix]
        public static void Prefix()
        {
            s_callCount++;
            if (Time.realtimeSinceStartup >= s_nextLogTime)
            {
                s_nextLogTime = Time.realtimeSinceStartup + 5f;
                Plugin.Log($"Patch_Server_SynchronizeObjectsRpc: {s_callCount} total invocations so far");
            }
        }
    }
}
