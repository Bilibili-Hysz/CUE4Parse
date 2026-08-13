using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;

namespace CUE4Parse.Tests;

public class MaterialLinkBuilderTests
{
    [Theory]
    [InlineData("/Game/Materials/M.M", "/Game/Materials/M")]
    [InlineData("Game/Materials/M.M", "/Game/Materials/M")]
    [InlineData("/Engine/Content/BasicShapes/BasicShapeMaterial.BasicShapeMaterial", "/Engine/Content/BasicShapes/BasicShapeMaterial")]
    public void NormalizeUnrealMaterialPath_UsesCanonicalPackagePath(string input, string expected)
    {
        Assert.Equal(expected, MaterialLinkBuilder.NormalizeUnrealMaterialPath(input));
    }

    [Fact]
    public void Build_NormalizesUnrealMaterialPathInModelLinks()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "Engine/Content/BasicShapes/BasicShapeMaterial.BasicShapeMaterial", []);

        Assert.Equal("/Engine/Content/BasicShapes/BasicShapeMaterial", Assert.Single(links.Slots).UnrealMaterialPath);
    }

    [Fact]
    public void BuildForMaterialSlots_PreservesOriginalSectionSlotsWhenAMaterialIsMissing()
    {
        var options = new ExporterOptions();
        var links = MaterialLinkBuilder.BuildForMaterialSlots(
            MaterialLinkMode.Relative,
            "Game/Meshes/Hero.uemodel",
            [(0, new MaterialExporter2(options)), (2, new MaterialExporter2(options))]);

        Assert.Equal([0, 2], links.Slots.Select(slot => slot.SlotIndex));
    }

    [Fact]
    public void Build_AssignsOnlySelectedTexturePreviewRolesAndPreservesParameterOrder()
    {
        var links = MaterialLinkBuilder.Build(
            MaterialLinkMode.Relative,
            "Game/Meshes/Hero.uemodel",
            "Game/Materials/M_Hero.json",
            "/Game/Materials/M_Hero.M_Hero",
            [
                MaterialLinkSourceParameter.Texture("Ramp", "/Game/Textures/T_Ramp.T_Ramp", "Game/Textures/T_Ramp.png", "image/png", [1]),
                MaterialLinkSourceParameter.Texture("PM_Diffuse", "/Game/Textures/T_Diffuse.T_Diffuse", "Game/Textures/T_Diffuse.png", "image/png", [2]),
                MaterialLinkSourceParameter.Texture("DiffuseColor", "/Game/Textures/T_DiffuseColor.T_DiffuseColor", "Game/Textures/T_DiffuseColor.png", "image/png", [3]),
                MaterialLinkSourceParameter.Texture("BaseColor", "/Game/Textures/T_Base.T_Base", "Game/Textures/T_Base.png", "image/png", [4]),
                MaterialLinkSourceParameter.Texture("PM_Normals", "/Game/Textures/T_Normal.T_Normal", "Game/Textures/T_Normal.png", "image/png", [5]),
                MaterialLinkSourceParameter.Texture("NormalMap", "/Game/Textures/T_NormalMap.T_NormalMap", "Game/Textures/T_NormalMap.png", "image/png", [6]),
                MaterialLinkSourceParameter.Texture("PM_Emissive", "/Game/Textures/T_Emissive.T_Emissive", "Game/Textures/T_Emissive.png", "image/png", [7]),
                MaterialLinkSourceParameter.Texture("EmissiveColorTexture", "/Game/Textures/T_EmissiveColor.T_EmissiveColor", "Game/Textures/T_EmissiveColor.png", "image/png", [8]),
                MaterialLinkSourceParameter.Scalar("Roughness", .25f),
                MaterialLinkSourceParameter.Vector("Tint", .1f, .2f, .3f, 1f)
            ]);

        var parameters = Assert.Single(links.Slots).Parameters;
        Assert.Collection(parameters,
            parameter => Assert.Equal(("Ramp", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("PM_Diffuse", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("DiffuseColor", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("BaseColor", "base_color", "../Textures/T_Base.png"), (parameter.Name, parameter.Semantic, parameter.TextureValue!.Uri)),
            parameter => Assert.Equal(("PM_Normals", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("NormalMap", "normal"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("PM_Emissive", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("EmissiveColorTexture", "emissive"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("Roughness", "custom", .25f), (parameter.Name, parameter.Semantic, parameter.ScalarValue)),
            parameter =>
            {
                Assert.Equal(("Tint", "custom"), (parameter.Name, parameter.Semantic));
                Assert.Equal(new[] { .1f, .2f, .3f, 1f }, parameter.VectorValue);
            });
    }

    [Fact]
    public void Build_UsesFallbackTexturesOnlyWhenNoExactCandidateExists()
    {
        var fallbackLinks = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("Ramp", "/Game/T_Ramp.T_Ramp", "Game/T_Ramp.png", "image/png", [1]),
            MaterialLinkSourceParameter.Texture("DiffuseColor", "/Game/T_DiffuseColor.T_DiffuseColor", "Game/T_DiffuseColor.png", "image/png", [2]),
            MaterialLinkSourceParameter.Texture("PM_Diffuse", "/Game/T_Diffuse.T_Diffuse", "Game/T_Diffuse.png", "image/png", [3]),
            MaterialLinkSourceParameter.Texture("PM_Normals", "/Game/T_Normal.T_Normal", "Game/T_Normal.png", "image/png", [4]),
            MaterialLinkSourceParameter.Texture("PM_Emissive", "/Game/T_Emissive.T_Emissive", "Game/T_Emissive.png", "image/png", [5])
        ]);

        var fallbackParameters = Assert.Single(fallbackLinks.Slots).Parameters;
        Assert.Equal("base_color", fallbackParameters.Single(parameter => parameter.Name == "PM_Diffuse").Semantic);
        Assert.Equal("normal", fallbackParameters.Single(parameter => parameter.Name == "PM_Normals").Semantic);
        Assert.Equal("custom", fallbackParameters.Single(parameter => parameter.Name == "PM_Emissive").Semantic);
        Assert.All(fallbackParameters.Where(parameter => parameter.Name is not "PM_Diffuse" and not "PM_Normals"), parameter => Assert.Equal("custom", parameter.Semantic));

        var firstTextureLinks = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("Ramp", "/Game/T_Ramp.T_Ramp", "Game/T_Ramp.png", "image/png", [1]),
            MaterialLinkSourceParameter.Texture("DiffuseColor", "/Game/T_DiffuseColor.T_DiffuseColor", "Game/T_DiffuseColor.png", "image/png", [2])
        ]);

        var firstTextureParameters = Assert.Single(firstTextureLinks.Slots).Parameters;
        Assert.Equal("base_color", firstTextureParameters[0].Semantic);
        Assert.Equal("custom", firstTextureParameters[1].Semantic);
    }

    [Fact]
    public void Build_AssignsSelectedPreviewVectorRolesAndKeepsOtherVectorsCustom()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Vector("Color", .1f, .1f, .1f, 1f),
            MaterialLinkSourceParameter.Vector("BaseColor", .2f, .2f, .2f, 1f),
            MaterialLinkSourceParameter.Vector("Emissive Color", .3f, .3f, .3f, 1f),
            MaterialLinkSourceParameter.Vector("EmissiveColor", .4f, .4f, .4f, 1f),
            MaterialLinkSourceParameter.Vector("Skin Boost Color And Exponent", .5f, .6f, .7f, 8f),
            MaterialLinkSourceParameter.Vector("Tint", .8f, .8f, .8f, 1f)
        ]);

        var parameters = Assert.Single(links.Slots).Parameters;
        Assert.Collection(parameters,
            parameter => Assert.Equal(("Color", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("BaseColor", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("Emissive Color", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("EmissiveColor", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("Skin Boost Color And Exponent", "custom"), (parameter.Name, parameter.Semantic)),
            parameter => Assert.Equal(("Tint", "custom"), (parameter.Name, parameter.Semantic)));
    }

    [Fact]
    public void Build_AssignsColorFactorRolesOnlyWhenTheMatchingPreviewTextureIsUsableAndAlphaIsPositive()
    {
        var textureless = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Vector("BaseColor", .2f, .2f, .2f, 1f),
            MaterialLinkSourceParameter.Vector("Emissive Color", .3f, .3f, .3f, 1f)
        ]);
        Assert.All(Assert.Single(textureless.Slots).Parameters, parameter => Assert.Equal("custom", parameter.Semantic));
        Assert.DoesNotContain(textureless.PbrInterfaces, role => role.Role is FModelPbrRole.BaseColorFactor or FModelPbrRole.EmissiveColorFactor);

        var alphaZero = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Base.T_Base", "Game/T_Base.png", "image/png", [1]),
            MaterialLinkSourceParameter.Texture("EmissiveColorTexture", "/Game/T_Emissive.T_Emissive", "Game/T_Emissive.png", "image/png", [2]),
            MaterialLinkSourceParameter.Vector("BaseColor", .2f, .2f, .2f, 0f),
            MaterialLinkSourceParameter.Vector("Emissive Color", .3f, .3f, .3f, 0f)
        ]);
        Assert.All(Assert.Single(alphaZero.Slots).Parameters.Where(parameter => parameter.Kind == 3), parameter => Assert.Equal("custom", parameter.Semantic));
        Assert.DoesNotContain(alphaZero.PbrInterfaces, role => role.Role is FModelPbrRole.BaseColorFactor or FModelPbrRole.EmissiveColorFactor);

        var eligible = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Base.T_Base", "Game/T_Base.png", "image/png", [1]),
            MaterialLinkSourceParameter.Texture("EmissiveColorTexture", "/Game/T_Emissive.T_Emissive", "Game/T_Emissive.png", "image/png", [2]),
            MaterialLinkSourceParameter.Vector("BaseColor", .2f, .2f, .2f, 1f),
            MaterialLinkSourceParameter.Vector("Emissive Color", .3f, .3f, .3f, 1f)
        ]);
        Assert.Equal("custom", Assert.Single(eligible.Slots[0].Parameters, parameter => parameter.Name == "BaseColor" && parameter.Kind == 3).Semantic);
        Assert.Equal("custom", Assert.Single(eligible.Slots[0].Parameters, parameter => parameter.Name == "Emissive Color").Semantic);
        Assert.Contains(eligible.PbrInterfaces, role => role.Role == FModelPbrRole.BaseColorFactor);
        Assert.Contains(eligible.PbrInterfaces, role => role.Role == FModelPbrRole.EmissiveColorFactor);
    }

    [Fact]
    public void Build_AssignsOnlyWinningPreviewScalarRolesAndKeepsOtherScalarsCustom()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Scalar("RoughnessMin", .1f),
            MaterialLinkSourceParameter.Scalar("SpecRoughnessMin", .2f),
            MaterialLinkSourceParameter.Scalar("RoughnessMax", .8f),
            MaterialLinkSourceParameter.Scalar("SpecRoughnessMax", .9f),
            MaterialLinkSourceParameter.Scalar("Roughness", .4f),
            MaterialLinkSourceParameter.Scalar("emissive mult", 2f),
            MaterialLinkSourceParameter.Scalar("Emissive_Mult", 3f),
            MaterialLinkSourceParameter.Scalar("EmissiveIntensity", 4f),
            MaterialLinkSourceParameter.Scalar("EmissionIntensity", 5f),
            MaterialLinkSourceParameter.Scalar("Rough", .6f)
        ]);

        var parameters = Assert.Single(links.Slots).Parameters;
        Assert.All(parameters, parameter => Assert.Equal("custom", parameter.Semantic));
    }

    [Fact]
    public void Build_UsesEmissiveStrengthScalarsBeforeVectorFallback()
    {
        var vectorFallback = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Vector("EmissiveMultiplier", 2f, 3f, 4f, 1f),
            MaterialLinkSourceParameter.Vector("Emissive Multiplier", 5f, 6f, 7f, 1f)
        ]);
        var fallbackParameter = Assert.Single(vectorFallback.Slots[0].Parameters, parameter => parameter.Name == "Emissive Multiplier");
        Assert.Equal("custom", fallbackParameter.Semantic);
        Assert.Contains(vectorFallback.PbrInterfaces, role => role.ParameterIndex == 1 && role.Role == FModelPbrRole.EmissiveStrength);

        var scalarWins = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Scalar("EmissiveIntensity", 2f),
            MaterialLinkSourceParameter.Vector("Emissive Multiplier", 5f, 6f, 7f, 1f)
        ]);
        Assert.Equal("custom", Assert.Single(scalarWins.Slots[0].Parameters, parameter => parameter.Name == "EmissiveIntensity").Semantic);
        Assert.Equal("custom", Assert.Single(scalarWins.Slots[0].Parameters, parameter => parameter.Name == "Emissive Multiplier").Semantic);
        Assert.Contains(scalarWins.PbrInterfaces, role => role.ParameterIndex == 0 && role.Role == FModelPbrRole.EmissiveStrength);
        Assert.DoesNotContain(scalarWins.PbrInterfaces, role => role.ParameterIndex == 1 && role.Role == FModelPbrRole.EmissiveStrength);
    }

    [Fact]
    public void Build_OmitsUnavailableExternalTexturesAndTheirPreviewRoles()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Broken.T_Broken", "Game/T_Broken.png", "image/png", [], isMaterialized: false),
            MaterialLinkSourceParameter.Vector("BaseColor", .2f, .2f, .2f, 1f)
        ]);

        Assert.Single(links.Slots[0].Parameters);
        Assert.Equal("custom", links.Slots[0].Parameters[0].Semantic);
        Assert.DoesNotContain(links.PbrInterfaces, role => role.Role is FModelPbrRole.BaseColor or FModelPbrRole.BaseColorFactor);

        var successful = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Base.T_Base", "Game/T_Base.png", "image/png", [])]);
        Assert.Equal("../T_Base.png", Assert.Single(successful.Slots[0].Parameters).TextureValue!.Uri);
        Assert.Contains(successful.PbrInterfaces, role => role.Role == FModelPbrRole.BaseColor);
    }

    [Fact]
    public void Build_UsesExactAoTexturePriorityWithoutHeuristicSpecularMaskRoles()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Base.T_Base", "Game/T_Base.png", "image/png", [0]),
            MaterialLinkSourceParameter.Texture("SpecularMasks", "/Game/T_Spec.T_Spec", "Game/T_Spec.png", "image/png", [1]),
            MaterialLinkSourceParameter.Texture("AO", "/Game/T_AO.T_AO", "Game/T_AO.png", "image/png", [2]),
            MaterialLinkSourceParameter.Texture("AEM", "/Game/T_AEM.T_AEM", "Game/T_AEM.png", "image/png", [3]),
            MaterialLinkSourceParameter.Texture("M", "/Game/T_M.T_M", "Game/T_M.png", "image/png", [4])
        ]);

        var parameters = Assert.Single(links.Slots).Parameters;
        Assert.Equal("custom", parameters.Single(parameter => parameter.Name == "M").Semantic);
        Assert.Equal("base_color", parameters.Single(parameter => parameter.Name == "BaseColor").Semantic);
        Assert.All(parameters.Where(parameter => parameter.Name is not "M" and not "BaseColor"), parameter => Assert.Equal("custom", parameter.Semantic));
    }

    [Fact]
    public void Build_KeepsV1PbrOnlyValuesCustomWhileRetainingV2Roles()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Base.T_Base", "Game/T_Base.png", "image/png", [1]),
            MaterialLinkSourceParameter.Texture("NormalMap", "/Game/T_Normal.T_Normal", "Game/T_Normal.png", "image/png", [2]),
            MaterialLinkSourceParameter.Texture("EmissiveColorTexture", "/Game/T_Emissive.T_Emissive", "Game/T_Emissive.png", "image/png", [3]),
            MaterialLinkSourceParameter.Texture("M", "/Game/T_AO.T_AO", "Game/T_AO.png", "image/png", [4]),
            MaterialLinkSourceParameter.Scalar("Roughness", .4f),
            MaterialLinkSourceParameter.Scalar("emissive mult", 2f),
            MaterialLinkSourceParameter.Vector("BaseColor", .1f, .2f, .3f, 1f),
            MaterialLinkSourceParameter.Vector("Emissive Color", .4f, .5f, .6f, 1f),
            MaterialLinkSourceParameter.Vector("Skin Boost Color And Exponent", .7f, .8f, .9f, 2f)
        ]);

        var parameters = Assert.Single(links.Slots).Parameters;
        Assert.Equal("base_color", parameters[0].Semantic);
        Assert.Equal("normal", parameters[1].Semantic);
        Assert.Equal("emissive", parameters[2].Semantic);
        Assert.All(parameters[3..], parameter => Assert.Equal("custom", parameter.Semantic));
        Assert.Contains(links.PbrInterfaces, role => role.Role == FModelPbrRole.Ao);
        Assert.Contains(links.PbrInterfaces, role => role.Role == FModelPbrRole.Roughness);
        Assert.Contains(links.PbrInterfaces, role => role.Role == FModelPbrRole.EmissiveStrength);
        Assert.Contains(links.PbrInterfaces, role => role.Role == FModelPbrRole.BaseColorFactor);
        Assert.Contains(links.PbrInterfaces, role => role.Role == FModelPbrRole.EmissiveColorFactor);
        Assert.Contains(links.PbrInterfaces, role => role.Role == FModelPbrRole.AoColorBoost);
    }

    [Fact]
    public void Build_UsesRequestedUriModesAndRejectsUnsafeBundlePaths()
    {
        var parameter = MaterialLinkSourceParameter.Texture("Mask", "/Game/T.T", "Game/Textures/T.png", "image/png", [1]);
        Assert.Empty(MaterialLinkBuilder.Build(MaterialLinkMode.None, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M", [parameter]).Slots);
        Assert.Equal("../Textures/T.png", MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M", [parameter]).Slots[0].Parameters[0].TextureValue!.Uri);
        var physicalBundleRoot = Path.GetFullPath("BundleRoot");
        Assert.Equal("Game/Textures/T.png", MaterialLinkBuilder.Build(MaterialLinkMode.Bundle, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M", [parameter], physicalBundleRoot).Slots[0].Parameters[0].TextureValue!.Uri);
        Assert.Throws<ArgumentException>(() => MaterialLinkBuilder.Build(MaterialLinkMode.Bundle, "Game/Meshes/Hero.uemodel", "../escape.json", "/Game/M.M", [parameter], "Game"));
    }

    [Fact]
    public void RelativeUrisUseOwnerDirectoryAcrossProjectSpecificExportRoots()
    {
        Assert.Equal(
            "../Texture/T_Hero.png",
            MaterialLinkBuilder.BuildUri(
                MaterialLinkMode.Relative,
                "CodeWorld/Content/Characters/Hero/Model/Hero.uemodel",
                "CodeWorld/Content/Characters/Hero/Texture/T_Hero.png",
                null));
        Assert.Throws<ArgumentException>(() => MaterialLinkBuilder.BuildUri(
            MaterialLinkMode.Relative,
            "CodeWorld/Content/Characters/Hero/Model/Hero.uemodel",
            "CodeWorld/Content/Characters/Hero/Texture/T_Hero:alternate.png",
            null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BundleRoot")]
    public void BundlePublicBuilders_RejectInvalidPhysicalBundleRoot(string? bundleRoot)
    {
        var parameter = MaterialLinkSourceParameter.Texture("Mask", "/Game/T.T", "Game/Textures/T.png", "image/png", [1]);

        Assert.Throws<ArgumentException>(() => MaterialLinkBuilder.BuildUri(
            MaterialLinkMode.Bundle, "Game/Meshes/Hero.uemodel", "Game/Textures/T.png", bundleRoot));
        Assert.Throws<ArgumentException>(() => MaterialLinkBuilder.Build(
            MaterialLinkMode.Bundle, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M", [parameter], bundleRoot,
            finalUris: true));
    }

    [Fact]
    public void BundlePublicBuilders_AcceptAbsolutePhysicalBundleRootAndKeepLogicalTargetRootRelative()
    {
        var bundleRoot = Path.GetFullPath("BundleRoot");
        var parameter = MaterialLinkSourceParameter.Texture("Mask", "/Game/T.T", "Game/Textures/T.png", "image/png", [1]);

        Assert.Equal("Game/Textures/T.png", MaterialLinkBuilder.BuildUri(
            MaterialLinkMode.Bundle, "Game/Meshes/Hero.uemodel", "Game/Textures/T.png", bundleRoot));
        Assert.Equal("Game/Materials/M.json", Assert.Single(MaterialLinkBuilder.Build(
            MaterialLinkMode.Bundle, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M", [parameter], bundleRoot,
            finalUris: true).Slots).MaterialJsonUri);
    }

    [Fact]
    public void CategorizedBundleLayout_UsesRootRelativeModelTextureAndMaterialPaths()
    {
        Assert.Equal("Models/Hero.uemodel", MaterialLinkBuilder.BuildModelPath(
            MaterialExportLayout.Categorized, "Game/Meshes/Hero.uemodel"));
        Assert.Equal("Textures/T_Body.png", MaterialLinkBuilder.BuildResourcePath(
            MaterialExportLayout.Categorized, "Game/Textures/T_Body.png", MaterialExportResource.Texture));
        Assert.Equal("Materials/M_Body.json", MaterialLinkBuilder.BuildResourcePath(
            MaterialExportLayout.Categorized, "Game/Materials/M_Body.json", MaterialExportResource.Material));
        Assert.Equal("Textures/T_Body.png", MaterialLinkBuilder.BuildUri(
            MaterialLinkMode.Bundle, "Models/Hero.uemodel", "Textures/T_Body.png", Path.GetFullPath("BundleRoot"),
            MaterialExportLayout.Categorized));
    }

    [Fact]
    public void CategorizedBundleLayout_UsesStableCollisionSafeNamesForDistinctIdentities()
    {
        var heroTexture = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Hero/T_Base.png", MaterialExportResource.Texture, "/Game/Hero/T_Base.T_Base");
        var weaponTexture = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Weapon/T_Base.png", MaterialExportResource.Texture, "/Game/Weapon/T_Base.T_Base");
        var heroMaterial = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Hero/M_Base.json", MaterialExportResource.Material, "/Game/Hero/M_Base");
        var weaponMaterial = MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Weapon/M_Base.json", MaterialExportResource.Material, "/Game/Weapon/M_Base");

        Assert.NotEqual(heroTexture, weaponTexture);
        Assert.NotEqual(heroMaterial, weaponMaterial);
        Assert.StartsWith("Textures/T_Base-", heroTexture);
        Assert.StartsWith("Materials/M_Base-", heroMaterial);
        Assert.Equal(heroTexture, MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized,
            "Game/Hero/T_Base.png", MaterialExportResource.Texture, "/Game/Hero/T_Base.T_Base"));
    }

    [Fact]
    public void CategorizedBundleLayout_UsesCanonicalIdentityForCollisionSafeModelNames()
    {
        var heroModel = MaterialLinkBuilder.BuildModelPath(MaterialExportLayout.Categorized,
            "Game/Hero/Chair.uemodel", "/Game/Hero/Chair.Chair");
        var lobbyModel = MaterialLinkBuilder.BuildModelPath(MaterialExportLayout.Categorized,
            "Game/Lobby/Chair.uemodel", "/Game/Lobby/Chair.Chair");

        Assert.NotEqual(heroModel, lobbyModel);
        Assert.Matches("^Models/Chair-[0-9a-f]{64}\\.uemodel$", heroModel);
        Assert.Matches("^Models/Chair-[0-9a-f]{64}\\.uemodel$", lobbyModel);
    }

    [Fact]
    public void Build_EmbeddedSkipsTexturesWithoutBytesAndWriterDeduplicatesRemainingBytes()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Embedded, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("Unsupported", "/Game/T_Empty.T_Empty", "Game/T_Empty.png", "image/png", []),
            MaterialLinkSourceParameter.Texture("A", "/Game/T_A.T_A", "Game/T_A.png", "image/png", [1, 2]),
            MaterialLinkSourceParameter.Texture("B", "/Game/T_A.T_A", "Game/T_A.png", "image/png", [1, 2])
        ]);

        var parameters = Assert.Single(links.Slots).Parameters;
        Assert.Equal(["A", "B"], parameters.Select(parameter => parameter.Name));
        Assert.Equal("base_color", parameters[0].Semantic);
        Assert.Equal("custom", parameters[1].Semantic);
        var archive = new CUE4Parse.UE4.Writers.FArchiveWriter();
        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Embedded, links);
        Assert.NotEmpty(archive.GetBuffer());
    }

    [Fact]
    public void Build_DerivesIndependentPbrRolesWithPreviewPriorities()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Base.T_Base", "Game/T_Base.png", "image/png", [1]),
            MaterialLinkSourceParameter.Texture("M", "/Game/T_BlackMask.T_BlackMask", "Game/T_BlackMask.png", "image/png", [2]),
            MaterialLinkSourceParameter.Texture("AO", "/Game/T_AO.T_AO", "Game/T_AO.png", "image/png", [3]),
            MaterialLinkSourceParameter.Vector("Color", .1f, .2f, .3f, 1f),
            MaterialLinkSourceParameter.Vector("Skin Boost Color And Exponent", .4f, .5f, .6f, 2f),
            MaterialLinkSourceParameter.Scalar("RoughnessMin", .1f),
            MaterialLinkSourceParameter.Scalar("RoughnessMax", .9f),
            MaterialLinkSourceParameter.Scalar("Roughness", .4f)
        ]);

        Assert.Equal(
        [
            (0, 0, FModelPbrRole.BaseColor),
            (0, 3, FModelPbrRole.BaseColorFactor),
            (0, 5, FModelPbrRole.RoughnessMin),
             (0, 6, FModelPbrRole.RoughnessMax),
             (0, 7, FModelPbrRole.Roughness)
        ],
        links.PbrInterfaces.Select(role => (role.SlotIndex, role.ParameterIndex, role.Role)));
    }

    [Fact]
    public void Build_AddsSkinBoostOnlyForUsableAoAndKeepsDuplicateRoles()
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("M", "/Game/T_AO.T_AO", "Game/T_AO.png", "image/png", [1]),
            MaterialLinkSourceParameter.Vector("Skin Boost Color And Exponent", .4f, .5f, .6f, 2f),
            MaterialLinkSourceParameter.Vector("Color", .1f, .2f, .3f, 1f)
        ]);

        Assert.Equal(
        [
            (0, 0, FModelPbrRole.BaseColor),
            (0, 2, FModelPbrRole.BaseColorFactor),
             (0, 0, FModelPbrRole.Ao),
             (0, 1, FModelPbrRole.AoColorBoost),
             (0, 1, FModelPbrRole.AoStrength)
         ],
         links.PbrInterfaces.Select(role => (role.SlotIndex, role.ParameterIndex, role.Role)));
    }

    [Theory]
    [InlineData("/Game/Foo/T_BlackMask")]
    [InlineData("T_BlackMask")]
    [InlineData("Texture2D'/Game/Foo/T_BlackMask.T_BlackMask'")]
    public void Build_RejectsBlackMaskAoByUnrealObjectBasename(string unrealTexturePath)
    {
        var links = MaterialLinkBuilder.Build(MaterialLinkMode.Relative, "Game/Meshes/Hero.uemodel", "Game/Materials/M.json", "/Game/M.M",
        [
            MaterialLinkSourceParameter.Texture("M", unrealTexturePath, "Game/T_BlackMask.png", "image/png", [1]),
            MaterialLinkSourceParameter.Vector("Skin Boost Color And Exponent", .4f, .5f, .6f, 2f)
        ]);

        Assert.DoesNotContain(links.PbrInterfaces, role => role.Role is FModelPbrRole.Ao or FModelPbrRole.AoColorBoost or FModelPbrRole.AoStrength);
    }

}
