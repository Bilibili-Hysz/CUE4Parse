namespace CUE4Parse_Conversion.UEScene;

public enum UESceneExportStrategy
{
    // Retained only so older persisted settings can be normalized safely.
    SelfContained = 0,
    ReuseGameExportTree = 1
}

public enum UESceneResourceLogicalRoot
{
    // Retained only for source compatibility; canonical publication always normalizes to GameExportTree.
    SceneBundle = 0,
    GameExportTree = 1
}

public enum UESceneResourcePublicationPolicy
{
    Overwrite,
    ReuseIdenticalOnly
}

/// <summary>Canonical UESCENE destination: a scene reference beside the project export tree.</summary>
public sealed record UESceneExportDestination(
    string ScenePhysicalRoot,
    string SceneFileName,
    string ResourcePhysicalRoot,
    UESceneResourceLogicalRoot ResourceLogicalRoot,
    UESceneResourcePublicationPolicy ResourcePublicationPolicy)
{
    public string ScenePhysicalPath => Path.Combine(ScenePhysicalRoot, SceneFileName);

    public static UESceneExportStrategy NormalizeStrategy(UESceneExportStrategy strategy) => strategy switch
    {
        UESceneExportStrategy.SelfContained => UESceneExportStrategy.ReuseGameExportTree,
        UESceneExportStrategy.ReuseGameExportTree => UESceneExportStrategy.ReuseGameExportTree,
        _ => throw new ArgumentOutOfRangeException(nameof(strategy))
    };

    public static UESceneResourceLogicalRoot NormalizeLogicalRoot(UESceneResourceLogicalRoot logicalRoot) => logicalRoot switch
    {
        UESceneResourceLogicalRoot.SceneBundle => UESceneResourceLogicalRoot.GameExportTree,
        UESceneResourceLogicalRoot.GameExportTree => UESceneResourceLogicalRoot.GameExportTree,
        _ => throw new ArgumentOutOfRangeException(nameof(logicalRoot))
    };

    /// <summary>Rejects legacy scene-private destinations before publication.
    /// A canonical UESCENE must be placed under the project's Content tree and use
    /// the map-qualified .umap.uescene filename.
    /// </summary>
    public static void ValidateCanonicalPlacement(string resourcePhysicalRoot, string scenePhysicalPath)
    {
        if (string.IsNullOrWhiteSpace(resourcePhysicalRoot) || !Path.IsPathFullyQualified(resourcePhysicalRoot))
            throw new ArgumentException("UEScene resource root must be an absolute path.", nameof(resourcePhysicalRoot));
        if (string.IsNullOrWhiteSpace(scenePhysicalPath) || !Path.IsPathFullyQualified(scenePhysicalPath))
            throw new ArgumentException("UEScene scene path must be an absolute path.", nameof(scenePhysicalPath));

        var root = Path.GetFullPath(resourcePhysicalRoot);
        var scene = Path.GetFullPath(scenePhysicalPath);
        var relative = Path.GetRelativePath(root, scene).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative.Equals("..", StringComparison.Ordinal) ||
            !relative.StartsWith("Content/", StringComparison.OrdinalIgnoreCase) ||
            !relative.EndsWith(".umap.uescene", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UEScene scene must be placed beside its UMAP under the project Content tree.");
    }

    public static UESceneExportDestination Allocate(string gameExportRoot, string canonicalUmapPath,
        UESceneExportStrategy strategy = UESceneExportStrategy.ReuseGameExportTree)
    {
        if (string.IsNullOrWhiteSpace(gameExportRoot) || !Path.IsPathFullyQualified(gameExportRoot))
            throw new ArgumentException("Game export root must be an absolute path.", nameof(gameExportRoot));

        strategy = NormalizeStrategy(strategy);
        var root = Path.GetFullPath(gameExportRoot);
        var segments = ValidateUmapPath(canonicalUmapPath);
        var fileName = Path.ChangeExtension(segments[^1], ".umap");
        var mapDirectory = segments[..^1];

        return new(
            Path.Combine([root, .. mapDirectory]),
            fileName + ".uescene",
            root,
            UESceneResourceLogicalRoot.GameExportTree,
            UESceneResourcePublicationPolicy.ReuseIdenticalOnly);
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
