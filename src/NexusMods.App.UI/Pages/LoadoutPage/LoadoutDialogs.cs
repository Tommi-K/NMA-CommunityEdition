using Humanizer;
using NexusMods.Abstractions.Games;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.LoadoutPage;

public static class LoadoutDialogs
{
    public static IDialog CreateCollection()
    {
        return DialogFactory.CreateStandardDialog(
            "Name your Collection",
            new StandardDialogParameters()
            {
                Text = "This is the name that will appear in the left hand menu and on the Collections page.",
                InputLabel = "Collection name",
                InputWatermark = "e.g. My Armour Mods",
            },
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(
                    "Create",
                    ButtonDefinitionId.Accept,
                    ButtonAction.Accept,
                    ButtonStyling.Primary
                ),
            ],
            DialogWindowSize.Small
        );
    }

    public static IDialog RenameCollection(string collectionName)
    {
        return DialogFactory.CreateStandardDialog(
            $"Rename your collection \"{collectionName}\"",
            new StandardDialogParameters()
            {
                Text = $"Rename your existing collection \"{collectionName}\" to something else.",
                InputLabel = "New collection name",
                InputWatermark = "e.g. My Armour Mods",
                InputText = collectionName,
            },
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(
                    "Rename",
                    ButtonDefinitionId.Accept,
                    ButtonAction.Accept,
                    ButtonStyling.Primary
                ),
            ],
            DialogWindowSize.Small
        );
    }

    

    /// <summary>
    /// Asks before replacing files already in the chosen folder, which is easy to hit when the user
    /// picks a Mod Organizer profile folder.
    /// </summary>
    public static IDialog OverwriteModListFiles(string[] fileNames, string directory)
    {
        return DialogFactory.CreateStandardDialog(
            fileNames.Length == 1 ? $"Replace {fileNames[0]}?" : "Replace existing files?",
            new StandardDialogParameters()
            {
                Text = $"""
                        {string.Join(" and ", fileNames)} already exist{(fileNames.Length == 1 ? "s" : string.Empty)} in:

                        {directory}

                        Exporting will overwrite them.
                        """,
            },
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(
                    "Replace",
                    ButtonDefinitionId.Accept,
                    ButtonAction.Accept,
                    ButtonStyling.Destructive
                ),
            ],
            DialogWindowSize.Small
        );
    }

    /// <summary>
    /// The button on <see cref="ImportModList"/> that downloads the missing mods before importing.
    /// </summary>
    public static readonly ButtonDefinitionId DownloadAndImportButtonId = ButtonDefinitionId.From("download-and-import");

    /// <summary>
    /// Asks the user to confirm an import, spelling out what it is about to change, and offers to
    /// download the mods the list names that aren't installed.
    /// </summary>
    /// <param name="scopeDescription">Which mods the import is about.</param>
    /// <param name="hasModList">Whether a modlist.txt was picked.</param>
    /// <param name="hasPluginList">Whether a plugins.txt was picked.</param>
    /// <param name="missingCount">How many mods the list names that the loadout doesn't have.</param>
    /// <param name="downloadableCount">How many of those the list says where to download from.</param>
    public static IDialog ImportModList(string scopeDescription, bool hasModList, bool hasPluginList, int missingCount, int downloadableCount)
    {
        var changes = new List<string>();
        if (hasModList) changes.Add($"• Reorder {scopeDescription} and switch them on or off to match modlist.txt");
        if (hasPluginList) changes.Add("• Set the load order to match plugins.txt");

        var missing = missingCount == 0
            ? "Mods the files do not mention keep their current order and stay as they are."
            : downloadableCount == 0
                ? $"""
                   Mods the files do not mention keep their current order and stay as they are.

                   {"mod".ToQuantity(missingCount)} in the list {(missingCount == 1 ? "is" : "are")} not installed, and the list doesn't say where to download {(missingCount == 1 ? "it" : "them")} from. {(missingCount == 1 ? "It is" : "They are")} listed afterwards so you can add {(missingCount == 1 ? "it" : "them")}.
                   """
                : $"""
                   Mods the files do not mention keep their current order and stay as they are.

                   {"mod".ToQuantity(missingCount)} in the list {(missingCount == 1 ? "is" : "are")} not installed. {(downloadableCount == missingCount ? (missingCount == 1 ? "It" : "All of them") : $"{downloadableCount} of them")} can be downloaded from Nexus Mods and installed first, then put in place with the rest. Mods with installer options will ask for them.
                   """;

        IEnumerable<DialogButtonDefinition> buttons = downloadableCount == 0
            ?
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Import", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
            ]
            :
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Import without downloading", ButtonDefinitionId.Accept, ButtonAction.Accept),
                new DialogButtonDefinition($"Download {downloadableCount} and import", DownloadAndImportButtonId, ButtonAction.Accept, ButtonStyling.Primary),
            ];

        return DialogFactory.CreateStandardDialog(
            "Import mod list",
            new StandardDialogParameters()
            {
                Text = $"""
                        This will:

                        {string.Join("\n", changes)}

                        {missing}
                        """,
                Icon = IconValues.PictogramPlaylistAdd,
            },
            buttons.ToArray()
        );
    }

    /// <summary>
    /// Reports what an import changed and which names it could not place.
    /// </summary>
    /// <param name="result">What the import changed.</param>
    /// <param name="downloads">What downloading the missing mods managed, when that was asked for.</param>
    internal static IDialog ModListImported(ModListImportResult result, ModListDownloadResult? downloads = null)
    {
        var lines = new List<string>();

        if (downloads is not null)
            lines.Add($"Downloaded and installed {"mod".ToQuantity(downloads.Installed)}.");

        if (result.ModListStatus == ModListImportStatus.Applied)
        {
            lines.Add($"Ordered {"mod".ToQuantity(result.ModsOrdered)}, switched {result.ModsEnabled} on and {result.ModsDisabled} off.");
        }

        switch (result.PluginListStatus)
        {
            case ModListImportStatus.Applied:
                lines.Add($"Set the load order of {"plugin".ToQuantity(result.PluginsOrdered)}.");
                break;
            case ModListImportStatus.NotSupported:
                lines.Add("This game builds its load order from the plugins themselves, so plugins.txt was not applied.");
                break;
        }

        if (downloads is not null) AppendUnmatched(lines, "Couldn't be downloaded or installed", downloads.Failed);
        AppendUnmatched(lines, "Named in modlist.txt but not installed", result.UnmatchedMods);
        AppendUnmatched(lines, "Named in plugins.txt but not in this loadout", result.UnmatchedPlugins);

        return DialogFactory.CreateStandardDialog(
            "Mod list imported",
            new StandardDialogParameters()
            {
                Text = string.Join("\n\n", lines),
                Icon = IconValues.PictogramCelebrate,
            },
            [
                new DialogButtonDefinition("Close", ButtonDefinitionId.Close, ButtonAction.Reject),
            ]
        );
    }

    /// <summary>
    /// The number of names a result dialog spells out before it falls back to a count.
    /// </summary>
    private const int MaxUnmatchedNamesShown = 15;

    private static void AppendUnmatched(List<string> lines, string heading, string[] names)
    {
        if (names.Length == 0) return;

        var shown = names.Take(MaxUnmatchedNamesShown).Select(static name => "• " + name);
        var remainder = names.Length - MaxUnmatchedNamesShown;

        lines.Add($"{heading} ({names.Length}):\n{string.Join("\n", shown)}"
                  + (remainder > 0 ? $"\n• and {remainder} more" : string.Empty));
    }

    public static IDialog ShareCollectionSuccess(string collectionName)
    {
        return DialogFactory.CreateStandardDialog(
            "Your Collection Has Been Added as a Draft",
            new StandardDialogParameters()
            {
                Text = $"""
                    Click View Page to edit details and optionally publish your collection as either:
                    
                    • Listed – Anyone can discover this collection on Nexus Mods.
                    • Unlisted – Only people with the link can view it.
                    
                    You can change the visibility at any time in your collection settings on the Nexus Mods page.
                    """,
                Icon = IconValues.PictogramCelebrate,
            },
            [
                new DialogButtonDefinition("Close", ButtonDefinitionId.Close, ButtonAction.Reject),
                new DialogButtonDefinition("View page", ButtonDefinitionId.Accept, ButtonAction.Accept,
                    ButtonStyling.Default, IconValues.OpenInNew
                ),
            ]
        );
    }

    public static IDialog UpdateCollection(string collectionName)
    {
        return DialogFactory.CreateStandardDialog(
            "Share Your Collection on Nexus Mods",
            new StandardDialogParameters()
            {
                Text = $"""
                     Upload "{collectionName}" to Nexus Mods to share it with friends or, if you choose, with the entire Nexus Mods community.
                     
                     Your collection will be added as a private draft until you publish it.
                     """,
                Icon = IconValues.PictogramUpload,
            },
            [
                new DialogButtonDefinition(
                    "Cancel",
                    ButtonDefinitionId.Cancel,
                    ButtonAction.Reject
                ),
                new DialogButtonDefinition(
                    "Share to Nexus Mods",
                    ButtonDefinitionId.Accept,
                    ButtonAction.Accept,
                    ButtonStyling.Primary
                ),
            ]
        );
    }

    public static IDialog UpdateCollectionSuccess(string collectionName)
    {
        return DialogFactory.CreateStandardDialog(
            "Your Collection Has Been Updated",
            new StandardDialogParameters()
            {
                Text = $"""
                    You have successfully uploaded a new revision of "{collectionName}".
                    """,
                Icon = IconValues.PictogramCelebrate,
            },
            [
                new DialogButtonDefinition("Close", ButtonDefinitionId.Close, ButtonAction.Reject),
                new DialogButtonDefinition("View page", ButtonDefinitionId.Accept, ButtonAction.Accept,
                    ButtonStyling.Default, IconValues.OpenInNew
                ),
            ]
        );
    }
}
