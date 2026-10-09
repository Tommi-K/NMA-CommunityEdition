using NexusMods.Abstractions.Serialization.ExpressionGenerator;
using NexusMods.App.UI.Pages;
using NexusMods.App.UI.Pages.Browser;
using NexusMods.App.UI.Pages.Changelog;
using NexusMods.App.UI.Pages.CollectionDownload;
using NexusMods.App.UI.Pages.DebugControls;
using NexusMods.App.UI.Pages.Diagnostics;
using NexusMods.App.UI.Pages.Diff.ApplyDiff;
using NexusMods.App.UI.Pages.Downloads;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.App.UI.Pages.LoadoutGroupFilesPage;
using NexusMods.App.UI.Pages.LoadoutPage;
using NexusMods.App.UI.Pages.MyGames;
using NexusMods.App.UI.Pages.MyLoadouts;
using NexusMods.App.UI.Pages.ObservableInfo;
using NexusMods.App.UI.Pages.Settings;
using NexusMods.App.UI.Pages.TextEdit;
using NexusMods.App.UI.WorkspaceSystem;

namespace NexusMods.App.UI;

internal class TypeFinder : ITypeFinder
{
    public IEnumerable<Type> DescendentsOf(Type type)
    {
        return AllTypes.Where(t => t.IsAssignableTo(type));
    }

    private static IEnumerable<Type> AllTypes => new[]
    {
        // factory context
        typeof(MyGamesPageContext),
        typeof(DiagnosticListPageContext),
        typeof(ApplyDiffPageContext),
        typeof(SettingsPageContext),
        typeof(ChangelogPageContext),
        typeof(TextEditorPageContext),
        typeof(MyLoadoutsPageContext),
        typeof(LoadoutGroupFilesPageContext),
        typeof(LibraryPageContext),
        typeof(LoadoutPageContext),
        typeof(CollectionLoadoutPageContext),
        typeof(ProtocolRegistrationTestPageContext),
        typeof(DownloadsPageContext),
        // Without this, closing the app with a browser tab open makes the whole saved
        // workspace fail to load again -- the restore reads every tab in one go, so one
        // unknown context takes the window's panels and tabs with it.
        typeof(BrowserPageContext),

        // workspace context
        typeof(EmptyContext),
        typeof(HomeContext),
        typeof(LoadoutContext),
        typeof(DownloadsContext),
        typeof(CollectionDownloadPageContext),
        typeof(ObservableInfoPageContext),
        typeof(DebugControlsPageContext),

        // other
        typeof(WindowData),
    };
}
