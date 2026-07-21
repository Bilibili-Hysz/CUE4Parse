using System.Security.Cryptography;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using CUE4Parse_Conversion.UEScene;
using FModel.ViewModels;

namespace CUE4Parse.Tests;

public class UESceneBundleExporterTests
{
    [Fact]
    public void TryWriteToDirPublishesDeduplicatedModelAndFinalBndxHash()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair")),
            Actor("/Game/Map.Map:PersistentLevel.B", Component("B.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1, 2, 3], [new(0), new(1)]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.True(result.Success);
        var scene = UESceneReader.Read(File.ReadAllBytes(result.ScenePath));
        var model = Assert.Single(scene.BundleEntries!, e => e.ResourceKind == (byte) BundleResourceKind.Model);
        Assert.Equal(uint.MaxValue, model.LodIndex);
        var asset = Assert.Single(scene.Assets!, asset => asset.Kind == AssetKind.StaticMesh);
        Assert.Equal(0u, asset.LodStart);
        Assert.Equal(2u, asset.LodCount);
        Assert.Collection(scene.Lods!, lod => Assert.Equal(0u, lod.LodIndex), lod => Assert.Equal(1u, lod.LodIndex));
        var bytes = File.ReadAllBytes(Path.Combine(temp.Directory.FullName, model.LogicalUri));
        Assert.Equal(SHA256.HashData(bytes), model.Sha256);
        Assert.Equal((ulong) bytes.Length, model.ByteLength);
    }

    [Fact]
    public void TryWriteToDirReturnsPublishedCanonicalIdentityGraph()
    {
        using var temp = new TestDirectory();
        var child = ComponentWithOverrides("Z.Child", "InstancedStaticMesh", "/Game/Meshes/Zebra", ["/Game/Materials/M_Zebra"])
            with { ParentComponentPath = "Z.Parent", InstanceTransforms = [Transform(1), Transform(2)] };
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.Z", child, Component("Z.Parent", "StaticMesh", "/Game/Meshes/Parent")),
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Apple"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1, 2, 3], [new(0)],
            resource.SourceMeshPath == "/Game/Meshes/Zebra"
                ? [new("material:/Game/Materials/M_Zebra", BundleResourceKind.MaterialJson, "Materials/M_Zebra.json", "application/json", [4])]
                : []));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var published = UESceneReader.Read(File.ReadAllBytes(result.ScenePath!));
        var returned = Assert.IsType<UESceneDocument>(result.FinalDocument);
        Assert.Same(returned, result.FinalPlan!.Document);
        Assert.Equal(published.Assets!.Select(a => (a.AssetId, a.CanonicalUnrealPath, a.LodStart, a.LodCount)),
            returned.Assets!.Select(a => (a.AssetId, a.CanonicalUnrealPath, a.LodStart, a.LodCount)));
        Assert.Equal(published.Actors, returned.Actors);
        Assert.Equal(published.Components, returned.Components);
        Assert.Equal(published.Transforms, returned.Transforms);
        Assert.Equal(published.Lights, returned.Lights);
        Assert.Equal(published.InstanceSets, returned.InstanceSets);
        Assert.Equal(published.MaterialOverrides, returned.MaterialOverrides);
        Assert.Equal(published.Diagnostics, returned.Diagnostics);
        Assert.Equal(published.BundleEntries!.Select(e => (e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex)),
            returned.BundleEntries!.Select(e => (e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex)));
        Assert.Equal(published.BundleEntries!.Select(e => (e.ResourceKind, e.LogicalUri, e.OwnerAssetId)),
            result.FinalPlan.Resources.Select(r => (r.ResourceKind, r.LogicalUri, r.OwnerAssetId)));
    }

    [Fact]
    public void TryWriteToDestinationPublishesSelfContainedSceneAndResourcesAtAllocatedRoots()
    {
        using var game = new TestDirectory();
        var destination = UESceneExportDestination.Allocate(game.Directory.FullName, "Content/Maps/City/City.umap");
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDestination(new(destination.ScenePhysicalPath, destination.ResourcePhysicalRoot,
            destination.ResourceLogicalRoot), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(destination.ScenePhysicalPath, result.ScenePath);
        Assert.True(File.Exists(destination.ScenePhysicalPath));
        var model = Assert.Single(result.FinalPlan!.Resources);
        Assert.True(File.Exists(Path.Combine(destination.ResourcePhysicalRoot, model.LogicalUri)));
    }

    [Fact]
    public void TryWriteToDestinationPublishesSelfContainedSceneAndSidecarsAtAllocatedRoot()
    {
        using var game = new TestDirectory();
        var destination = UESceneExportDestination.Allocate(game.Directory.FullName, "Content/Maps/City/City.umap");
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A",
            ComponentWithOverrides("A.Mesh", "StaticMesh", "/Game/Meshes/Chair", ["/Game/Materials/M_Chair"]))));
        var policy = new UESceneResourceUriPolicy(destination.ResourceLogicalRoot);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)],
            [new("material:/Game/Materials/M_Chair", BundleResourceKind.MaterialJson,
                 policy.Allocate("/Game/Materials/M_Chair", UESceneResourceKind.Material, ".json"), "application/json", [2]),
             new("/Game/Textures/T_Chair.T_Chair", BundleResourceKind.Texture,
                 policy.Allocate("/Game/Textures/T_Chair", UESceneResourceKind.Texture, ".png"), "image/png", [3])]));

        var result = exporter.TryWriteToDestination(new(destination.ScenePhysicalPath, destination.ResourcePhysicalRoot,
            destination.ResourceLogicalRoot, destination.ResourcePublicationPolicy), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(game.Directory.FullName, "Uescenes", "Content", "Maps", "City", "City", "City.uescene"), result.ScenePath);
        Assert.False(File.Exists(Path.Combine(game.Directory.FullName, "Uescenes", "City.uescene")));
        var scene = UESceneReader.Read(File.ReadAllBytes(result.ScenePath!));
        Assert.Equal(3, result.FinalPlan!.Resources.Count);
        Assert.All(result.FinalPlan.Resources, resource => Assert.True(File.Exists(Path.Combine(destination.ResourcePhysicalRoot, resource.LogicalUri))));
        Assert.All(scene.BundleEntries!, entry => Assert.True(File.Exists(Path.Combine(destination.ResourcePhysicalRoot, entry.LogicalUri))));
    }

    [Fact]
    public void TryWriteToDestinationPublishesReuseSceneBesideUmapAndResourcesAtGameRoot()
    {
        using var game = new TestDirectory();
        var destination = UESceneExportDestination.Allocate(game.Directory.FullName, "Content/Maps/City/City.umap", UESceneExportStrategy.ReuseGameExportTree);
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))),
            UESceneResourceLogicalRoot.GameExportTree, token: CancellationToken.None);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDestination(new(destination.ScenePhysicalPath, destination.ResourcePhysicalRoot,
            destination.ResourceLogicalRoot), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(destination.ScenePhysicalPath, result.ScenePath);
        Assert.True(File.Exists(Path.Combine(game.Directory.FullName, "Content", "Maps", "City", "City.umap.uescene")));
        Assert.True(File.Exists(Path.Combine(destination.ResourcePhysicalRoot, Assert.Single(result.FinalPlan!.Resources).LogicalUri)));
        Assert.Equal("Content/Meshes/Chair.uemodel", Assert.Single(result.FinalPlan.Resources).LogicalUri);
    }

    [Fact]
    public void TryWriteToDestinationReusesIdenticalGameTreeSceneResourcesAndSidecars()
    {
        using var game = new TestDirectory();
        var destination = UESceneExportDestination.Allocate(game.Directory.FullName, "Content/Maps/City/City.umap", UESceneExportStrategy.ReuseGameExportTree);
        var policy = new UESceneResourceUriPolicy(destination.ResourceLogicalRoot);
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A",
            ComponentWithOverrides("A.Mesh", "StaticMesh", "/Game/Meshes/Chair", ["/Game/Materials/M_Chair"]))), destination.ResourceLogicalRoot,
            new CUE4Parse_Conversion.UEScene.ExporterOptions(policy));
        UESceneProducedResource Producer(UEScenePlannedResource resource, CancellationToken _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)],
            [new("material:/Game/Materials/M_Chair", BundleResourceKind.MaterialJson, "Content/Materials/M_Chair.json", "application/json", [2]),
             new("/Game/Textures/T_Chair.T_Chair", BundleResourceKind.Texture, "Content/Textures/T_Chair.png", "image/png", [3])]);
        var publication = new UEScenePublicationDestination(destination.ScenePhysicalPath, destination.ResourcePhysicalRoot,
            destination.ResourceLogicalRoot, destination.ResourcePublicationPolicy);

        var first = new UESceneBundleExporter(plan, Producer).TryWriteToDestination(publication, CancellationToken.None);
        var second = new UESceneBundleExporter(plan, Producer).TryWriteToDestination(publication, CancellationToken.None);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal(destination.ScenePhysicalPath, second.ScenePath);
        var scene = UESceneReader.Read(File.ReadAllBytes(second.ScenePath!));
        Assert.All(scene.BundleEntries!, entry => Assert.True(File.Exists(Path.Combine(destination.ResourcePhysicalRoot, entry.LogicalUri))));
    }

    [Fact]
    public void BuildPlanRejectsSuppliedUriPolicyWithMismatchedLogicalRoot()
    {
        var world = World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair")));

        var error = Assert.Throws<InvalidDataException>(() => UESceneBundleExporter.BuildPlan(world,
            UESceneResourceLogicalRoot.SceneBundle, new CUE4Parse_Conversion.UEScene.ExporterOptions(new UESceneResourceUriPolicy(UESceneResourceLogicalRoot.GameExportTree))));

        Assert.Contains("logical root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryWriteToDestinationRejectsPlanMetadataRootMismatchBeforeCreatingRoot()
    {
        using var temp = new TestDirectory();
        var root = new DirectoryInfo(Path.Combine(temp.Directory.FullName, "not-created"));
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDestination(new(Path.Combine(root.FullName, "Map.uescene"), root.FullName,
            UESceneResourceLogicalRoot.GameExportTree, UESceneResourcePublicationPolicy.Overwrite), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("logical root", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(root.Exists);
    }

    [Fact]
    public void GameTreeExternalFallbackUsesFullSha256IdentityWithoutUriCollisions()
    {
        var policy = new UESceneResourceUriPolicy(UESceneResourceLogicalRoot.GameExportTree);
        const string first = "/External/Shared/Resource";
        const string second = "/Other/Shared/Resource";

        var firstUri = policy.Allocate(first, UESceneResourceKind.Texture, ".png");
        var secondUri = policy.Allocate(second, UESceneResourceKind.Texture, ".png");

        Assert.NotEqual(firstUri, secondUri);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(first))).ToLowerInvariant(), firstUri);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(second))).ToLowerInvariant(), secondUri);
    }

    [Fact]
    public void ResourceLogicalRootIsPersistedAsOptionalGameExportTreeMetadata()
    {
        var world = World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair")));

        var selfContained = UESceneBundleExporter.BuildPlan(world);
        var reuse = UESceneBundleExporter.BuildPlan(world, UESceneResourceLogicalRoot.GameExportTree, token: CancellationToken.None);
        var roundTripped = UESceneReader.Read(UESceneWriter.Write(reuse.Document));

        Assert.Equal(0ul, selfContained.Document.Metadata.OptionalFeatures & SceneMetadata.GameExportTreeRootBit);
        Assert.NotEqual(0ul, reuse.Document.Metadata.OptionalFeatures & SceneMetadata.GameExportTreeRootBit);
        Assert.NotEqual(0ul, roundTripped.Metadata.OptionalFeatures & SceneMetadata.GameExportTreeRootBit);
    }

    [Fact]
    public void ReuseDestinationRejectsIncompatibleExistingResourceWithoutPublishingScene()
    {
        using var game = new TestDirectory();
        var destination = UESceneExportDestination.Allocate(game.Directory.FullName, "Content/Maps/City/City.umap", UESceneExportStrategy.ReuseGameExportTree);
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))),
            UESceneResourceLogicalRoot.GameExportTree, token: CancellationToken.None);
        var resourcePath = Path.Combine(game.Directory.FullName, "Content", "Meshes", "Chair.uemodel");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        File.WriteAllBytes(resourcePath, [9]);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDestination(new(destination.ScenePhysicalPath, destination.ResourcePhysicalRoot,
            destination.ResourceLogicalRoot, destination.ResourcePublicationPolicy), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal([9], File.ReadAllBytes(resourcePath));
        Assert.False(File.Exists(destination.ScenePhysicalPath));
    }

    [Fact]
    public void ReuseDestinationLeavesIdenticalExistingResourceUntouched()
    {
        using var game = new TestDirectory();
        var destination = UESceneExportDestination.Allocate(game.Directory.FullName, "Content/Maps/City/City.umap", UESceneExportStrategy.ReuseGameExportTree);
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))),
            UESceneResourceLogicalRoot.GameExportTree, token: CancellationToken.None);
        var resourcePath = Path.Combine(game.Directory.FullName, "Content", "Meshes", "Chair.uemodel");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        File.WriteAllBytes(resourcePath, [1]);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDestination(new(destination.ScenePhysicalPath, destination.ResourcePhysicalRoot,
            destination.ResourceLogicalRoot, destination.ResourcePublicationPolicy), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal([1], File.ReadAllBytes(resourcePath));
        Assert.True(File.Exists(destination.ScenePhysicalPath));
    }

    [Fact]
    public void ReuseManifestUrisMatchCanonicalGameExportPaths()
    {
        var modelUri = UESceneProjectExportUri.FromFixedPath("ProjectName/Content/Meshes/Chair.uasset", "ProjectName", ".uemodel");
        var materialUri = UESceneProjectExportUri.FromFixedPath("ProjectName/Content/Materials/M_Chair.uasset", "ProjectName", ".json");
        var textureUri = UESceneProjectExportUri.FromFixedPath("ProjectName/Content/Textures/T_Chair.uasset", "ProjectName", ".png");
        var manifest = new ResourceManifest();
        manifest.AddOrGet("material:/Game/Materials/M_Chair", materialUri, "application/json", [2], true);
        manifest.AddOrGet("/Game/Textures/T_Chair.T_Chair", textureUri, "image/png", [3], true);
        manifest.Seal();

        var produced = CUE4ParseViewModel.CreateUESceneProducedResource(modelUri, [1], [(0u, 1f)], manifest);

        Assert.Equal("Content/Meshes/Chair.uemodel", produced.LogicalUri);
        Assert.Collection(produced.Sidecars,
            sidecar => Assert.Equal("Content/Materials/M_Chair.json", sidecar.LogicalUri),
            sidecar => Assert.Equal("Content/Textures/T_Chair.png", sidecar.LogicalUri));
    }

    [Theory]
    [InlineData(UESceneResourceLogicalRoot.SceneBundle)]
    [InlineData(UESceneResourceLogicalRoot.GameExportTree)]
    public void SharedUriPolicyMakesPlannerAndManifestUrisIdentical(UESceneResourceLogicalRoot logicalRoot)
    {
        var policy = new UESceneResourceUriPolicy(logicalRoot);
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A",
            ComponentWithOverrides("A.Mesh", "StaticMesh", "/Game/Meshes/Chair", ["/Game/Materials/M_Chair"]))), logicalRoot,
            new CUE4Parse_Conversion.UEScene.ExporterOptions(policy));
        var manifest = new ResourceManifest();
        var expectedModel = Assert.Single(plan.Document.Assets!, asset => asset.Kind == AssetKind.StaticMesh).LogicalUri;
        var expectedMaterial = Assert.Single(plan.Document.Assets!, asset => asset.Kind == AssetKind.Material).LogicalUri;

        manifest.AddOrGet("material:/Game/Materials/M_Chair", policy.Allocate("/Game/Materials/M_Chair", UESceneResourceKind.Material, ".json"), "application/json", [1], true);
        manifest.Seal();

        Assert.Equal(expectedModel, policy.AllocateVariant("/Game/Meshes/Chair",
            Assert.Single(plan.Resources).CanonicalUnrealPath, UESceneResourceKind.Model, ".uemodel"));
        Assert.Equal(expectedMaterial, Assert.Single(manifest.Resources).RelativeUri);
    }

    [Fact]
    public void ReusePolicyIgnoresNonContentFixedPathsWhenCanonicalIdentityIsGameAsset()
    {
        var policy = new UESceneResourceUriPolicy(UESceneResourceLogicalRoot.GameExportTree);

        Assert.Equal("Content/Meshes/Chair.uemodel", policy.Allocate("/Game/Meshes/Chair.Chair", UESceneResourceKind.Model, ".uemodel"));
    }

    [Fact]
    public void TryWriteToDirReportsCountsFromFinalDocumentAndPlan()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(true, false,
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair")),
            Actor("/Game/Map.Map:PersistentLevel.B", Component("B.Grass", "InstancedStaticMesh", "/Game/Meshes/Grass", instances: [Transform(1), Transform(2)]))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1, 2, 3], [new(0)]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.ActorCount);
        Assert.Equal(2, result.ComponentCount);
        Assert.Equal(2, result.MeshCount);
        Assert.Equal(2, result.InstanceCount);
        Assert.Equal(1, result.DiagnosticCount);
        Assert.Equal(2, result.ResourceCount);
    }

    [Fact]
    public void ProducedResourcePreservesMeshExporterLodMetadata()
    {
        var produced = UESceneProducedResource.FromMeshExporterLods("Models/Chair.uemodel", [1, 2, 3], [(0u, 1f), (1u, .5f)]);

        Assert.Equal("Models/Chair.uemodel", produced.LogicalUri);
        Assert.Equal([1, 2, 3], produced.Bytes);
        Assert.Collection(produced.Lods,
            lod => { Assert.Equal(0u, lod.SourceLodIndex); Assert.Equal(1f, lod.SourceScreenSize); },
            lod => { Assert.Equal(1u, lod.SourceLodIndex); Assert.Equal(.5f, lod.SourceScreenSize); });
    }

    [Fact]
    public void TryWriteToDirResolvesManifestSidecarsIntoTypedFinalDocument()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var modelPath = Path.Combine(temp.Directory.FullName, Assert.Single(plan.Document.Assets!, asset => asset.Kind == AssetKind.StaticMesh).LogicalUri);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1, 2, 3], [new(0)],
            [new("material:/Game/Materials/M_Chair", BundleResourceKind.MaterialJson, "Materials/M_Chair.json", "application/json", [4]),
             new("/Game/Textures/T_Chair.T_Chair", BundleResourceKind.Texture, "Textures/T_Chair.png", "image/png", [5])]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.NotNull(result.FinalDocument);
        var document = result.FinalDocument!;
        Assert.Equal(3, result.FinalPlan!.Resources.Count);
        var material = Assert.Single(document.Assets!, asset => asset.Kind == AssetKind.Material);
        var texture = Assert.Single(document.Assets!, asset => asset.Kind == AssetKind.Texture);
        Assert.Equal("Materials/M_Chair.json", material.LogicalUri);
        Assert.Equal("Textures/T_Chair.png", texture.LogicalUri);
        Assert.Collection(document.BundleEntries!.Where(entry => entry.ResourceKind != (byte) BundleResourceKind.Model),
            entry => Assert.Equal((byte) BundleResourceKind.MaterialJson, entry.ResourceKind),
            entry => Assert.Equal((byte) BundleResourceKind.Texture, entry.ResourceKind));

        foreach (var entry in document.BundleEntries!.Where(entry => entry.ResourceKind != (byte) BundleResourceKind.Model))
        {
            var bytes = File.ReadAllBytes(Path.Combine(temp.Directory.FullName, entry.LogicalUri));
            Assert.Equal(SHA256.HashData(bytes), entry.Sha256);
            Assert.Equal((ulong) bytes.Length, entry.ByteLength);
            var asset = Assert.Single(document.Assets!, asset => asset.AssetId == entry.OwnerAssetId);
            Assert.Equal(entry.Sha256, asset.Sha256);
            Assert.Equal(entry.ByteLength, asset.ByteLength);
        }
    }

    [Fact]
    public void TryWriteToDirMarksOverrideOnlyMaterialUnavailableWithoutBndx()
    {
        using var temp = new TestDirectory();
        var component = Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair") with
        {
            OverrideMaterialPaths = ["/Game/Materials/M_Override"]
        };
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", component)));
        var exporter = new UESceneBundleExporter(plan, (resource, _) =>
            UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var scene = UESceneReader.Read(File.ReadAllBytes(result.ScenePath));
        var material = Assert.Single(scene.Assets!, asset => asset.CanonicalUnrealPath == "/Game/Materials/M_Override");
        Assert.Equal(Availability.Missing, material.Availability);
        Assert.DoesNotContain(scene.BundleEntries!, entry => entry.OwnerAssetId == material.AssetId);
        Assert.Contains(scene.Diagnostics!, diagnostic => diagnostic.Code == UESceneDiagnosticCodes.MissingMaterialSidecar && diagnostic.SubjectId == material.AssetId);
    }

    [Fact]
    public void TryWriteToDirReconcilesManifestMaterialSidecarWithComponentOverride()
    {
        using var temp = new TestDirectory();
        var component = Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair") with
        {
            OverrideMaterialPaths = ["/Game/Materials/M_Override"]
        };
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", component)));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)],
            [new("material:/Game/Materials/M_Override", BundleResourceKind.MaterialJson, "Materials/M_Override.json", "application/json", [4])]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var scene = UESceneReader.Read(File.ReadAllBytes(result.ScenePath));
        var material = Assert.Single(scene.Assets!, asset => asset.Kind == AssetKind.Material && asset.CanonicalUnrealPath == "/Game/Materials/M_Override");
        var materialOverride = Assert.Single(scene.MaterialOverrides!);
        var bndx = Assert.Single(scene.BundleEntries!, entry => entry.ResourceKind == (byte) BundleResourceKind.MaterialJson);
        Assert.Single(scene.Assets!, asset => asset.Kind == AssetKind.Material);
        Assert.Equal(Availability.Available, material.Availability);
        Assert.Equal(material.AssetId, materialOverride.OverrideMaterialAssetId);
        Assert.Equal(material.AssetId, bndx.OwnerAssetId);
        Assert.Equal(material.LogicalUri, bndx.LogicalUri);
        Assert.Equal(material.Sha256, bndx.Sha256);
        Assert.Equal(material.ByteLength, bndx.ByteLength);
        Assert.DoesNotContain(scene.Diagnostics!, diagnostic => diagnostic.Code == UESceneDiagnosticCodes.MissingMaterialSidecar);
    }

    [Fact]
    public void TryWriteToDirKeepsDistinctManifestMaterialIdentitiesSeparate()
    {
        using var temp = new TestDirectory();
        var first = Component("A.First", "StaticMesh", "/Game/Meshes/First") with { OverrideMaterialPaths = ["/Game/Materials/M_First"] };
        var second = Component("A.Second", "StaticMesh", "/Game/Meshes/Second") with { OverrideMaterialPaths = ["/Game/Materials/M_Second"] };
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", first, second)));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)],
            resource.SourceMeshPath!.EndsWith("First", StringComparison.Ordinal)
                ? [new("material:/Game/Materials/M_First", BundleResourceKind.MaterialJson, "Materials/M_First.json", "application/json", [4])]
                : [new("material:/Game/Materials/M_Second", BundleResourceKind.MaterialJson, "Materials/M_Second.json", "application/json", [5])]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var scene = UESceneReader.Read(File.ReadAllBytes(result.ScenePath));
        var materials = scene.Assets!.Where(asset => asset.Kind == AssetKind.Material).ToArray();
        var bndx = scene.BundleEntries!.Where(entry => entry.ResourceKind == (byte) BundleResourceKind.MaterialJson).ToArray();
        Assert.Equal(2, materials.Length);
        Assert.Equal(2, bndx.Length);
        Assert.All(materials, material => Assert.Equal(Availability.Available, material.Availability));
        Assert.Equal(2, materials.Select(material => material.AssetId).Distinct().Count());
        Assert.Equal(2, bndx.Select(entry => entry.OwnerAssetId).Distinct().Count());
        Assert.All(scene.MaterialOverrides!, materialOverride => Assert.Contains(materials, material => material.AssetId == materialOverride.OverrideMaterialAssetId));
    }

    [Fact]
    public void TryWriteToDirDeduplicatesSidecarsByCanonicalManifestIdentity()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.First", "StaticMesh", "/Game/Meshes/First")),
            Actor("/Game/Map.Map:PersistentLevel.B", Component("B.Second", "StaticMesh", "/Game/Meshes/Second"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)], [new("/Game/Textures/T_Shared.T_Shared", BundleResourceKind.Texture, "Textures/T_Shared.png", "image/png", [7])]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.NotNull(result.FinalDocument);
        var document = result.FinalDocument!;
        Assert.Single(document.Assets!, asset => asset.Kind == AssetKind.Texture);
        Assert.Single(document.BundleEntries!, entry => entry.ResourceKind == (byte) BundleResourceKind.Texture);
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(temp.Directory.FullName, "Textures", "T_Shared.png")));
    }

    [Fact]
    public void TryWriteToDirSidecarPromotionFailureRestoresExistingResourcesAndScene()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var modelPath = Path.Combine(temp.Directory.FullName, Assert.Single(plan.Document.Assets!, asset => asset.Kind == AssetKind.StaticMesh).LogicalUri);
        var materialPath = Path.Combine(temp.Directory.FullName, "Materials", "M_Chair.json");
        Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
        File.WriteAllBytes(materialPath, [9]);
        var scene = WriteExistingScene(temp.Directory);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)],
            [new("material:/Game/Materials/M_Chair", BundleResourceKind.MaterialJson, "Materials/M_Chair.json", "application/json", [4]),
             new("/Game/Textures/T_Chair.T_Chair", BundleResourceKind.Texture, "Textures/T_Chair.png", "image/png", [5])]))
        {
            FinalResourcePromotionFailure = index => index == 2
        };

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal([9], File.ReadAllBytes(materialPath));
        Assert.False(File.Exists(Path.Combine(temp.Directory.FullName, "Textures", "T_Chair.png")));
        Assert.Equal([9, 8, 7], File.ReadAllBytes(scene));
    }

    [Fact]
    public void TryWriteToDirTexturePromotionFailureRestoresEveryExistingSidecarAndLeavesNoScene()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var model = Assert.Single(plan.Document.Assets!, asset => asset.Kind == AssetKind.StaticMesh);
        var modelPath = Path.Combine(temp.Directory.FullName, model.LogicalUri);
        var materialPath = Path.Combine(temp.Directory.FullName, "Materials", "M_Chair.json");
        var texturePath = Path.Combine(temp.Directory.FullName, "Textures", "T_Chair.png");
        foreach (var path in new[] { modelPath, materialPath, texturePath })
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(modelPath, [9]);
        File.WriteAllBytes(materialPath, [8]);
        File.WriteAllBytes(texturePath, [7]);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)],
            [new("material:/Game/Materials/M_Chair", BundleResourceKind.MaterialJson, "Materials/M_Chair.json", "application/json", [4]),
             new("/Game/Textures/T_Chair.T_Chair", BundleResourceKind.Texture, "Textures/T_Chair.png", "image/png", [5])]))
        {
            FinalResourcePromotionFailure = index => index == 2
        };

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal([9], File.ReadAllBytes(modelPath));
        Assert.Equal([8], File.ReadAllBytes(materialPath));
        Assert.Equal([7], File.ReadAllBytes(texturePath));
        Assert.False(File.Exists(Path.Combine(temp.Directory.FullName, "Scenes", "Map.uescene")));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirCancellationDuringSidecarPromotionRollsBackResources()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var modelPath = Path.Combine(temp.Directory.FullName, Assert.Single(plan.Document.Assets!, asset => asset.Kind == AssetKind.StaticMesh).LogicalUri);
        var materialPath = Path.Combine(temp.Directory.FullName, "Materials", "M_Chair.json");
        Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
        File.WriteAllBytes(materialPath, [9]);
        using var cancellation = new CancellationTokenSource();
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)],
            [new("material:/Game/Materials/M_Chair", BundleResourceKind.MaterialJson, "Materials/M_Chair.json", "application/json", [4]),
             new("/Game/Textures/T_Chair.T_Chair", BundleResourceKind.Texture, "Textures/T_Chair.png", "image/png", [5])]))
        {
            BeforeFinalResourcePromotion = index => { if (index == 2) cancellation.Cancel(); }
        };

        var result = exporter.TryWriteToDir(temp.Directory, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal([9], File.ReadAllBytes(materialPath));
        Assert.False(File.Exists(modelPath));
        Assert.False(File.Exists(Path.Combine(temp.Directory.FullName, "Textures", "T_Chair.png")));
        Assert.False(File.Exists(Path.Combine(temp.Directory.FullName, "Scenes", "Map.uescene")));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void FModelProducedResourceAdapterPreservesSealedManifestBytesUriAndMime()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("material:/Game/Materials/M_Chair", "Materials/M_Chair.json", "application/json", [4], true);
        manifest.AddOrGet("/Game/Textures/T_Chair.T_Chair", "Textures/T_Chair.png", "image/png", [5], true);
        manifest.Seal();

        var produced = CUE4ParseViewModel.CreateUESceneProducedResource("Models/Chair.uemodel", [1, 2, 3], [(0u, 1f)], manifest);

        Assert.Collection(produced.Sidecars,
            sidecar => { Assert.Equal(BundleResourceKind.MaterialJson, sidecar.Kind); Assert.Equal("Materials/M_Chair.json", sidecar.LogicalUri); Assert.Equal("application/json", sidecar.Mime); Assert.Equal([4], sidecar.Bytes); },
             sidecar => { Assert.Equal(BundleResourceKind.Texture, sidecar.Kind); Assert.Equal("Textures/T_Chair.png", sidecar.LogicalUri); Assert.Equal("image/png", sidecar.Mime); Assert.Equal([5], sidecar.Bytes); });
    }

    [Fact]
    public void TryWriteToDirRejectsConflictingSidecarsBeforeStaging()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.First", "StaticMesh", "/Game/Meshes/First")),
            Actor("/Game/Map.Map:PersistentLevel.B", Component("B.Second", "StaticMesh", "/Game/Meshes/Second"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)], [new("/Game/Textures/T_Shared.T_Shared", BundleResourceKind.Texture,
                "Textures/T_Shared.png", "image/png", resource.CanonicalUnrealPath.EndsWith("First") ? [7] : [8])]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("conflicting sidecar", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRejectsConflictingReconciledMaterialSidecarsBeforeStaging()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.First", "StaticMesh", "/Game/Meshes/First")),
            Actor("/Game/Map.Map:PersistentLevel.B", Component("B.Second", "StaticMesh", "/Game/Meshes/Second"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)],
            [new(resource.CanonicalUnrealPath.EndsWith("First") ? "material:/Game/Materials/M" : "/Game/Materials/M",
                BundleResourceKind.MaterialJson,
                resource.CanonicalUnrealPath.EndsWith("First") ? "Materials/M.json" : "Materials/M_Conflict.json",
                "application/json", resource.CanonicalUnrealPath.EndsWith("First") ? [7] : [8]) ]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("conflicting sidecar", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRejectsCaseEquivalentFinalTargetsBeforeStaging()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.First", "StaticMesh", "/Game/Meshes/First")),
            Actor("/Game/Map.Map:PersistentLevel.B", Component("B.Second", "StaticMesh", "/Game/Meshes/Second"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)], [new(resource.CanonicalUnrealPath.EndsWith("First") ? "/Game/Textures/T_First" : "/Game/Textures/T_Second", BundleResourceKind.Texture,
                resource.CanonicalUnrealPath.EndsWith("First") ? "Textures/T.png" : "textures/t.png", "image/png",
                [7])]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("conflicting final resources target", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("Scenes/Map.uescene")]
    [InlineData("scenes/map.uescene")]
    public void TryWriteToDirRejectsResourceTargetAliasingFinalSceneBeforeStaging(string resourceUri)
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(
            resource.LogicalUri, [1], [new(0)], [new("/Game/Textures/T_Scene", BundleResourceKind.Texture,
                resourceUri, "image/png", [7])]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("scene target", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRejectsProducedLogicalUriMismatchBeforeStaging()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (_, _) => UESceneProducedResource.FromMeshExport("Models/Other.uemodel", [1], [new(0)]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("logical URI", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRejectsMismatchedLodMetadataBeforeStaging()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(1)]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("LOD metadata", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRejectsBackslashTraversalInSourceWorldBeforeStaging()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        plan = plan with { Document = plan.Document with { Metadata = plan.Document.Metadata with { SourceWorldPath = "..\\outside" } } };
        var outside = Path.Combine(temp.Directory.Parent!.FullName, "outside.uescene");
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("source world", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outside));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRejectsInjectedReparsePointBeforeStaging()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var outside = Path.Combine(temp.Directory.Parent!.FullName, "outside.uemodel");
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]))
        {
            PathAttributes = path => path.EndsWith("Models", StringComparison.OrdinalIgnoreCase) ? FileAttributes.Directory | FileAttributes.ReparsePoint : null
        };

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reparse", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outside));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRejectsDirectorySymbolicLinkBeforeStaging()
    {
        using var temp = new TestDirectory();
        using var outside = new TestDirectory();
        var link = Path.Combine(temp.Directory.FullName, "Models");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Directory.FullName);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating a directory symbolic link requires unavailable Windows privilege.");
        }
        catch (IOException ex) when (ex.HResult == unchecked((int) 0x80070522) || ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Creating a directory symbolic link requires unavailable Windows privilege.");
        }

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]));

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reparse", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(outside.Directory.FullName, "Game_Meshes_Chair.uemodel")));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirProducerFailurePreservesPreexistingSceneAndLeavesNoStage()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (_, _) => throw new InvalidOperationException("expected"));
        var scene = WriteExistingScene(temp.Directory);

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(scene));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("invalid", SceneExportErrorCode.InvalidData)]
    [InlineData("denied", SceneExportErrorCode.AccessDenied)]
    [InlineData("disk-full", SceneExportErrorCode.DiskFull)]
    public void TryWriteToDirClassifiesProducerFailures(string failure, SceneExportErrorCode expectedCode)
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        Exception expected = failure switch
        {
            "invalid" => new InvalidDataException("invalid details"),
            "denied" => new UnauthorizedAccessException("denied details"),
            _ => new IOException("disk full details", unchecked((int) 0x80070070))
        };
        var exporter = new UESceneBundleExporter(plan, (_, _) => throw expected);

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Failure?.Code);
        Assert.Same(expected, result.Failure?.Exception);
    }

    [Fact]
    public void FormatUESceneExportErrorsUsesCodesInsteadOfExceptionMessages()
    {
        var result = new SceneExportResult(false, null, "secret primary details",
            Failure: new SceneExportError(SceneExportErrorCode.AccessDenied, "secret primary details", new UnauthorizedAccessException("secret primary details")),
            StructuredRecoveryErrors:
            [
                new SceneExportError(SceneExportErrorCode.RollbackFailed, "secret rollback details", new IOException("secret rollback details"))
            ]);

        var message = CUE4ParseViewModel.FormatUESceneExportErrors(result);

        Assert.Contains("Access to the export destination was denied", message);
        Assert.Contains("previous files could not be fully restored", message);
        Assert.DoesNotContain("secret", message);
    }

    [Fact]
    public void TryWriteToDirPromotionFailureRestoresExistingResource()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.One", "StaticMesh", "/Game/Meshes/One"), Component("A.Two", "StaticMesh", "/Game/Meshes/Two"))));
        var existing = Path.Combine(temp.Directory.FullName, Assert.Single(plan.Document.Assets!, asset =>
            asset.CanonicalUnrealPath == "/Game/Meshes/One").LogicalUri);
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllBytes(existing, [9]);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)])) { FinalResourcePromotionFailure = index => index == 1 };
        var scene = WriteExistingScene(temp.Directory);

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(existing));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(scene));
    }

    [Fact]
    public void TryWriteToDirCancellationBeforeScenePublishPreservesPreexistingSceneAndLeavesNoStage()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        using var cancellation = new CancellationTokenSource();
        var exporter = new UESceneBundleExporter(plan, (resource, _) => { cancellation.Cancel(); return UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]); });
        var scene = WriteExistingScene(temp.Directory);

        var result = exporter.TryWriteToDir(temp.Directory, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(SceneExportErrorCode.Cancelled, result.Failure?.Code);
        Assert.IsType<OperationCanceledException>(result.Failure?.Exception);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(scene));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirScenePromotionFailureRestoresPreexistingSceneAndAllResources()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.One", "StaticMesh", "/Game/Meshes/One"), Component("A.Two", "StaticMesh", "/Game/Meshes/Two"))));
        var oldResource = Path.Combine(temp.Directory.FullName, "Models", "Game_Meshes_One.uemodel");
        var newResource = Path.Combine(temp.Directory.FullName, "Models", "Game_Meshes_Two.uemodel");
        Directory.CreateDirectory(Path.GetDirectoryName(oldResource)!);
        File.WriteAllBytes(oldResource, [1, 2, 3]);
        var scene = WriteExistingScene(temp.Directory);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [4, 5, 6], [new(0)])) { FinalScenePromotionFailure = true };

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SceneExportErrorCode.PromotionFailed, result.Failure?.Code);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(scene));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(oldResource));
        Assert.False(File.Exists(newResource));
        Assert.Empty(Directory.GetDirectories(temp.Directory.FullName, ".uescene-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TryWriteToDirRollbackFailureReportsUnrestoredResource()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(
            Actor("/Game/Map.Map:PersistentLevel.A", Component("A.One", "StaticMesh", "/Game/Meshes/One"), Component("A.Two", "StaticMesh", "/Game/Meshes/Two"))));
        var existing = Path.Combine(temp.Directory.FullName, Assert.Single(plan.Document.Assets!, asset =>
            asset.CanonicalUnrealPath == "/Game/Meshes/One").LogicalUri);
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllBytes(existing, [9]);
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]))
        {
            FinalResourcePromotionFailure = index => index == 1,
            RollbackFailure = (_, _) => new IOException("restore blocked")
        };

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SceneExportErrorCode.PromotionFailed, result.Failure?.Code);
        var rollbackFailure = Assert.Single(result.RecoveryErrors
            .Where(error => error.Code == SceneExportErrorCode.RollbackFailed)
            .Take(1));
        Assert.IsType<IOException>(rollbackFailure.Exception);
        Assert.Contains("rollback failed", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restore blocked", result.Error!);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(existing));
        Assert.NotNull(result.RecoveryStagePath);
        Assert.True(Directory.Exists(result.RecoveryStagePath));
    }

    [Fact]
    public void TryWriteToDirStageCleanupFailureReportsRetainedRecoveryPath()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) =>
            UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1], [new(0)]))
        {
            FinalScenePromotionFailure = true,
            StageCleanupFailure = _ => new IOException("cleanup blocked")
        };

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.False(result.Success);
        var cleanupFailure = Assert.Single(result.RecoveryErrors, error => error.Code == SceneExportErrorCode.CleanupFailed);
        Assert.IsType<IOException>(cleanupFailure.Exception);
        Assert.Contains("cleanup failed", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup blocked", result.Error!);
        Assert.NotNull(result.RecoveryStagePath);
        Assert.True(Directory.Exists(result.RecoveryStagePath));
    }

    [Fact]
    public void TryWriteToDirSuccessfulPublishCleanupFailureKeepsPublishedBundleSuccessful()
    {
        using var temp = new TestDirectory();
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));
        var exporter = new UESceneBundleExporter(plan, (resource, _) => UESceneProducedResource.FromMeshExport(resource.LogicalUri, [1, 2, 3], [new(0)]))
        {
            StageCleanupFailure = _ => new IOException("cleanup blocked")
        };

        var result = exporter.TryWriteToDir(temp.Directory, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Failure);
        Assert.Contains(result.RecoveryErrors, error => error.Code == SceneExportErrorCode.CleanupFailed);
        Assert.NotNull(result.ScenePath);
        Assert.True(File.Exists(result.ScenePath));
        Assert.NotNull(result.RecoveryStagePath);
        Assert.True(Directory.Exists(result.RecoveryStagePath));
        Assert.Contains("cleanup failed", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup blocked", result.Error!);
    }

    [Fact]
    public void ExtractsSupportedPersistentLevelActors()
    {
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("/Game/Map.Map:PersistentLevel.A.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));

        Assert.Single(plan.Document.Actors!);
        Assert.Single(plan.Document.Components!);
        Assert.Equal("/Game/Map.Map:PersistentLevel.A", plan.Document.Actors![0].SourceObjectPath);
    }

    [Fact]
    public void DeduplicatesStaticMeshesByCanonicalPath()
    {
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair")), Actor("/Game/Map.Map:PersistentLevel.B", Component("B.Mesh", "StaticMesh", "/Game/Meshes/Chair"))));

        Assert.Single(plan.Document.Assets!);
        Assert.Single(plan.Document.BundleEntries!);
    }

    [Fact]
    public void CreatesDistinctMeshVariantsForDifferentMaterialOverrides()
    {
        var red = Component("A.Red", "StaticMesh", "/Game/Meshes/Chair") with { OverrideMaterialPaths = ["/Game/Materials/Red"] };
        var blue = Component("A.Blue", "StaticMesh", "/Game/Meshes/Chair") with { OverrideMaterialPaths = ["/Game/Materials/Blue"] };
        var repeatedRed = Component("A.RedAgain", "StaticMesh", "/Game/Meshes/Chair") with { OverrideMaterialPaths = ["/Game/Materials/Red"] };
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", red, blue, repeatedRed)));

        var components = plan.Document.Components!;
        Assert.Equal(2, plan.Document.Assets!.Count(asset => asset.Kind == AssetKind.StaticMesh));
        Assert.Equal(2, plan.Resources.Count);
        Assert.NotEqual(components[0].MeshAssetId, components[1].MeshAssetId);
        Assert.Equal(components[0].MeshAssetId, components[2].MeshAssetId);
        Assert.All(plan.Resources, resource => Assert.Contains("-variant-", resource.LogicalUri));
    }

    [Fact]
    public void PlanModelUri_EqualsBundleModeUEFormatModelUri()
    {
        const string canonicalPath = "/Game/Meshes/Chair.Chair";
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", canonicalPath))));
        var plannedModel = Assert.Single(plan.Document.Assets!, asset => asset.Kind == AssetKind.StaticMesh);

        Assert.Equal(
            MaterialLinkBuilder.BuildModelPath(MaterialExportLayout.Categorized, "Game/Meshes/Chair.uemodel", canonicalPath),
            plannedModel.LogicalUri);
    }

    [Fact]
    public void DeduplicatesOverrideMaterialsByCanonicalPathAndWritesScene()
    {
        var first = Component("A.First", "StaticMesh", "/Game/Meshes/First") with { OverrideMaterialPaths = ["/Game/Materials/Shared"] };
        var second = Component("A.Second", "StaticMesh", "/Game/Meshes/Second") with { OverrideMaterialPaths = ["/Game/Materials/Shared"] };
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", first, second)));

        UESceneWriter.Write(plan.Document);
        var material = Assert.Single(plan.Document.Assets!, a => a.Kind == AssetKind.Material);
        Assert.All(plan.Document.MaterialOverrides!, materialOverride => Assert.Equal(material.AssetId, materialOverride.OverrideMaterialAssetId));
    }

    [Fact]
    public void KeepsIsmTransformsInCompactInstanceStream()
    {
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.ISM", "InstancedStaticMesh", "/Game/Meshes/Grass", instances: [Transform(1), Transform(2)]))));

        Assert.Single(plan.Document.Actors!);
        Assert.Single(plan.Document.Components!);
        Assert.Equal(ComponentKind.InstancedStaticMesh, plan.Document.Components![0].ComponentKind);
        Assert.Equal(2, plan.Document.InstanceTransforms!.Count);
    }

    [Fact]
    public void SkipsMissingMeshAndEmitsComponentDiagnostic()
    {
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Mesh", "StaticMesh", null))));

        Assert.Empty(plan.Document.Components!);
        Assert.Contains(plan.Document.Diagnostics!, d => d.Code == UESceneDiagnosticCodes.MissingMesh && d.SourceObjectPath == "A.Mesh");
    }

    [Fact]
    public void SkipsUnsupportedComponentsWithDiagnostic()
    {
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Light", "PointLight", null))));

        Assert.Empty(plan.Document.Components!);
        Assert.Contains(plan.Document.Diagnostics!, d => d.Code == UESceneDiagnosticCodes.UnsupportedComponent && d.SourceObjectPath == "A.Light");
    }

    [Theory]
    [InlineData("Directional", LightType.Directional)]
    [InlineData("Point", LightType.Point)]
    [InlineData("Spot", LightType.Spot)]
    [InlineData("Rect", LightType.Rect)]
    public void ExtractsLightRecordsWithTypeSpecificFieldsAndEmittedMeshAttachment(string type, LightType expectedType)
    {
        var mesh = Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair", transform: Transform(10, 0, 0));
        var light = Light("A.Light", type, parent: "A.Mesh", transform: Transform(2, 3, 4));

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", mesh, light)));

        var record = Assert.Single(plan.Document.Lights!);
        var meshRecord = Assert.Single(plan.Document.Components!);
        var transform = plan.Document.Transforms!.Single(t => t.TransformId == record.TransformId);
        Assert.Equal(expectedType, record.LightType);
        Assert.Equal(meshRecord.ComponentId, record.ParentComponentId);
        Assert.Equal(plan.Document.Actors![0].ActorId, record.ActorId);
        Assert.Equal(2d, transform.TranslationX);
        Assert.Equal(3d, transform.TranslationY);
        Assert.Equal(4d, transform.TranslationZ);
        Assert.Equal((byte) 10, record.ColorR);
        Assert.Equal((byte) 40, record.ColorA);
        Assert.Equal(123f, record.Intensity);
        Assert.True((record.Flags & LightFlags.CastsShadows) != 0);
        if (type == "Directional")
        {
            Assert.Equal(ELightUnits.Unitless, record.LightUnits);
            Assert.Equal(0.5f, record.LightSourceAngle);
            Assert.Equal(0.25f, record.LightSourceSoftAngle);
        }
        else
        {
            Assert.Equal(ELightUnits.Lumens, record.LightUnits);
            Assert.Equal(500f, record.AttenuationRadius);
        }
        if (type is "Point" or "Spot")
        {
            Assert.Equal(8f, record.FalloffExponent);
            Assert.Equal(3f, record.SourceRadius);
            Assert.Equal(4f, record.SoftSourceRadius);
            Assert.Equal(5f, record.SourceLength);
            Assert.True((record.Flags & LightFlags.UsesInverseSquaredFalloff) != 0);
        }
        if (type == "Spot")
        {
            Assert.Equal(11f, record.InnerConeAngleDegrees);
            Assert.Equal(22f, record.OuterConeAngleDegrees);
        }
        if (type == "Rect")
        {
            Assert.Equal(20f, record.RectWidth);
            Assert.Equal(30f, record.RectHeight);
            Assert.Equal(45f, record.BarnDoorAngle);
            Assert.Equal(6f, record.BarnDoorLength);
        }
        UESceneWriter.Write(plan.Document);
    }

    [Fact]
    public void BakesUnemittedLightAncestorsAndDoesNotCreateEmptyLite()
    {
        var root = Component("A.Root", "Scene", null, transform: Transform(10, 0, 0));
        var light = Light("A.Light", "Point", parent: "A.Root", transform: Transform(2, 0, 0));
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", root, light)));

        var record = Assert.Single(plan.Document.Lights!);
        var transform = plan.Document.Transforms!.Single(t => t.TransformId == record.TransformId);
        Assert.Equal(0UL, record.ParentComponentId);
        Assert.Equal(12d, transform.TranslationX);
        Assert.Empty(plan.Document.Components!);
        Assert.Contains(plan.Document.Diagnostics!, d => d.Code == UESceneDiagnosticCodes.UnsupportedParent && d.SourceObjectPath == "A.Light");
        var noLights = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.B", root))).Document;
        Assert.Null(noLights.Lights);
        Assert.Equal(0UL, UESceneReader.Read(UESceneWriter.Write(noLights)).Metadata.OptionalFeatures & SceneMetadata.LightRecordsBit);
    }

    [Fact]
    public void DiagnosesUnsupportedLightSeparatelyFromGenericComponents()
    {
        var unsupported = Light("A.Sky", "SkyLight", transform: Transform());
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", unsupported)));

        Assert.Null(plan.Document.Lights);
        var diagnostic = Assert.Single(UESceneReader.Read(UESceneWriter.Write(plan.Document)).Diagnostics!);
        Assert.Equal(UESceneDiagnosticCodes.UnsupportedLight, diagnostic.Code);
        Assert.Equal("A.Sky", diagnostic.SourceObjectPath);
    }

    [Fact]
    public void DiagnosesStreamingAndWorldPartitionInputs()
    {
        var plan = UESceneBundleExporter.BuildPlan(World(streaming: true, worldPartition: true));

        Assert.Contains(plan.Document.Diagnostics!, d => d.Code == UESceneDiagnosticCodes.StreamingLevels);
        Assert.Contains(plan.Document.Diagnostics!, d => d.Code == UESceneDiagnosticCodes.WorldPartition);
    }

    [Fact]
    public void MergesRootOwnedInstanceAndFallbackComponentSourcesByStablePath()
    {
        var components = UESceneBundleExporter.MergeComponentSources("Root", ["Root", "Owned"], ["Instance", "Owned"], ["Fallback", "Instance"], x => x);

        Assert.Equal(["Root", "Owned", "Instance", "Fallback"], components);
    }

    [Fact]
    public void BakesGenericParentChainIntoSupportedComponentAndDetachesIt()
    {
        var root = Component("A.Root", "Scene", null, transform: Transform(10, 0, 0, 0, 0, 0, 1, 2, 2, 2));
        var pivot = Component("A.Pivot", "Scene", null, parent: "A.Root", transform: Transform(0, 5, 0, 0, 0, 0.70710677f, 0.70710677f, 1, 1, 1));
        var mesh = Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair", parent: "A.Pivot", transform: Transform(1, 0, 0));

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", root, pivot, mesh)));

        var component = Assert.Single(plan.Document.Components!);
        var transform = Assert.Single(plan.Document.Transforms!.Where(t => t.TransformId == component.LocalTransformId));
        Assert.Equal(0UL, component.ParentComponentId);
        Assert.Equal(10f, transform.TranslationX, 4);
        Assert.Equal(12f, transform.TranslationY, 4);
        Assert.Equal(0f, transform.TranslationZ, 4);
        Assert.Equal(2f, transform.ScaleX, 4);
        Assert.Equal(2f, transform.ScaleY, 4);
        Assert.Equal(2f, transform.ScaleZ, 4);
        Assert.Contains(plan.Document.Diagnostics!, d => d.Code == UESceneDiagnosticCodes.UnsupportedParent && d.SourceObjectPath == "A.Mesh" && d.Message == "Unsupported parent ancestry was baked into the local transform.");
    }

    [Fact]
    public void SkippedComponentsLeaveOnlyFinalDocumentReferences()
    {
        var skipped = Component("A.Light", "PointLight", null);
        var mesh = Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair", parent: "A.Light");

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", skipped, mesh)));

        var component = Assert.Single(plan.Document.Components!);
        var actor = Assert.Single(plan.Document.Actors!);
        Assert.Equal(0UL, component.ParentComponentId);
        Assert.Equal(component.ComponentId, actor.RootComponentId);
        Assert.All(plan.Document.Diagnostics!, diagnostic =>
        {
            Assert.Equal(2, diagnostic.SubjectKind);
            Assert.Equal(actor.ActorId, diagnostic.SubjectId);
        });
    }

    [Fact]
    public void LinksChildToSupportedParentEvenWhenChildPrecedesParent()
    {
        var child = Component("A.Child", "StaticMesh", "/Game/Meshes/Child", parent: "A.Parent", transform: Transform(3, 4, 5));
        var parent = Component("A.Parent", "StaticMesh", "/Game/Meshes/Parent", transform: Transform(10, 0, 0));

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", child, parent)));

        var childRecord = plan.Document.Components!.Single(c => c.SourceObjectPath == "A.Child");
        var parentRecord = plan.Document.Components.Single(c => c.SourceObjectPath == "A.Parent");
        var childTransform = plan.Document.Transforms!.Single(t => t.TransformId == childRecord.LocalTransformId);
        Assert.Equal(parentRecord.ComponentId, childRecord.ParentComponentId);
        Assert.Equal(3f, childTransform.TranslationX);
        Assert.Equal(4f, childTransform.TranslationY);
        Assert.Equal(5f, childTransform.TranslationZ);
        Assert.DoesNotContain(plan.Document.Diagnostics!, d => d.Code == UESceneDiagnosticCodes.UnsupportedParent && d.SourceObjectPath == "A.Child");
    }

    [Fact]
    public void BakesOnlyGenericAncestorsBeforeLaterSupportedParent()
    {
        var child = Component("A.Child", "StaticMesh", "/Game/Meshes/Child", parent: "A.Generic", transform: Transform(1, 0, 0));
        var generic = Component("A.Generic", "Scene", null, parent: "A.Parent", transform: Transform(0, 2, 0));
        var parent = Component("A.Parent", "StaticMesh", "/Game/Meshes/Parent", transform: Transform(10, 0, 0));

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", child, generic, parent)));

        var childRecord = plan.Document.Components!.Single(c => c.SourceObjectPath == "A.Child");
        var parentRecord = plan.Document.Components.Single(c => c.SourceObjectPath == "A.Parent");
        var childTransform = plan.Document.Transforms!.Single(t => t.TransformId == childRecord.LocalTransformId);
        Assert.Equal(parentRecord.ComponentId, childRecord.ParentComponentId);
        Assert.Equal(1f, childTransform.TranslationX);
        Assert.Equal(2f, childTransform.TranslationY);
        Assert.Equal(0f, childTransform.TranslationZ);
    }

    [Fact]
    public void SkipsSupportedComponentWhoseGenericParentAncestryCycles()
    {
        var first = Component("A.First", "Scene", null, parent: "A.Second");
        var second = Component("A.Second", "Scene", null, parent: "A.First");
        var mesh = Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair", parent: "A.First");

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", first, second, mesh)));

        Assert.Empty(plan.Document.Components!);
        var diagnostic = Assert.Single(plan.Document.Diagnostics!, d => d.SourceObjectPath == "A.Mesh");
        Assert.Equal(UESceneDiagnosticCodes.UnsupportedComponent, diagnostic.Code);
        Assert.Equal("Unsupported parent ancestry contains a cycle; component was skipped.", diagnostic.Message);
    }

    [Fact]
    public void SkipsSupportedComponentsWhoseParentsCycle()
    {
        var first = Component("A.First", "StaticMesh", "/Game/Meshes/First", parent: "A.Second");
        var second = Component("A.Second", "StaticMesh", "/Game/Meshes/Second", parent: "A.First");

        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", first, second)));

        Assert.Empty(plan.Document.Components!);
        Assert.Equal(2, plan.Document.Diagnostics!.Count(d => d.Code == UESceneDiagnosticCodes.UnsupportedComponent));
        Assert.All(plan.Document.Diagnostics!, diagnostic =>
        {
            Assert.Equal(UESceneDiagnosticCodes.UnsupportedComponent, diagnostic.Code);
            Assert.Equal("Unsupported parent ancestry contains a cycle; component was skipped.", diagnostic.Message);
        });
        UESceneWriter.Write(plan.Document);
    }

    [Fact]
    public void PreservesActorTransformWithoutDuplicatingRootComponentTransform()
    {
        var actorTransform = Transform(7, 8, 9, 0, 0, 0.70710677f, 0.70710677f, 2, 2, 2);
        var root = Component("A.Root", "StaticMesh", "/Game/Meshes/Root", transform: Transform());
        var source = Component("A.Source", "StaticMesh", "/Game/Meshes/Source", parent: "A.Root", transform: Transform(3, 0, 0));
        var actor = new UEScenePlanningActor("/Game/Map.Map:PersistentLevel.A", "A", "/Script/Engine.Actor", actorTransform, [root, source]);

        var plan = UESceneBundleExporter.BuildPlan(World(actor));

        var actorRecord = Assert.Single(plan.Document.Actors!);
        var actorRecordTransform = plan.Document.Transforms!.Single(t => t.TransformId == actorRecord.LocalTransformId);
        var rootRecord = plan.Document.Components!.Single(c => c.SourceObjectPath == "A.Root");
        var rootTransform = plan.Document.Transforms.Single(t => t.TransformId == rootRecord.LocalTransformId);
        var sourceRecord = plan.Document.Components.Single(c => c.SourceObjectPath == "A.Source");
        var sourceTransform = plan.Document.Transforms.Single(t => t.TransformId == sourceRecord.LocalTransformId);
        Assert.Equal(7f, actorRecordTransform.TranslationX);
        Assert.Equal(2f, actorRecordTransform.ScaleX);
        Assert.Equal(0f, rootTransform.TranslationX);
        Assert.Equal(1f, rootTransform.ScaleX);
        Assert.Equal(rootRecord.ComponentId, sourceRecord.ParentComponentId);
        Assert.Equal(3f, sourceTransform.TranslationX);
    }

    [Theory]
    [InlineData("InstancedStaticMesh")]
    [InlineData("HierarchicalInstancedStaticMesh")]
    public void SkipsEntireInstancedComponentWithInvalidInstanceTransform(string kind)
    {
        var invalid = Transform(float.NaN);
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Instances", kind, "/Game/Meshes/Grass", instances: [Transform(1), invalid]))));

        Assert.Empty(plan.Document.Components!);
        var diagnostic = Assert.Single(plan.Document.Diagnostics!);
        Assert.Equal(UESceneDiagnosticCodes.InvalidInstanceTransform, diagnostic.Code);
        Assert.Equal("A.Instances", diagnostic.SourceObjectPath);
        Assert.Equal("Instanced component has an invalid instance transform and was skipped.", diagnostic.Message);
    }

    [Theory]
    [InlineData("InstancedStaticMesh")]
    [InlineData("HierarchicalInstancedStaticMesh")]
    public void NormalizesFiniteInstancedComponentRotations(string kind)
    {
        var nearlyNormalized = Transform(1, 2, 3, 0, 0, 0.3188521f, 0.9460182f);
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", Component("A.Instances", kind, "/Game/Meshes/Grass", instances: [nearlyNormalized]))));

        Assert.Single(plan.Document.Components!);
        Assert.Empty(plan.Document.Diagnostics!);
        var instance = Assert.Single(plan.Document.InstanceTransforms!);
        var lengthSquared = instance.RotationX * instance.RotationX + instance.RotationY * instance.RotationY + instance.RotationZ * instance.RotationZ + instance.RotationW * instance.RotationW;
        Assert.InRange(lengthSquared, 0.99999f, 1.00001f);
    }

    [Fact]
    public void DiagnosesSocketAttachmentWithExactMessage()
    {
        var component = Component("A.Mesh", "StaticMesh", "/Game/Meshes/Chair") with { AttachSocketName = "Grip" };
        var plan = UESceneBundleExporter.BuildPlan(World(Actor("/Game/Map.Map:PersistentLevel.A", component)));

        var diagnostic = Assert.Single(plan.Document.Diagnostics!);
        Assert.Equal(UESceneDiagnosticCodes.UnsupportedSocket, diagnostic.Code);
        Assert.Equal("A.Mesh", diagnostic.SourceObjectPath);
        Assert.Equal("Socket attachment is unsupported and was skipped.", diagnostic.Message);
    }

    private static UEScenePlanningWorld World(params UEScenePlanningActor[] actors) => World(false, false, actors);
    private static UEScenePlanningWorld World(bool streaming, bool worldPartition, params UEScenePlanningActor[] actors) => new("/Game/Map", "/Game/Map.Map:PersistentLevel", actors, streaming, worldPartition);
    private static UEScenePlanningActor Actor(string path, params UEScenePlanningComponent[] components) => new(path, path.Split('.').Last(), "/Script/Engine.Actor", Transform(), components);
    private static UEScenePlanningComponent Component(string path, string kind, string? mesh, IReadOnlyList<UEScenePlanningTransform>? instances = null, string? parent = null, UEScenePlanningTransform? transform = null) => new(path, path.Split('.').Last(), "/Script/Engine." + kind + "Component", kind, transform ?? Transform(), mesh, true, parent, "", instances ?? [], []);
    private static UEScenePlanningComponent ComponentWithOverrides(string path, string kind, string? mesh, IReadOnlyList<string?> overrides) => new(path, path.Split('.').Last(), "/Script/Engine." + kind + "Component", kind, Transform(), mesh, true, null, "", [], overrides);
    private static UEScenePlanningComponent Light(string path, string type, string? parent = null, UEScenePlanningTransform? transform = null) =>
        Component(path, type + "Light", null, parent: parent, transform: transform) with
        {
            Light = new(type, 10, 20, 30, 40, 123, type == "Directional" ? ELightUnits.Unitless : ELightUnits.Lumens, true,
                type == "Directional" ? 0 : 500, type is "Point" or "Spot" ? 8 : 0, type is "Point" or "Spot",
                type == "Spot" ? 11 : 0, type == "Spot" ? 22 : 0, type == "Rect" ? 20 : 0, type == "Rect" ? 30 : 0,
                type is "Point" or "Spot" ? 3 : 0, type is "Point" or "Spot" ? 4 : 0, type is "Point" or "Spot" ? 5 : 0,
                type == "Directional" ? .5f : 0, type == "Directional" ? .25f : 0, type == "Rect" ? 45 : 0, type == "Rect" ? 6 : 0)
        };
    private static UEScenePlanningTransform Transform(float x = 0, float y = 0, float z = 0, float rx = 0, float ry = 0, float rz = 0, float rw = 1, float sx = 1, float sy = 1, float sz = 1) => new(x, y, z, rx, ry, rz, rw, sx, sy, sz);
    private static string WriteExistingScene(DirectoryInfo root)
    {
        var path = Path.Combine(root.FullName, "Scenes", "Map.uescene");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [9, 8, 7]);
        return path;
    }

    private sealed class TestDirectory : IDisposable
    {
        public DirectoryInfo Directory { get; } = new(Path.Combine(Path.GetTempPath(), "CUE4Parse.Tests", Path.GetRandomFileName()));
        public TestDirectory() => Directory.Create();
        public void Dispose() { if (Directory.Exists) Directory.Delete(true); }
    }
}
