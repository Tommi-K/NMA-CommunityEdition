using NexusMods.Paths;

namespace NexusMods.Games.CreationEngine;

public static class KnownCEExtensions
{
    public static readonly Extension BSA = new Extension(".bsa");
    public static readonly Extension BA2 = new Extension(".ba2");
    public static readonly Extension ESM = new Extension(".esm");
    public static readonly Extension ESP = new Extension(".esp");
    public static readonly Extension ESL = new Extension(".esl");
    
    public static readonly Extension[] PluginFiles = [ESM, ESP, ESL];

    /// <summary>
    /// Plugin extensions for the pre-Fallout 4 Gamebryo engines, which have no light
    /// plugins and will not load an <c>.esl</c>.
    /// </summary>
    public static readonly Extension[] GamebryoPluginFiles = [ESM, ESP];
    
    public static readonly Extension[] Plugins = { ESM, ESP, ESL };
}
