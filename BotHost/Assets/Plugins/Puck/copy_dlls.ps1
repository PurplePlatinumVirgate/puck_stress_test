# Copies the Harmony / Cecil / MonoMod assemblies that ship with Puck into
# Assets/Plugins/Puck/ so the BotHost Unity project can build against them
# (BotHost.asmdef precompiledReferences "0Harmony.dll"; the bot uses Harmony to
# patch NGO at runtime). These DLLs are NOT shipped with this repo — you supply
# them from your own Puck install. Run this BEFORE opening the project in Unity.
# See BUILD.md.
#
# We deliberately do NOT copy Puck.dll: the bot transcribes the Puck types it
# needs (see Assets/Scripts/Mirror/), so the proprietary game assembly is not a
# build or runtime dependency. Newtonsoft.Json is provided by the UPM package
# (com.unity.nuget.newtonsoft-json), so copying it here would create a duplicate.
#
# Source: set the PUCK_MANAGED environment variable to your Puck managed-DLL
# directory (…\Puck\Puck_Data\Managed), or pass it as the first argument.
# Destination: this script's own folder (Assets/Plugins/Puck).
param([string]$Src = $env:PUCK_MANAGED)

if ([string]::IsNullOrWhiteSpace($Src)) {
    Write-Error "Set PUCK_MANAGED to your Puck '...\Puck_Data\Managed' directory, or pass it as the first argument. See BUILD.md."
    exit 1
}
$src = $Src
$dst = $PSScriptRoot

# Harmony plus its runtime dependencies (Cecil + MonoMod). This is the complete
# set the BotHost build/run needs.
$dlls = @(
    "0Harmony.dll",
    "Mono.Cecil.dll",
    "MonoMod.RuntimeDetour.dll",
    "MonoMod.Utils.dll"
)

foreach ($dll in $dlls) {
    $srcPath = Join-Path $src $dll
    $dstPath = Join-Path $dst $dll
    if (-not (Test-Path $srcPath)) {
        Write-Warning "Missing: $srcPath"
        continue
    }
    Copy-Item $srcPath $dstPath -Force
    Write-Host "Copied $dll"
}
