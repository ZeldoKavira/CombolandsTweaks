# Combolands Tweaks

BepInEx 5 plugin for **Combolands: Roguelike Citybuilder** (Crux Games, Steam appid 4075620; Unity 6, Mono).

## Features

- **Placement scores** - hover a card (or pick one) and every tile shows what the building would earn there, in
  average points per turn (or per activation): its own score, bonuses to nearby buildings, triggers, cooldown
  effects (Lamppost, irrigation), placement bonuses (Scaffolding, Nesting Doll, ...) and one-off points when built.
  Best tiles are framed in gold; Tab jumps to them. Cards show their best score; multi-placement cards get a planned
  best set of tiles.
- **Upgrade, paint and council vote previews** - hover a building upgrade, paint or vote to see points per turn now
  and with it on every building it affects.
- **Heirloom counts** - heirloom popups show how many you own and matching buildings on the map.
- **Draw pool** (F10), **building scores** (F11), **extra game speed**, all in the **F8** settings window.

## Install

Needs [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64). Extract the release zip into the game's
`BepInEx` folder (it holds `plugins/CombolandsTweaks.dll`), or install with DeckyModManager on Steam Deck.

## Development

- `CombolandsTweaks/` - the plugin (`zeldo.combolands.tweaks`), net472, references the game install directly.
- `dev-deploy.sh` - builds Release and copies the DLL to `BepInEx/scripts` (hot reload via ScriptEngine, F6 or file
  watcher). `--plugins` installs to `BepInEx/plugins` instead (game closed).
- `decompile.sh` - decompiles the game's assemblies into `decompiled/` (git-ignored). Re-run after a game update.
- `refs/Managed` - stripped reference assemblies (metadata only) so CI can build; regenerate with
  `refs/update-refs.ps1` after a game update or when a new game assembly is referenced.
- Release: bump `Version` in `Plugin.cs`, commit, push a `vX.Y.Z` tag; CI checks the tag matches and publishes
  `CombolandsTweaks-X.Y.Z.zip`.
