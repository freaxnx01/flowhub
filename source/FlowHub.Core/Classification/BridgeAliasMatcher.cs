namespace FlowHub.Core.Classification;

/// <summary>
/// Detects a leading repo-alias token in a capture. A match requires the first
/// whitespace-delimited token (lowercased) to be in the aliases AND a
/// non-empty body to follow — a bare alias with no text is not routable.
/// </summary>
public static class BridgeAliasMatcher
{
    /// <summary>
    /// Detects a leading repo-alias token in a capture. A match requires the first
    /// whitespace-delimited token (lowercased) to be in <paramref name="aliases"/> AND a
    /// non-empty body to follow — a bare alias with no text is not routable.
    /// </summary>
    public static bool TryMatch(
        string content,
        IReadOnlySet<string> aliases,
        out string alias,
        out string remainder)
    {
        alias = string.Empty;
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(content) || aliases.Count == 0)
        {
            return false;
        }

        var trimmed = content.TrimStart();

        var tokenEnd = 0;
        while (tokenEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[tokenEnd]))
        {
            tokenEnd++;
        }

        // Need the token followed by at least one whitespace char, then a body.
        if (tokenEnd == 0 || tokenEnd >= trimmed.Length)
        {
            return false;
        }

        var candidate = trimmed[..tokenEnd].ToLowerInvariant();
        if (!aliases.Contains(candidate))
        {
            return false;
        }

        var body = trimmed[tokenEnd..].TrimStart();
        if (body.Length == 0)
        {
            return false;
        }

        alias = candidate;
        remainder = body.TrimEnd();
        return true;
    }
}
