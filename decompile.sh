#!/usr/bin/env bash
# Decompiles the game's own assemblies into decompiled/ (git-ignored). Re-run after a game update.
set -euo pipefail
cd "$(dirname "$0")"
MANAGED="/c/Program Files (x86)/Steam/steamapps/common/Combolands/Combolands_Data/Managed"
DOTNET=/c/Users/bruta/ClaudeTools/dotnet/dotnet.exe
"$DOTNET" run -c Release --project tools/Decompile -- decompiled \
    "$MANAGED/Assembly-CSharp.dll" "$MANAGED/Assembly-CSharp-firstpass.dll"
