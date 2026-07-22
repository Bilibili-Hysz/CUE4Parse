using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.ViewModels;
using FModel.ViewModels.Commands;

namespace CUE4Parse.Tests;

public class UESceneEligibilityTests
{
    [Fact]
    public void NonWorldPackageIsRejected()
    {
        var texture = new FakeGameFile("/Game/Textures/T_Logo.uasset");

        Assert.False(UESceneEligibility.IsEligible(texture));
        Assert.False(new TabItem(texture, string.Empty).TabCommand.CanExecute("Save_UEScene_Bundle"));
    }

    [Fact]
    public void UWorldPackageIsEligible()
    {
        var world = new FakeGameFile("/Game/Maps/Arena.umap");

        Assert.True(UESceneEligibility.IsEligible(world));
        Assert.True(new TabItem(world, string.Empty).TabCommand.CanExecute("Save_UEScene_Bundle"));
    }

    [Fact]
    public void RightClickSelectionFiltersNonWorldEntries()
    {
        var world = new FakeGameFile("/Game/Maps/Arena.umap");
        var texture = new FakeGameFile("/Game/Textures/T_Logo.uasset");

        var eligible = UESceneEligibility.FilterEligible([texture, world]);

        Assert.Equal([world], eligible);
    }

    [Fact]
    public void SaveModelsRemainsEnabledForNonWorldPackage()
    {
        var mesh = new FakeGameFile("/Game/Meshes/Chair.uasset");

        Assert.True(new TabItem(mesh, string.Empty).TabCommand.CanExecute("Save_Models"));
    }
}
