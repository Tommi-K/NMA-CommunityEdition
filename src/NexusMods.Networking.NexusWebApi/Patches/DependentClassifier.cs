using JetBrains.Annotations;

namespace NexusMods.Networking.NexusWebApi.Patches;

/// <summary>
/// What kind of add-on a mod that requires another one is.
/// </summary>
[PublicAPI]
public enum DependentKind
{
    /// <summary>
    /// Anything else: a mod that merely builds on the one it requires.
    /// </summary>
    None = 0,

    /// <summary>
    /// A patch, usually making the mod it requires work with another one.
    /// </summary>
    Patch = 1,

    /// <summary>
    /// A translation of the mod it requires.
    /// </summary>
    Translation = 2,
}

/// <summary>
/// A language a translation is in.
/// </summary>
/// <param name="Code">The ISO 639-1 code, such as <c>de</c>. Matches <see cref="System.Globalization.CultureInfo.TwoLetterISOLanguageName"/>.</param>
/// <param name="Name">The English name, such as <c>German</c>.</param>
[PublicAPI]
public sealed record ModLanguage(string Code, string Name);

/// <summary>
/// The kind of a mod and, for a translation, its language.
/// </summary>
[PublicAPI]
public readonly record struct DependentClassification(DependentKind Kind, ModLanguage? Language);

/// <summary>
/// Sorts mods that require another into patches, translations and everything else, going by
/// what their authors call them.
/// </summary>
/// <remarks>
/// The site has no field for either: a translation's language is not exposed by the API, and
/// only some games have a category for patches. The names are what is left, and they are
/// consistent enough to go on - "X - Y Patch", "X German Translation", "X - Русский".
/// </remarks>
[PublicAPI]
public static class DependentClassifier
{
    private sealed record LanguageDefinition(
        ModLanguage Language,
        // How speakers of the language write its name, and the language's own word for a
        // translation. Distinctive enough to stand on their own.
        string[] NativeNames,
        // Upper-case tags such as "DE", "RU" or "CHS". Two capitals can be anything, so these
        // only count when set apart from the rest of the name or next to a word for translation.
        string[] Tags);

    private static readonly LanguageDefinition[] Languages =
    [
        new(new ModLanguage("en", "English"), [], ["EN", "ENG"]),
        new(new ModLanguage("de", "German"), ["Deutsch", "Übersetz", "Uebersetz"], ["DE", "GER"]),
        new(new ModLanguage("fr", "French"), ["Français", "Francais", "Française", "Francaise", "Traduction", "Traduit"], ["FR", "VF"]),
        new(new ModLanguage("es", "Spanish"), ["Español", "Espanol", "Castellano", "Traducción", "Traduccion", "Traducido", "Traducida"], ["ES", "SPA"]),
        new(new ModLanguage("it", "Italian"), ["Italiano", "Italiana", "Italiane", "Traduzion", "Tradott"], ["ITA"]),
        new(new ModLanguage("pt", "Portuguese"), ["Português", "Portugues", "Brasileiro", "PT-BR", "PT BR", "PT_BR", "PTBR", "Tradução", "Traducao", "Traduzido", "Dublagem"], ["PT"]),
        new(new ModLanguage("ru", "Russian"), ["Русск", "Русиф", "Перевод", "Локализ"], ["RU", "RUS"]),
        new(new ModLanguage("uk", "Ukrainian"), ["Українськ", "Україн", "Переклад", "Локаліз"], ["UA", "UKR"]),
        new(new ModLanguage("pl", "Polish"), ["Polski", "Polska", "Polskie", "Polskiej", "Spolszcz", "Tłumacz"], ["PL", "POL"]),
        new(new ModLanguage("cs", "Czech"), ["Čeština", "Cestina", "Česk", "Překlad"], ["CZ"]),
        new(new ModLanguage("sk", "Slovak"), ["Slovenčina", "Preklad"], ["SK"]),
        new(new ModLanguage("sl", "Slovenian"), ["Slovenščina", "Slovenscina"], ["SLO"]),
        new(new ModLanguage("hr", "Croatian"), ["Hrvatsk"], ["HR", "CRO"]),
        new(new ModLanguage("sr", "Serbian"), ["Srpsk", "Српск"], ["SRB"]),
        new(new ModLanguage("bg", "Bulgarian"), ["Българ"], ["BG"]),
        new(new ModLanguage("hu", "Hungarian"), ["Magyar", "Fordít"], ["HU", "HUN"]),
        new(new ModLanguage("ro", "Romanian"), ["Română", "Românesc"], ["RO"]),
        new(new ModLanguage("nl", "Dutch"), ["Nederlands", "Vertaling"], ["NL"]),
        new(new ModLanguage("sv", "Swedish"), ["Svenska", "Översättning"], ["SV", "SWE"]),
        new(new ModLanguage("da", "Danish"), ["Dansk", "Oversættelse"], ["DK"]),
        new(new ModLanguage("nb", "Norwegian"), ["Norsk", "Oversettelse"], ["NOR"]),
        new(new ModLanguage("fi", "Finnish"), ["Suomi", "Suomenk", "Käännös"], ["FI"]),
        new(new ModLanguage("lt", "Lithuanian"), ["Lietuvi"], ["LT"]),
        new(new ModLanguage("et", "Estonian"), ["Eesti"], ["EE"]),
        new(new ModLanguage("el", "Greek"), ["Ελληνικ", "Μετάφραση"], ["GR"]),
        new(new ModLanguage("tr", "Turkish"), ["Türkçe", "Turkce", "Çeviri", "Ceviri"], ["TR", "TUR"]),
        new(new ModLanguage("ar", "Arabic"), ["العربية", "عربي"], ["ARA"]),
        new(new ModLanguage("he", "Hebrew"), ["עברית"], ["HEB"]),
        new(new ModLanguage("zh", "Chinese"), ["中文", "简体", "繁體", "繁体", "汉化", "漢化", "翻译", "翻譯", "Simplified Chin", "Traditional Chin"], ["CN", "CHS", "CHT", "CHN", "ZH", "SC", "TC"]),
        new(new ModLanguage("ja", "Japanese"), ["日本語", "翻訳"], ["JP", "JPN"]),
        new(new ModLanguage("ko", "Korean"), ["한국어", "한글", "번역"], ["KR", "KOR"]),
        new(new ModLanguage("th", "Thai"), ["ภาษาไทย"], []),
        new(new ModLanguage("vi", "Vietnamese"), ["Tiếng Việt", "Việt hóa", "Viet Hoa"], ["VN"]),
        new(new ModLanguage("id", "Indonesian"), ["Bahasa Indonesia"], []),
    ];

