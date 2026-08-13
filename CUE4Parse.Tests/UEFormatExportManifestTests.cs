using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.UEScene;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using FModel.Framework;
using Newtonsoft.Json.Linq;

namespace CUE4Parse.Tests;

public class UEFormatExportManifestTests
{
    [Fact]
    public void ManifestArtifactCanBePreparedBeforeFinalScenePromotion()
    {
        using var temporary = new TemporaryDirectory();
        var projectRoot = Path.Combine(temporary.Path, "Game");
        var destination = new UESceneExportDestination(Path.Combine(projectRoot, "Content", "Maps"), "Level.umap.uescene", projectRoot,
            UESceneResourceLogicalRoot.GameExportTree, UESceneResourcePublicationPolicy.ReuseIdenticalOnly);
        var result = Result(destination.ScenePhysicalPath, Document(),
        [
            new UESceneFinalResource((byte) BundleResourceKind.Model, 1, "Content/Meshes/Hero.uemodel",
                "application/x-ue-model", [2])
        ]);

        var artifact = UEFormatExportManifestWriter.CreateUESceneArtifact("operation-staged", "Game", "/Game/Maps/Level.umap",
            destination, result, MaterialLinkMode.Bundle, MaterialExportLayout.PreserveHierarchy, [0x55, 0x45, 0x53]);
        var json = JObject.Parse(Encoding.UTF8.GetString(artifact.Bytes));

        Assert.Equal("Content/Maps/Level.umap.uescene.EXPORT_MANIFEST.json", artifact.RelativePath);
        Assert.Equal("operation-staged", json.Value<string>("operation_id"));
        Assert.Equal(3, json["scene"]!.Value<long>("size"));
        Assert.False(File.Exists(destination.ScenePhysicalPath));
    }

