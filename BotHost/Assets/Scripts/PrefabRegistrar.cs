using System;
using System.Collections.Generic;
using System.Reflection;
using PuckStressTest.Mirror;
using Unity.Netcode;
using UnityEngine;

namespace PuckStressTest
{
    // Builds runtime "prefab" GameObjects matching Puck's 13 networked
    // prefabs (B323 — was 10 in B202) and registers them in NGO's
    // NetworkPrefabOverrideLinks so that scene synchronization can
    // deserialize Player/Stick/Puck/etc. SceneObject payloads without
    // the FastBufferReader running off the end of the buffer.
    //
    // Hash → prefab mapping captured by ConfigCaptureMod's runtime
    // dump 2026-05-13 (B323 baseline):
    //   340656796   Stick Positioner            4 NBs
    //   923338123   Player Body (Attacker)      5 NBs
    //   1021701660  Replay Camera               1 NB
    //   1396033496  Player Body (Goalie)        5 NBs
    //   1769466816  Spectator Camera            1 NB
    //   2055993102  Player                      6 NBs
    //   2597195694  Team Blue Position Select Camera  1 NB (BaseCamera)
    //   2761164069  Player Camera               1 NB
    //   2994858414  Team Red Position Select Camera   1 NB (BaseCamera)
    //   3292036842  Puck                        5 NBs
    //   3464149273  Stick (Goalie)              4 NBs
    //   3665057982  Cinematic Camera            1 NB (BaseCamera)
    //   3726304409  Stick (Attacker)            4 NBs
    //
    // B323 vs B202: drastic simplification. PlayerBodyV2 → PlayerBody
    // (8→5 NBs, no triple SyncAudio pairs); Stick Positioner 8→4 NBs;
    // Puck 12→5 NBs; Stick 5→4 NBs; 3 new BaseCamera prefabs (Team
    // Blue/Red Position Select, Cinematic Camera). Hashes for Spectator
    // Camera, Player Camera, Replay Camera all changed.
    //
    // Each prefab GameObject is held inactive so Awake doesn't fire on
    // the template — it fires on the Object.Instantiate copy NGO
    // creates when the server spawns the object.
    public static class PrefabRegistrar
    {
        public struct Entry
        {
            public uint   Hash;
            public string Name;
            // Locally compiled NetworkBehaviour types attached in order
            // (matches the server prefab's component order — NGO walks
            // sync bytes per-NetworkBehaviour in that order). Use these
            // for prefabs we have local Mirror_* classes for.
            public Type[] BehaviourTypes;
            // Fallback: resolve types by name across loaded assemblies.
            // Used when we haven't transcribed mirror classes yet —
            // the resolver returns null in our env (Puck.dll disabled),
            // so the AddComponent call is a no-op and the prefab has
            // an empty NetworkBehaviour list. NGO will overrun on these
            // until they get proper mirrors.
            public string[] BehaviourTypeNames;
            public string[] HelperComponentTypeNames; // Rigidbody, AudioSource, etc.
        }

