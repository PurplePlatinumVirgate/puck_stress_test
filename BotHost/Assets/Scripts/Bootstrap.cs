using UnityEngine;

namespace PuckStressTest
{
    // Auto-create the BotHost GameObject at startup so we don't need a
    // hand-curated scene file. RuntimeInitializeOnLoadMethod fires after
    // any scene loads, so this works equally in the editor and in
    // headless builds.
    public static class Bootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            // If a BotHost is already in the scene (e.g. someone added it
            // by hand), don't create a duplicate.
            if (Object.FindFirstObjectByType<BotHost>() != null) return;

            // Strip stack traces from Log/Warning/Error so NGO's verbose
            // dispatch errors (notably "NetworkBehaviour index N out of
            // bounds" from NetworkVariableDeltaMessage when our prefab
            // mirror's NB count differs from the server's) don't pin the
            // main thread inside StackTraceUtility.ExtractStackTrace.
            // With 12 bots × server tick rate × N NV deltas/tick the
            // unfiltered volume hit 165k errors per run, each emitting
            // a ~10-line stack trace — log thread choke turns
            // measured RTT into 200ms+ spikes. The ScriptOnly setting
            // would still capture C#-side stacks; we want None so the
            // logger only writes the message text.
            Application.SetStackTraceLogType(LogType.Error,     StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Assert,    StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning,   StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Log,       StackTraceLogType.None);
            // Keep ScriptOnly for Exception so real integration bugs
            // remain visible in C# stack form. The native-stack capture
            // (StackTraceLogType.Full) is what's expensive; ScriptOnly
            // stays cheap while preserving root-cause information.
            Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);

            // Apply Harmony patches BEFORE NetworkManager spawns; the
            // SceneEventData ctor postfix relies on running before any
            // NGO message-handling instances exist.
            Debug.Log("[Bootstrap] stage: pre-harmony");
            HarmonyPatcher.Apply();
            Debug.Log("[Bootstrap] stage: post-harmony");

            var go = new GameObject("BotHost");
            Debug.Log("[Bootstrap] stage: GO-created");
            go.AddComponent<BotHost>();
            Debug.Log("[Bootstrap] stage: component-added (Awake ran)");
            Object.DontDestroyOnLoad(go);
            Debug.Log("[Bootstrap] stage: bootstrap-done");
        }
    }
}
