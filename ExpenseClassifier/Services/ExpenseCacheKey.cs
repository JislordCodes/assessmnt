using System.Text;
using System.Text.RegularExpressions;

namespace ExpenseClassifier.Services;

/// <summary>
/// Builds canonical cache keys so cosmetic differences in an expense description
/// (case, surrounding/duplicate whitespace, punctuation, thousands separators) hit the same cache entry,
/// while anything that changes meaning (digits, decimal points, currency symbols) does not.
/// </summary>
public static partial class ExpenseCacheKey
{
    private const string Prefix = "expense:v1:";

    public static string Create(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        var text = description.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

        text = ThousandsSeparator().Replace(text, string.Empty);      // 15,000 -> 15000 (but 1,5 is kept as two tokens)
        text = CurrencyGap().Replace(text, "$1");                     // "₦ 15000" -> "₦15000"
        text = NonTokenCharacters().Replace(text, " ");               // punctuation -> space (keeps letters, digits, currency symbols, '.')
        text = StrayDots().Replace(text, " ");                        // sentence dots -> space, decimal points (3.50) survive
        text = Whitespace().Replace(text, " ").Trim();

        // Punctuation-only input would otherwise collapse every such string into one shared key.
        return text.Length == 0 ? Prefix + "raw:" + description.Trim() : Prefix + text;
    }

    [GeneratedRegex(@"(?<=\d),(?=\d{3}(?!\d))")]
    private static partial Regex ThousandsSeparator();

    [GeneratedRegex(@"(\p{Sc})\s+(?=\d)")]
    private static partial Regex CurrencyGap();

    [GeneratedRegex(@"[^\p{L}\p{N}\p{Sc}.]+")]
    private static partial Regex NonTokenCharacters();

    [GeneratedRegex(@"(?<!\d)\.|\.(?!\d)")]
    private static partial Regex StrayDots();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
