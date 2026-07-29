using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BlockGame.Core.Services;

public static class WildcardMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        Regex regex = Cache.GetOrAdd(pattern, static value =>
        {
            string expression = "^" + Regex.Escape(value.Trim())
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new Regex(
                expression,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(100));
        });

        try
        {
            return regex.IsMatch(input.Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

