using System.Globalization;
using System.Net;

namespace BlockGame.Core.Services;

public static class WebsiteDomainRules
{
    private static readonly char[] Separators = [';', '；', '\r', '\n'];

    public static IReadOnlyList<string> SplitAndNormalize(string patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
        {
            return [];
        }

        var domains = new List<string>();
        foreach (string entry in patterns.Split(
                     Separators,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalize(entry, out string domain, out _))
            {
                domains.Add(domain);
            }
        }

        return domains
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizePattern(string patterns)
        => string.Join(';', SplitAndNormalize(patterns));

    public static string? ValidatePattern(string patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
        {
            return "匹配内容不能为空。 ";
        }

        foreach (string entry in patterns.Split(
                     Separators,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNormalize(entry, out _, out string? error))
            {
                return $"网站规则“{entry}”无效：{error}";
            }
        }

        return SplitAndNormalize(patterns).Count == 0
            ? "匹配内容不能为空。 "
            : null;
    }

    public static bool IsMatch(string queryDomain, string blockedDomain)
    {
        string query = queryDomain.Trim().TrimEnd('.').ToLowerInvariant();
        string blocked = blockedDomain.Trim().TrimEnd('.').ToLowerInvariant();
        return query.Equals(blocked, StringComparison.OrdinalIgnoreCase)
            || query.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalize(
        string value,
        out string domain,
        out string? error)
    {
        domain = string.Empty;
        error = null;
        string candidate = value.Trim();
        if (candidate.Length == 0)
        {
            error = "域名不能为空。 ";
            return false;
        }

        if (candidate.StartsWith("*://", StringComparison.Ordinal))
        {
            candidate = candidate[4..];
        }

        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "网址格式不正确。 ";
                return false;
            }

            candidate = uri.Host;
        }
        else
        {
            candidate = candidate.TrimStart('/');
            int pathStart = candidate.IndexOfAny(['/', '\\', '?', '#']);
            if (pathStart >= 0)
            {
                candidate = candidate[..pathStart];
            }

            if (Uri.TryCreate("http://" + candidate, UriKind.Absolute, out Uri? uri)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                candidate = uri.Host;
            }
        }

        candidate = candidate.Trim().TrimEnd('.');
        if (candidate.StartsWith("*.", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }
        else if (candidate.StartsWith(".", StringComparison.Ordinal))
        {
            candidate = candidate[1..];
        }

        if (candidate.Contains('*') || candidate.Contains('?'))
        {
            error = "只支持“*.example.com”这种子域名通配，不支持域名中间的 * 或 ?。 ";
            return false;
        }

        if (IPAddress.TryParse(candidate, out _))
        {
            error = "DNS网站规则必须填写域名，不能填写IP地址。 ";
            return false;
        }

        try
        {
            candidate = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = "国际化域名格式不正确。 ";
            return false;
        }

        if (candidate.Length > 253 || !candidate.Contains('.'))
        {
            error = "请输入包含顶级域名的完整域名，例如 poki.com。 ";
            return false;
        }

        string[] labels = candidate.Split('.');
        if (labels.Any(label =>
                label.Length is < 1 or > 63
                || label.StartsWith('-')
                || label.EndsWith('-')
                || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            error = "域名标签只能包含字母、数字和连字符。 ";
            return false;
        }

        domain = candidate;
        return true;
    }
}
