using CUE4Parse_Conversion;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using FModel.Settings;
using FModel.ViewModels;

namespace CUE4Parse.Tests;

[Collection(UserSettingsTestCollection.Name)]
public class MaterialLinkSettingsTests
{
    [Fact]
    public void UserSettings_DefaultsAndExportOptions_UseRelativeLinksWithNoBundleRoot()
    {
        var original = UserSettings.Default;
        try
        {
            var settings = new UserSettings
            {
                CurrentDir = DirectorySettings.Default("Test", "Test")
            };
            UserSettings.Default = settings;

            Assert.Equal(MaterialLinkMode.Relative, settings.MaterialLinkMode);
            Assert.Equal(string.Empty, settings.MaterialLinkBundleRoot);
            Assert.Equal(MaterialLinkMode.Relative, settings.ExportOptions.MaterialLinkMode);
            Assert.Equal(string.Empty, settings.ExportOptions.MaterialLinkBundleRoot);
        }
        finally
        {
            UserSettings.Default = original;
        }
    }

    [Fact]
    public void ExporterOptions_Defaults_KeepMaterialLinksDisabled()
    {
        var options = new ExporterOptions();

        Assert.Equal(MaterialLinkMode.None, options.MaterialLinkMode);
        Assert.Null(options.MaterialLinkBundleRoot);
    }

    [Fact]
    public void ExportOptions_BundleRootIsTheSelectedAbsolutePhysicalRoot()
    {
        var original = UserSettings.Default;
        try
        {
            var bundleRoot = Path.GetFullPath(Path.Combine("Exports", "Bundle"));
            UserSettings.Default = new UserSettings
            {
                MaterialLinkMode = MaterialLinkMode.Bundle,
                MaterialLinkBundleRoot = bundleRoot,
                CurrentDir = DirectorySettings.Default("Test", "Test")
            };

            Assert.Equal(bundleRoot, UserSettings.Default.ExportOptions.MaterialLinkBundleRoot);
        }
        finally
        {
            UserSettings.Default = original;
        }
    }

    [Fact]
    public void SettingsViewModel_CancelAndSave_RespectMaterialLinkSnapshot()
    {
        var original = UserSettings.Default;
        try
        {
            UserSettings.Default = new UserSettings
            {
                MaterialLinkMode = MaterialLinkMode.Relative,
                MaterialLinkBundleRoot = "Original",
                CurrentDir = DirectorySettings.Default("Test", "Test")
            };

            var settingsView = new SettingsViewModel();
            settingsView.Initialize();
            settingsView.SelectedMaterialLinkMode = MaterialLinkMode.Bundle;
            settingsView.MaterialLinkBundleRoot = "Changed";

            Assert.Equal(MaterialLinkMode.Relative, UserSettings.Default.MaterialLinkMode);
            Assert.Equal("Original", UserSettings.Default.MaterialLinkBundleRoot);

            settingsView.Initialize();
            Assert.Equal(MaterialLinkMode.Relative, settingsView.SelectedMaterialLinkMode);
            Assert.Equal("Original", settingsView.MaterialLinkBundleRoot);

            settingsView.SelectedMaterialLinkMode = MaterialLinkMode.Embedded;
            settingsView.MaterialLinkBundleRoot = "Applied";
            settingsView.Save(out _);

            Assert.Equal(MaterialLinkMode.Embedded, UserSettings.Default.MaterialLinkMode);
            Assert.Equal("Applied", UserSettings.Default.MaterialLinkBundleRoot);
        }
        finally
        {
            UserSettings.Default = original;
        }
    }

    [Fact]
    public void UESceneDirectory_DefaultsUnderOutputAndSettingsSnapshotRespectsCancelAndSave()
    {
        var original = UserSettings.Default;
        try
        {
            UserSettings.Default = new UserSettings
            {
                OutputDirectory = "Output",
                UESceneDirectory = "Original",
                CurrentDir = DirectorySettings.Default("Test", "Test")
            };
            var settingsView = new SettingsViewModel();
            settingsView.Initialize();
            settingsView.UESceneDirectory = "Changed";

            Assert.Equal("Original", UserSettings.Default.UESceneDirectory);

            settingsView.Initialize();
            Assert.Equal("Original", settingsView.UESceneDirectory);

            settingsView.UESceneDirectory = "Applied";
            settingsView.Save(out _);

            Assert.Equal("Applied", UserSettings.Default.UESceneDirectory);
            Assert.Equal(Path.Combine("Output", "UEScene"), new UserSettings { OutputDirectory = "Output" }.UESceneDirectory);
        }
        finally
        {
            UserSettings.Default = original;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/Bundles")]
    public void SettingsViewModel_SaveRejectsInvalidBundleRoot(string bundleRoot)
    {
        var original = UserSettings.Default;
        try
        {
            UserSettings.Default = new UserSettings
            {
                MaterialLinkMode = MaterialLinkMode.Relative,
                MaterialLinkBundleRoot = "Original",
                CurrentDir = DirectorySettings.Default("Test", "Test")
            };
            var settingsView = new SettingsViewModel();
            settingsView.Initialize();
            settingsView.SelectedMaterialLinkMode = MaterialLinkMode.Bundle;
            settingsView.MaterialLinkBundleRoot = bundleRoot;

            Assert.False(settingsView.Save(out _, out var error));
            Assert.NotNull(error);
            Assert.Equal(MaterialLinkMode.Relative, UserSettings.Default.MaterialLinkMode);
            Assert.Equal("Original", UserSettings.Default.MaterialLinkBundleRoot);
        }
        finally
        {
            UserSettings.Default = original;
        }
    }
}
