using CUE4Parse_Conversion.UEScene;
using FModel.Settings;
using FModel.ViewModels;

namespace CUE4Parse.Tests;

public class UESceneFModelAllocationTests
{
    [Fact]
    public void UserSettingsDefaultsToGameExportTreeUESceneExport()
    {
        Assert.Equal(UESceneExportStrategy.ReuseGameExportTree, new UserSettings().UESceneExportStrategy);
    }

    [Fact]
    public void AllocatorUsesGameRootAndStripsOnlyProjectPrefixAfterFixPath()
    {
        var destination = CUE4ParseViewModel.AllocateUESceneDestination(new DummyFileProvider(), "/Game/Maps/City/City.umap",
            @"D:\Exports", UESceneExportStrategy.SelfContained);

        Assert.Equal(Path.Combine(@"D:\Exports", "ProjectName", "Content", "Maps", "City"), destination.ScenePhysicalRoot);
        Assert.Equal(Path.Combine(destination.ScenePhysicalRoot, "City.umap.uescene"), destination.ScenePhysicalPath);
    }

    [Fact]
    public void AllocatorRejectsNonProjectFixedPath()
    {
        Assert.Throws<ArgumentException>(() => CUE4ParseViewModel.AllocateUESceneDestination(new DummyFileProvider(), "Engine/Content/Maps/City.umap",
            @"D:\Exports", UESceneExportStrategy.SelfContained));
    }

    [Fact]
    public void CanonicalProjectExportUriUsesContentAsTheStableRootForDefaultAndCustomProjects()
    {
        Assert.Equal("Content/Meshes/Chair/Chair.uemodel",
            UESceneProjectExportUri.FromFixedPath("ProjectName/Content/Meshes/Chair/Chair.uasset", "ProjectName", ".uemodel"));
        Assert.Equal("Content/Meshes/Chair/Chair.uemodel",
            UESceneProjectExportUri.FromFixedPath("CodeWorld/Content/Meshes/Chair/Chair.uasset", "CodeWorld", ".uemodel"));
        Assert.Equal("Content/Meshes/Chair/Chair.uemodel",
            UESceneProjectExportUri.FromFixedPath("CodeWorld/Content/Meshes/Chair/Chair.uasset", null, ".uemodel"));
        Assert.Equal("Content/Meshes/Chair/Chair.uemodel",
            UESceneProjectExportUri.FromFixedPath("Content/Meshes/Chair/Chair.uasset", null, ".uemodel"));
    }

    [Fact]
    public void CanonicalProjectExportUriMapsCustomMountContentPathsToTheSameLogicalRoot()
    {
        Assert.Equal("Content/Characters/Hero/Lynn.uemodel",
            UESceneProjectExportUri.FromUnrealPath("/CodeWorld/Content/Characters/Hero/Lynn.Lynn", ".uemodel"));
    }

    [Theory]
    [InlineData("/Game//Meshes/Chair.Chair")]
    [InlineData("/Game/./Meshes/Chair.Chair")]
    [InlineData("/Game/Meshes/../Chair.Chair")]
    [InlineData("\\Game\\Meshes\\Chair.Chair")]
    public void CanonicalProjectExportUriRejectsUnsafeUnrealPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => UESceneProjectExportUri.FromUnrealPath(path, ".uemodel"));
    }

    [Theory]
    [InlineData("CodeWorld/Characters/Chair.uasset")]
    [InlineData("CodeWorld/Maps/City.umap")]
    [InlineData("../CodeWorld/Content/Meshes/Chair/Chair.uasset")]
    public void CanonicalProjectExportUriRejectsPathsWithoutASafeContentRoot(string path)
    {
        Assert.Throws<ArgumentException>(() => UESceneProjectExportUri.FromFixedPath(path, "CodeWorld", ".uemodel"));
    }
}
