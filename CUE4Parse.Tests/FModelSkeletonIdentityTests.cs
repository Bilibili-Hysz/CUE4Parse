using System.Reflection;
using System.Runtime.CompilerServices;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Animations.UEFormat;

namespace CUE4Parse.Tests;

public class FModelSkeletonIdentityTests
{
    [Fact]
    public void FromSkeleton_UsesPathGuidAndOrderedFinalRefBoneInfo()
    {
        var referenceSkeleton = (FReferenceSkeleton) RuntimeHelpers.GetUninitializedObject(typeof(FReferenceSkeleton));
        typeof(FReferenceSkeleton).GetField(nameof(FReferenceSkeleton.FinalRefBoneInfo), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(referenceSkeleton, new[]
            {
                new FMeshBoneInfo(new FName("root"), -1),
                new FMeshBoneInfo(new FName("spine"), 0),
                new FMeshBoneInfo(new FName("hand_l"), 1)
            });
        var validGuid = new FGuid(0x01234567, 0x89abcdef, 0x10203040, 0x50607080);
        var skeleton = new USkeleton
        {
            Name = "/Game/Characters/SK_Hero.SK_Hero",
            Guid = validGuid,
            ReferenceSkeleton = referenceSkeleton
        };

        var validIdentity = FModelSkeletonIdentity.FromSkeleton(skeleton);

        Assert.Equal("/Game/Characters/SK_Hero.SK_Hero", skeleton.GetPathName());
        Assert.Equal(skeleton.GetPathName(), validIdentity.SkeletonPath);
        Assert.Equal(validGuid, validIdentity.SkeletonGuid);
        Assert.Equal("b985620a29d2275d8951d0c0c56e1dbf6d143ccde0d258f9f91bef1951372bf0",
            Convert.ToHexStringLower(validIdentity.BoneLayoutHash));

        skeleton.Guid = default;

        Assert.Null(FModelSkeletonIdentity.FromSkeleton(skeleton).SkeletonGuid);
    }

    [Fact]
    public void LayoutHash_IsStableAndUsesCanonicalByteStream()
    {
        var bones = new[] { ("root", -1), ("spine_\u9aa8", 0) };

        var first = FModelSkeletonIdentity.ComputeLayoutHash(bones);
        var second = FModelSkeletonIdentity.ComputeLayoutHash(bones);

        Assert.Equal(first, second);
        Assert.Equal("306b481b7b174578f3a037b4aa0346f79a09edef3a312ebb29b1d63719f78b1f", Convert.ToHexStringLower(first));
    }

    [Fact]
    public void LayoutHash_ChangesForBoneNameOrderOrParentChanges()
    {
        var original = FModelSkeletonIdentity.ComputeLayoutHash(new[] { ("root", -1), ("spine", 0) });

        Assert.NotEqual(original, FModelSkeletonIdentity.ComputeLayoutHash(new[] { ("ROOT", -1), ("spine", 0) }));
        Assert.NotEqual(original, FModelSkeletonIdentity.ComputeLayoutHash(new[] { ("spine", 0), ("root", -1) }));
        Assert.NotEqual(original, FModelSkeletonIdentity.ComputeLayoutHash(new[] { ("root", -1), ("spine", -1) }));
    }

    [Fact]
    public void Constructor_RepresentsMissingAndPresentSourceGuidWithoutSynthesis()
    {
        var hash = FModelSkeletonIdentity.ComputeLayoutHash(new[] { ("root", -1) });
        var missing = new FModelSkeletonIdentity("/Game/Characters/SK_Hero.SK_Hero", default(FGuid), hash);
        var sourceGuid = new FGuid(0x01234567, 0x89abcdef, 0x10203040, 0x50607080);
        var present = new FModelSkeletonIdentity("/Game/Characters/SK_Hero.SK_Hero", sourceGuid, hash);

        Assert.Null(missing.SkeletonGuid);
        Assert.Equal(sourceGuid, present.SkeletonGuid);
    }

    [Fact]
    public void Constructor_ValidatesRequiredPathAndHashLength()
    {
        Assert.Throws<ArgumentException>(() => new FModelSkeletonIdentity("", null, new byte[32]));
        Assert.Throws<ArgumentException>(() => new FModelSkeletonIdentity("/Game/Skeletons/Hero.Hero", null, new byte[31]));
    }

    [Fact]
    public void BoneLayoutHash_IsDefensivelyCopied()
    {
        var sourceHash = FModelSkeletonIdentity.ComputeLayoutHash(new[] { ("root", -1) });
        var expected = (byte[]) sourceHash.Clone();
        var identity = new FModelSkeletonIdentity("/Game/Skeletons/Hero.Hero", null, sourceHash);

        sourceHash[0] ^= 0xff;
        identity.BoneLayoutHash[1] ^= 0xff;

        Assert.Equal(expected, identity.BoneLayoutHash);
    }
}
