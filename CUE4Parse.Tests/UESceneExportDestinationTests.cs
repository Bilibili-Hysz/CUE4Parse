using CUE4Parse_Conversion.UEScene;

namespace CUE4Parse.Tests;

public class UESceneExportDestinationTests
{
    [Fact]
    public void DefaultStrategyIsGameExportTree()
    {
        Assert.Equal(UESceneExportStrategy.ReuseGameExportTree, UESceneExportDestination.NormalizeStrategy(default(UESceneExportStrategy)));
    }

    [Fact]
    public void DefaultUsesCanonicalMapPathBesideUmap()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/City.umap");

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Content", "Maps", "City"), destination.ScenePhysicalRoot);
        Assert.Equal("City.umap.uescene", destination.SceneFileName);
        Assert.Equal(@"D:\Exports\Game", destination.ResourcePhysicalRoot);
        Assert.Equal(UESceneResourceLogicalRoot.GameExportTree, destination.ResourceLogicalRoot);
    }

    [Fact]
    public void DefaultCanonicalizesUppercaseUmapExtension()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/City.UMAP");

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Content", "Maps", "City"), destination.ScenePhysicalRoot);
        Assert.Equal("City.umap.uescene", destination.SceneFileName);
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
    public void SameBasenameInDifferentParentsGetsDistinctGameTreeRoots()
    {
        var city = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/City/Shared.umap");
        var forest = UESceneExportDestination.Allocate(@"D:\Exports\Game", "Content/Maps/Forest/Shared.umap");

        Assert.NotEqual(city.ScenePhysicalRoot, forest.ScenePhysicalRoot);
    }

    [Fact]
    public void NormalizesSafeBackslashSeparatedCanonicalPath()
    {
        var destination = UESceneExportDestination.Allocate(@"D:\Exports\Game", @"Content\Maps\City\City.umap");

        Assert.Equal(Path.Combine(@"D:\Exports\Game", "Content", "Maps", "City"), destination.ScenePhysicalRoot);
    }

    [Fact]
    public void RejectsLegacyScenePrivatePlacement()
    {
        using var temp = new TestDirectory();

        Assert.Throws<InvalidDataException>(() => UESceneExportDestination.ValidateCanonicalPlacement(
            temp.Directory.FullName, Path.Combine(temp.Directory.FullName, "Level.uescene")));
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

    private sealed class TestDirectory : IDisposable
    {
        public DirectoryInfo Directory { get; } = new(Path.Combine(Path.GetTempPath(), "CUE4Parse.Tests", Path.GetRandomFileName()));
        public TestDirectory() => Directory.Create();
        public void Dispose() { if (Directory.Exists) Directory.Delete(true); }
    }
}
