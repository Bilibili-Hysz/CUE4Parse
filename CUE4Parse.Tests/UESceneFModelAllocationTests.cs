using CUE4Parse_Conversion.UEScene;
using FModel.Settings;
using FModel.ViewModels;

namespace CUE4Parse.Tests;

public class UESceneFModelAllocationTests
{
    [Fact]
    public void UserSettingsDefaultsToSelfContainedUESceneExport()
    {
        Assert.Equal(UESceneExportStrategy.SelfContained, new UserSettings().UESceneExportStrategy);
    }

    [Fact]
    public void AllocatorUsesGameRootAndStripsOnlyProjectPrefixAfterFixPath()
    {
        var destination = CUE4ParseViewModel.AllocateUESceneDestination(new DummyFileProvider(), "/Game/Maps/City/City.umap",
            @"D:\Exports", UESceneExportStrategy.SelfContained);

        Assert.Equal(Path.Combine(@"D:\Exports", "ProjectName", "Uescenes", "Content", "Maps", "City", "City"), destination.ScenePhysicalRoot);
        Assert.Equal(Path.Combine(destination.ScenePhysicalRoot, "City.uescene"), destination.ScenePhysicalPath);
    }

    [Fact]
    public void AllocatorRejectsNonProjectFixedPath()
    {
        Assert.Throws<ArgumentException>(() => CUE4ParseViewModel.AllocateUESceneDestination(new DummyFileProvider(), "Engine/Content/Maps/City.umap",
            @"D:\Exports", UESceneExportStrategy.SelfContained));
    }

    [Fact]
    public void CanonicalProjectExportUriRequiresTheExactProviderProjectPrefix()
    {
        Assert.Equal("Content/Meshes/Chair/Chair.uemodel",
            UESceneProjectExportUri.FromFixedPath("ProjectName/Content/Meshes/Chair/Chair.uasset", "ProjectName", ".uemodel"));
        Assert.Throws<ArgumentException>(() => UESceneProjectExportUri.FromFixedPath(
            "DifferentProject/Content/Meshes/Chair/Chair.uasset", "ProjectName", ".uemodel"));
    }
}
