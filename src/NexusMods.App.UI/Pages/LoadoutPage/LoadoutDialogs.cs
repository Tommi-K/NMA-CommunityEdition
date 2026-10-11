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
    /// Asks the user to confirm an import, spelling out what it is about to change.
    /// </summary>
    public static IDialog ImportModList(string scopeDescription, bool hasModList, bool hasPluginList)
    {
        var changes = new List<string>();
        if (hasModList) changes.Add($"• Reorder {scopeDescription} and switch them on or off to match modlist.txt");
        if (hasPluginList) changes.Add("• Set the load order to match plugins.txt");

        return DialogFactory.CreateStandardDialog(
            "Import mod list",
            new StandardDialogParameters()
            {
                Text = $"""
                        This will:

                        {string.Join("\n", changes)}

                        Mods the files do not mention keep their current order and stay as they are. Mods
                        the files name but you have not installed are listed afterwards so you can add them.
                        """,
                Icon = IconValues.PictogramPlaylistAdd,
            },
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(
                    "Import",
                    ButtonDefinitionId.Accept,
                    ButtonAction.Accept,
                    ButtonStyling.Primary
                ),
            ]
        );
    }

    /// <summary>
    /// Reports what an import changed and which names it could not place.
    /// </summary>
    public static IDialog ModListImported(ModListImportResult result)
    {
        var lines = new List<string>();

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
