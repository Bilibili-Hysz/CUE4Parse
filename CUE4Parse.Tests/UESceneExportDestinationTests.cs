using CUE4Parse_Conversion.UEScene;

namespace CUE4Parse.Tests;

public class UESceneExportDestinationTests
{
    [Fact]
    public void DefaultStrategyIsSelfContained()
    {
        Assert.Equal(UESceneExportStrategy.SelfContained, default(UESceneExportStrategy));
    }

    [Fact]
    public void SelfContainedUsesCanonicalMapPathBelowUescenes()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/City.umap");

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Uescenes", "Content", "Maps", "City", "City"), destination.ScenePhysicalRoot);
        Assert.Equal("City.uescene", destination.SceneFileName);
        Assert.Equal(destination.ScenePhysicalRoot, destination.ResourcePhysicalRoot);
        Assert.Equal(UESceneResourceLogicalRoot.SceneBundle, destination.ResourceLogicalRoot);
    }

    [Fact]
    public void SelfContainedCanonicalizesUppercaseUmapExtension()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/City.UMAP");

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Uescenes", "Content", "Maps", "City", "City"), destination.ScenePhysicalRoot);
        Assert.Equal("City.uescene", destination.SceneFileName);
    }

    [Fact]
    public void ReuseGameExportTreeWritesSceneBesideUmapAndUsesGameRootForResources()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/City.umap", UESceneExportStrategy.ReuseGameExportTree);

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Content", "Maps", "City"), destination.ScenePhysicalRoot);
        Assert.Equal("City.umap.uescene", destination.SceneFileName);
        Assert.Equal(@"D:\Exports\Game", destination.ResourcePhysicalRoot);
        Assert.Equal(UESceneResourceLogicalRoot.GameExportTree, destination.ResourceLogicalRoot);
    }

    [Fact]
    public void ReuseGameExportTreeCanonicalizesUppercaseUmapExtension()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/City.UMAP", UESceneExportStrategy.ReuseGameExportTree);

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Content", "Maps", "City"), destination.ScenePhysicalRoot);
        Assert.Equal("City.umap.uescene", destination.SceneFileName);
    }

    [Fact]
    public void SameBasenameInDifferentParentsGetsDistinctSelfContainedRoots()
    {
        var city = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/Shared.umap");
        var forest = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/Forest/Shared.umap");

        Assert.NotEqual(city.ScenePhysicalRoot, forest.ScenePhysicalRoot);
    }

    [Fact]
    public void NormalizesSafeBackslashSeparatedCanonicalPath()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", @"Content\Maps\City\City.umap");

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Uescenes", "Content", "Maps", "City", "City"), destination.ScenePhysicalRoot);
    }

    [Theory]
    [InlineData("Content/Maps/../City/City.umap")]
    [InlineData("Content\\Maps\\..\\City\\City.umap")]
    [InlineData("../Content/Maps/City/City.umap")]
    [InlineData("D:/Exports/Content/Maps/City/City.umap")]
    [InlineData("/Content/Maps/City/City.umap")]
    [InlineData("Game/Maps/City/City.umap")]
    [InlineData("Content/Maps/City/City.uasset")]
    public void RejectsUnsafeOrNonUmapCanonicalPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => UESceneExportDestination.Allocate(@"D:\Exports\Game", path));
    }

    [Theory]
    [InlineData("Exports/Game")]
    [InlineData("")]
    public void RejectsNonAbsoluteGameExportRoots(string root)
    {
        Assert.Throws<ArgumentException>(() => UESceneExportDestination.Allocate(root, "Content/Maps/City/City.umap"));
    }
}
