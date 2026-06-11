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
            // Diagnostic / Linux-Mono escape hatch: skip all Harmony patching.
            // The bot's scene-placed re-injection (Patch_SynchronizeSceneNetworkObjects)
            // is load-bearing, so with this set the bot won't spawn its Player —
            // but it isolates whether Harmony's detours are what SIGSEGVs on Linux.
            if (Environment.GetEnvironmentVariable("BOT_NO_HARMONY") == "1")
            {
                Debug.LogWarning("[HarmonyPatcher] SKIPPED (BOT_NO_HARMONY=1) — scene-sync patch OFF");
                return;
            }
            try
            {
                s_harmony = new Harmony(Id);
                s_harmony.PatchAll(Assembly.GetExecutingAssembly());
                Debug.Log("[HarmonyPatcher] applied.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HarmonyPatcher] failed to apply: {ex}");
            }
        }
    }

    // NOTE (2026-06-10): the former Patch_SceneEventData_Ctor (a postfix on
    // NGO's SceneEventData *constructor*) was REMOVED. Its body had already been
    // emptied — the B323 scene-sync misalignment was root-caused and fixed in
    // NetworkObject.cs, so the serialization-logs flag it used to flip is no
    // longer needed. It was therefore a no-op detour on a constructor, and
    // constructor detours are the most fragile Harmony operation on Mono — a
    // prime suspect for the Linux-Mono SIGSEGV. Removing it drops the patch
    // surface to just the load-bearing scene-placed re-injection below. If a
    // future NGO drift breaks scene-sync alignment again, restore from git.

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
