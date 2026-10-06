using CUE4Parse.GameTypes.Nascar.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Houdini;
using CUE4Parse_Conversion;

namespace CUE4Parse.Tests;

public class HoudiniExportQueueTests
{
    [Fact]
    public void NewMeshTypesCanQueueWithoutDecodingTheirEmptyGeometry()
    {
        using var session = new ExportSession();
        session.Add(new UHoudiniStaticMesh { Name = "HoudiniMesh" });
        session.Add(new UHoudiniAsset { Name = "HoudiniAsset" });
        session.Add(new UIRMesh { Name = "IRMesh" });
        Assert.Equal(3, session.TotalQueued);
        Assert.False(session.IsRunning);
    }
}
