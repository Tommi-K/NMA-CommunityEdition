# Skyrim Special Edition

!!! example "We're working on it"
    Skyrim Special Edition support is currently in development. Get involved by joining us on [GitHub](https://github.com/Tommi-K/NMA-CommunityEdition/issues/new/choose)!

## Features
In addition to basic mod management features, players also benefit from these dedicated features:

### Plugin management
The app writes the game's `Plugins.txt` for you, listing and activating every plugin your enabled mods provide. Plugins are ordered so that masters load before the plugins that depend on them, and light plugins (`.esl`) and regular plugins (`.esp`) load after the masters they need.

### Loadout Health Check
Get information on potential issues in your loadout(s). [Learn more about Health Checks.](../features/HealthCheck.md)

Diagnostics are shown in the following situations, based on the master files each plugin declares:

- A plugin requires a master file which is not provided by any installed mod.
- A plugin requires a master file which is installed but belongs to a disabled mod.

### Turning off official add-ons
The game's three add-ons — Dawnguard, Hearthfire and Dragonborn — can be switched off per loadout without uninstalling them. The files stay in your game folder; the app simply leaves the add-on's plugin out of the generated plugin list.

!!! note "Update.esm is always loaded"
    `Update.esm` is a patch to the base game rather than optional content, so it is not offered as something to switch off.

### Launching with SKSE
If [SKSE64](https://skse.silverlock.org/) is installed in the game folder, the app launches the game through `skse64_loader.exe` so script extender mods load. Otherwise the game is started normally.

### Mod installation
Mods packaged as FOMODs are installed through the guided installer. Other archives are unpacked into the game's `Data` folder, with SKSE files placed in the game's root folder instead.

## Compatibility
This game can be managed via the app on the following operating systems and platforms. The application will automatically detect valid installations from supported game stores if possible.

|| :fontawesome-brands-windows: Windows |  :fontawesome-brands-linux: Linux | :fontawesome-brands-apple: macOS |
|---|---|---|---|
| :fontawesome-brands-steam: [Steam](https://store.steampowered.com/app/489830/The_Elder_Scrolls_V_Skyrim_Special_Edition/) | :material-check-circle: | :material-check-circle:[^1] | :material-close-thick: |
| <img src="../../images/GOG.com_logo_white.svg" alt="GOG" width="14"/> [GOG](https://www.gog.com/en/game/the_elder_scrolls_v_skyrim_special_edition) | :material-check-circle:[^2] | :material-check-circle:[^2][^3] | :material-close-thick: |
| <img src="../../images/epic-games.svg" alt="Epic Games Store" width="14"/> Epic Games Store[^5] | :material-check-circle: | :material-check-circle:[^3] | :material-close-thick: |
| :fontawesome-brands-xbox: [Xbox Game Pass (PC)](https://www.xbox.com/en-GB/games/store/the-elder-scrolls-v-skyrim-special-edition/9p81d4lnmnw3) | :material-check-circle:[^4] | :material-close-thick: | :material-close-thick: |

[^1]: Runs through Proton. Skyrim Special Edition has no native Linux version.
[^2]: Offline backup installs from GOG.com cannot be detected automatically.
[^3]: [Heroic Launcher](https://heroicgameslauncher.com/) or another Wine prefix is required.
[^4]: Xbox Game Pass detection is off by default. Turn on "Enable Xbox Game Pass" in the :material-cog: Settings menu.
[^5]: The app looks for an Epic installation, but this game no longer has a listing on the Epic Games Store.
