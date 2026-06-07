# Brain/ — bot decision logic

Two implementations of `IBrain`:

- **BotBrain** (in the parent directory, `BotBrain.cs`) — hand-coded
  heuristic. The default. Used to generate BC training data.
- **OnnxBrain** — loads a trained policy (.onnx) and runs CPU
  inference each tick. Selected via `--brain onnx --policy-path X.onnx`.

Both call `MirrorPlayerInput.Send*` (the action chokepoint) and read
state through `ObsBuilder` → `MirrorSynchronizedObjectManager.LatestPositions`
+ Mirror NVs (the state chokepoint), so `SnapshotLogger` works
identically under either brain.

## Enabling OnnxBrain

ONNX inference is **off by default** and is not needed for stress testing — the
default heuristic `BotBrain` drives the bots. `OnnxBrain.cs` is guarded by
`PUCKBOT_ONNX_AVAILABLE`; without that symbol it compiles to an inert stub. To
enable the real implementation:

1. **Install Microsoft.ML.OnnxRuntime** in the Unity project. Three
   options, easiest first:
   - **NuGetForUnity** (recommended): install
     [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) via
     UPM, then add `Microsoft.ML.OnnxRuntime` (CPU) from the NuGet
     package picker. Pulls the right native DLLs into `Assets/Packages/`.
   - **Manual DLL drop**: download
     `Microsoft.ML.OnnxRuntime.<ver>.nupkg` from nuget.org, unzip,
     and copy:
     - `lib/netstandard2.0/Microsoft.ML.OnnxRuntime.dll` →
       `BotHost/Assets/Plugins/`
     - `runtimes/win-x64/native/onnxruntime.dll` →
       `BotHost/Assets/Plugins/x86_64/`
     Then in Unity, mark the .dll's platform settings as Standalone /
     x86_64.
   - **Unity Sentis** (alternative inference engine): Sentis can
     consume ONNX directly. If you go this route, `OnnxBrain.cs`
     needs a different inference call but the ObsBuilder + decode
     logic stays the same.

2. **Re-add the asmdef reference.** In `BotHost.asmdef`, add
   `"Microsoft.ML.OnnxRuntime.dll"` back to `precompiledReferences` (it was
   removed for the stress-test-only build so the project compiles without the
   runtime).

3. **Define the symbol.** In Unity:
   `Edit → Project Settings → Player → Other Settings →
   Scripting Define Symbols`, add `PUCKBOT_ONNX_AVAILABLE`.

4. **Rebuild BotHost** via `HeadlessBuild.BuildWindowsServer`.

## Running

```
BotHost.exe --bots 12 --bots-per-process 1 \
    --brain onnx --policy-path path/to/policy.onnx \
    --duration 60
```

The bot loads the model at OnEnable (per child process), runs CPU
inference every tick, and logs to the same `Logs/snapshots/` tree.

## Sharing the obs schema

`ObsBuilder.cs` produces the 256-float observation vector consumed
by both training (post-snapshot) and inference (live). Today
`SnapshotLogger.cs` has its own copy of the obs-building math; the
intent is to refactor SnapshotLogger to call ObsBuilder.Build()
instead, eliminating the duplication. That refactor is a follow-up:
preserving the validated byte layout while editing the largest
single file in the bot is high-risk for low immediate gain. Until
then, ObsBuilder and SnapshotLogger MUST stay in lock-step manually.
