@echo off
REM Launches Puck as a headless test server for the bot stress harness.
REM
REM SETUP: copy this whole folder's contents into a working server directory
REM (e.g. next to your plugins), then run this script from there. See
REM SERVER_SETUP.md for the full walkthrough.
REM
REM What this does:
REM   - Runs Puck.exe in Unity batch mode (no graphics, no UI), which is what
REM     puts the game into server mode (ServerConfigurationManager only loads
REM     a config when Application.isBatchMode == true).
REM   - Points it at server_configuration.json, which has
REM     usePuckBannedSteamIds=false (so the gameplay server admits clients
REM     immediately without consulting Puck Central) and no password.
REM
REM Puck's game server listens on UDP 30609 (its default game port; shown in the
REM server log as "Server started on port 30609"). Bots connect to 127.0.0.1:30609.
REM
REM To stop the server: close this console window or Ctrl+C.

setlocal

REM Point this at your own Puck install. Either set the PUCK_EXE environment
REM variable, or edit the fallback path below.
if "%PUCK_EXE%"=="" set PUCK_EXE=C:\Path\To\SteamLibrary\steamapps\common\Puck\Puck.exe
set CONFIG=%~dp0server_configuration.json

if not exist "%PUCK_EXE%" (
  echo ERROR: Puck.exe not found at %PUCK_EXE%
  echo Set the PUCK_EXE environment variable or edit it in this script.
  exit /b 1
)

if not exist "%CONFIG%" (
  echo ERROR: server config not found at %CONFIG%
  exit /b 1
)

echo Launching Puck headless server...
echo   exe:    %PUCK_EXE%
echo   config: %CONFIG%
echo   cwd:    %~dp0  (so the Plugins\ folder is found here)
echo.

REM Run with CWD = this folder so Puck's ModManagerV2 looks for Plugins/
REM here (it uses Path.GetFullPath(".")/Plugins).
cd /d "%~dp0"
"%PUCK_EXE%" -batchmode -nographics --serverConfigurationPath "%CONFIG%"
