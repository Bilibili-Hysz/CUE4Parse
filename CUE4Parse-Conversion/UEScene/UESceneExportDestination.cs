namespace CUE4Parse_Conversion.UEScene;

public enum UESceneExportStrategy
{
    SelfContained,
    ReuseGameExportTree
}

public enum UESceneResourceLogicalRoot
{
    SceneBundle,
    GameExportTree
}

public enum UESceneResourcePublicationPolicy
{
    Overwrite,
    ReuseIdenticalOnly
}

/// <summary>Pure allocation of a scene file and the root used by its resource logical URIs.</summary>
public sealed record UESceneExportDestination(
    string ScenePhysicalRoot,
    string SceneFileName,
    string ResourcePhysicalRoot,
    UESceneResourceLogicalRoot ResourceLogicalRoot,
    UESceneResourcePublicationPolicy ResourcePublicationPolicy)
{
    public string ScenePhysicalPath => Path.Combine(ScenePhysicalRoot, SceneFileName);

    public static UESceneExportDestination Allocate(string gameExportRoot, string canonicalUmapPath,
        UESceneExportStrategy strategy = UESceneExportStrategy.SelfContained)
    {
        if (string.IsNullOrWhiteSpace(gameExportRoot) || !Path.IsPathFullyQualified(gameExportRoot))
            throw new ArgumentException("Game export root must be an absolute path.", nameof(gameExportRoot));
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));

        var root = Path.GetFullPath(gameExportRoot);
        var segments = ValidateUmapPath(canonicalUmapPath);
        var fileName = Path.ChangeExtension(segments[^1], ".umap");
        var mapName = Path.GetFileNameWithoutExtension(fileName);
        var mapDirectory = segments[..^1];

        return strategy switch
        {
            UESceneExportStrategy.SelfContained => AllocateSelfContained(root, mapDirectory, mapName),
            UESceneExportStrategy.ReuseGameExportTree => new(
                Path.Combine([root, .. mapDirectory]),
                fileName + ".uescene",
                root,
                UESceneResourceLogicalRoot.GameExportTree,
                UESceneResourcePublicationPolicy.ReuseIdenticalOnly),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy))
        };
    }

    private static UESceneExportDestination AllocateSelfContained(string gameExportRoot, string[] mapDirectory, string mapName)
    {
        var sceneRoot = Path.Combine([gameExportRoot, "Uescenes", .. mapDirectory, mapName]);
        return new(sceneRoot, mapName + ".uescene", sceneRoot, UESceneResourceLogicalRoot.SceneBundle,
            UESceneResourcePublicationPolicy.Overwrite);
    }

    private static string[] ValidateUmapPath(string canonicalUmapPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalUmapPath) || Path.IsPathRooted(canonicalUmapPath) || canonicalUmapPath.Contains(':'))
            throw new ArgumentException("UMAP path must be a safe slash-separated relative path.", nameof(canonicalUmapPath));

        var segments = canonicalUmapPath.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (segments.Length < 2 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..") ||
            !string.Equals(segments[0], "Content", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(segments[^1]), ".umap", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(segments[^1])) ||
            segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("UMAP path must be a safe Content-relative .umap path.", nameof(canonicalUmapPath));

        return segments;
    }
}