    [Fact]
    public void LegacySceneBundleManifestNormalizesToGameTree()
    {
        using var temporary = new TemporaryDirectory();
        var destination = new UESceneExportDestination(Path.Combine(temporary.Path, "Content", "Maps"), "Level.umap.uescene", temporary.Path,
            UESceneResourceLogicalRoot.SceneBundle, UESceneResourcePublicationPolicy.Overwrite);
        Directory.CreateDirectory(Path.GetDirectoryName(destination.ScenePhysicalPath)!);
        File.WriteAllBytes(destination.ScenePhysicalPath, [0x55, 0x45, 0x53]);
        var document = Document() with
        {
            Diagnostics = [new DiagnosticRecord(2, 1, 11, 0, 1, "/Game/Mesh", "LOD0 fallback", 0, 0, 0)]
        };
        var result = Result(destination.ScenePhysicalPath, document,
        [
            new UESceneFinalResource((byte) BundleResourceKind.Model, 7, "Content/Models/Hero.uemodel",
                "application/x-ue-model", [1, 2, 3]),
            new UESceneFinalResource((byte) BundleResourceKind.Texture, 7, "Content/Textures/Hero_D.png",
                "image/png", [4, 5])
        ]);
        var createdAt = new DateTimeOffset(2026, 8, 8, 8, 0, 0, TimeSpan.Zero);

        var written = UEFormatExportManifestWriter.WriteUEScene("operation-123", "Game", "/Game/Maps/Level.umap",
            destination, result, MaterialLinkMode.Bundle, MaterialExportLayout.Categorized, createdAt);
        var firstBytes = File.ReadAllBytes(written.ManifestPath);
        var second = UEFormatExportManifestWriter.WriteUEScene("operation-123", "Game", "/Game/Maps/Level.umap",
            destination, result, MaterialLinkMode.Bundle, MaterialExportLayout.Categorized, createdAt);
        var json = JObject.Parse(File.ReadAllText(written.ManifestPath));

        Assert.Equal("Level.umap.uescene.EXPORT_MANIFEST.json", Path.GetFileName(written.ManifestPath));
        Assert.Equal(UEFormatExportManifestWriter.Schema, json.Value<string>("schema"));
        Assert.Equal(1, json.Value<int>("schema_version"));
        Assert.Equal("operation-123", json.Value<string>("operation_id"));
        Assert.Equal("reuse_game_export_tree", json["route"]!.Value<string>("scene_layout"));
        Assert.Equal("GameExportTree", json["route"]!.Value<string>("resource_logical_root"));
        Assert.Equal("Bundle", json["route"]!.Value<string>("material_strategy"));
        Assert.Equal("../..", json["output"]!.Value<string>("resource_root_relative"));
        Assert.Equal(2, json["resources"]!.Count());
        Assert.Equal("Content/Models/Hero.uemodel", json["resources"]![0]!.Value<string>("logical_uri"));
        Assert.Equal(3, json["resources"]![0]!.Value<long>("size"));
        Assert.Equal(1, json["diagnostics"]!.Count());
        Assert.Equal(written.ManifestSha256, second.ManifestSha256);
        Assert.Equal(firstBytes, File.ReadAllBytes(second.ManifestPath));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".*.tmp"));
    }

    [Fact]
    public void GameExportTreeUsesSceneSpecificManifestAndRelativeRoot()
    {
        using var temporary = new TemporaryDirectory();
        var projectRoot = Path.Combine(temporary.Path, "Game");
        var sceneRoot = Path.Combine(projectRoot, "Content", "Maps");
        Directory.CreateDirectory(sceneRoot);
        var destination = new UESceneExportDestination(sceneRoot, "Level.umap.uescene", projectRoot,
            UESceneResourceLogicalRoot.GameExportTree, UESceneResourcePublicationPolicy.ReuseIdenticalOnly);
        Directory.CreateDirectory(Path.GetDirectoryName(destination.ScenePhysicalPath)!);
        File.WriteAllBytes(destination.ScenePhysicalPath, [1]);
        var result = Result(destination.ScenePhysicalPath, Document(),
        [
            new UESceneFinalResource((byte) BundleResourceKind.Model, 1, "Content/Meshes/Hero.uemodel",
                "application/x-ue-model", [2])
        ]);

        var written = UEFormatExportManifestWriter.WriteUEScene("operation-456", "Game", "/Game/Maps/Level.umap",
            destination, result, MaterialLinkMode.Bundle, MaterialExportLayout.PreserveHierarchy,
            DateTimeOffset.UnixEpoch);
        var json = JObject.Parse(File.ReadAllText(written.ManifestPath));

        Assert.Equal(destination.ScenePhysicalPath + ".EXPORT_MANIFEST.json", written.ManifestPath);
        Assert.Equal("reuse_game_export_tree", json["route"]!.Value<string>("scene_layout"));
        Assert.Equal("GameExportTree", json["route"]!.Value<string>("resource_logical_root"));
        Assert.Equal(Path.GetFullPath(projectRoot), json["output"]!.Value<string>("resource_root"));
        Assert.Equal("../..", json["output"]!.Value<string>("resource_root_relative"));
    }

    [Fact]
    public void UnsafeResourceUriIsRejectedWithoutPublishingManifest()
    {
        using var temporary = new TemporaryDirectory();
        var destination = new UESceneExportDestination(Path.Combine(temporary.Path, "Content", "Maps"), "Level.umap.uescene", temporary.Path,
            UESceneResourceLogicalRoot.SceneBundle, UESceneResourcePublicationPolicy.Overwrite);
        Directory.CreateDirectory(Path.GetDirectoryName(destination.ScenePhysicalPath)!);
        File.WriteAllBytes(destination.ScenePhysicalPath, [1]);
        var result = Result(destination.ScenePhysicalPath, Document(),
        [
            new UESceneFinalResource((byte) BundleResourceKind.Model, 1, "../outside.uemodel",
                "application/x-ue-model", [2])
        ]);

        Assert.Throws<InvalidDataException>(() => UEFormatExportManifestWriter.WriteUEScene("operation-unsafe", "Game",
            "/Game/Maps/Level.umap", destination, result, MaterialLinkMode.Bundle,
            MaterialExportLayout.Categorized, DateTimeOffset.UnixEpoch));
        Assert.False(File.Exists(UEFormatExportManifestWriter.GetManifestPath(destination)));
    }

    [Fact]
    public void RouteSummaryExposesAllEffectivePolicies()
    {
        using var temporary = new TemporaryDirectory();
        var destination = new UESceneExportDestination(Path.Combine(temporary.Path, "Content", "Maps"), "Level.umap.uescene", temporary.Path,
            UESceneResourceLogicalRoot.SceneBundle, UESceneResourcePublicationPolicy.Overwrite);

        var summary = UEFormatExportManifestWriter.FormatRouteSummary("operation-summary", destination,
            MaterialLinkMode.Bundle, MaterialExportLayout.Categorized);

        Assert.Contains("Operation operation-summary", summary);
        Assert.Contains("Route: UEScene + UEFormat", summary);
        Assert.Contains("Materials: Bundle", summary);
        Assert.Contains("Layout: reuse_game_export_tree", summary);
        Assert.Contains("URI Root: GameExportTree", summary);
    }

    private static UESceneDocument Document() => new(new SceneMetadata("FModel", "1", "test",
        "/Game/Maps/Level.Level", "/Game/Maps/Level.PersistentLevel", "Game", "UE5", 0));

    private static SceneExportResult Result(string scenePath, UESceneDocument document,
        IReadOnlyList<UESceneFinalResource> resources) => new(true, scenePath, null,
        FinalDocument: document, FinalPlan: new UESceneFinalBundlePlan(document, resources),
        ActorCount: 2, ComponentCount: 3, MeshCount: 1, InstanceCount: 4,
        DiagnosticCount: document.Diagnostics?.Count ?? 0, ResourceCount: resources.Count);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "fmodel-export-manifest-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
