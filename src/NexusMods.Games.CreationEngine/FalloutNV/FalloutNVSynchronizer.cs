using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.FalloutNV;

public class FalloutNVSynchronizer : ACreationEngineSynchronizer
{
    public FalloutNVSynchronizer(IServiceProvider provider, ICreationEngineGame game) : base(provider, game)
    {

    }

    // Vanilla files that ship in Data/ under an exact name. New Vegas has had no content
    // updates in well over a decade, so unlike Fallout 4 this set is fixed and can be
    // matched exactly - which matters because the base game's archives are prefixed just
    // "Fallout - ", and prefix matching on that would swallow any mod whose name starts
    // with "Fallout ".
    private static readonly HashSet<string> VanillaDataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "FalloutNV.esm",
        "Fallout - Meshes.bsa",
        "Fallout - Misc.bsa",
        "Fallout - Sound.bsa",
        "Fallout - Textures.bsa",
        "Fallout - Textures2.bsa",
        "Fallout - Voices1.bsa",
        "Update.bsa",
    };

    // The official add-ons. Each one ships an .esm, one or two .bsa archives named after
    // it, and a zero byte .nam marker, so these are matched as prefixes. The names are
    // distinctive enough that a mod will not collide with them by accident.
    private static readonly string[] VanillaDlcPrefixes =
    [
        "DeadMoney",
        "HonestHearts",
        "OldWorldBlues",
        "LonesomeRoad",
        "GunRunnersArsenal",
        "CaravanPack",
        "ClassicPack",
        "MercenaryPack",
        "TribalPack",
    ];

    // Files in the game root that come from the store rather than from a mod: the
    // executables, the launcher, and the redistributable runtime DLLs they are shipped
    // with. Marking them ignored stops a Steam re-verify from looking like a pile of
    // user-installed files when the upstream hashes DB doesn't cover this build.
    private static readonly HashSet<string> VanillaRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "FalloutNV.exe",
        "FalloutNVLauncher.exe",
        "GECK.exe",
        "GECKPowerUp.exe",
        "Fallout_default.ini",
        "binkw32.dll",
        "steam_api.dll",
        "installscript.vdf",
        "msvcm90.dll",
        "msvcp90.dll",
        "msvcr90.dll",
        "Microsoft.VC90.CRT.manifest",
        "d3dx9_38.dll",
        "xlive.dll",
        // Epic and GOG builds
        "EOSSDK-Win32-Shipping.dll",
        "Galaxy.dll",
        "GalaxyPeer.dll",
        "goggame.sdb",
    };

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        // Keep the base class's rule: mod-supplied .bsa archives are not backed up either,
        // they can always be re-extracted from the library.
        if (base.IsIgnoredBackupPath(path))
            return true;

        if (path.LocationId != LocationId.Game)
            return false;

        var relative = path.Path.ToString();

        // Root vanilla files (case-insensitive to survive case oddities on Windows/Wine).
        if (!relative.Contains('/') && VanillaRootFiles.Contains(relative))
            return true;

        if (relative.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            var name = relative.AsSpan("Data/".Length);

            // Only match at the top of Data/, not deeper, so a mod subfolder that happens
            // to share a name stays manageable.
            if (name.Contains('/'))
                return false;

            if (VanillaDataFiles.Contains(name.ToString()))
                return true;

            foreach (var prefix in VanillaDlcPrefixes)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Bound the match: the next character must start an extension or the
                // " - Main.bsa" style suffix, so "DeadMoneyTweaks.esp" is not caught.
                if (name.Length == prefix.Length) return true;
                var next = name[prefix.Length];
                if (next == '.' || next == ' ') return true;
            }
        }

        return false;
    }
}
