using System.Text;

namespace TwitchChatOverlay.Core.Pipeline;

/// <summary>
/// Latin → Cyrillic transliteration for viewer names. Without it a Russian TTS voice spells
/// a Latin nickname out letter by letter instead of pronouncing it.
/// </summary>
public static class Transliterator
{
    /// <summary>Digraphs first — order matters, "sh" must win over "s" + "h".</summary>
    private static readonly (string Latin, string Cyrillic)[] Digraphs =
    {
        ("shch", "щ"),
        ("sch", "щ"),
        ("sh", "ш"),
        ("ch", "ч"),
        ("zh", "ж"),
        ("yu", "ю"),
        ("ya", "я"),
        ("yo", "ё"),
        ("ye", "е"),
        ("ts", "ц"),
        ("kh", "х"),
        ("ee", "и"),
        ("oo", "у")
    };

    private static readonly Dictionary<char, string> SingleLetters = new()
    {
        ['a'] = "а", ['b'] = "б", ['d'] = "д", ['e'] = "е",
        ['f'] = "ф", ['g'] = "г", ['h'] = "х", ['i'] = "и", ['j'] = "дж",
        ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о",
        ['p'] = "п", ['q'] = "к", ['r'] = "р", ['s'] = "с", ['t'] = "т",
        ['u'] = "у", ['v'] = "в", ['w'] = "в", ['x'] = "кс",
        ['z'] = "з"
    };

    private static bool IsLatinVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    public static string LatinToCyrillic(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var lower = input.ToLowerInvariant();
        var result = new StringBuilder(lower.Length);
        var index = 0;

        while (index < lower.Length)
        {
            var matchedDigraph = false;

            foreach (var (latin, cyrillic) in Digraphs)
            {
                if (index + latin.Length <= lower.Length &&
                    string.CompareOrdinal(lower, index, latin, 0, latin.Length) == 0)
                {
                    result.Append(cyrillic);
                    index += latin.Length;
                    matchedDigraph = true;
                    break;
                }
            }

            if (matchedDigraph)
            {
                continue;
            }

            var c = lower[index];
            var next = index + 1 < lower.Length ? lower[index + 1] : '\0';

            if (c == 'y')
            {
                // "y" is a consonant only before a vowel ("yes" → "йес"); elsewhere it is a
                // vowel, so names like "Tommy" become "томми" instead of "томмй".
                result.Append(IsLatinVowel(next) ? "й" : "и");
            }
            else if (c == 'c')
            {
                // Soft before e/i/y ("cinema" → "синема"), hard otherwise ("cat" → "кат").
                result.Append(next is 'e' or 'i' or 'y' ? "с" : "к");
            }
            else if (SingleLetters.TryGetValue(c, out var mapped))
            {
                result.Append(mapped);
            }
            else if (c is '_' or '-' or '.')
            {
                result.Append(' ');
            }
            else
            {
                // Digits and already-Cyrillic characters pass through untouched.
                result.Append(c);
            }

            index++;
        }

        return result.ToString();
    }
}
