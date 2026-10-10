<div align="center">
  <h1>NMA Community Edition</h1>
  <p><em>A community-maintained fork of the Nexus Mods App, focused on keeping Linux modding alive.</em></p>
</div>

---

## Introduction

The official [Nexus Mods App](https://help.nexusmods.com/article/164-discontinuing-the-nexus-mods-app-faq) was paused by Nexus Mods / Black Tree Gaming Ltd. to refocus on Vortex. **NMA Community Edition** picks up where they left off, with a focus on the **Linux ecosystem** (Steam, GOG via Heroic, Wine prefixes, etc.) — a use case the upstream app supported well but no longer maintains.

This is an **unofficial fork**, not affiliated with, endorsed by, or supported by Nexus Mods. All credit for the original application goes to the upstream team and contributors.

## Features

### Supported games

- **Cyberpunk 2077**
- **Stardew Valley**
- **Skyrim Special Edition**
- **Fallout 4**
- **Baldur's Gate 3**
- **Mount & Blade II: Bannerlord**

### Supported stores

- **Steam** (native + Proton)
- **GOG** (native + via Heroic Games Launcher)
- **Epic Games Store** (where applicable per game)
- **Microsoft Store / Xbox Game Pass** (where applicable per game)

### Built-in browser

Mod pages open in a Chromium tab inside the app instead of handing you off to your system browser.

- **Your Nexus Mods sign-in is kept** across restarts, so signing in is a one-off.
- **Collection auto-downloading without premium**: the app works through a collection one mod at a time, driving each mod's download page itself. Premium still downloads through the API.
- **Runs in the background**: a download run uses a single tab and never pulls the window in front of you, so you can start a collection and go do something else. There's a cancel button for the whole run.
- **Ad and tracker blocking**, on by default — toggle it in Settings > Privacy.

### Other

- **Linux-first**: bug fixes and packaging target Linux desktop environments first.
- **Reorderable tabs**: drag a tab header sideways to move it.
- **Add games manually** when the store locators can't find an install.
- **Accurate Play button on Linux**: the app follows the real game process through Steam/Proton, Heroic (GOG) and Epic, instead of flipping back to "Launch" the moment the launcher returns.
- **Much smaller disk footprint** for games the hashes database has no manifest for — the app records a baseline of the install instead of archiving the whole game folder.
- **9 UI languages**: English, French, German, Italian, Polish, Portuguese (Brazil), Russian, Turkish, Ukrainian — switchable in Settings > General > Language.
- **AppImage** distribution for easy install on any Linux distribution.

### Fixes worth calling out

- **FOMOD installers during collection installs**: collections install their mods in parallel, and every install shared one set of installer state — which left blank installer windows, installs waiting forever, and mods installed with another mod's choices. Each install now has its own, and installer windows are shown one at a time.
- **Truncated downloads**: a server that cuts a download short while still reporting success is now detected and the download retried, instead of a partial file being archived and extracting garbage on install.
- **Cyberpunk 2077** deployment covers every archive root, and no longer leaves duplicate file entries behind.
- **Synchroniser** no longer keeps stale deletion markers that contradicted active mods.

## Roadmap

The fork follows community demand rather than a rigid roadmap. Likely directions:

- **More games**: porting / completing support for popular Nexus Mods titles (Starfield, Fallout: New Vegas, Oblivion, etc.) as time and testing allow.
- **More Linux launchers**: better integration with Lutris, Bottles and other Wine front-ends.
- **Continued bug fixes**: real-world issues reported on the [issues page](../../issues) drive priorities.

If you want to suggest something or contribute, open an issue or pull request.

## Installation

Grab the latest AppImage from the [Releases page](../../releases), make it executable, and run it:

```bash
chmod +x NMAcommunity.App.x86_64.AppImage
./NMAcommunity.App.x86_64.AppImage
```

Detailed changes for each version are in the release notes, not duplicated here.

## Building from source

```bash
git clone https://github.com/RoLuNeo080521/NMA-CommunityEdition.git
cd NMA-CommunityEdition
dotnet build
```

You'll need the .NET 9 SDK installed.

## Credits

All credit for the original application goes to the **Nexus Mods team** and the upstream contributors. The original repository is [Nexus-Mods/NexusMods.App](https://github.com/Nexus-Mods/NexusMods.App). This fork merely maintains community patches on top of their work.

## License

GPL-3.0-only, as inherited from the upstream project. All original copyright notices are preserved. Full license text in [`LICENSE`](./LICENSE).
