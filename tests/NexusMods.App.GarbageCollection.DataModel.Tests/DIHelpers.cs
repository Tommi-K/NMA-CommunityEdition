using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Backend;
using NexusMods.Games.TestFramework;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.App.GarbageCollection.DataModel.Tests;

public static class DIHelpers
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddDefaultServicesForTesting()
            .AddGameServices()
            .AddGames()
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }
}
