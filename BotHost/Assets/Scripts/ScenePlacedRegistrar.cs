using System;
using System.Collections;
using System.Reflection;
using PuckStressTest.Mirror;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PuckStressTest
{
    // Pre-populates NGO's NetworkSceneManager.ScenePlacedObjects with
    // stub NetworkObjects for every scene-placed singleton in Puck's
    // gameplay scene. B323/B897 ground-truth: 33 entries (unchanged from
    // B323 through B897; captured by
    // ConfigCaptureMod 2026-05-13 from `srv_nbdump.log`'s
    // `SPAWNED NETWORK OBJECTS` + `[NB-LAYOUT]` sections).
    //
    // B323 vs B202: scene layout drastically simplified. B202 had 32
    // scene-placed objects with many multi-NB managers (Player Manager,
    // Puck Manager, Vote Manager, Replay Manager, UI Manager with 33
    // NBs, etc.). B323 collapsed most managers — gameplay scene now
    // carries 4 manager singletons (Chat / SynchronizedObject / Server /
    // Game Manager, 1 NB each), 14 PlayerPosition objects (6 per team
    // × 2 teams + 2 centers), 12 PuckPosition objects (Warmup 1..11 +
    // Playing), 3 BaseCamera scene objects (Team Blue/Red Position
    // Select + Cinematic), 1 Replay Camera, and 1 Level (17 NBs from
    // Level + 8× SyncAudio pair).
    //
    // Without this registrar, NGO's scene-sync loop fails resolving
    // the first scene-placed hash and aborts before reaching the
    // dynamically-spawned Player / Puck / Stick. With it, lookups
    // succeed, NV bytes get consumed through our (mostly empty) Mirror
    // layout, and the loop completes.
    public static class ScenePlacedRegistrar
    {
        public struct Entry
        {
            public uint   Hash;
            public string Name;
            // Number of NetworkBehaviours on the server's prefab, in
            // order. Use null (or omit) for empty NBs (most slots),
            // or a specific Type for the rare NB with NetworkVariables
            // we need to mirror correctly.
            public Type[] NbTypes;
        }

        // Hash → name → NB layout. Captured 2026-05-13 by
        // ConfigCaptureMod from a fresh dedicated-server boot
        // (srv_nbdump.log). Order within the array doesn't matter
        // (lookup is by hash). NB types in NbTypes MUST match the
        // server's `nbsOrdered` from the [NB-LAYOUT] dump because NGO
        // walks ChildNetworkBehaviours in declaration order when
        // reading NV/sync data.
        public static readonly Entry[] Entries =
        {
            // ── Manager singletons (4) ─────────────────────────────
            new Entry { Hash = 3289839906, Name = "Chat Manager",                NbTypes = new[] { typeof(MirrorEmpty) } }, // ChatManager(0 NVs)
            new Entry { Hash = 3420402611, Name = "Synchronized Object Manager", NbTypes = new[] { typeof(MirrorSynchronizedObjectManager) } }, // hooks Server_SynchronizeObjectsRpc
            new Entry { Hash = 3445996078, Name = "Server Manager",              NbTypes = new[] { typeof(MirrorServerManager) } }, // ServerManager(1 NV: Server struct)
            new Entry { Hash = 3885464518, Name = "Game Manager",                NbTypes = new[] { typeof(MirrorGameManager) } }, // GameManager(1 NV: GameState)

            // ── PlayerPosition scene objects (14) ──────────────────
            // 2 per team × 6 positions (LW/C/RW/LD/RD/G) + 2 extra
            // "C" duplicates. Each is a single PlayerPosition NB
            // with one NetworkObjectReference NV (ClaimedByPlayer).
            new Entry { Hash =  545915040, Name = "C (PlayerPosition)",          NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 1032755835, Name = "C (PlayerPosition)",          NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash =  684054533, Name = "LW (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 1131824149, Name = "LW (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 1799701982, Name = "RW (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 3600366787, Name = "RW (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 1628817490, Name = "LD (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 2244056922, Name = "LD (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 3563399594, Name = "RD (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 3964409804, Name = "RD (PlayerPosition)",         NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 1383825887, Name = "G (PlayerPosition)",          NbTypes = new[] { typeof(MirrorPlayerPosition) } },
            new Entry { Hash = 3319866970, Name = "G (PlayerPosition)",          NbTypes = new[] { typeof(MirrorPlayerPosition) } },

            // ── PuckPosition scene objects (12) ────────────────────
            // Warmup spawn points 1..11 + Playing spawn. PuckPosition
            // is a NetworkBehaviour with 0 NVs (just position metadata)
            // — MirrorEmpty stub suffices.
            new Entry { Hash =  319124411, Name = "Warmup 1 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash =  909643044, Name = "Warmup 2 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash =  417913614, Name = "Warmup 3 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash = 2538970534, Name = "Warmup 4 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash = 1875770022, Name = "Warmup 5 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash =  977634164, Name = "Warmup 6 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash = 2028575167, Name = "Warmup 7 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash =   15782295, Name = "Warmup 8 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash =  448476725, Name = "Warmup 9 (PuckPosition)",     NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash = 2768092110, Name = "Warmup 10 (PuckPosition)",    NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash = 4115211627, Name = "Warmup 11 (PuckPosition)",    NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash = 2934727896, Name = "Playing (PuckPosition)",      NbTypes = new[] { typeof(MirrorEmpty) } },

            // ── Cameras (4) ────────────────────────────────────────
            // BaseCamera and ReplayCamera both 0 NVs.
            new Entry { Hash = 1738640324, Name = "Team Blue Position Select Camera", NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash =  271183144, Name = "Team Red Position Select Camera",  NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash =  344193829, Name = "Cinematic Camera",            NbTypes = new[] { typeof(MirrorEmpty) } },
            new Entry { Hash = 4041641782, Name = "Replay Camera (scene)",       NbTypes = new[] { typeof(MirrorEmpty) } },

            // ── Level Default (17 NBs) ─────────────────────────────
            // Level(0 NVs) + 8× [SynchronizedAudio(2 NVs), SyncAudio-
            // Controller(0 NVs)] pairs. The pairs serialize Volume +
            // Pitch bytes — must use MirrorSynchronizedAudio so the
            // 16 bytes per pair are consumed.
            new Entry { Hash = 2691081831, Name = "Level Default",               NbTypes = new[] {
                typeof(MirrorEmpty),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
                typeof(MirrorSynchronizedAudio), typeof(MirrorSynchronizedAudioCtrl),
            } },
        };

        private static Type[] MakeEmpties(int n)
        {
            var arr = new Type[n];
            for (int i = 0; i < n; i++) arr[i] = typeof(MirrorEmpty);
            return arr;
        }

        private static FieldInfo s_globalIdHashField;
        // Per-NetworkManager stub set. Each bot needs its own stub
        // GameObjects because NetworkSpawnManager.SpawnNetworkObject-
        // LocallyCommon throws if `IsSpawned` is already true on the
        // stub — and in a 12-bot single-process run, the second bot's
        // sync would fail trying to re-spawn the first bot's stubs.
        private static readonly System.Collections.Generic.Dictionary<NetworkManager, System.Collections.Generic.Dictionary<uint, NetworkObject>> s_stubsByNm = new();

        // Build a per-NetworkManager stub set and inject it into NGO's
        // ScenePlacedObjects. Idempotent — repeated calls reuse the
        // same set for the same NM. NGO wipes ScenePlacedObjects on
        // every Synchronize event (HandleSceneEvent line 2635), so the
        // Harmony prefix on SynchronizeSceneNetworkObjects re-injects
        // per-sync.
        public static void EnsureBuilt(NetworkManager nm)
        {
            EnsureStubsForNm(nm);
            ReinjectInto(nm);
        }

        private static System.Collections.Generic.Dictionary<uint, NetworkObject> EnsureStubsForNm(NetworkManager nm)
        {
            if (nm == null) return null;
            if (s_stubsByNm.TryGetValue(nm, out var existing)) return existing;

            if (s_globalIdHashField == null)
            {
                s_globalIdHashField = typeof(NetworkObject).GetField(
                    "GlobalObjectIdHash",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            var byHash = new System.Collections.Generic.Dictionary<uint, NetworkObject>();
            int botIndex = s_stubsByNm.Count; // for unique GameObject names
            foreach (var e in Entries)
            {
                try
                {
                    var go = BuildOne(e, botIndex);
                    if (go == null) continue;
                    byHash[e.Hash] = go.GetComponent<NetworkObject>();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ScenePlacedRegistrar] build '{e.Name}' (hash {e.Hash}) for nm#{botIndex} failed: {ex.Message}");
                }
            }
            s_stubsByNm[nm] = byHash;
            Debug.Log($"[ScenePlacedRegistrar] built {byHash.Count}/{Entries.Length} stubs for NetworkManager#{botIndex}");
            return byHash;
        }

        // Inject the per-NM stub set into that NM's ScenePlacedObjects
        // dict, keyed by EVERY plausible scene handle NGO might query
        // with at lookup time. NGO computes the lookup key as
        // `SceneBeingSynchronized.handle`, where SceneBeingSynchronized
        // is set per-iteration via SetTheSceneBeingSynchronized — which
        // falls back through ScenesLoaded → active scene → NM
        // GameObject's scene. The actual handle empirically differs
        // between 1-bot (active scene, e.g. -76) and N-bot (some other
        // loaded scene, e.g. -12), so we key our stubs under all loaded
        // scene handles plus the active and DDOL handles. NGO's
        // ScenePlacedObjects dict supports multiple per-hash inner
        // entries (one per scene), so this is harmless: only the
        // matching inner key is used at lookup time.
        public static void ReinjectInto(NetworkManager nm)
        {
            object sceneMgr = nm?.SceneManager;
            if (sceneMgr == null) return;
            var byHash = EnsureStubsForNm(nm);
            if (byHash == null) return;

            var sceneHandles = new System.Collections.Generic.HashSet<int>();
            sceneHandles.Add(SceneManager.GetActiveScene().handle);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid()) sceneHandles.Add(s.handle);
            }
            // DDOL scene handle (only available via NetworkSceneManager.DontDestroyOnLoadScene)
            try
            {
                var ddolField = sceneMgr.GetType().GetField("DontDestroyOnLoadScene",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (ddolField != null)
                {
                    var ddol = ddolField.GetValue(sceneMgr);
                    var handleProp = ddol.GetType().GetProperty("handle");
                    if (handleProp != null) sceneHandles.Add((int)handleProp.GetValue(ddol));
                }
            }
            catch { }

            try
            {
                var smType = sceneMgr.GetType();
                var dictField = smType.GetField("ScenePlacedObjects",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var outer = (IDictionary)dictField.GetValue(sceneMgr);
                int reinjected = 0;
                foreach (var kv in byHash)
                {
                    if (!outer.Contains(kv.Key))
                    {
                        var innerType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(int), typeof(NetworkObject));
                        outer.Add(kv.Key, Activator.CreateInstance(innerType));
                    }
                    var innerDict = (IDictionary)outer[kv.Key];
                    foreach (var h in sceneHandles)
                    {
                        if (!innerDict.Contains(h))
                        {
                            innerDict.Add(h, kv.Value);
                            reinjected++;
                        }
                    }
                }
                Debug.Log($"[ScenePlacedRegistrar] reinjected {reinjected} (handles=[{string.Join(",", sceneHandles)}], nm-count={s_stubsByNm.Count}, outer.Count={outer.Count})");
            }
            catch (Exception ex)
            {
                Debug.LogError("[ScenePlacedRegistrar] ReinjectInto failed: " + ex);
            }
        }

        private static GameObject BuildOne(Entry e, int botIndex)
        {
            // Build deactivated to avoid Awake on the NetworkBehaviours;
            // activate after AddComponent so OnNetworkSpawn fires later
            // when NGO spawns it. Per-bot suffix on the GO name keeps
            // the SpawnedObjects diagnostic intelligible across bots.
            var go = new GameObject($"ScenePlaced_{e.Name}_{e.Hash}_bot{botIndex}");
            go.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(go);

            var no = go.AddComponent<NetworkObject>();
            try { s_globalIdHashField?.SetValue(no, e.Hash); }
            catch (Exception ex) { Debug.LogWarning($"[ScenePlacedRegistrar] {e.Name}: set hash failed: {ex.Message}"); }

            // Force IsSceneObject = false BEFORE adding components.
            // NGO's PopulateScenePlacedObjects (NetworkSceneManager.cs
            // line 2741) walks DDOL with FindObjectsByType and adds
            // any NetworkObject whose `IsSceneObject != false` to its
            // dict, throwing on duplicate hashes. With multiple bots
            // in one process, all bots' stubs share DDOL → duplicate
            // exception → connections drop. Setting IsSceneObject =
            // false up-front opts our stubs out of the auto-populate;
            // our `ReinjectInto` still injects them manually so NGO's
            // sync-loop lookup succeeds. NGO will overwrite this flag
            // back to true inside SpawnNetworkObjectLocallyCommon
            // when the stub actually spawns.
            try
            {
                var prop = typeof(NetworkObject).GetProperty(
                    "IsSceneObject",
                    BindingFlags.Public | BindingFlags.Instance);
                prop?.SetValue(no, (bool?)false);
            }
            catch (Exception ex) { Debug.LogWarning($"[ScenePlacedRegistrar] {e.Name}: set IsSceneObject failed: {ex.Message}"); }

            if (e.NbTypes != null)
                foreach (var t in e.NbTypes)
                    go.AddComponent(t);

            go.SetActive(true);
            return go;
        }
    }
}
