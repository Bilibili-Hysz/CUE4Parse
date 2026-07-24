using System.Reflection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using CUE4Parse_Conversion.UEScene;

namespace CUE4Parse.Tests;

public class MaterialExporterIntegrationTests
{
    [Theory]
    [InlineData("Game/Materials/M_Chair.M_Chair", "/Game/Materials/M_Chair")]
    [InlineData("Engine/Materials/M_Default.M_Default", "/Engine/Materials/M_Default")]
    public void BuildMaterialLinks_UsesCanonicalMaterialIdentityAndPreservesUris(string materialPath, string expectedIdentity)
    {
        var exporter = CreateMaterialExporter(materialPath, true);

        var links = exporter.BuildMaterialLinks(
            MaterialLinkMode.Bundle, "Game/Models/Chair.uemodel", 0, "0", Path.GetFullPath("BundleRoot"));

        var slot = Assert.Single(links.Slots);
        Assert.Equal(expectedIdentity, slot.UnrealMaterialPath);
        Assert.Equal(materialPath[..^Path.GetExtension(materialPath).Length] + ".json", slot.MaterialJsonUri);
        Assert.Equal("Game/Textures/T.png", Assert.Single(slot.Parameters).TextureValue!.Uri);
    }

    [Theory]
    [InlineData(MaterialLinkMode.Relative, "Models/M.uemodel", "Materials/M.M", "../Textures/T.png", "../Materials/M.json")]
    [InlineData(MaterialLinkMode.Bundle, "Game/Models/M.uemodel", "Game/Materials/M.M", "Game/Textures/T.png", "Game/Materials/M.json")]
    public void PopulateManifestThenBuildMaterialLinks_ReusesFinalManifestUris(
        MaterialLinkMode mode, string modelPath, string materialPath, string textureUri, string jsonUri)
    {
        var bundleRoot = mode == MaterialLinkMode.Bundle ? Path.GetFullPath("BundleRoot") : null;
        var exporter = CreateMaterialExporter(materialPath, mode == MaterialLinkMode.Bundle);
        var manifest = new ResourceManifest();

        exporter.PopulateManifest(manifest, mode, modelPath, bundleRoot);
        manifest.Seal();
        var links = exporter.BuildMaterialLinks(mode, modelPath, 0, "0", bundleRoot, manifest);

        var texture = Assert.Single(manifest.Resources, resource => resource.UnrealObjectPath.EndsWith("Textures/T.T", StringComparison.Ordinal));
        var material = Assert.Single(manifest.Resources, resource => resource.UnrealObjectPath.StartsWith("material:", StringComparison.Ordinal));
        Assert.Equal(textureUri, texture.RelativeUri);
        Assert.Equal(mode == MaterialLinkMode.Bundle ? "Game/Textures/T.png" : "Textures/T.png", texture.PhysicalTargetPath);
        Assert.Equal(jsonUri, material.RelativeUri);
        Assert.Equal(mode == MaterialLinkMode.Bundle ? "Game/Materials/M.json" : "Materials/M.json", material.PhysicalTargetPath);

        var slot = Assert.Single(links.Slots);
        Assert.Equal(jsonUri, slot.MaterialJsonUri);
        Assert.Equal(textureUri, Assert.Single(slot.Parameters).TextureValue!.Uri);
    }

    [Fact]
    public void CategorizedBundleLayout_UsesContainedRootRelativeManifestTargets()
    {
        var exporter = CreateMaterialExporter("Game/Materials/M.M", true);
        var manifest = new ResourceManifest();

        var bundleRoot = Path.GetFullPath("BundleRoot");
        exporter.PopulateManifest(manifest, MaterialLinkMode.Bundle, "Models/M.uemodel", bundleRoot,
            MaterialExportLayout.Categorized);
        manifest.Seal();
        var links = exporter.BuildMaterialLinks(MaterialLinkMode.Bundle, "Models/M.uemodel", 0, "0", bundleRoot,
            manifest, MaterialExportLayout.Categorized);

        var texture = Assert.Single(manifest.Resources, resource => resource.UnrealObjectPath.EndsWith("Textures/T.T", StringComparison.Ordinal));
        var material = Assert.Single(manifest.Resources, resource => resource.UnrealObjectPath.StartsWith("material:", StringComparison.Ordinal));
        Assert.Equal(texture.RelativeUri, texture.PhysicalTargetPath);
        Assert.Equal(material.RelativeUri, material.PhysicalTargetPath);
        Assert.Equal(material.RelativeUri, Assert.Single(links.Slots).MaterialJsonUri);
        Assert.Equal(texture.RelativeUri, Assert.Single(links.Slots[0].Parameters).TextureValue!.Uri);
        Assert.StartsWith("Textures/T-", texture.RelativeUri);
        Assert.StartsWith("Materials/M-", material.RelativeUri);
    }

