using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;

namespace CUE4Parse.Tests;

public class MeshExporterIntegrationTests
{
    [Theory]
    [InlineData(MaterialExportLayout.PreserveHierarchy, "Game/Meshes/M.uemodel", "Game/Textures/T.png", "Game/Materials/M.json", "Game/Meshes/M.uemodel", "Game/Textures/T.png", "Game/Materials/M.json")]
    [InlineData(MaterialExportLayout.Categorized, "Models/M.uemodel", "Textures/T.png", "Materials/M.json", "Models/M.uemodel", "Textures/T.png", "Materials/M.json")]
    public void BundleExport_PublishesEveryTargetUnderAbsoluteBundleRootWithoutLeakingItIntoUris(
        MaterialExportLayout layout, string logicalModelPath, string logicalTexturePath, string logicalMaterialPath,
        string modelTarget, string textureTarget, string materialTarget)
    {
        using var tempDir = new TemporaryDirectory();
        var bundleRoot = Path.Combine(tempDir.Directory.FullName, "Windows", "BundleRoot");
        Directory.CreateDirectory(bundleRoot);
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T",
            MaterialLinkBuilder.BuildUri(MaterialLinkMode.Bundle, logicalModelPath, logicalTexturePath, bundleRoot, layout),
            "image/png", [1], true, textureTarget);
        manifest.AddOrGet("material:/Game/Materials/M",
            MaterialLinkBuilder.BuildUri(MaterialLinkMode.Bundle, logicalModelPath, logicalMaterialPath, bundleRoot, layout),
            "application/json", [2], true, materialTarget);
        var mesh = new Mesh(modelTarget, [3], []) { ExportManifest = manifest };
        var options = new ExporterOptions
        {
            MaterialLinkMode = MaterialLinkMode.Bundle,
            MaterialLinkBundleRoot = bundleRoot,
            MaterialExportLayout = layout
        };

        Assert.True(new MeshExporter([mesh], options: options).TryWriteToDir(new DirectoryInfo(bundleRoot), out _, out var savedPath));

