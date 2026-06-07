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
            string outDir = "Build/BotHost";
            System.IO.Directory.CreateDirectory(outDir);
            string outPath = System.IO.Path.Combine(outDir, "BotHost.exe");

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
            // explicitly reset it to Player so we don't accidentally pick
            // up a previous Server subtarget setting.
            EditorUserBuildSettings.standaloneBuildSubtarget =
                StandaloneBuildSubtarget.Player;

            // Disable IL stripping for the bot. 0Harmony.dll references
            // System.Reflection.Emit.Label etc., which UnityLinker can't
            // resolve under default stripping levels and bails the build.
            // The bot binary is not size-sensitive — it's a developer
            // tool — so disabling stripping is the cheapest fix.
            PlayerSettings.SetManagedStrippingLevel(
                NamedBuildTarget.Standalone,
                ManagedStrippingLevel.Disabled);

            // Standard Standalone Windows player; we run it with
            // -batchmode -nographics for headless ops. NOT
            // StandaloneBuildSubtarget.Server, which is a dedicated-server
            // build flavor that needs the "Windows Dedicated Server" Hub
            // module — we don't need it for a client bot.
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
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