    [Theory]
    [InlineData(UESceneResourceLogicalRoot.SceneBundle)]
    [InlineData(UESceneResourceLogicalRoot.GameExportTree)]
    public void UEScenePolicyUsesCanonicalMaterialAndTextureIdentities(UESceneResourceLogicalRoot logicalRoot)
    {
        var policy = new UESceneResourceUriPolicy(logicalRoot);
        var options = new CUE4Parse_Conversion.ExporterOptions
        {
            MaterialFormat = EMaterialFormat.AllLayers,
            TextureFormat = ETextureFormat.Png,
            UESceneResourceUriPolicy = policy
        };
        var exporter = CreateMaterialExporter("/Game/Materials/M_Chair.M_Chair", true, options);
        var manifest = new ResourceManifest();

        exporter.PopulateManifest(manifest, MaterialLinkMode.Bundle,
            policy.Allocate("/Game/Meshes/Chair.Chair", UESceneResourceKind.Model, ".uemodel"), Path.GetFullPath("BundleRoot"),
            MaterialExportLayout.Categorized);
        manifest.Seal();

        Assert.Equal(policy.Allocate("/Game/Materials/M_Chair.M_Chair", UESceneResourceKind.Material, ".json"),
            Assert.Single(manifest.Resources, resource => resource.UnrealObjectPath.StartsWith("material:", StringComparison.Ordinal)).RelativeUri);
        Assert.Equal(policy.Allocate("Game/Textures/T.T", UESceneResourceKind.Texture, ".png"),
            Assert.Single(manifest.Resources, resource => !resource.UnrealObjectPath.StartsWith("material:", StringComparison.Ordinal)).RelativeUri);
    }

    [Fact]
    public void MaterialPathNormalizationOnlyAffectsSerializedMaterialIdentity()
    {
        var policy = new UESceneResourceUriPolicy(UESceneResourceLogicalRoot.GameExportTree);
        var options = new CUE4Parse_Conversion.ExporterOptions
        {
            MaterialFormat = EMaterialFormat.AllLayers,
            TextureFormat = ETextureFormat.Png,
            UESceneResourceUriPolicy = policy
        };
        const string rawMaterialPath = "Game/Materials/M_Chair.M_Chair";
        var exporter = CreateMaterialExporter(rawMaterialPath, true, options);
        var manifest = new ResourceManifest();

        exporter.PopulateManifest(manifest, MaterialLinkMode.Bundle, "Game/Models/Chair.uemodel", Path.GetFullPath("BundleRoot"));
        manifest.Seal();
        var links = exporter.BuildMaterialLinks(MaterialLinkMode.Bundle, "Game/Models/Chair.uemodel", 0, "0", Path.GetFullPath("BundleRoot"), manifest);

        var material = Assert.Single(manifest.Resources, resource => resource.UnrealObjectPath == "material:" + rawMaterialPath);
        var expectedMaterialUri = policy.Allocate(rawMaterialPath, UESceneResourceKind.Material, ".json");
        Assert.Equal(expectedMaterialUri, material.RelativeUri);
        Assert.Equal(expectedMaterialUri, material.PhysicalTargetPath);
        Assert.Equal(material.RelativeUri, Assert.Single(links.Slots).MaterialJsonUri);
        Assert.Equal("/Game/Materials/M_Chair", Assert.Single(links.Slots).UnrealMaterialPath);
        Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawMaterialPath))).ToLowerInvariant(), material.RelativeUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BundleRoot")]
    public void BuildMaterialLinks_BundleModeCannotBypassPhysicalBundleRootValidation(string? bundleRoot)
    {
        var exporter = CreateMaterialExporter("Game/Materials/M.M", true);

        Assert.Throws<ArgumentException>(() => exporter.BuildMaterialLinks(
            MaterialLinkMode.Bundle, "Game/Models/M.uemodel", 0, bundleRoot: bundleRoot));
    }

    [Fact]
    public void TryWriteToDir_ReparsePointTargetReturnsFalseWithoutWriting()
    {
        using var tempDir = new TemporaryDirectory();
        var textures = Path.Combine(tempDir.Directory.FullName, "Textures");
        Directory.CreateDirectory(textures);
        var exporter = CreateMaterialExporter("Textures/M.M", false);
        exporter.PathAttributes = path => path == textures ? FileAttributes.Directory | FileAttributes.ReparsePoint : null;

        Assert.False(exporter.TryWriteToDir(tempDir.Directory, out _, out _));
        Assert.False(File.Exists(Path.Combine(textures, "M.json")));
        Assert.False(File.Exists(Path.Combine(textures, "T.png")));
    }

    private static MaterialExporter2 CreateMaterialExporter(string materialPath, bool bundle, CUE4Parse_Conversion.ExporterOptions? options = null)
    {
        options ??= new CUE4Parse_Conversion.ExporterOptions { MaterialFormat = EMaterialFormat.AllLayers, TextureFormat = ETextureFormat.Png };
        var texture = new UTexture2D { Name = bundle ? "Game/Textures/T.T" : "Textures/T.T" };
        var platformData = new FTexturePlatformData();
        platformData.Mips = [new FTexture2DMipMap(new FByteArrayData([0, 0, 255, 255]), 1, 1, 1)];
        typeof(UTexture).GetProperty(nameof(UTexture.PlatformData), BindingFlags.Instance | BindingFlags.Public)!.SetValue(texture, platformData);
        typeof(UTexture).GetProperty(nameof(UTexture.Format), BindingFlags.Instance | BindingFlags.Public)!.SetValue(texture, EPixelFormat.PF_B8G8R8A8);

        return new MaterialExporter2(new TestMaterial(materialPath, texture), options.Value);
    }

    private sealed class TestMaterial : UUnrealMaterial
    {
        private readonly UTexture2D _texture;

        public TestMaterial(string materialPath, UTexture2D texture)
        {
            Name = materialPath;
            _texture = texture;
        }

        public override void GetParams(CMaterialParams parameters) { }

        public override void GetParams(CMaterialParams2 parameters, EMaterialFormat format)
        {
            parameters.Textures["BaseColor"] = _texture;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory();
        public void Dispose() => Directory.Delete(true);
    }
}
