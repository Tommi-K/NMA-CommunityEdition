# Fallout 4

!!! example "We're working on it"
    Fallout 4 support is currently in development. Get involved by joining us on [GitHub](https://github.com/Tommi-K/NMA-CommunityEdition/issues/new/choose)!

## Features
In addition to basic mod management features, players also benefit from these dedicated features:

### Plugin management
The app writes the game's `plugins.txt` for you, listing and activating every plugin your enabled mods provide. Plugins are ordered so that masters load before the plugins that depend on them, and light plugins (`.esl`) and regular plugins (`.esp`) load after the masters they need.

### Loadout Health Check
Get information on potential issues in your loadout(s). [Learn more about Health Checks.](../features/HealthCheck.md)

Diagnostics are shown in the following situations, based on the master files each plugin declares:

- A plugin requires a master file which is not provided by any installed mod.
- A plugin requires a master file which is installed but belongs to a disabled mod.

### Turning off official add-ons
The game's own add-ons — Automatron, Wasteland Workshop, Far Harbor, Contraptions Workshop, Vault-Tec Workshop, Nuka-World and the High Resolution Texture Pack — can be switched off per loadout without uninstalling them. The files stay in your game folder; the app simply leaves the add-on's plugin out of the generated plugin list.

### Launching with F4SE
If [F4SE](https://f4se.silverlock.org/) is installed in the game folder, the app launches the game through `f4se_loader.exe` so script extender mods load. Otherwise the game is started normally.

### Mod installation
Mods packaged as FOMODs are installed through the guided installer. Other archives are unpacked into the game's `Data` folder, with F4SE and plugin preloader files placed in the game's root folder instead.

## Compatibility
This game can be managed via the app on the following operating systems and platforms. The application will automatically detect valid installations from supported game stores if possible.

|| :fontawesome-brands-windows: Windows |  :fontawesome-brands-linux: Linux | :fontawesome-brands-apple: macOS |
|---|---|---|---|
| :fontawesome-brands-steam: [Steam](https://store.steampowered.com/app/377160/Fallout_4/) | :material-check-circle: | :material-check-circle:[^1] | :material-close-thick: |
| <img src="../../images/GOG.com_logo_white.svg" alt="GOG" width="14"/> [GOG](https://www.gog.com/en/game/fallout_4_game_of_the_year_edition) | :material-check-circle:[^2] | :material-check-circle:[^2][^3] | :material-close-thick: |
| <img src="../../images/epic-games.svg" alt="Epic Games Store" width="14"/> [Epic Games Store](https://store.epicgames.com/en-US/p/fallout-4) | :material-check-circle: | :material-check-circle:[^3] | :material-close-thick: |
| :fontawesome-brands-xbox: [Xbox Game Pass (PC)](https://www.xbox.com/en-GB/games/store/fallout-4/bb8t6pt0d5qq) | :material-check-circle:[^4] | :material-close-thick: | :material-close-thick: |

[^1]: Runs through Proton. Fallout 4 has no native Linux version.
[^2]: Offline backup installs from GOG.com cannot be detected automatically.
[^3]: [Heroic Launcher](https://heroicgameslauncher.com/) or another Wine prefix is required.
[^4]: Xbox Game Pass detection is off by default. Turn on "Enable Xbox Game Pass" in the :material-cog: Settings menu.
