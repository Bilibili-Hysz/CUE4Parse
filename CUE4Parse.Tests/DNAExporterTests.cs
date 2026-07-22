using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.DNA;

namespace CUE4Parse.Tests;

public class DNAExporterTests
{
    [Fact]
    public void TryWriteToDir_CompletesSidecarBeforeReturning()
    {
        using var tempDir = new TemporaryDirectory();
        var asset = new UDNAAsset
        {
            Name = "TestDna",
            DNAData = new Lazy<byte[]>(() => [0x44, 0x4E, 0x41])
        };

        var result = new DNAExporter(asset, new ExporterOptions()).TryWriteToDir(tempDir.Directory, out _, out var savedPath);

        Assert.True(result);
        Assert.True(File.Exists(savedPath));
        Assert.Equal([0x44, 0x4E, 0x41], File.ReadAllBytes(savedPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory();
        public void Dispose() => Directory.Delete(true);
    }
}
