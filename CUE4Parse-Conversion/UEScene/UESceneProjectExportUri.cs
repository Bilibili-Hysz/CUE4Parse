namespace CUE4Parse_Conversion.UEScene;

/// <summary>Canonical logical paths below a project's exported game root.</summary>
public static class UESceneProjectExportUri
{
    public static string FromFixedPath(string fixedPath, string projectName, string extension)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name is required.", nameof(projectName));
        var prefix = projectName + "/";
        var normalized = fixedPath.Replace('\\', '/');
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("Fixed path is not rooted at the active project.", nameof(fixedPath));
        return ChangeExtension(normalized[prefix.Length..], extension);
    }

    public static string FromUnrealPath(string unrealPath, string extension)
    {
        var normalized = unrealPath.Replace('\\', '/');
        var path = normalized.StartsWith("/Game/", StringComparison.Ordinal)
            ? "Content/" + normalized["/Game/".Length..]
            : normalized.TrimStart('/');
        return ChangeExtension(path, extension);
    }

    private static string ChangeExtension(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.')
            throw new ArgumentException("Extension must start with a dot.", nameof(extension));
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "Content", StringComparison.Ordinal))
            throw new ArgumentException("Path must be Content-relative.", nameof(path));
        var name = segments[^1].Split('.')[0];
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Path must have an asset name.", nameof(path));
        return string.Join('/', segments[..^1]) + "/" + name + extension;
    }

}
