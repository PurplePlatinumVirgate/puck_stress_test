using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace PuckStressTest
{
    // One bot = one NetworkManager. NetworkManager is a singleton in the
    // common case, but NGO supports multiple NetworkManager instances per
    // process if each lives on its own GameObject and we never call
    // .Singleton accessors. We always go through `_nm` directly.
    public class BotInstance : MonoBehaviour
    {
        private BotConfig _config;
        private int _index;
        private NetworkManager _nm;
        private bool _started;
        private bool _shutdownRequested;
        // Mod-id auto-discovery: connect empty, parse the disconnect
        // payload if the server kicks us with code 8 (MissingMods), and
        // reconnect once advertising whatever IDs the server demanded.
        private ulong[] _enabledModIds = Array.Empty<ulong>();
        private bool _modRetryPending;
        private bool _modRetried;

        public BotConfig Config => _config;
        public int Index => _index;

        public void Init(BotConfig config, int index)
        {
            _config = config;
            _index = index;
        }

        private void Start()
        {
            _nm = gameObject.AddComponent<NetworkManager>();
            ConfigureNetworkManager();
            ConfigureTransport();
            HookEvents();
            Connect();
        }

        private void Connect()
        {
            // Build the JSON connection payload Puck's server expects.
            // EnabledModIds is empty on the first attempt; if the server
            // kicks us with MissingMods, the disconnect handler refills
            // it from the server's clientRequiredModIds and we retry.
            // B323: SocketId is gone; replaced by Key (backend auth ticket
            // — empty here because BotAuthBypassMod intercepts the WS
            // emit server-side for botsteam* SteamIds). EnabledModIds is
            // string[] now; stringify our internal ulong list.
            var modIdsStr = new string[_enabledModIds.Length];
            for (int i = 0; i < _enabledModIds.Length; i++)
                modIdsStr[i] = _enabledModIds[i].ToString();
            var data = new ConnectionData
            {
                Password = _config.ServerPassword,
                SteamId  = $"botsteam{_config.Seed:D4}{_index:D2}",
                Key      = "",
                EnabledModIds = modIdsStr,
                Handedness    = ConnectionData.PlayerHandedness.Left,
            };
            _nm.NetworkConfig.ConnectionData = Encoding.ASCII.GetBytes(data.ToJson());

            Debug.Log(
                $"[Bot {_index:D2}] connecting to " +
                $"{_config.ServerAddress}:{_config.ServerPort} " +
                $"as {data.SteamId} " +
                $"(mods={_enabledModIds.Length})");

            _started = _nm.StartClient();
            if (!_started)
            {
                Debug.LogError($"[Bot {_index:D2}] StartClient() returned false");
                return;
            }

            // StartClient → Initialize() → Prefabs.Initialize() which CLEARS
            // NetworkPrefabOverrideLinks and rebuilds it from the editor
            // prefab list (NetworkPrefabs.cs:103). Our pre-StartClient
            // injection is wiped. Re-inject now, before the transport
            // completes its UDP handshake and NGO builds the
            // ConnectionRequestMessage (which calls GetConfig(false) and
            // hashes the prefab keys).
            //
            // Also reset the cached hash so GetConfig recomputes with the
            // re-injected keys.
            PrefabRegistrar.RegisterInto(_nm.NetworkConfig);
            ResetCachedConfigHash(_nm.NetworkConfig);

            ulong recomputed = _nm.NetworkConfig.GetConfig(false);
            Debug.Log($"[Bot {_index:D2}] post-StartClient hash=0x{recomputed:X16}");

            // Tell NGO's NetworkSceneManager that any scene hash the
            // server may broadcast resolves to our placeholder build index
            // 0 (the project's only scene). This sidesteps the
            // "Scene Hash X does not exist in HashToBuildIndex" exception
            // that drops the connection right after approval. The bot
            // never actually loads Puck's scenes — it just acks them.
            PrePopulateSceneHashes(_nm);

            // Pre-populate ScenePlacedObjects so NGO's scene-sync loop
            // can resolve all 33 scene-placed singletons (managers,
            // player positions, goals, level, etc.) into our local
            // stubs instead of returning null and aborting. Without
            // this, the loop bails on the first missing object and
            // the 11 dynamically-spawned Puck clones in the same
            // batch never spawn either. See ScenePlacedRegistrar.
            ScenePlacedRegistrar.EnsureBuilt(_nm);

            // EnableSerializationLogs was used to root-cause the
            // scene-placed sync issue (resolved 2026-04-28). Now that
            // scene sync deserialises cleanly, the per-packet [Read]
            // / [Start Data Dump] hex output only burns CPU and disk.
            // Re-enable temporarily if a future drift causes
            // scene-sync regressions.
            // EnableNgoSerializationLogs(_nm);
        }

        private static void EnableNgoSerializationLogs(NetworkManager nm)
        {
            try
            {
                var sm = nm.SceneManager;
                if (sm == null) return;
                // Look for SceneEventData fields/dicts on SceneManager.
                var smType = sm.GetType();
                foreach (var f in smType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                {
                    object v = f.GetValue(sm);
                    if (v == null) continue;
                    EnableOnAny(v);
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[Bot] EnableNgoSerializationLogs failed: {ex.Message}"); }
        }

        private static void EnableOnAny(object v)
        {
            try
            {
                if (v is System.Collections.IEnumerable en && !(v is string))
                {
                    foreach (var item in en)
                    {
                        if (item is System.Collections.DictionaryEntry de) EnableOnAny(de.Value);
                        else EnableOnAny(item);
                    }
                    return;
                }
                var t = v.GetType();
                if (t.Name == "SceneEventData")
                {
                    var f = t.GetField("EnableSerializationLogs",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    f?.SetValue(v, true);
                    Debug.Log("[Bot] enabled NGO SceneEventData serialization logs");
                }
            }
            catch { }
        }

        // Hashes the server has been observed to send. Discovered
        // empirically; any new hashes get added on the fly via
        // EnsureSceneHashIfMissing below.
        private static readonly uint[] KnownPuckSceneHashes =
        {
            217390723u,    // B202 scene hash
            1400888491u,   // B323 scene hash (captured 2026-05-13 from
                           // bot smoke crash: "Scene Hash 1400888491
                           // does not exist in the HashToBuildIndex
                           // table"). Pre-populating bypasses the
                           // exception so the bot can ack the scene-
                           // load event without owning the actual scene.
        };

        private static void PrePopulateSceneHashes(NetworkManager nm)
        {
            try
            {
                object sceneMgr = nm.SceneManager;
                if (sceneMgr == null)
                {
                    Debug.LogWarning("[Bot] NetworkSceneManager is null after StartClient; cannot prepopulate.");
                    return;
                }
                var t = sceneMgr.GetType();
                var h2bi = (System.Collections.IDictionary)t
                    .GetField("HashToBuildIndex", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(sceneMgr);
                var bi2h = (System.Collections.IDictionary)t
                    .GetField("BuildIndexToHash", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(sceneMgr);
                foreach (uint h in KnownPuckSceneHashes)
                {
                    if (!h2bi.Contains(h)) h2bi.Add(h, 0);
                }
                if (!bi2h.Contains(0) && KnownPuckSceneHashes.Length > 0)
                {
                    bi2h.Add(0, KnownPuckSceneHashes[0]);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Bot] PrePopulateSceneHashes failed: " + ex);
            }
        }

        private static void ResetCachedConfigHash(NetworkConfig cfg)
        {
            var f = typeof(NetworkConfig).GetField(
                "m_ConfigHash",
                BindingFlags.NonPublic | BindingFlags.Instance);
            f?.SetValue(cfg, (ulong?)null);
        }

        // Captured from a server build by ConfigCaptureMod (see
        // ../ConfigCaptureMod/). These are the inputs to NGO's
        // NetworkConfig.GetConfig() hash. If any of these changes on
        // Puck's side (build version bump, new networked prefab, etc.),
        // re-run the capture mod and update these constants.
        // Bumped to 323 on 2026-05-13 for the B323 cutover. Source:
        // Puck_B323/Puck/ConnectionManager.cs:22 reads
        // `NetworkManager.Singleton.NetworkConfig.ProtocolVersion =
        // ApplicationManager.Version;` where ApplicationManager.Version
        // returns `ushort.Parse(Application.version)`. B323's Puck.exe
        // has Application.version == "323".
        // Bumped to 897 on 2026-06-04 for the B897 cutover: ConfigCaptureMod
        // on a B897 dedicated-server rig reported
        // ProtocolVersion 897 with the identical 13-prefab list, so this is
        // the only NetworkConfig delta vs B323. Target hash 0x069D32B3F7683A6A.
        private const ushort PuckProtocolVersion = 897;
        private static readonly uint[] PuckPrefabHashes =
        {
            340656796u,
            923338123u,
            1396033496u,
            1915519032u,
            2055993102u,
            3236080593u,
            3292036842u,
            3464149273u,
            3726304409u,
            4103617937u,
        };

        private void ConfigureNetworkManager()
        {
            _nm.NetworkConfig ??= new NetworkConfig();

            // Server has ConnectionApproval=true (it validates the JSON
            // ConnectionData payload). Client must set the same flag, or
            // NGO's ConnectionRequestMessage serializer omits the
            // length-prefixed ConnectionData and the server's deserializer
            // logs "Incomplete connection request message given config -
            // possible NetworkConfig mismatch." and disconnects.
            _nm.NetworkConfig.ConnectionApproval = true;

            // ProtocolVersion: Puck sets this from Application.version
            // parsed as ushort (ConnectionManager.cs:27-31). For build B202
            // it parses to 202.
            _nm.NetworkConfig.ProtocolVersion = PuckProtocolVersion;

            // Other NetworkConfig fields hashed by GetConfig() are at NGO
            // defaults on both sides (TickRate=30, ForceSamePrefabs=true,
            // EnableSceneManagement=true, EnsureNetworkVariableLengthSafety
            // =false, RpcHashSize=VarIntFourBytes), so no overrides needed.

            // Register real prefab templates for the 13 hashes the
            // server uses, so NGO can deserialize SceneObject sync data
            // into matching NetworkBehaviours instead of overflowing
            // the FastBufferReader. See PrefabRegistrar.
            //
            // This also satisfies the config-hash check, since the same
            // dictionary keys participate in both code paths.
            PrefabRegistrar.RegisterInto(_nm.NetworkConfig);

            // Sanity-check: compute our hash and compare to server's
            // captured value. If equal, the connection should pass the
            // CompareConfig check.
            var dict = (System.Collections.IDictionary)
                _nm.NetworkConfig.Prefabs.GetType()
                    .GetField("NetworkPrefabOverrideLinks",
                        BindingFlags.Public | BindingFlags.Instance)
                    .GetValue(_nm.NetworkConfig.Prefabs);
            ulong computed = _nm.NetworkConfig.GetConfig(false);
            Debug.Log(
                $"[Bot {_index:D2}] config: ProtocolVersion={_nm.NetworkConfig.ProtocolVersion} " +
                $"prefabs={dict.Count} TickRate={_nm.NetworkConfig.TickRate} " +
                $"FSP={_nm.NetworkConfig.ForceSamePrefabs} " +
                $"ESM={_nm.NetworkConfig.EnableSceneManagement} " +
                $"ENVLS={_nm.NetworkConfig.EnsureNetworkVariableLengthSafety} " +
                $"RPCH={_nm.NetworkConfig.RpcHashSize} " +
                $"hash=0x{computed:X16} (server expected 0x069D32B3F7683A6A)");
        }

        private static void InjectPrefabKeys(NetworkConfig cfg, uint[] keys)
        {
            object prefabs = cfg.Prefabs;
            if (prefabs == null) return;
            var f = prefabs.GetType().GetField(
                "NetworkPrefabOverrideLinks",
                BindingFlags.Public | BindingFlags.Instance);
            if (f == null)
            {
                Debug.LogError("[Bot] NetworkPrefabOverrideLinks field not found");
                return;
            }
            var dict = (System.Collections.IDictionary)f.GetValue(prefabs);
            foreach (uint k in keys)
            {
                if (!dict.Contains(k)) dict.Add(k, null);
            }
        }

        private void ConfigureTransport()
        {
            var utp = gameObject.AddComponent<UnityTransport>();
            utp.SetConnectionData(_config.ServerAddress, _config.ServerPort);
            _nm.NetworkConfig.NetworkTransport = utp;
            // NGO's UnityTransport already bumps reliable WindowSize
            // to 64 internally on driver init (UnityTransport.cs:
            // 1320-1323). No further tuning needed here.
        }

        private void HookEvents()
        {
            _nm.OnClientConnectedCallback += id =>
                Debug.Log($"[Bot {_index:D2}] OnClientConnected localId={id}");

            _nm.OnClientDisconnectCallback += id =>
            {
                Debug.LogWarning(
                    $"[Bot {_index:D2}] OnClientDisconnect localId={id} " +
                    $"reason='{_nm.DisconnectReason}'");
                if (_modRetried || _shutdownRequested) return;
                var ids = TryParseRequiredMods(_nm.DisconnectReason);
                if (ids != null && ids.Length > 0)
                {
                    _enabledModIds = ids;
                    _modRetryPending = true;
                    // NGO does not auto-shutdown the NetworkManager when
                    // the server kicks us — IsListening stays true and
                    // StartClient refuses. Trigger Shutdown so the
                    // reconnect path in Update() can fire once it lands.
                    try { _nm.Shutdown(discardMessageQueue: true); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            };

            _nm.OnConnectionEvent += (_, evt) =>
                Debug.Log($"[Bot {_index:D2}] OnConnectionEvent {evt.EventType}");
        }

        private void Update()
        {
            if (!_modRetryPending || _shutdownRequested) return;
            // Wait for the prior NM to finish winding down before tearing
            // it out — Destroy on a still-listening NM causes asserts.
            if (_nm != null && (_nm.IsListening || _nm.ShutdownInProgress)) return;
            _modRetryPending = false;
            _modRetried = true;
            Debug.Log(
                $"[Bot {_index:D2}] reconnecting with {_enabledModIds.Length} " +
                $"server-required mod ids (rebuilding NetworkManager)");

            // NGO leaves NetworkPrefabOverrideLinks, ScenePlacedObjects,
            // and assorted SceneEventData state attached to the NM
            // instance even after Shutdown(). Reusing the same NM for a
            // second StartClient corrupts scene-sync (the second sync
            // sees stale entries from the first attempt and fails the
            // first hash lookup, e.g. Spectator Manager 2834597543).
            // Cheapest robust fix: destroy and rebuild the NM. Also
            // resets ScenePlacedRegistrar's per-NM stub dict via the new
            // instance becoming a fresh key.
            DestroyImmediate(_nm);
            _nm = gameObject.AddComponent<NetworkManager>();
            ConfigureNetworkManager();
            ConfigureTransport();
            HookEvents();
            Connect();
        }

        private static ulong[] TryParseRequiredMods(string disconnectReason)
        {
            if (string.IsNullOrEmpty(disconnectReason)) return null;
            try
            {
                var obj = JObject.Parse(disconnectReason);
                // The missing-mods kick carries the list of mod ids the server
                // demands. Its exact shape varies by Puck build:
                //   - older: { "code": 8, "clientRequiredModIds": [ ... ] }
                //   - B897 : { "code": 7, "data": { "clientRequiredModIds": [ ... ] } }
                // Don't gate on the numeric code (MissingMods has been both 7 and
                // 8 across builds) — the presence of the id list is the
                // unambiguous "advertise these and retry" signal. Look for it at
                // the top level OR nested under "data".
                var arr = (obj["clientRequiredModIds"]
                           ?? obj["data"]?["clientRequiredModIds"]) as JArray;
                if (arr == null) return null;
                var list = new List<ulong>(arr.Count);
                foreach (var t in arr)
                {
                    // B897 serialises the ids as JSON strings ("3724352946");
                    // older builds used integers. Accept both.
                    if (t.Type == JTokenType.Integer) list.Add(t.Value<ulong>());
                    else if (t.Type == JTokenType.String
                             && ulong.TryParse(t.Value<string>(), out var v)) list.Add(v);
                }
                return list.ToArray();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Bot] could not parse disconnect reason: {ex.Message}");
                return null;
            }
        }

        public void Shutdown()
        {
            if (_shutdownRequested) return;
            _shutdownRequested = true;
            try
            {
                if (_nm != null && _nm.IsClient) _nm.Shutdown(discardMessageQueue: true);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void OnDestroy() => Shutdown();
    }
}
