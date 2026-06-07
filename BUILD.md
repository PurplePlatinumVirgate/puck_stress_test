# Building the Puck Stress Test harness

There are two things to build: the **bots** (a Unity project → `BotHost.exe`)
and the **server-side mods** (.NET class libraries → `.dll` plugins). Both build
against DLLs from your own Puck install — no proprietary game files are shipped
in this repo.

## 1. Prerequisites

| Tool | Version | Why |
|------|---------|-----|
| **Puck** (Steam) | current | The game. Provides the managed DLLs the bots and mods compile against. Steam AppID **2994020**. |
| **Puck Dedicated Server** | current | The server you run bots against. Installed via SteamCMD, AppID **3481440**. See [SERVER_SETUP.md](SERVER_SETUP.md). |
| **Unity Hub + Unity Editor** | **6000.0.44f1** | Builds `BotHost`. The version must match Puck's engine version exactly. Download: <https://unity.com/download>. |
| **.NET SDK** | 6.0 or newer | Builds the server-side mods (`dotnet build`). <https://dotnet.microsoft.com/download>. |
| **PowerShell** | 5+ / 7+ | Runs the helper scripts (Windows ships with it). |

> **Why the exact Unity version?** Puck's networking (NGO/UTP) wire format is tied
> to its engine build. The bots pin Unity 6000.0.44f1 and NGO 2.4.0 to match.

### Find your Puck install and set `PUCK_MANAGED`

The build needs the path to Puck's managed-DLL directory, e.g.
`...\steamapps\common\Puck\Puck_Data\Managed`. Set it once as an environment
variable so every build picks it up:

```powershell
# PowerShell — persists for your user (reopen the shell afterward)
[Environment]::SetEnvironmentVariable(
  "PUCK_MANAGED",
  "C:\Path\To\SteamLibrary\steamapps\common\Puck\Puck_Data\Managed",
  "User")
```

(If you don't set it, edit the `<PuckManaged>` fallback in each mod's `.csproj`.)

## 2. Build the server-side mods

From the repo root, build each mod in Release:

```powershell
dotnet build BotAuthBypassMod\BotAuthBypassMod.csproj -c Release
dotnet build TelemetryMod\TelemetryMod.csproj       -c Release   # optional
dotnet build ConfigCaptureMod\ConfigCaptureMod.csproj -c Release # optional
```

Each produces `bin\Release\netstandard2.1\<ModName>.dll`. Where to deploy them is
covered in [SERVER_SETUP.md](SERVER_SETUP.md). Only **BotAuthBypassMod** is
required for bots to connect (and even that is optional if you use the server
config flag — see SERVER_SETUP.md).

## 3. Build the bots (BotHost)

1. **Copy the Puck game DLLs** into the Unity project (they are gitignored and
   not shipped — you supply them from your install):
   ```powershell
   pwsh BotHost\Assets\Plugins\Puck\copy_dlls.ps1
   # uses $env:PUCK_MANAGED, or pass the path: copy_dlls.ps1 "<...>\Managed"
   ```
2. **Open `BotHost/` in Unity Hub once** ("Add project from disk") so it resolves
   the packages in `Packages/manifest.json` (NGO 2.4.0, UTP, InputSystem,
   Newtonsoft). First open takes a few minutes.
3. **Build a headless server build** — either from the Editor
   (*File → Build Settings → Dedicated Server / Windows → Build*), or from the
   command line:
   ```powershell
   & "C:\Program Files\Unity\Hub\Editor\6000.0.44f1\Editor\Unity.exe" `
     -batchmode -quit -nographics `
     -projectPath "<REPO>\BotHost" `
     -executeMethod PuckStressTest.EditorTools.HeadlessBuild.BuildWindowsServer `
     -logFile -
   ```
   Output: `BotHost\Build\BotHost\BotHost.exe`.

## 4. Verify

Run the smoke test in [SERVER_SETUP.md](SERVER_SETUP.md) → "Run bots against it".
4 bots is a good first check (12 is heavier on your machine):

```powershell
& "<REPO>\BotHost\Build\BotHost\BotHost.exe" -batchmode -nographics `
    --playbook "<REPO>\playbooks\smoke_12bots_30s.json" `
    --bots 4 --server 127.0.0.1 --port 30609
```

Success = bots connect and hold their seats for the run; per-child logs land in
`BotHost\Build\BotHost\Logs\children\*.log`. If not, see
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).

## Notes

- **Nothing proprietary is committed.** `Puck.dll`, the Unity engine DLLs, and
  the ONNX runtime are all gitignored; you obtain them yourself. Re-run
  `copy_dlls.ps1` after every Puck update.
- **Run `copy_dlls.ps1` *before* opening the project in Unity** — `BotHost.asmdef`
  references `0Harmony.dll`, which the script supplies from your Puck install.
- **The ONNX inference brain is optional and off by default.** The bots run a
  hand-coded heuristic brain; ONNX-policy inference is not needed for stress
  testing and is excluded from the default build (no `PUCKBOT_ONNX_AVAILABLE`
  define, no asmdef reference, runtime DLL not shipped). To turn it on, follow
  `BotHost/Assets/Scripts/Brain/README.md`.
