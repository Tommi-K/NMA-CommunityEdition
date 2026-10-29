<div align="center">
  <h1>NMA Community Edition</h1>
  <p><strong>A community fork of the Nexus Mods App, built for modding on Linux.</strong></p>
  <p>
    <a href="https://github.com/RoLuNeo080521/NMA-CommunityEdition/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/RoLuNeo080521/NMA-CommunityEdition?sort=semver"></a>
    <a href="https://github.com/RoLuNeo080521/NMA-CommunityEdition/actions/workflows/tests.yml"><img alt="Tests" src="https://github.com/RoLuNeo080521/NMA-CommunityEdition/actions/workflows/tests.yml/badge.svg"></a>
    <a href="./LICENSE.md"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue"></a>
  </p>
</div>

---

## Why this fork

Nexus Mods [paused the Nexus Mods App](https://help.nexusmods.com/article/164-discontinuing-the-nexus-mods-app-faq) to focus on Vortex. Vortex is a Windows app, so Linux players lost their native mod manager.

**NMA Community Edition** keeps the app going for them. It handles Steam and Proton, GOG and Epic games installed with Heroic, GOG games installed with Lutris, and Wine prefixes, and it ships as a single AppImage.

> This is an **unofficial fork**. It is not affiliated with, endorsed by or supported by Nexus Mods. All credit for the original application goes to the upstream team and contributors.

## What the fork adds

- **GOG games installed with Lutris** are detected and launched, with recent Lutris versions and the Flatpak build.
- **Epic games installed with Heroic** are detected and launched through Heroic, native and Flatpak.
- **Game versions newer than the Nexus file database** (for example Cyberpunk 2077 2.5) are handled safely. NMA takes the game folder as it first indexed it as the original game files, so "Clean folder" no longer deletes the game.
- **Fallout 4 end to end**, F4SE included, on Steam through Proton.
- **The Play button follows the running game** on Linux, through launcher-to-game hand-offs.
- **No telemetry.** No diagnostic or usage data is sent to Nexus Mods. The app identifies itself as NMA Community Edition to the Nexus Mods API.
- **A missing game no longer breaks the app.** If a managed game is uninstalled or moved, its loadouts are hidden until the game is found again.
- **Many fixes to mod files and downloads**: files shared by two mods, deleted files that came back, downloads stuck "in progress", leftover temporary archives.
- **Kept up to date**: ships .NET 10 and the latest runtime patch, keeps its dependencies free of known vulnerabilities, and tells you when a new release is out.
- **Reorderable tabs**: drag a tab header sideways to move it.
- **Add games manually** when the store locators can't find an install.
- **Much smaller disk footprint** for games the hashes database has no manifest for: the app records a baseline of the install instead of archiving the whole game folder.

The [release notes](../../releases) list every change.

### Built-in browser

Mod pages open in a Chromium tab inside the app instead of handing you off to your system browser.

- **Your Nexus Mods sign-in is kept** across restarts, so signing in is a one-off.
- **Collection auto-downloading without premium**: the app works through a collection one mod at a time, driving each mod's download page itself. Premium still downloads through the API.
- **Runs in the background**: a download run uses a single tab and never pulls the window in front of you, so you can start a collection and go do something else. There's a cancel button for the whole run.
- **Ad and tracker blocking**, on by default. Toggle it in Settings > Privacy.

### Fixes worth calling out

- **FOMOD installers during collection installs**: collections install their mods in parallel, and every install shared one set of installer state, which left blank installer windows, installs waiting forever, and mods installed with another mod's choices. Each install now has its own, and installer windows are shown one at a time.
- **Truncated downloads**: a server that cuts a download short while still reporting success is now detected and the download retried, instead of a partial file being archived and extracting garbage on install.
- **Cyberpunk 2077** deployment covers every archive root, and no longer leaves duplicate file entries behind.
- **Synchroniser** no longer keeps stale deletion markers that contradicted active mods.

## Supported games

| Game | Notes |
|---|---|
| Cyberpunk 2077 | REDmod load order |
| Stardew Valley | SMAPI mods |
| Skyrim Special Edition | SKSE launch |
| Fallout 4 | F4SE launch |
| Fallout: New Vegas | |
| Baldur's Gate 3 | |
| Mount & Blade II: Bannerlord | |

On Linux, NMA finds games installed with:

- **Steam**, native and Proton games
- **Heroic Games Launcher** (GOG and Epic Games Store)
- **Lutris** (GOG), native and Flatpak
- **Wine prefixes** (GOG and Epic Games Store installs)

## Install

Download `NMAcommunity.App.x86_64.AppImage` from the [latest release](../../releases/latest), make it executable and run it:

```bash
chmod +x NMAcommunity.App.x86_64.AppImage
./NMAcommunity.App.x86_64.AppImage
```

- On a system without FUSE, run it with `APPIMAGE_EXTRACT_AND_RUN=1 ./NMAcommunity.App.x86_64.AppImage`.
- Data lives in `~/.local/share/NMAcommunity.App/`, and logs in `~/.local/state/NMAcommunity.App/`. Both are separate from the official app, so the two can be installed side by side.

## Roadmap

The fork follows what players need rather than a fixed plan. Likely next steps:

- **More games**, such as Starfield and Oblivion, as time and testing allow.
- **More Linux launchers**: Lutris beyond GOG, Bottles.
- **Bug fixes first.** Reports on the [issues page](../../issues) set the priorities.

Suggestions and pull requests are welcome.

## Building from source

```bash
git clone --recurse-submodules https://github.com/RoLuNeo080521/NMA-CommunityEdition.git
cd NMA-CommunityEdition
dotnet build --configuration Release
```

You need:

- the .NET 10 SDK;
- network access to `data.nexusmods.com` during the build;
- the git submodules. In an existing clone, fetch them with `git submodule update --init --recursive`.

[`CLAUDE.md`](./CLAUDE.md) documents the build, tests, AppImage packaging and releases. It is written in French.

## Made with Claude

NMA Community Edition is maintained by [RoLuNeo080521](https://github.com/RoLuNeo080521) with **[Claude Code](https://claude.com/claude-code)**, Anthropic's AI coding assistant. Since v1.1.0, the fork's fixes and features have been built together:

- **Claude** tracks bugs down to their root cause, writes the fixes and their tests, and prepares the pull requests and release notes.
- **The maintainer** decides what goes in, tests each release's AppImage and publishes it.

That partnership is what lets a one-person fork ship fixes this quickly. Commits written with Claude carry a `Co-Authored-By: Claude` trailer, and [`CLAUDE.md`](./CLAUDE.md) holds the project rules Claude works by.

## Credits

All credit for the original application goes to the **Nexus Mods team** and the upstream contributors. The original repository is [Nexus-Mods/NexusMods.App](https://github.com/Nexus-Mods/NexusMods.App), and this fork builds on their work.

## License

GPL-3.0-only, as inherited from the upstream project. All original copyright notices are kept. The full text is in [`LICENSE.md`](./LICENSE.md).
