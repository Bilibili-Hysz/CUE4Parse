using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using FModel.Settings;
using FModel.Views.Snooper.Models;

namespace CUE4Parse.Tests;

[Collection(UserSettingsTestCollection.Name)]
public class UModelSaveTests
{
    [Fact]
    public void Save_InvalidBundleRoot_ReturnsFalseWithDiagnostic()
    {
        var previousMeshFormat = UserSettings.Default.MeshExportFormat;
        var previousLinkMode = UserSettings.Default.MaterialLinkMode;
        var previousBundleRoot = UserSettings.Default.MaterialLinkBundleRoot;
        var previousCurrentDir = UserSettings.Default.CurrentDir;
        try
        {
            UserSettings.Default.CurrentDir = DirectorySettings.Default("Test", "Test");
            UserSettings.Default.MeshExportFormat = EMeshFormat.UEFormat;
            UserSettings.Default.MaterialLinkMode = MaterialLinkMode.Bundle;
            UserSettings.Default.MaterialLinkBundleRoot = "relative-root";

            var result = new TestModel().Save(out var label, out var savedPath);

            Assert.False(result);
            Assert.Contains("absolute physical bundle root", label, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(savedPath);
        }
        finally
        {
            UserSettings.Default.MeshExportFormat = previousMeshFormat;
            UserSettings.Default.MaterialLinkMode = previousLinkMode;
            UserSettings.Default.MaterialLinkBundleRoot = previousBundleRoot;
            UserSettings.Default.CurrentDir = previousCurrentDir;
        }
    }

    private sealed class TestModel : UModel
    {
        public TestModel() : base(new UStaticMesh { Name = "TestMesh" }) { }
    }
}
