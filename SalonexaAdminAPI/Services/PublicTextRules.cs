using System.Globalization;
using System.Text;

namespace SalonexaAdminAPI.Services;

/// <summary>
/// Content rules for text owners type in that customers later see (category, service name, description,
/// salon name). Offensive, sexual, massage/spa-related and dangerous wording is refused. Keep the lists in
/// step with the app's category-name-rules.ts, which gives the same answer instantly in the form.
/// This is a safety net, not a moderator: new slang can slip past, so also review what owners publish.
/// </summary>
public static class PublicTextRules
{
    // Whole words only, so ordinary names such as "Cocktail" or "Class" are never caught by accident.
    private static readonly HashSet<string> BlockedWords = new(StringComparer.Ordinal)
    {
        // Sexual
        "sex", "sexy", "sexual", "nude", "nudes", "naked", "xxx", "nsfw", "escort", "escorts", "erotic",
        "fetish", "brothel", "hooker", "hookers", "stripper", "strippers", "stripclub", "orgy", "hentai",
        "tits", "pussy", "cock", "dick", "dildo", "blowjob", "handjob", "milf", "callgirl", "callgirls",
        // Massage / spa
        "massage", "massages", "masseuse", "masseur", "bodyrub", "happyending",
        "spa", "spas", "bodyspa", "hairspa", "facespa", "footspa", "headspa",
        // More sexual / abusive
        "cum", "jizz", "rape", "rapist", "rapes", "molest", "pedo", "paedo", "pedophile", "paedophile", "incest",
        "bugger", "prick", "wank", "piss", "pissed", "crap", "douche", "douchebag", "jackass", "dumbass", "moron",
        "idiot", "scumbag", "tranny", "dyke", "spic", "wetback", "coon", "negro", "slutty", "whorish", "horny",
        // Offensive
        "fuck", "fucker", "fucking", "shit", "bitch", "bastard", "asshole", "arse", "cunt", "slut", "whore", "wanker",
        "bollocks", "twat", "retard", "retarded", "nigger", "nigga", "faggot", "fag", "paki", "chink", "kike", "gook",
        // Hindi / Tamil
        "chutiya", "chutiye", "madarchod", "behenchod", "bhosdike", "bhosdi", "randi", "harami", "gandu", "lund", "gaand",
        "chut", "chod", "chodu", "chodna", "lauda", "lavda", "bhosda", "bhenchod", "kutta", "kutti", "kamina",
        "kamine", "kameena", "punda", "pundai", "thevidiya", "thevidiyaa", "thevdiya", "otha", "koothi", "oombu",
        "mayiru", "soothu",
        // Dangerous / illegal
        "drug", "drugs", "weed", "marijuana", "cannabis", "ganja", "cocaine", "heroin", "meth", "opium", "lsd",
        "weapon", "weapons", "gun", "guns", "firearm", "firearms", "explosive", "explosives",
        "bomb", "bombs", "poison", "gambling", "casino", "betting", "terror", "terrorist", "suicide", "murder",
    };

    // Long enough that they cannot appear inside an innocent word, so they are also caught when
    // glued to other letters ("bodymassage") or spaced out ("m a s s a g e").
    private static readonly string[] BlockedFragments =
    {
        "massage", "masseu", "porn", "fuck", "prostitut", "erotic", "cocaine", "heroin", "madarchod", "behenchod",
        "bhosdike", "nigger", "faggot", "explosive", "happyending", "bodyrub", "bodytobody",
        "pedophil", "paedophil", "molest", "asshole", "dickhead", "bhenchod",
    };