    /// <summary>
    /// Words for "translation" and "localisation", in English and the languages mods are most
    /// often translated into. Matched at the start of a word, so a stem covers its inflections.
    /// </summary>
    private static readonly string[] TranslationWords =
    [
        "translat", "traslat", "locali", "language pack", "langpack", "lang pack",
        "übersetz", "uebersetz", "tradu", "tłumacz", "spolszcz", "překlad", "preklad", "fordít", "magyarít",
        "vertaling", "översättning", "käännös", "oversættelse", "oversettelse", "çeviri", "ceviri", "prevod",
        "перевод", "русификат", "русифиц", "локализ", "переклад", "українізац", "μετάφραση",
        "翻译", "翻譯", "翻訳", "中文化", "日本語化", "한글화", "번역",
    ];

    /// <summary>
    /// Words that follow a language's English name when the name is about the text rather than
    /// the content, as in "German version". "Japanese" on its own is as likely to be a katana.
    /// </summary>
    private static readonly string[] LanguageQualifiers = ["version", "language", "lang", "text", "voice", "dub", "loc", "mcm", "trans", "patch"];

    /// <summary>
    /// Matched at the start of a word. "Compatible" is left out on purpose: it is as often a
    /// selling point ("An Intuitive and Compatible Spell Toggle") as a patch.
    /// </summary>
    private static readonly string[] PatchWords = ["compatibil", "compatabil", "compatch", "conflict resol"];

    /// <summary>
    /// Matched as whole words, so that a patcher - a framework such as RobCo Patcher or
    /// SkyPatcher, whose name every mod built on it repeats - doesn't count.
    /// </summary>
    private static readonly string[] PatchWholeWords = ["patch", "patches", "patched", "compat"];

    /// <summary>
    /// Tags also recognised in mixed case, as in "Patch - Rus". The others are too easily part
    /// of a name in mixed case: "Sci-Fi", "Attila the Hun", "Hot Spa".
    /// </summary>
    private static readonly HashSet<string> MixedCaseTags = ["RUS", "GER", "ITA", "POL", "UKR", "CHS", "CHT", "CHN", "JPN", "KOR", "TUR", "ENG", "FR"];

    /// <summary>
    /// Classifies a mod by its name and, when known, the category it is filed under.
    /// </summary>
    public static DependentClassification Classify(string name, string? categoryName = null)
    {
        var category = categoryName ?? string.Empty;

        var hasTranslationWord = ContainsAnyWord(name, TranslationWords) ||
                                 ContainsWord(category, "translat") || ContainsWord(category, "locali");

        var language = DetectLanguage(name, hasTranslationWord);
        if (hasTranslationWord || language is not null)
            return new DependentClassification(DependentKind.Translation, language);

        if (ContainsAnyWord(name, PatchWords) ||
            PatchWholeWords.Any(word => FindWords(name, word, StringComparison.OrdinalIgnoreCase, wholeWord: true).Any()) ||
            ContainsWord(category, "patch") || ContainsWord(category, "compat"))
            return new DependentClassification(DependentKind.Patch, Language: null);

        return new DependentClassification(DependentKind.None, Language: null);
    }

