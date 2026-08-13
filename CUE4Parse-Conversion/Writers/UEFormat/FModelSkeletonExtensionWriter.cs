using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion.Writers.UEFormat.Structs;

namespace CUE4Parse_Conversion.Writers.UEFormat;

public static class FModelSkeletonExtensionWriter
{
    private const byte ExtensionVersion = 1;

    public static void WriteTargetSkeleton(
        FArchiveWriter archive,
        string sourceAnimationPath,
        FModelSkeletonIdentity identity)
    {
        WriteTarget(archive, sourceAnimationPath, identity);
    }

    public static void WriteTargetPose(
        FArchiveWriter archive,
        string sourcePosePath,
        FModelSkeletonIdentity identity)
    {
        WriteTarget(archive, sourcePosePath, identity);
    }

    private static void WriteTarget(
        FArchiveWriter archive,
        string sourceObjectPath,
        FModelSkeletonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(archive);
        sourceObjectPath = FModelSkeletonIdentity.NormalizeLogicalUnrealPath(
            sourceObjectPath,
            nameof(sourceObjectPath));
        ArgumentNullException.ThrowIfNull(identity);

        using var chunk = new FDataChunk("FMODEL_TARGET_SKELETON", 1);
        chunk.Write(ExtensionVersion);
        chunk.WriteFString(sourceObjectPath);
        WriteIdentity(chunk, identity);
        chunk.Serialize(archive);
    }

    private static void WriteIdentity(FArchiveWriter archive, FModelSkeletonIdentity identity)
    {
        archive.WriteFString(identity.SkeletonPath);
        archive.Write(identity.SkeletonGuid is { } ? (byte) 1 : (byte) 0);
        if (identity.SkeletonGuid is { } guid)
        {
            archive.Write(guid.AsByteSpan().ToArray());
        }
        archive.Write(identity.BoneLayoutHash);
    }
}
