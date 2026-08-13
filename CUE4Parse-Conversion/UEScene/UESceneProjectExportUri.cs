namespace CUE4Parse_Conversion.UEScene;

/// <summary>
/// Canonical logical paths below a project's exported game root.
///
/// FModel's fixed paths are normally written as <c>ProjectName/Content/...</c>,
/// but some games use a project root other than the conventional <c>Game</c>.
/// This helper therefore treats the <c>Content</c> segment as the stable anchor
/// and does not hard-code a particular project name.
/// </summary>
public static class UESceneProjectExportUri
{
    public static string FromFixedPath(string fixedPath, string? projectName, string extension)
    {
        var normalized = NormalizePath(fixedPath, nameof(fixedPath));
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/', StringSplitOptions.None).Any(segment => segment.Length == 0))
            throw new ArgumentException("Fixed path must be a relative slash-separated path.", nameof(fixedPath));
        var contentRelative = NormalizeContentRelativePath(normalized, projectName, nameof(fixedPath));
        return ChangeExtension(contentRelative, extension);
    }

    public static string FromUnrealPath(string unrealPath, string extension)
    {
        var normalized = NormalizePath(unrealPath, nameof(unrealPath), rejectBackslashes: true);
        ValidateUnrealPathSegments(normalized, nameof(unrealPath));
        var path = normalized.StartsWith("/Game/", StringComparison.Ordinal)
            ? "Content/" + normalized["/Game/".Length..]
            : normalized.TrimStart('/');
        var contentRelative = NormalizeContentRelativePath(path, null, nameof(unrealPath));
        return ChangeExtension(contentRelative, extension);
    }

    /// <summary>Returns whether a canonical Unreal path is rooted at an exported Content tree.
    /// The path is validated before the answer is returned; malformed input never falls through
    /// to the external-resource hash namespace.
    /// </summary>
    public static bool IsContentRootedUnrealPath(string unrealPath)
    {
        var normalized = NormalizePath(unrealPath, nameof(unrealPath), rejectBackslashes: true);
        ValidateUnrealPathSegments(normalized, nameof(unrealPath));
        var segments = normalized.Split('/', StringSplitOptions.None);
        return normalized.StartsWith("/Game/", StringComparison.Ordinal) ||
               (segments.Length >= 3 && segments[0].Length == 0 &&
                string.Equals(segments[2], "Content", StringComparison.Ordinal));
    }

    private static string NormalizeContentRelativePath(string normalizedPath, string? projectName, string parameterName)
    {
        var segments = normalizedPath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(IsUnsafeSegment))
            throw new ArgumentException("Path contains an unsafe or empty segment.", parameterName);

        var normalizedProjectName = NormalizeProjectName(projectName, parameterName);
        if (normalizedProjectName is not null &&
            segments.Length >= 2 &&
            string.Equals(segments[0], normalizedProjectName, StringComparison.Ordinal) &&
            string.Equals(segments[1], "Content", StringComparison.Ordinal))
        {
            return string.Join('/', segments[1..]);
        }

        if (string.Equals(segments[0], "Content", StringComparison.Ordinal))
            return string.Join('/', segments);

        // Do not depend on the provider's project name being "Game" (or even
        // being available). Any safe <ProjectName>/Content/... shape is a valid
        // fixed export path, including CodeWorld/Content/....
        if (segments.Length >= 2 && string.Equals(segments[1], "Content", StringComparison.Ordinal))
            return string.Join('/', segments[1..]);

        throw new ArgumentException("Path must be rooted at a Content directory.", parameterName);
    }

    private static string ChangeExtension(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.' ||
            extension.IndexOfAny(['/','\\']) >= 0)
            throw new ArgumentException("Extension must start with a dot and contain no path separators.", nameof(extension));

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "Content", StringComparison.Ordinal))
            throw new ArgumentException("Path must be Content-relative.", nameof(path));

        var leaf = segments[^1];
        if (leaf.Count(character => character == '.') > 1)
            throw new ArgumentException("Path must contain at most one package/object separator.", nameof(path));
        var separator = leaf.LastIndexOf('.');
        var name = separator > 0 ? leaf[..separator] : leaf;
        if (string.IsNullOrWhiteSpace(name) || IsUnsafeSegment(name))
            throw new ArgumentException("Path must have an asset name.", nameof(path));

        segments[^1] = name + extension;
        return string.Join('/', segments);
    }

    private static string NormalizePath(string path, string parameterName, bool rejectBackslashes = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new ArgumentException("Path is required and cannot contain NUL.", parameterName);
        if (rejectBackslashes && path.Contains('\\'))
            throw new ArgumentException("Path must use POSIX separators.", parameterName);

        var normalized = path.Replace('\\', '/');
        if (normalized.Contains(':'))
            throw new ArgumentException("Path must be a relative slash-separated path.", parameterName);
        return normalized;
    }

    private static void ValidateUnrealPathSegments(string normalized, string parameterName)
    {
        if (!normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Unreal path must be an absolute single-rooted path.", parameterName);
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length < 3 || segments[0].Length != 0 || segments[1..].Any(IsUnsafeSegment))
            throw new ArgumentException("Unreal path contains an unsafe or empty segment.", parameterName);
    }

    private static string? NormalizeProjectName(string? projectName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return null;

        var normalized = projectName.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Contains('/') || IsUnsafeSegment(normalized))
            throw new ArgumentException("Project name must be a single safe path segment.", parameterName);
        return normalized;
    }

    private static bool IsUnsafeSegment(string segment) => segment is "" or "." or "..";
}