        // Order doesn't matter — the dict is keyed by hash. Names must
        // match real Puck.dll types.
        public static readonly Entry[] Prefabs =
        {
            new Entry {
                Hash = 2055993102, Name = "Player",
                BehaviourTypes = new[] {
                    typeof(MirrorPlayer),
                    typeof(MirrorPlayerController),
                    typeof(MirrorPlayerInput),
                    typeof(MirrorPlayerInputController),
                    typeof(MirrorPlayerVoiceRecorder),
                    typeof(MirrorPlayerVoiceRecCtrl),
                },
                HelperComponentTypeNames = new[] { "UnityEngine.AudioSource" },
            },
            new Entry {
                Hash = 340656796, Name = "Stick Positioner",
                // B323: 8 NBs (per spawned [NB-LAYOUT] Stick Positioner(Clone)).
                // Prefab-list dump shows 4 distinct types but actual spawned
                // instance carries 3 SyncAudio pairs (same gotcha as Puck/PlayerBody).
                BehaviourTypes = new[] {
                    typeof(MirrorStickPositioner),
                    typeof(MirrorStickPositionerController),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                },
            },
            new Entry {
                Hash = 923338123, Name = "Player Body (Attacker)",
                // B323: 9 NBs (per spawned [NB-LAYOUT] Player Body (Attacker)(Clone)).
                // PlayerBody has 8 NVs. 3 SyncAudio pairs as with all body prefabs.
                BehaviourTypes = new[] {
                    typeof(MirrorPlayerBodyV2),
                    typeof(MirrorPlayerBodyV2Controller),
                    typeof(MirrorSynchronizedObject),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                },
            },
            new Entry {
                Hash = 1021701660, Name = "Replay Camera",
                // B323: new hash (was 4103617937 in B202).
                BehaviourTypes = new[] {
                    typeof(MirrorReplayCamera),
                },
            },
            new Entry {
                Hash = 1396033496, Name = "Player Body (Goalie)",
                // B323: 9 NBs (per spawned [NB-LAYOUT] Player Body (Goalie)(Clone)).
                // Same shape as Attacker.
                BehaviourTypes = new[] {
                    typeof(MirrorPlayerBodyV2),
                    typeof(MirrorPlayerBodyV2Controller),
                    typeof(MirrorSynchronizedObject),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                },
            },
            new Entry {
                Hash = 1769466816, Name = "Spectator Camera",
                // B323: new hash (was 1915519032 in B202). 1 NB.
                BehaviourTypes = new[] {
                    typeof(MirrorSpectatorCamera),
                },
            },
            new Entry {
                Hash = 2055993102, Name = "Player",
                BehaviourTypes = new[] {
                    typeof(MirrorPlayer),
                    typeof(MirrorPlayerController),
                    typeof(MirrorPlayerInput),
                    typeof(MirrorPlayerInputController),
                    typeof(MirrorPlayerVoiceRecorder),
                    typeof(MirrorPlayerVoiceRecCtrl),
                },
                HelperComponentTypeNames = new[] { "UnityEngine.AudioSource" },
            },
            new Entry {
                Hash = 2597195694, Name = "Team Blue Position Select Camera",
                // B323 NEW. Single BaseCamera NB (0 NVs); pad with
                // MirrorEmpty so the slot byte-aligns to 0-NV.
                BehaviourTypes = new[] { typeof(MirrorEmpty) },
            },
            new Entry {
                Hash = 2761164069, Name = "Player Camera",
                // B323: new hash (was 3236080593). 1 NB.
                BehaviourTypes = new[] {
                    typeof(MirrorPlayerCamera),
                },
            },
            new Entry {
                Hash = 2994858414, Name = "Team Red Position Select Camera",
                // B323 NEW. Same BaseCamera shape as Team Blue.
                BehaviourTypes = new[] { typeof(MirrorEmpty) },
            },
            new Entry {
                Hash = 3292036842, Name = "Puck",
                // B323: 11 NBs (was 12 in B202; SyncObjCtrl became
                // MonoBehaviour so dropped from the wire). Ordered NB
                // list per [NB-LAYOUT] Puck(Clone) NID=34 dump:
                //   Puck(1), SynchronizedObject(0),
                //   NetworkObjectCollisionRecorder(1),
                //   4× (SynchronizedAudio(2), SynchronizedAudioController(0))
                // The PREFAB-list dump shows only distinct NB type
                // names (5) — the SPAWN dump shows the actual ordered
                // 11. NGO walks ChildNetworkBehaviours per-spawn in
                // declared order, so we need all 11 stubs even if
                // some types repeat.
                BehaviourTypes = new[] {
                    typeof(MirrorPuck),
                    typeof(MirrorSynchronizedObject),
                    typeof(MirrorNetworkObjectCollisionBuffer),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                    typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                },
            },
            new Entry {
                Hash = 3464149273, Name = "Stick (Goalie)",
                // B323: 4 NBs (was 5; SyncObjCtrl pair removed).
                BehaviourTypes = new[] {
                    typeof(MirrorStick),
                    typeof(MirrorStickController),
                    typeof(MirrorSynchronizedObject),
                    typeof(MirrorNetworkObjectCollisionBuffer),
                },
            },
            new Entry {
                Hash = 3665057982, Name = "Cinematic Camera",
                // B323 NEW. Single BaseCamera NB.
                BehaviourTypes = new[] { typeof(MirrorEmpty) },
            },
            new Entry {
                Hash = 3726304409, Name = "Stick (Attacker)",
                BehaviourTypes = new[] {
                    typeof(MirrorStick),
                    typeof(MirrorStickController),
                    typeof(MirrorSynchronizedObject),
                    typeof(MirrorNetworkObjectCollisionBuffer),
                },
            },
        };

        private static readonly Dictionary<uint, GameObject> _builtPrefabs = new();
        private static FieldInfo s_globalIdHashField;

        // Build all prefabs once (process-wide). Subsequent bots reuse
        // the same prefab GameObjects, since NetworkPrefab.Prefab is a
        // template that NGO Instantiates per spawn.
        public static void EnsureBuilt()
        {
            if (_builtPrefabs.Count == Prefabs.Length) return;

            // Force-load the game assemblies. Mono lazy-loads by reference;
            // since our static C# code never names a Puck type, Puck.dll
            // is not in the AppDomain when ResolveType runs. Load it
            // explicitly here.
            ForceLoad("Puck");
            ForceLoad("Assembly-CSharp-firstpass");

            s_globalIdHashField = typeof(NetworkObject).GetField(
                "GlobalObjectIdHash",
                BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var entry in Prefabs)
            {
                if (_builtPrefabs.ContainsKey(entry.Hash)) continue;
                var go = BuildOne(entry);
                if (go != null) _builtPrefabs[entry.Hash] = go;
            }
        }