    // Letters from other alphabets that look like Latin ones, used to disguise a word ("mаssаge" with Cyrillic а).
    private static readonly Dictionary<char, char> LookAlikes = new()
    {
        ['0'] = 'o', ['1'] = 'i', ['!'] = 'i', ['|'] = 'i', ['3'] = 'e', ['4'] = 'a', ['@'] = 'a',
        ['5'] = 's', ['$'] = 's', ['7'] = 't', ['8'] = 'b',
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['х'] = 'x', ['у'] = 'y', ['і'] = 'i',
        ['ѕ'] = 's', ['к'] = 'k', ['м'] = 'm', ['т'] = 't', ['н'] = 'h', ['в'] = 'b', ['ј'] = 'j', ['ԁ'] = 'd',
        ['α'] = 'a', ['ε'] = 'e', ['ο'] = 'o', ['ι'] = 'i', ['ν'] = 'v', ['ρ'] = 'p', ['κ'] = 'k', ['τ'] = 't',
        ['υ'] = 'u', ['χ'] = 'x', ['η'] = 'n', ['β'] = 'b',
    };

    private static readonly string[] CollapsedFragments;
    // Catches stretched spellings ("seex", "massaage"). Short entries are skipped so "Wed" is not read as "weed".
    private static readonly HashSet<string> CollapsedWords;

    static PublicTextRules()
    {
        CollapsedFragments = BlockedFragments.Select(Collapse).ToArray();
        // Keep an entry when it is long enough, or when stretching does not change it ("sex" stays "sex").
        // A short entry that shrinks ("weed" -> "wed") would clash with ordinary words, so it is left out, as are
        // a few short words that are also real names or abbreviations ("Gunn", "LSD").
        CollapsedWords = BlockedWords
            .Where(word => !CollapseExempt.Contains(word))
            .Select(word => (Original: word, Collapsed: Collapse(word)))
            .Where(pair => pair.Collapsed.Length >= 4 || pair.Collapsed == pair.Original)
            .Select(pair => pair.Collapsed).ToHashSet(StringComparer.Ordinal);
    }

    private static readonly HashSet<string> CollapseExempt = new(StringComparer.Ordinal) { "gun", "guns", "lsd", "fag", "cum" };

    public static bool IsAllowed(string? text) => !IsBlocked(text);

    /// <summary>The first field that fails the rules, as a message the app can show; null when all are fine.</summary>
    public static string? Rejection(params (string Label, string? Text)[] fields)
    {
        foreach (var (label, text) in fields)
            if (IsBlocked(text))
                return $"The {label} is not allowed. Please use wording that describes a salon service.";
        return null;
    }

    public static bool IsBlocked(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = Normalize(text);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // "s e x", "s.e.x" and "s-e-x" arrive as separate single letters; also test them joined.
        var run = new StringBuilder();
        foreach (var token in tokens.Append(" ").ToList())
        {
            if (token.Length == 1 && token != " ") { run.Append(token); continue; }
            if (run.Length > 1) tokens.Add(run.ToString());
            run.Clear();
        }
        foreach (var token in tokens)
        {
            if (BlockedWords.Contains(token)) return true;
            if (token.Length > 3 && token.EndsWith('s') && BlockedWords.Contains(token[..^1])) return true;
            if (CollapsedWords.Contains(Collapse(token))) return true;
        }
        var squashed = Collapse(normalized.Replace(" ", ""));
        return CollapsedFragments.Any(fragment => squashed.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>Lower-cases, flattens accents and full-width letters, removes invisible characters,
    /// undoes look-alike characters (m@ssage, p0rn, Cyrillic а) and keeps letters only.</summary>
    private static string Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            // Combining accents and zero-width / formatting characters carry no visible letter.
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            var lower = char.ToLowerInvariant(character);
            if (LookAlikes.TryGetValue(lower, out var plain)) text.Append(plain);
            else text.Append(char.IsLetter(lower) ? lower : ' ');
        }
        return text.ToString();
    }

    /// <summary>Squeezes repeated letters ("maassaage" becomes "masage").</summary>
    private static string Collapse(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value)
            if (text.Length == 0 || text[^1] != character) text.Append(character);
        return text.ToString();
    }
}
