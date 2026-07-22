using CUE4Parse_Conversion.UEFormat.Structs;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.Animations.UEFormat;

public static class FModelSkeletonExtensionWriter
{
    private const byte ExtensionVersion = 1;

    public static void WriteModelIdentity(FArchiveWriter archive, FModelSkeletonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(identity);
        using var extensionArchive = new FArchiveWriter();
        using (var chunk = new FDataChunk("FMODEL_SKELETON_IDENTITY", 1))
        {
            chunk.Write(ExtensionVersion);
            WriteIdentity(chunk, identity);
            chunk.Serialize(extensionArchive);
        }
        archive.Write(extensionArchive.GetBuffer());
    }

    public static void WriteTargetSkeleton(FArchiveWriter archive, string sourceAnimationPath, FModelSkeletonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(sourceAnimationPath);
        ArgumentNullException.ThrowIfNull(identity);
        using var extensionArchive = new FArchiveWriter();
        using (var chunk = new FDataChunk("FMODEL_TARGET_SKELETON", 1))
        {
            chunk.Write(ExtensionVersion);
            chunk.WriteFString(sourceAnimationPath);
            WriteIdentity(chunk, identity);
            chunk.Serialize(extensionArchive);
        }
        archive.Write(extensionArchive.GetBuffer());
    }

    private static void WriteIdentity(FArchiveWriter archive, FModelSkeletonIdentity identity)
    {
        archive.WriteFString(identity.SkeletonPath);
        archive.Write(identity.SkeletonGuid is { } ? (byte) 1 : (byte) 0);
        if (identity.SkeletonGuid is { } guid) archive.Write(guid.AsByteSpan().ToArray());
        archive.Write(identity.BoneLayoutHash);
    }
}
