namespace CUE4Parse_Conversion.UEFormat.MaterialLinks;

/// <summary>
/// Canonical POSIX logical-resource URI helpers shared by the linked UEFormat
/// material path. Owner-relative references are resolved from the directory
/// containing the owner file, following USD filesystem asset-path semantics.
/// </summary>
public static class RelativeResourceUri
{
    /// <summary>Returns a safe URI from <paramref name="ownerFilePath"/> to <paramref name="targetFilePath"/>.</summary>
    public static string FromOwner(string ownerFilePath, string targetFilePath)
    {
        var owner = NormalizeLogicalPath(ownerFilePath, nameof(ownerFilePath));
        var target = NormalizeLogicalPath(targetFilePath, nameof(targetFilePath));
        var ownerSegments = owner.Split('/');
        var targetSegments = target.Split('/');
        var ownerDirectoryLength = ownerSegments.Length - 1;

        var commonLength = 0;
        while (commonLength < ownerDirectoryLength && commonLength < targetSegments.Length &&
               string.Equals(ownerSegments[commonLength], targetSegments[commonLength], StringComparison.Ordinal))
        {
            commonLength++;
        }

        var relativeSegments = new List<string>();
        for (var index = commonLength; index < ownerDirectoryLength; index++)
            relativeSegments.Add("..");
        for (var index = commonLength; index < targetSegments.Length; index++)
            relativeSegments.Add(targetSegments[index]);

        if (relativeSegments.Count == 0)
            relativeSegments.Add(targetSegments[^1]);

        return NormalizeResourceUri(string.Join('/', relativeSegments), allowLeadingParents: true);
    }

    /// <summary>Validates a non-owner-relative logical path.</summary>
    public static string NormalizeLogicalPath(string path, string parameterName = "path") =>
        NormalizeResourceUri(path, allowLeadingParents: false, parameterName: parameterName);

    /// <summary>
    /// Validates a logical resource URI. Only owner-relative URIs may contain
    /// leading <c>..</c> segments, and the URI must still identify a resource.
    /// </summary>
    public static string NormalizeResourceUri(string path, bool allowLeadingParents = false, string parameterName = "path")
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains('\\') ||
            path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':') || path.Any(char.IsControl))
        {
            throw new ArgumentException("Resource paths must be safe POSIX logical paths.", parameterName);
        }

        var segments = path.Split('/', StringSplitOptions.None);
        var seenResourceSegment = false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
                throw new ArgumentException("Resource paths must not contain empty or current-directory segments.", parameterName);
            if (segment == "..")
            {
                if (!allowLeadingParents || seenResourceSegment)
                    throw new ArgumentException("Resource path traversal is only allowed in leading owner-relative segments.", parameterName);
                continue;
            }

            seenResourceSegment = true;
        }

        if (!seenResourceSegment)
            throw new ArgumentException("Resource paths must identify a resource after any leading parent segments.", parameterName);

        return string.Join('/', segments);
    }
}