        private static void ForceLoad(string assemblyName)
        {
            try
            {
                var asm = Assembly.Load(assemblyName);
                Debug.Log($"[PrefabRegistrar] loaded {asm.FullName} types={asm.GetTypes().Length}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PrefabRegistrar] failed to load assembly '{assemblyName}': {ex.Message}");
            }
        }

        public static void RegisterInto(NetworkConfig cfg)
        {
            EnsureBuilt();

            var prefabs = cfg.Prefabs;
            var dictField = prefabs.GetType().GetField(
                "NetworkPrefabOverrideLinks",
                BindingFlags.Public | BindingFlags.Instance);
            var dict = (System.Collections.IDictionary)dictField.GetValue(prefabs);

            foreach (var kv in _builtPrefabs)
            {
                var np = new NetworkPrefab { Prefab = kv.Value };
                dict[kv.Key] = np;
            }
            Debug.Log($"[PrefabRegistrar] registered {dict.Count} prefabs in NetworkPrefabOverrideLinks");
        }

        private static GameObject BuildOne(Entry e)
        {
            // Build the template DEACTIVATED so AddComponent doesn't
            // trigger Awake on every NetworkBehaviour stub for the
            // template itself. NGO's Object.Instantiate copy inherits
            // the deactivated state — but we then activate the copy via
            // a fresh template build below.
            //
            // Update: NGO's spawn pipeline doesn't auto-activate the
            // instance, and inactive instances do NOT receive
            // OnNetworkSpawn callbacks reliably. So we activate the
            // template after AddComponent calls — empty stub
            // NetworkBehaviours don't fail in Awake, and MirrorPlayer
            // doesn't either.
            var go = new GameObject($"PrefabTemplate_{e.Name}");
            go.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(go);

            var no = go.AddComponent<NetworkObject>();
            try
            {
                s_globalIdHashField?.SetValue(no, e.Hash);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PrefabRegistrar] {e.Name}: failed to set GlobalObjectIdHash: {ex.Message}");
            }

            // Opt these prefab templates out of NGO's PopulateScenePlacedObjects
            // auto-walk: it walks DDOL with FindObjectsByType<NetworkObject>
            // and adds any NO whose IsSceneObject != false to ScenePlacedObjects.
            // Without this, our 13 prefab templates pollute ScenePlacedObjects
            // alongside the real scene-placed stubs (outer.Count went 33 → 46
            // in the bot log), and the bot's lookup for a scene-placed hash
            // can hit a prefab template by mistake. NGO will re-set the flag
            // when it actually spawns one of these.
            try
            {
                var prop = typeof(NetworkObject).GetProperty(
                    "IsSceneObject",
                    BindingFlags.Public | BindingFlags.Instance);
                prop?.SetValue(no, (bool?)false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PrefabRegistrar] {e.Name}: failed to set IsSceneObject=false: {ex.Message}");
            }

            if (e.HelperComponentTypeNames != null)
                foreach (var helper in e.HelperComponentTypeNames)
                    AddComponentByName(go, helper, optional: true);

            // Prefer locally compiled types when supplied — these are
            // the Mirror_* NetworkBehaviour classes that have the right
            // NetworkVariable layout for byte-stream alignment.
            if (e.BehaviourTypes != null)
                foreach (var t in e.BehaviourTypes)
                    AddComponentByType(go, t);

            // Fallback: resolve by name (used while a prefab still has
            // no mirror classes written).
            if (e.BehaviourTypeNames != null)
                foreach (var nb in e.BehaviourTypeNames)
                    AddComponentByName(go, nb, optional: false);

            // Now that all components are added, activate the template.
            // Awake fires on every NetworkBehaviour. Since stubs have no
            // Awake logic and MirrorPlayer's Awake is also a no-op, this
            // is safe.
            go.SetActive(true);

            Debug.Log($"[PrefabRegistrar] built '{e.Name}' (hash {e.Hash}) with {go.GetComponents<Component>().Length} components");
            return go;
        }

        private static void AddComponentByType(GameObject go, Type t)
        {
            try
            {
                go.AddComponent(t);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PrefabRegistrar] adding {t.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AddComponentByName(GameObject go, string typeName, bool optional)
        {
            try
            {
                Type t = ResolveType(typeName);
                if (t == null)
                {
                    if (!optional) Debug.LogError($"[PrefabRegistrar] type not found: {typeName}");
                    return;
                }
                go.AddComponent(t);
            }
            catch (Exception ex)
            {
                string sev = optional ? "WARN" : "ERROR";
                Debug.Log($"[PrefabRegistrar] {sev} adding {typeName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Type ResolveType(string typeName)
        {
            // Search all loaded assemblies; Puck types live in Puck.dll
            // and don't have a stable namespace.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(typeName, throwOnError: false);
                if (t != null) return t;
                // Fallback: search by simple name.
                if (!typeName.Contains("."))
                {
                    foreach (var candidate in asm.GetTypes())
                    {
                        if (candidate.Name == typeName) return candidate;
                    }
                }
            }
            return null;
        }
    }
}
