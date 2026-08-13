using CUE4Parse_Conversion.PoseAsset;

namespace CUE4Parse.Tests;

public class PoseAssetExportTests
{
    [Fact]
    public void PoseAsset_DirectoryWriteCompletesBeforeReportingSuccess()
    {
        var temporary = Directory.CreateTempSubdirectory();
        try
        {
            var exporter = new PoseAsset("Game/Content/Poses/P_Hero.uepose", [0x55, 0x45, 0x50, 0x4F, 0x53, 0x45]);

            var saved = exporter.TryWriteToDir(temporary, out var label, out var savedPath);

            Assert.True(saved);
            Assert.Equal("P_Hero.uepose", label);
            Assert.True(File.Exists(savedPath));
            Assert.Equal([0x55, 0x45, 0x50, 0x4F, 0x53, 0x45], File.ReadAllBytes(savedPath));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }
}
