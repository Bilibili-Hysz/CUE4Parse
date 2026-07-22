using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Misc;

namespace CUE4Parse_Conversion.Animations.UEFormat;

public sealed class FModelSkeletonIdentity
{
    private static readonly byte[] LayoutHashPrefix = Encoding.ASCII.GetBytes("fmodel-skeleton-layout-v1\0");
    private readonly byte[] _boneLayoutHash;

    public string SkeletonPath { get; }
    public FGuid? SkeletonGuid { get; }
    public byte[] BoneLayoutHash => (byte[]) _boneLayoutHash.Clone();

    public FModelSkeletonIdentity(string skeletonPath, FGuid? skeletonGuid, byte[] boneLayoutHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(skeletonPath);
        ArgumentNullException.ThrowIfNull(boneLayoutHash);
        if (boneLayoutHash.Length != SHA256.HashSizeInBytes)
            throw new ArgumentException("Bone layout hash must contain exactly 32 bytes.", nameof(boneLayoutHash));

        SkeletonPath = skeletonPath;
        SkeletonGuid = skeletonGuid is { } guid && guid.IsValid() ? guid : null;
        _boneLayoutHash = (byte[]) boneLayoutHash.Clone();
    }

    public static FModelSkeletonIdentity FromSkeleton(USkeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var bones = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        return new FModelSkeletonIdentity(
            skeleton.GetPathName(),
            skeleton.Guid,
            ComputeLayoutHash(bones.Select(bone => (bone.Name.Text, bone.ParentIndex))));
    }

    public static byte[] ComputeLayoutHash(IEnumerable<(string Name, int ParentIndex)> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        var orderedBones = bones.ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(LayoutHashPrefix);

        Span<byte> integerBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(integerBytes, checked((uint) orderedBones.Length));
        hash.AppendData(integerBytes);

        foreach (var (name, parentIndex) in orderedBones)
        {
            ArgumentNullException.ThrowIfNull(name);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteUInt32LittleEndian(integerBytes, checked((uint) nameBytes.Length));
            hash.AppendData(integerBytes);
            hash.AppendData(nameBytes);
            BinaryPrimitives.WriteInt32LittleEndian(integerBytes, parentIndex);
            hash.AppendData(integerBytes);
        }

        return hash.GetHashAndReset();
    }
}
