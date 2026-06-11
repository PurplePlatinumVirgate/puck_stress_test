using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PuckStressTest.EditorTools
{
    public static class HeadlessBuild
    {
        // Invoke from CLI:
        //   Unity.exe -batchmode -quit -nographics \
        //     -projectPath <BotHost> \
        //     -executeMethod PuckStressTest.EditorTools.HeadlessBuild.BuildWindowsServer \
        //     -logFile -
        //
        // Output: Build/BotHost/BotHost.exe (subsystem: server / dedicated).
        public static void BuildWindowsServer()
        {
            Build(BuildTarget.StandaloneWindows64, "Build/BotHost", "BotHost.exe");
        }

        // Linux DEDICATED SERVER subtarget — the canonical Linux bot build.
        // Strips the graphics subsystem entirely (no GfxDevice), which fixes the
        // headless-Linux NullGfxDevice first-frame SIGSEGV a plain Linux *player*
        // build hits ("Shader Sprites/Default not supported on this GPU" → null
        // deref before the game's first Start). Requires the "Linux Dedicated
        // Server Build Support" module (Unity Hub). The bot is still a network
        // client; run it headless:
        //   ./BotHost.x86_64 -batchmode -nographics --bots N --server <ip> --port 30609 --playbook ...
        //
        //   Unity -batchmode -quit -nographics -projectPath <BotHost> \
        //     -executeMethod PuckStressTest.EditorTools.HeadlessBuild.BuildLinuxDedicatedServer -logFile -
        //
        // Output: Build/BotHost-linux/BotHost.x86_64 (separate dir from the
        // Windows build so both platforms coexist — BotHost_Data is platform-
        // specific and can't be shared). The plain Linux player build was removed
        // (it crashes headless; use this).
        public static void BuildLinuxDedicatedServer()
        {
            Build(BuildTarget.StandaloneLinux64, "Build/BotHost-linux", "BotHost.x86_64",
                  StandaloneBuildSubtarget.Server);
        }

        // Shared build path so Windows + Linux targets stay in sync. Each platform
        // writes to its own outDir (BotHost_Data is platform-specific, so they
        // cannot share one directory).
        static void Build(BuildTarget target, string outDir, string outName,
                          StandaloneBuildSubtarget subtarget = StandaloneBuildSubtarget.Player)
        {
            System.IO.Directory.CreateDirectory(outDir);
            string outPath = System.IO.Path.Combine(outDir, outName);

            // Use whatever scenes are enabled in EditorBuildSettings; if
            // none, fall back to a default empty scene we make on the fly.
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                // Create a throwaway empty scene file to satisfy the build.
                var scenePath = "Assets/Scenes/SampleScene.unity";
                if (!System.IO.File.Exists(scenePath))
                {
                    System.IO.Directory.CreateDirectory("Assets/Scenes");
                    var newScene = UnityEditor.SceneManagement.EditorSceneManager
                        .NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
                    UnityEditor.SceneManagement.EditorSceneManager
                        .SaveScene(newScene, scenePath);
                }
                scenes = new[] { scenePath };
            }

            // EditorUserBuildSettings persists the subtarget across runs;
            // set it explicitly each build (Player or Server) so we don't
            // inherit a stale setting.
            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;

            // Disable IL stripping for the bot. 0Harmony.dll references
            // System.Reflection.Emit.Label etc., which UnityLinker can't
            // resolve under default stripping levels and bails the build.
            // The bot binary is not size-sensitive — it's a developer
            // tool — so disabling stripping is the cheapest fix. The setting
            // is per NamedBuildTarget, so target the right one for the subtarget.
            var named = subtarget == StandaloneBuildSubtarget.Server
                ? NamedBuildTarget.Server
                : NamedBuildTarget.Standalone;
            PlayerSettings.SetManagedStrippingLevel(named, ManagedStrippingLevel.Disabled);

            // Headless Linux (-nographics → NullGfxDevice) SIGSEGVs in the first
            // frame: the Unity splash screen renders its logo with the built-in
            // Sprites/Default shader, which has no null-device subshader
            // ("Shader Sprites/Default not supported on this GPU") → null deref,
            // landing after bootstrap and before the game's first Start().
            // Disable the splash so nothing renders pre-game. (Honored on
            // Plus/Pro; Personal forces the logo — fall back to the Linux
            // Dedicated Server subtarget or xvfb if this build still crashes.)
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;

            // Standard Standalone player (Win64 or Linux64); we run it with
            // -batchmode -nographics for headless ops. NOT
            // StandaloneBuildSubtarget.Server, which is a dedicated-server
            // build flavor that needs the platform's "Dedicated Server" Hub
            // module — we don't need it for a client bot.
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outPath,
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)subtarget,
                options = BuildOptions.Development | BuildOptions.AllowDebugging,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"[HeadlessBuild] {summary.result} — output {summary.outputPath} " +
                      $"size={summary.totalSize}B errors={summary.totalErrors} warnings={summary.totalWarnings}");

            if (summary.result != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }
    }
}
