# Combolands Tweaks

BepInEx 5 plugin for **Combolands: Roguelike Citybuilder** (Crux Games, Steam appid 4075620; Unity 6000.0.66f2, Mono).

## Layout

- `CombolandsTweaks/` - the plugin (`zeldo.combolands.tweaks`), net472, references the game install directly.
- `dev-deploy.sh` - builds Release and copies the DLL to `BepInEx/scripts` (hot reload via ScriptEngine, F6 or file watcher). `--plugins` installs to `BepInEx/plugins` instead (game closed).
- `decompile.sh` - decompiles `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll` into `decompiled/` (git-ignored). Re-run after a game update.
- `tools/Decompile/` - the decompiler (ICSharpCode.Decompiler whole-project output).

Log: `C:\Program Files (x86)\Steam\steamapps\common\Combolands\BepInEx\LogOutput.log`.