    /// <summary>
    /// Every language <see cref="Classify"/> can recognise, in no particular order.
    /// </summary>
    public static IEnumerable<ModLanguage> KnownLanguages => Languages.Select(static definition => definition.Language);

    /// <summary>
    /// The language <paramref name="name"/> names, if any.
    /// </summary>
    /// <remarks>
    /// A translation into English names English as well as the original's language more often
    /// than the other way round, so any other language wins over English.
    /// </remarks>
    private static ModLanguage? DetectLanguage(string name, bool hasTranslationWord)
    {
        ModLanguage? english = null;

        foreach (var definition in Languages)
        {
            if (!NamesLanguage(name, definition, hasTranslationWord)) continue;
            if (definition.Language.Code != "en") return definition.Language;
            english = definition.Language;
        }

        return english;
    }

    private static bool NamesLanguage(string name, LanguageDefinition definition, bool hasTranslationWord)
    {
        if (ContainsAnyWord(name, definition.NativeNames)) return true;

        foreach (var tag in definition.Tags)
        {
            foreach (var (index, length) in FindWords(name, tag, StringComparison.Ordinal, wholeWord: true))
            {
                if (hasTranslationWord || IsSetApart(name, index, length)) return true;
            }

            if (!MixedCaseTags.Contains(tag)) continue;

            foreach (var (index, length) in FindWords(name, tag, StringComparison.OrdinalIgnoreCase, wholeWord: true))
            {
                if (IsSetApart(name, index, length)) return true;
            }
        }

        foreach (var (index, length) in FindWords(name, definition.Language.Name, StringComparison.OrdinalIgnoreCase, wholeWord: true))
        {
            // "Katana - Japanese", "[Japanese]" or "Japanese version" is about the text;
            // "Japanese Katana" is not.
            if (hasTranslationWord || IsSetApart(name, index, length)) return true;
            if (IsFollowedByAny(name, index + length, LanguageQualifiers)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the match stands apart from the rest of the name: at the end, give or take a
    /// version number, or with a separator after it and either a separator or nothing before
    /// it - "X - RU", "(RU) X", "RU - X", "X - RU - Y".
    /// </summary>
    private static bool IsSetApart(string name, int index, int length)
    {
        var after = index + length;
        while (after < name.Length && char.IsWhiteSpace(name[after])) after++;

        if (!HasLetters(name, after)) return true;
        if (!IsSeparator(name[after])) return false;

        var before = index - 1;
        while (before >= 0 && char.IsWhiteSpace(name[before])) before--;

        return before < 0 || IsSeparator(name[before]);
    }

    // An underscore is left out: it joins words in names like "NL_MCM" rather than splitting them.
    private static bool IsSeparator(char c) => c is '-' or '–' or '—' or '|' or '[' or ']' or '(' or ')' or '{' or '}' or '/' or ':' or ',';

    private static bool HasLetters(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (char.IsLetter(text[i])) return true;
        }

        return false;
    }

    private static bool IsFollowedByAny(string name, int position, string[] words)
    {
        while (position < name.Length && !char.IsLetter(name[position])) position++;
        if (position >= name.Length) return false;

        var rest = name.AsSpan(position);
        foreach (var word in words)
        {
            if (!rest.StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (rest.Length == word.Length || !char.IsLetter(rest[word.Length])) return true;
        }

        return false;
    }

    private static bool ContainsAnyWord(string text, string[] words)
    {
        foreach (var word in words)
        {
            if (ContainsWord(text, word)) return true;
        }

        return false;
    }

    private static bool ContainsWord(string text, string word)
    {
        return FindWords(text, word, StringComparison.OrdinalIgnoreCase, wholeWord: false).Any();
    }

    /// <summary>
    /// Finds <paramref name="word"/> where it starts a word of <paramref name="text"/>, and with
    /// <paramref name="wholeWord"/> also ends one.
    /// </summary>
    /// <remarks>
    /// Scripts written without spaces between words, such as Chinese or Japanese, have no word
    /// boundaries to go by, so a word in one of them matches anywhere.
    /// </remarks>
    private static IEnumerable<(int Index, int Length)> FindWords(string text, string word, StringComparison comparison, bool wholeWord)
    {
        if (word.Length == 0) yield break;

        var unbounded = IsUnspacedScript(word[0]);
        var start = 0;

        while (start < text.Length)
        {
            var index = text.IndexOf(word, start, comparison);
            if (index < 0) yield break;

            var end = index + word.Length;
            var startsWord = unbounded || index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var endsWord = unbounded || !wholeWord || end == text.Length || !char.IsLetterOrDigit(text[end]);

            if (startsWord && endsWord) yield return (index, word.Length);
            start = index + 1;
        }
    }

    // CJK radicals onwards: Chinese, Japanese and Korean, plus Thai, which also goes without spaces.
    private static bool IsUnspacedScript(char c) => c >= '⺀' || c is >= '฀' and <= '๿';
}
