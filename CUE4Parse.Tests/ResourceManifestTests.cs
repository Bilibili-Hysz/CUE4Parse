using CUE4Parse_Conversion.UEFormat;

namespace CUE4Parse.Tests;

public class ResourceManifestTests
{
    [Fact]
    public void AddOrGet_DeduplicatesIdenticalResourceDeclaration()
    {
        var manifest = new ResourceManifest();
        var first = manifest.AddOrGet("/Game/T_A.T_A", "Textures/T_A.png", "image/png", [1, 2, 3], true);
        var second = manifest.AddOrGet("/Game/T_A.T_A", "Textures/T_A.png", "image/png", [1, 2, 3], true);

        Assert.Same(first, second);
        var single = Assert.Single(manifest.Resources);
        Assert.Equal<byte>([1, 2, 3], single.Bytes);
    }

    [Theory]
    [InlineData("Textures/T_B.png", "image/png", true, "Textures/T_A.png")]
    [InlineData("Textures/T_A.png", "image/jpeg", true, "Textures/T_A.png")]
    [InlineData("Textures/T_A.png", "image/png", false, "Textures/T_A.png")]
    [InlineData("Textures/T_A.png", "image/png", true, "Textures/other.png")]
    public void AddOrGet_RejectsConflictingResourceDeclaration(
        string uri, string mime, bool isExternal, string physicalTarget)
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T_A.T_A", "Textures/T_A.png", "image/png", [1, 2, 3], true, "Textures/T_A.png");

        Assert.Throws<InvalidDataException>(() => manifest.AddOrGet(
            "/Game/T_A.T_A", uri, mime, [1, 2, 3], isExternal, physicalTarget));
    }

    [Fact]
    public void AddOrGet_RejectsConflictingResourceBytes()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T_A.T_A", "Textures/T_A.png", "image/png", [1, 2, 3], true);

        Assert.Throws<InvalidDataException>(() => manifest.AddOrGet(
            "/Game/T_A.T_A", "Textures/T_A.png", "image/png", [4, 5, 6], true));
    }

    [Fact]
    public void AddOrGet_RejectsDifferentIdentityClaimingExistingUri()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T_A.T_A", "Textures/shared.png", "image/png", [1], true);

        Assert.Throws<InvalidDataException>(() => manifest.AddOrGet(
            "/Game/T_B.T_B", "Textures/shared.png", "image/png", [2], true));
        Assert.Single(manifest.Resources);
    }

    [Fact]
    public void AddOrGet_RejectsDifferentIdentityClaimingExistingPhysicalTarget()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T_A.T_A", "Textures/T_A.png", "image/png", [1], true, "staging/shared.png");

        Assert.Throws<InvalidDataException>(() => manifest.AddOrGet(
            "/Game/T_B.T_B", "Textures/T_B.png", "image/png", [2], true, "staging/shared.png"));
        Assert.Single(manifest.Resources);
    }

    [Fact]
    public void AllWritten_ReturnsTrueOnlyWhenAllSucceed()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T_A.T_A", "T_A.png", "image/png", [1], true);
        manifest.AddOrGet("/Game/T_B.T_B", "T_B.png", "image/png", [2], true);

        Assert.False(manifest.AllWritten);

        manifest.MarkWritten("/Game/T_A.T_A");
        Assert.False(manifest.AllWritten);

        manifest.MarkWritten("/Game/T_B.T_B");
        Assert.True(manifest.AllWritten);
    }

    [Fact]
    public void HasFailures_DetectsAnyFailure()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T_A.T_A", "T_A.png", "image/png", [1], true);
        manifest.AddOrGet("/Game/T_B.T_B", "T_B.png", "image/png", [2], true);

        manifest.MarkFailed("/Game/T_A.T_A");
        Assert.True(manifest.HasFailures);
        Assert.False(manifest.AllWritten);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MarkStatus_RejectsUnknownResourceIdentity(bool written)
    {
        var manifest = new ResourceManifest();

        Assert.Throws<KeyNotFoundException>(() =>
        {
            if (written)
                manifest.MarkWritten("/Game/Missing.Missing");
            else
                manifest.MarkFailed("/Game/Missing.Missing");
        });
    }

    [Fact]
    public void Resources_AreIndexedSequentially()
    {
        var manifest = new ResourceManifest();
        var a = manifest.AddOrGet("/Game/A", "A.png", "image/png", [1], true);
        var b = manifest.AddOrGet("/Game/B", "B.png", "image/png", [2], true);

        Assert.Equal(0, a.Id);
        Assert.Equal(1, b.Id);
    }

    [Fact]
    public void EmptyManifest_AllWrittenIsFalse()
    {
        var manifest = new ResourceManifest();
        Assert.False(manifest.AllWritten);
        Assert.False(manifest.HasFailures);
    }

    [Fact]
    public void Seal_RejectsNewResources_AndExposesStatus()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T.T", "Textures/T.png", "image/png", [1], true);

        manifest.Seal();

        Assert.True(manifest.IsSealed);
        Assert.Throws<InvalidOperationException>(() => manifest.AddOrGet("/Game/U.U", "Textures/U.png", "image/png", [1], true));
        Assert.Equal(WriteStatus.Pending, manifest.GetStatus("/Game/T.T"));
    }

    [Fact]
    public void Seal_ResourcesIsSnapshotAndCannotBeMutated()
    {
        var manifest = new ResourceManifest();
        manifest.AddOrGet("/Game/T.T", "Textures/T.png", "image/png", [1], true);

        var resources = manifest.Resources;
        manifest.Seal();

        Assert.Single(resources);
        Assert.Throws<InvalidOperationException>(() => manifest.AddOrGet("/Game/U.U", "Textures/U.png", "image/png", [2], true));
        Assert.Single(manifest.Resources);
    }
}
