#!/usr/bin/env bash
# Development install with hot reload (BepInEx ScriptEngine from BepInEx/BepInEx.Debug).
#
# The mod lives in BepInEx/scripts instead of BepInEx/plugins. ScriptEngine loads it from a copy in memory, so the
# DLL isn't locked while the game runs, and its file watcher reloads the mod a few seconds after the DLL changes
# (or press F6). Run this after each build; the game can stay open.
#
#   ./dev-deploy.sh            build Release and copy it into BepInEx/scripts
#   ./dev-deploy.sh --plugins  go back to a normal install (game must be closed)
set -euo pipefail
cd "$(dirname "$0")"

GAME="/c/Program Files (x86)/Steam/steamapps/common/Combolands"
BUILD="CombolandsTweaks/bin/Release"
PLUGINS="$GAME/BepInEx/plugins"
SCRIPTS="$GAME/BepInEx/scripts"
DOTNET=/c/Users/bruta/ClaudeTools/dotnet/dotnet.exe

running() { tasklist 2>/dev/null | grep -qi "Combolands.exe"; }

DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_CLI_TELEMETRY_OPTOUT=1 "$DOTNET" build CombolandsTweaks -c Release -nologo -v q

if [[ "${1:-}" == "--plugins" ]]; then
    running && { echo "Close the game first (plugins/ DLLs are locked while it runs)."; exit 1; }
    cp "$BUILD/CombolandsTweaks.dll" "$PLUGINS/"
    rm -f "$SCRIPTS/CombolandsTweaks.dll" "$SCRIPTS/CombolandsTweaks.pdb"
    echo "Installed to plugins/ (no hot reload)."
    exit 0
fi

if [[ ! -f "$PLUGINS/ScriptEngine.dll" ]]; then
    echo "ScriptEngine.dll is missing from BepInEx/plugins; hot reload needs it."
    exit 1
fi

# A copy left in plugins/ would load too, and ScriptEngine refuses a second plugin with the same GUID.
if [[ -f "$PLUGINS/CombolandsTweaks.dll" ]]; then
    running && { echo "Close the game once so the plugins/ copy can be removed."; exit 1; }
    rm -f "$PLUGINS/CombolandsTweaks.dll"
fi

mkdir -p "$SCRIPTS"
cp "$BUILD/CombolandsTweaks.pdb" "$SCRIPTS/" 2>/dev/null || true
cp "$BUILD/CombolandsTweaks.dll" "$SCRIPTS/"
echo "Deployed to scripts/$(running && echo ' - reloading in a few seconds')."
