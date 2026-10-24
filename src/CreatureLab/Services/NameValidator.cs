using System.Globalization;
using System.Text;

namespace CreatureLab.Services;

public readonly record struct NameCheck(bool IsValid, string? Value, string? Error)
{
    public static NameCheck Ok(string? value) => new(true, value, null);
    public static NameCheck Fail(string error) => new(false, null, error);
}

/// <summary>
/// Cleans and checks the creature and artist names: trims, collapses whitespace,
/// strips invisible characters, enforces the length limit, and applies the word list.
/// </summary>
/// <remarks>
/// Word list format (one entry per line, # for comments): a plain word blocks that
/// whole word; a word starting with * also blocks it inside other words. Matching
/// ignores case, accents, common letter swaps (0→o, 1→i, 3→e, 4→a, 5→s, 7→t, @→a,
/// $→s) and stretched letters ("fuuun" matches "fun").
/// </remarks>
public sealed class NameValidator
{
    private readonly HashSet<string> _words = new(StringComparer.Ordinal);
    private readonly List<string> _fragments = [];

    public NameValidator(IEnumerable<string> wordList, int maxLength)
    {
        MaxLength = maxLength;
        foreach (var raw in wordList)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var isFragment = line.StartsWith('*');
            var word = Squash(Normalize(isFragment ? line[1..] : line));
            if (word.Length == 0) continue;

            _words.Add(word);
            if (isFragment) _fragments.Add(word);
        }
    }

    public int MaxLength { get; }
    public int WordCount => _words.Count;

    public static NameValidator FromFile(string path, int maxLength, ILogger logger)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning("Word list not found at {Path}; name filtering is off.", path);
            return new NameValidator([], maxLength);
        }

        var validator = new NameValidator(File.ReadLines(path), maxLength);
        logger.LogInformation("Loaded {Count} blocked words from {Path}.", validator.WordCount, path);
        return validator;
    }

    /// <param name="raw">What the visitor typed.</param>
    /// <param name="required">Whether an empty value is an error (creature name) or null (artist name).</param>
    /// <param name="what">"creature name" or "artist name", for error messages.</param>
    public NameCheck Check(string? raw, bool required, string what)
    {
        var cleaned = Clean(raw);
        if (cleaned.Length == 0) return required ? NameCheck.Fail("Give your creature a name!") : NameCheck.Ok(null);
        if (cleaned.Length > MaxLength) return NameCheck.Fail($"Keep the {what} to {MaxLength} characters or fewer.");
        if (IsBlocked(cleaned)) return NameCheck.Fail($"Let's pick a different {what}. That one isn't tank-friendly.");
        return NameCheck.Ok(cleaned);
    }

    public bool IsBlocked(string text)
    {
        var normalized = Normalize(text);

        foreach (var token in Tokens(normalized))
        {
            if (_words.Contains(token) || _words.Contains(CollapseRuns(token, 1)) || _words.Contains(CollapseRuns(token, 2)))
            {
                return true;
            }
        }

        if (_fragments.Count == 0) return false;

        var squashed = Squash(normalized);
        string[] variants = [squashed, CollapseRuns(squashed, 1), CollapseRuns(squashed, 2)];
        foreach (var fragment in _fragments)
        {
            foreach (var variant in variants)
            {
                if (variant.Contains(fragment, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    /// <summary>Trim, collapse runs of whitespace, and drop control and invisible formatting characters.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            var category = char.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || category == UnicodeCategory.Control)
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            // Drops bidi overrides and zero-width characters, but keeps the joiner emoji need.
            if (category == UnicodeCategory.Format && ch != '‍') continue;

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch
            {
                '0' => 'o',
                '1' or '!' => 'i',
                '3' => 'e',
                '4' or '@' => 'a',
                '5' or '$' => 's',
                '7' or '+' => 't',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Tokens(string normalized)
    {
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var isLetter = i < normalized.Length && char.IsLetter(normalized[i]);
            if (isLetter && start < 0) start = i;
            else if (!isLetter && start >= 0)
            {
                yield return normalized[start..i];
                start = -1;
            }
        }
    }

    private static string Squash(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Shortens runs of the same letter to at most <paramref name="max"/> ("fuuun" → "fun" or "fuun").</summary>
    private static string CollapseRuns(string text, int max)
    {
        var sb = new StringBuilder(text.Length);
        var run = 0;
        for (var i = 0; i < text.Length; i++)
        {
            run = i > 0 && text[i] == text[i - 1] ? run + 1 : 1;
            if (run <= max) sb.Append(text[i]);
        }
        return sb.ToString();
    }
}
