namespace BlockGame.Core.Services;

public static class HostsFileRenderer
{
    public const string BeginMarker = "# BEGIN BlockGame managed website rules";
    public const string EndMarker = "# END BlockGame managed website rules";

    public static string Render(string existingContent, IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(existingContent);
        ArgumentNullException.ThrowIfNull(domains);

        string[] normalizedDomains = domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool containsManagedSection = existingContent.Contains(
            BeginMarker,
            StringComparison.Ordinal);
        if (!containsManagedSection && normalizedDomains.Length == 0)
        {
            return existingContent;
        }

        string normalizedExisting = existingContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalizedExisting.Split('\n');
        var retainedLines = new List<string>(lines.Length);
        bool insideManagedSection = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.Equals(trimmed, BeginMarker, StringComparison.Ordinal))
            {
                insideManagedSection = true;
                continue;
            }

            if (insideManagedSection)
            {
                if (string.Equals(trimmed, EndMarker, StringComparison.Ordinal))
                {
                    insideManagedSection = false;
                }

                continue;
            }

            retainedLines.Add(line);
        }

        while (retainedLines.Count > 0
               && string.IsNullOrWhiteSpace(retainedLines[^1]))
        {
            retainedLines.RemoveAt(retainedLines.Count - 1);
        }

        string baseContent = string.Join("\r\n", retainedLines);
        if (normalizedDomains.Length == 0)
        {
            return baseContent.Length == 0 ? string.Empty : baseContent + "\r\n";
        }

        var managedLines = new List<string>
        {
            BeginMarker,
            "# This section is maintained automatically. Edit website rules in BlockGame."
        };
        foreach (string domain in normalizedDomains)
        {
            managedLines.Add($"0.0.0.0 {domain}");
            managedLines.Add($":: {domain}");
        }

        managedLines.Add(EndMarker);
        string managedContent = string.Join("\r\n", managedLines) + "\r\n";
        return baseContent.Length == 0
            ? managedContent
            : baseContent + "\r\n\r\n" + managedContent;
    }
}
