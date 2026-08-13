using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Misc;

namespace CUE4Parse_Conversion.Writers.UEFormat;

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

        SkeletonPath = NormalizeLogicalUnrealPath(skeletonPath, nameof(skeletonPath));
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

    public static string NormalizeLogicalUnrealPath(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Logical Unreal paths must be non-empty.", paramName);
        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Logical Unreal paths must not include leading or trailing whitespace.", paramName);
        if (path.Contains('\0'))
            throw new ArgumentException("Logical Unreal paths must not contain NUL.", paramName);
        if (path.Contains('\\'))
            throw new ArgumentException("Logical Unreal paths must use forward slashes only.", paramName);
        if (IsDriveAbsolutePath(path)
            || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Logical Unreal paths must not be absolute filesystem paths.", paramName);

        var normalized = path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length <= 1)
            throw new ArgumentException("Logical Unreal paths must contain at least one non-root segment.", paramName);

        for (var index = 1; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0 || segment is "." or "..")
                throw new ArgumentException("Logical Unreal paths must not contain empty or traversal segments.", paramName);
            if (segment.Any(char.IsControl))
                throw new ArgumentException("Logical Unreal paths must not contain control characters.", paramName);
        }

        return normalized;
    }

    private static bool IsDriveAbsolutePath(string path)
    {
        if (path.Length < 3 || path[1] != ':' || path[2] != '/') return false;
        var drive = path[0];
        return drive is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }
}
