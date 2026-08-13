using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;

namespace CUE4Parse.Tests;

public class BundleOutputDirectoryTests
{
    [Theory]
    [InlineData(EMeshFormat.UEFormat, true)]
    [InlineData(EMeshFormat.ActorX, false)]
    public void BundleRootIsUsedOnlyForUeFormatMeshes(EMeshFormat meshFormat, bool expected)
    {
        var options = new ExporterOptions
        {
            MaterialLinkMode = MaterialLinkMode.Bundle,
            MaterialLinkBundleRoot = Path.GetFullPath("BundleRoot"),
            MeshFormat = meshFormat
        };

        Assert.Equal(expected, Exporter.UsesBundleOutputRoot(typeof(UStaticMesh), options));
        Assert.Equal(expected, Exporter.UsesBundleOutputRoot(typeof(USkeletalMesh), options));
    }

    [Fact]
    public void BundleRootIsNotUsedForNonMeshExports()
    {
        var options = new ExporterOptions
        {
            MaterialLinkMode = MaterialLinkMode.Bundle,
            MeshFormat = EMeshFormat.UEFormat
        };

        Assert.False(Exporter.UsesBundleOutputRoot(typeof(UAnimSequence), options));
        Assert.False(Exporter.UsesBundleOutputRoot(typeof(ALandscapeProxy), options));
    }

    [Fact]
    public void ActorXBundle_DoesNotValidateOrUseTheUeFormatBundleRoot()
    {
        var options = new ExporterOptions
        {
            MaterialLinkMode = MaterialLinkMode.Bundle,
            MeshFormat = EMeshFormat.ActorX,
            MaterialLinkBundleRoot = string.Empty
        };

        Assert.False(Exporter.UsesBundleOutputRoot(typeof(UStaticMesh), options));
        Assert.False(Exporter.UsesBundleOutputRoot(typeof(USkeletalMesh), options));
    }
}