        Assert.Equal(Path.GetFullPath(Path.Combine(bundleRoot, modelTarget)), savedPath);
        Assert.All(manifest.Resources, resource =>
        {
            Assert.DoesNotContain(bundleRoot, resource.RelativeUri, StringComparison.OrdinalIgnoreCase);
            Assert.False(Path.IsPathRooted(resource.RelativeUri));
            Assert.StartsWith(bundleRoot + Path.DirectorySeparatorChar,
                Path.GetFullPath(Path.Combine(bundleRoot, resource.PhysicalTargetPath)), StringComparison.OrdinalIgnoreCase);
        });
        Assert.True(File.Exists(Path.Combine(bundleRoot, textureTarget)));
        Assert.True(File.Exists(Path.Combine(bundleRoot, materialTarget)));
    }

    [Fact]
    public void BundleExport_RejectsMissingPhysicalRoot()
    {
        var options = new ExporterOptions { MaterialLinkMode = MaterialLinkMode.Bundle, MeshFormat = EMeshFormat.UEFormat };

        var error = Assert.Throws<ArgumentException>(() => new MeshExporter([], options: options));

        Assert.Contains("absolute physical bundle root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UeFormatBundleConfiguration_RejectsMissingPhysicalRoot()
    {
        var options = new ExporterOptions { MaterialLinkMode = MaterialLinkMode.Bundle, MeshFormat = EMeshFormat.UEFormat };

        var error = Assert.Throws<ArgumentException>(() => MeshExporter.ValidateUeFormatBundleConfiguration(options));

        Assert.Contains("absolute physical bundle root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UeFormatAllLods_RejectsDivergentMaterialTopology()
    {
        var error = Assert.Throws<InvalidDataException>(() => MeshExporter.ValidateUeFormatMaterialTopology([
            (0u, (IReadOnlyList<(int SourceMaterialIndex, int SectionIndex)>)[(0, 0), (1, 1)]),
            (1u, (IReadOnlyList<(int SourceMaterialIndex, int SectionIndex)>)[(0, 0), (2, 1)])
        ]));

        Assert.Contains("LOD 1", error.Message);
        Assert.Contains("material topology", error.Message);
    }

    [Fact]
    public void UeFormatAllLods_RejectsDifferentSectionCount()
    {
        var error = Assert.Throws<InvalidDataException>(() => MeshExporter.ValidateUeFormatMaterialTopology([
            (0u, (IReadOnlyList<(int SourceMaterialIndex, int SectionIndex)>)[(0, 0), (1, 1)]),
            (1u, (IReadOnlyList<(int SourceMaterialIndex, int SectionIndex)>)[(0, 0)])
        ]));

        Assert.Contains("LOD 1", error.Message);
        Assert.Contains("material topology", error.Message);
    }

    [Fact]
    public void UeFormatAllLods_PermitsMatchingMaterialTopology()
    {
        MeshExporter.ValidateUeFormatMaterialTopology([
            (0u, (IReadOnlyList<(int SourceMaterialIndex, int SectionIndex)>)[(0, 0), (1, 1)]),
            (2u, (IReadOnlyList<(int SourceMaterialIndex, int SectionIndex)>)[(0, 0), (1, 1)])
        ]);
    }

    [Fact]
    public void SkeletalUeFormatLods_PreserveSourceIdentityAndRejectDivergentTopology()
    {
        var first = CreateSkeletalLod(0);
        var second = CreateSkeletalLod(0);

        var included = MeshExporter.PrepareUeFormatLods([
            (Lod: (CBaseMeshLod) first, SourceLodIndex: 2u),
            (Lod: (CBaseMeshLod) second, SourceLodIndex: 5u)
        ], ELodFormat.AllLods);

        Assert.Equal([2u, 5u], included.Select(entry => entry.SourceLodIndex));

        second.Sections = new Lazy<CMeshSection[]>(() => [new CMeshSection(2, 0, 1, null, null)]);
        var error = Assert.Throws<InvalidDataException>(() => MeshExporter.PrepareUeFormatLods([
            (Lod: (CBaseMeshLod) first, SourceLodIndex: 2u),
            (Lod: (CBaseMeshLod) second, SourceLodIndex: 5u)
        ], ELodFormat.AllLods));

        Assert.Contains("LOD 5", error.Message);
    }

    [Fact]
    public void CategorizedBundle_DistinctBasenamesPublishWithoutManifestCollision()
    {
        using var tempDir = new TemporaryDirectory();
        var heroTarget = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Hero/T_Base.png", MaterialExportResource.Texture, "/Game/Hero/T_Base.T_Base");
        var weaponTarget = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Weapon/T_Base.png", MaterialExportResource.Texture, "/Game/Weapon/T_Base.T_Base");
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Hero/T_Base.T_Base", heroTarget, "image/png", [1], true, heroTarget);
        manifest.AddOrGet("/Game/Weapon/T_Base.T_Base", weaponTarget, "image/png", [2], true, weaponTarget);
        var mesh = new Mesh("Models/Hero.uemodel", [3], []) { ExportManifest = manifest };

        Assert.True(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.Equal(2, manifest.Resources.Count);
        Assert.NotEqual(heroTarget, weaponTarget);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(tempDir.Directory.FullName, heroTarget)));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(tempDir.Directory.FullName, weaponTarget)));
    }

    [Fact]
    public void CategorizedBundle_UsesFullIdentityHashToAvoidTruncatedHashCollisions()
    {
        const string heroIdentity = "/Game/Hero/T_Base.T_Base";
        const string weaponIdentity = "/Game/Weapon/T_Base.T_Base";
        var heroTarget = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Hero/T_Base.png", MaterialExportResource.Texture, heroIdentity);
        var weaponTarget = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Weapon/T_Base.png", MaterialExportResource.Texture, weaponIdentity);

        Assert.EndsWith("-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(heroIdentity))) + ".png", heroTarget);
        Assert.EndsWith("-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(weaponIdentity))) + ".png", weaponTarget);
        Assert.NotEqual(heroTarget, weaponTarget);
    }

    [Fact]
    public void ExternalResource_UsesRelativeUriAndContainedPhysicalTarget()
    {
        using var tempDir = new TemporaryDirectory();
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T", "../Textures/T.png", "image/png", [1, 2, 3], true, "Textures/T.png");
        var mesh = new Mesh("Models/M.uemodel", [0x01], []) { ExportManifest = manifest };
        var exporter = new MeshExporter([mesh]);

        Assert.True(exporter.TryWriteToDir(tempDir.Directory, out _, out _));

        Assert.Equal("../Textures/T.png", manifest.Resources[0].RelativeUri);
        var target = Path.GetFullPath(Path.Combine(tempDir.Directory.FullName, "Textures/T.png"));
        Assert.StartsWith(tempDir.Directory.FullName + Path.DirectorySeparatorChar, target, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.True(File.Exists(Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel")));
    }

    [Fact]
    public void ExternalResource_RejectsReparsePointAncestorWithoutPublishingModel()
    {
        using var tempDir = new TemporaryDirectory();
        var textures = Path.Combine(tempDir.Directory.FullName, "Textures");
        Directory.CreateDirectory(textures);

        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T", "Textures/T.png", "image/png", [1], true, "Textures/T.png");
        var mesh = new Mesh("Models/M.uemodel", [2], [])
        {
            ExportManifest = manifest,
            PathAttributes = path => path == textures ? FileAttributes.Directory | FileAttributes.ReparsePoint : null
        };

        Assert.False(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(File.Exists(Path.Combine(textures, "T.png")));
        Assert.False(File.Exists(Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel")));
    }

    [Fact]
    public void ExternalResource_ReparsePointRejectionPreservesExistingModel()
    {
        using var tempDir = new TemporaryDirectory();
        var textures = Path.Combine(tempDir.Directory.FullName, "Textures");
        var modelPath = Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel");
        Directory.CreateDirectory(textures);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, [9]);

        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T", "Textures/T.png", "image/png", [1], true, "Textures/T.png");
        var mesh = new Mesh("Models/M.uemodel", [2], [])
        {
            ExportManifest = manifest,
            PathAttributes = path => path == textures ? FileAttributes.Directory | FileAttributes.ReparsePoint : null
        };

        Assert.False(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.Equal([9], File.ReadAllBytes(modelPath));
        Assert.False(File.Exists(Path.Combine(textures, "T.png")));
    }

    [Fact]
    public void ExternalResource_ReplacesExistingTargetOnlyAfterCompleteWrite()
    {
        using var tempDir = new TemporaryDirectory();
        var target = Path.Combine(tempDir.Directory.FullName, "Textures/T.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, [9, 9, 9]);
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T", "Textures/T.png", "image/png", [1, 2, 3], true, "Textures/T.png");
        var mesh = new Mesh("Models/M.uemodel", [0x01], []) { ExportManifest = manifest };

        Assert.True(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "T.png.tmp-*"));
    }

    [Fact]
    public void ExternalMaterialJsonWriteFailure_ReturnsFalseAndDoesNotPublishModel()
    {
        using var tempDir = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Directory.FullName, "Materials"));
        var manifest = new ResourceManifest();
        manifest.AddOrGet("material:/Game/Materials/M", "Materials/M.json", "application/json", [0x7B], true, "Materials");
        var mesh = new Mesh("Models/M.uemodel", [0x01], []) { ExportManifest = manifest };
        var exporter = new MeshExporter([mesh]);

        Assert.False(exporter.TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(File.Exists(Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel")));
    }

    [Fact]
    public void EmbeddedExport_SucceedsWithoutJsonSidecar()
    {
        using var tempDir = new TemporaryDirectory();
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T", "Textures/T.png", "image/png", [1], false);
        var mesh = new Mesh("Models/M.uemodel", [0x01], []) { ExportManifest = manifest };
        var exporter = new MeshExporter([mesh]);

        Assert.True(exporter.TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.True(File.Exists(Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel")));
        Assert.False(File.Exists(Path.Combine(tempDir.Directory.FullName, "Textures/T.png")));
        Assert.False(File.Exists(Path.Combine(tempDir.Directory.FullName, "Materials/M.json")));
    }

    [Fact]
    public void ExternalEmptyBytes_FailAndDoNotPublishModel()
    {
        using var tempDir = new TemporaryDirectory();
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/Empty.Empty", "Textures/Empty.png", "image/png", [], true);
        var mesh = new Mesh("Models/M.uemodel", [0x01], []) { ExportManifest = manifest };

        Assert.False(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(File.Exists(Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel")));
        Assert.Equal(WriteStatus.Failed, manifest.GetStatus("/Game/Textures/Empty.Empty"));
    }

    [Fact]
    public void ExternalResourceFailure_RemovesStaleModelAndPreservesFinalSidecars()
    {
        using var tempDir = new TemporaryDirectory();
        var modelPath = Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel");
        var sidecarPath = Path.Combine(tempDir.Directory.FullName, "Textures/T.png");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
        File.WriteAllBytes(modelPath, [0x99]);
        File.WriteAllBytes(sidecarPath, [0x88]);

        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T", "Textures/T.png", "image/png", [0x01], true);
        manifest.AddOrGet("/Game/Textures/Empty.Empty", "Textures/Empty.png", "image/png", [], true);
        var mesh = new Mesh("Models/M.uemodel", [0x02], []) { ExportManifest = manifest };

        Assert.False(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(File.Exists(modelPath));
        Assert.Equal([0x88], File.ReadAllBytes(sidecarPath));
    }

    [Fact]
    public void ExternalEmptyModel_RemovesStaleFinalModel()
    {
        using var tempDir = new TemporaryDirectory();
        var modelPath = Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, [0x99]);

        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/T.T", "Textures/T.png", "image/png", [0x01], true);
        var mesh = new Mesh("Models/M.uemodel", [], []) { ExportManifest = manifest };

        Assert.False(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(File.Exists(modelPath));
        Assert.False(File.Exists(Path.Combine(tempDir.Directory.FullName, "Textures/T.png")));
    }

    [Fact]
    public void ExternalExportSuccess_ReplacesSidecarsAndPublishesModelLast()
    {
        using var tempDir = new TemporaryDirectory();
        var modelPath = Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel");
        var firstSidecarPath = Path.Combine(tempDir.Directory.FullName, "Textures/A.png");
        var secondSidecarPath = Path.Combine(tempDir.Directory.FullName, "Materials/M.json");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(firstSidecarPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondSidecarPath)!);
        File.WriteAllBytes(modelPath, [0x90]);
        File.WriteAllBytes(firstSidecarPath, [0x91]);
        File.WriteAllBytes(secondSidecarPath, [0x92]);

        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/A.A", "Textures/A.png", "image/png", [0x01], true);
        manifest.AddOrGet("material:/Game/Materials/M", "Materials/M.json", "application/json", [0x02], true);
        var mesh = new Mesh("Models/M.uemodel", [0x03], []) { ExportManifest = manifest };

        Assert.True(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.Equal([0x01], File.ReadAllBytes(firstSidecarPath));
        Assert.Equal([0x02], File.ReadAllBytes(secondSidecarPath));
        Assert.Equal([0x03], File.ReadAllBytes(modelPath));
        Assert.Empty(Directory.GetDirectories(tempDir.Directory.FullName, ".ueformat-stage-*"));
    }

    [Fact]
    public void FailedSecondSidecarPromotion_RestoresAllFinalSidecarsAndRemovesModel()
    {
        using var tempDir = new TemporaryDirectory();
        var modelPath = Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel");
        var firstSidecarPath = Path.Combine(tempDir.Directory.FullName, "Textures/A.png");
        var secondSidecarPath = Path.Combine(tempDir.Directory.FullName, "Materials/M.json");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(firstSidecarPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondSidecarPath)!);
        File.WriteAllBytes(modelPath, [0x90]);
        File.WriteAllBytes(firstSidecarPath, [0x91]);
        File.WriteAllBytes(secondSidecarPath, [0x92]);

        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/Textures/A.A", "Textures/A.png", "image/png", [0x01], true);
        manifest.AddOrGet("material:/Game/Materials/M", "Materials/M.json", "application/json", [0x02], true);
        var mesh = new Mesh("Models/M.uemodel", [0x03], [])
        {
            ExportManifest = manifest,
            FinalResourcePromotionFailure = promotionIndex => promotionIndex == 1
        };

        Assert.False(new MeshExporter([mesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.Equal([0x91], File.ReadAllBytes(firstSidecarPath));
        Assert.Equal([0x92], File.ReadAllBytes(secondSidecarPath));
        Assert.False(File.Exists(modelPath));
        Assert.Empty(Directory.GetDirectories(tempDir.Directory.FullName, ".ueformat-stage-*"));
    }

    [Fact]
    public void AnyLodOrDnaFailure_MakesMeshExporterReturnFalse()
    {
        using var tempDir = new TemporaryDirectory();
        var validMesh = new Mesh("Models/M.uemodel", [0x01], []);
        Assert.False(new MeshExporter([new FailingExporter(), validMesh]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(new MeshExporter([validMesh], [new FailingExporter()]).TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(File.Exists(Path.Combine(tempDir.Directory.FullName, "Models/M.uemodel")));
    }

    [Fact]
    public void LaterLodFailure_IsNotMaskedByEarlierSuccessfulLod()
    {
        using var tempDir = new TemporaryDirectory();
        var firstLod = new Mesh("Models/M_LOD0.uemodel", [0x01], []);
        var exporter = new MeshExporter([firstLod, new FailingExporter()]);

        Assert.False(exporter.TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.True(File.Exists(Path.Combine(tempDir.Directory.FullName, "Models/M_LOD0.uemodel")));
    }

    private sealed class FailingExporter : IExporter
    {
        public bool TryWriteToDir(DirectoryInfo directoryInfo, out string label, out string savedFileName)
        {
            label = string.Empty;
            savedFileName = string.Empty;
            return false;
        }

        public bool TryWriteToZip(out byte[] zipFile) { zipFile = []; return false; }
        public void AppendToZip() { }
    }

    private static CSkelMeshLod CreateSkeletalLod(int materialIndex) => new()
    {
        Sections = new Lazy<CMeshSection[]>(() => [new CMeshSection(materialIndex, 0, 1, null, null)]),
        Indices = new Lazy<uint[]>(() => [0, 1, 2])
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory();
        public void Dispose() => Directory.Delete(true);
    }
}
