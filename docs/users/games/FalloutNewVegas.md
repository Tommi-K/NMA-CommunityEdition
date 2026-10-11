# Fallout: New Vegas

!!! example "We're working on it"
    Fallout: New Vegas support is currently in development. Get involved by joining us on [GitHub](https://github.com/Tommi-K/NMA-CommunityEdition/issues/new/choose)!

## Features
In addition to basic mod management features, players also benefit from these dedicated features:

### Plugin management
The app writes the game's `Plugins.txt` for you, listing every plugin your enabled mods provide. Plugins are ordered so that masters come before the plugins that depend on them.

!!! note "Load order is decided by file dates"
    Fallout: New Vegas predates the plugin ordering used by Fallout 4 and Skyrim Special Edition. The game reads `Plugins.txt` only to decide which plugins are *switched on*; the order it loads them in comes from the plugin files' modification dates on disk. The app does not currently set those dates, so use a tool such as LOOT if you need a specific load order.

### Loadout Health Check
Get information on potential issues in your loadout(s). [Learn more about Health Checks.](../features/HealthCheck.md)

Diagnostics are shown in the following situations, based on the master files each plugin declares:

- A plugin requires a master file which is not provided by any installed mod.
- A plugin requires a master file which is installed but belongs to a disabled mod.

### Launching with NVSE
If [NVSE](https://github.com/xNVSE/NVSE) is installed in the game folder, the app launches the game through `nvse_loader.exe` so script extender mods load. Otherwise the game is started normally.

### Mod installation
Mods packaged as FOMODs are installed through the guided installer. Other archives are unpacked into the game's `Data` folder, with script extender files, ENB wrappers and the 4GB patcher placed in the game's root folder instead.

!!! warning "Official add-ons cannot be switched off"
    Unlike Fallout 4 and Skyrim Special Edition, the app cannot disable this game's own add-ons (Dead Money, Honest Hearts, Old World Blues, Lonesome Road, Gun Runners' Arsenal and the Courier's Stash packs). New Vegas activates them through marker files that sit next to each add-on's plugin, and the game honours those whatever `Plugins.txt` says.

## Compatibility
This game can be managed via the app on the following operating systems and platforms. The application will automatically detect valid installations from supported game stores if possible.

|| :fontawesome-brands-windows: Windows |  :fontawesome-brands-linux: Linux | :fontawesome-brands-apple: macOS |
|---|---|---|---|
| :fontawesome-brands-steam: [Steam](https://store.steampowered.com/app/22380/Fallout_New_Vegas/) | :material-check-circle: | :material-check-circle:[^1] | :material-close-thick: |
| <img src="../../images/GOG.com_logo_white.svg" alt="GOG" width="14"/> [GOG](https://www.gog.com/en/game/fallout_new_vegas_ultimate_edition_game) | :material-check-circle:[^2] | :material-check-circle:[^2][^3] | :material-close-thick: |
| <img src="../../images/epic-games.svg" alt="Epic Games Store" width="14"/> [Epic Games Store](https://store.epicgames.com/en-US/p/fallout-new-vegas) | :material-close-thick:[^4] | :material-close-thick:[^4] | :material-close-thick: |

[^1]: Runs through Proton. Fallout: New Vegas has no native Linux version.
[^2]: Offline backup installs from GOG.com cannot be detected automatically.
[^3]: [Heroic Launcher](https://heroicgameslauncher.com/) is required.
[^4]: An Epic copy is not detected automatically and has to be added by hand. Once added it is managed like any other installation.
