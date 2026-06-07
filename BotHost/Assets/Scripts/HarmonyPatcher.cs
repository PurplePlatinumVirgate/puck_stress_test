using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PuckStressTest
{
    // First Harmony patch: flip NGO's SceneEventData.EnableSerializationLogs
    // to true on every instance so we get per-SceneObject byte position
    // logs during scene synchronization. The flag is internal; it's
    // initialized to false in the field declaration and there is no
    // public way to flip it after the fact for the auto-created
    // synchronization SceneEventData. We postfix the constructor.
    //
    // Once the diagnostic data points at the actual misalignment
    // source, this file will grow surgical patches against whatever
    // NGO method is misbehaving.
    public static class HarmonyPatcher
    {
        private const string Id = "com.puckstresstest.harmony";
        private static Harmony s_harmony;

        // Keep Harmony in the build (pre-link strip). Unity's IL
        // linker can't see through reflection-only patch attribute
        // discovery, so we name a Harmony type statically.
        private static readonly Type s_keepAlive = typeof(Harmony);

        public static void Apply()
        {
            if (s_harmony != null) return;
            try
            {
                s_harmony = new Harmony(Id);
                s_harmony.PatchAll(Assembly.GetExecutingAssembly());
                Debug.Log("[HarmonyPatcher] applied; SceneEventData log flag will be flipped on every new instance.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HarmonyPatcher] failed to apply: {ex}");
            }
        }
    }

    // Patches NGO's SceneEventData ctor to flip on
    // EnableSerializationLogs. Accidentally load-bearing: although it
    // was added as a one-shot diagnostic, removing it consistently
    // breaks the bot's own-Player spawn. With the flag on, NGO's
    // per-iteration debug builder logs context inside the SceneObject
    // loop; the NRE that fires when spawnedNetworkObject is null
    // (scene-placed objects we don't have) bubbles up to NGO's outer
    // try/catch, which prints the builder and exits cleanly. Apparently
    // exiting the loop EARLY (via the NRE) is more recoverable than
    // letting it run to OverflowException at the next iteration. Until
    // we mirror all 33 scene-placed objects (#19), keep this on.
    //
    // 2026-04-27 follow-up: tried replacing this with a postfix on
    // CreateLocalNetworkObject that returns a placeholder NetworkObject
    // for missing scene-placed hashes. NGO then takes the success path
    // through SynchronizeNetworkBehaviours (which has its own catch+seek-
    // clamp at NetworkObject.cs:3078-3081), but byte alignment still
    // broke between iterations — next SceneObject deserialized to
    // Hash=0 and the loop OverflowException'd. Suspect the catch's
    // `reader.Seek(seekToEndOfSynchData)` clamps to Length when
    // sizeOfSynchronizationData computed from the empty-children path
    // ends up wrong. Reverted; staying with the NRE-based abort which
    // at least lets the bot's own Player spawn via the post-handshake
    // CreateObjectMessage. Real fix is task #19 (mirror scene-placed
    // objects properly so NGO's success path runs with real children).
    [HarmonyPatch]
    internal static class Patch_SceneEventData_Ctor
    {
        private const string TypeName = "Unity.Netcode.SceneEventData";

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(TypeName, throwOnError: false);
                if (t == null) continue;
                var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ctors.Length > 0) return ctors[0];
            }
            Debug.LogError("[Patch_SceneEventData_Ctor] could not find Unity.Netcode.SceneEventData");
            return null;
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            // B323 scene-sync misalignment root-caused (Puck moved
            // sizeOfSyncData into SceneObject.SynchronizationDataSize)
            // and patched in NetworkObject.cs. Serialization-logs no
            // longer needed for normal runs. Re-enable here if a
            // future drift breaks alignment again.
        }
    }

    // Re-inject our scene-placed stubs into NetworkSceneManager's
    // ScenePlacedObjects dict immediately before NGO walks the scene-
    // sync batch. The handler in `HandleSceneEvent` calls
    // `ScenePlacedObjects.Clear()` (NetworkSceneManager.cs:2635) on
    // every Synchronize event, wiping any pre-population. NGO's
    // PopulateScenePlacedObjects then refills from the active scene
    // via FindObjectsByType — but our stubs live in DDOL keyed by
    // SampleScene.handle, not the loaded scene's handle, so they're
    // not picked up. This prefix bridges that gap.
    [HarmonyPatch]
    internal static class Patch_SynchronizeSceneNetworkObjects
    {
        private const string TypeName = "Unity.Netcode.SceneEventData";
        private const string MethodName = "SynchronizeSceneNetworkObjects";

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(TypeName, throwOnError: false);
                if (t == null) continue;
                return t.GetMethod(MethodName, BindingFlags.NonPublic | BindingFlags.Instance);
            }
            return null;
        }

        [HarmonyPrefix]
        private static void Prefix(Unity.Netcode.NetworkManager networkManager)
        {
            try { ScenePlacedRegistrar.ReinjectInto(networkManager); }
            catch (Exception ex) { Debug.LogWarning("[Patch_SynchronizeSceneNetworkObjects] prefix failed: " + ex.Message); }
        }
    }

}
