using System;
using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion.Writers.UEFormat.Structs;

namespace CUE4Parse_Conversion.Writers.UEFormat;

public static class FModelSkeletonExtensionWriter
{
    private const byte ExtensionVersion = 1;

    public static void WriteModelIdentity(
        FDataAttributeSet root,
        FModelSkeletonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(identity);

        root.AddAttribute("FMODEL_SKELETON_IDENTITY", attribute =>
        {
            WriteEnvelope(attribute, 1);
            attribute.Write(ExtensionVersion);
            WriteIdentity(attribute, identity);
        });
    }

    public static void WriteTargetSkeleton(
        FDataAttributeSet root,
        string sourceAnimationPath,
        FModelSkeletonIdentity identity)
    {
        WriteTarget(root, sourceAnimationPath, identity);
    }

    public static void WriteTargetPose(
        FDataAttributeSet root,
        string sourcePosePath,
        FModelSkeletonIdentity identity)
    {
        WriteTarget(root, sourcePosePath, identity);
    }

    private static void WriteTarget(
        FDataAttributeSet root,
        string sourceObjectPath,
        FModelSkeletonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(root);
        sourceObjectPath = FModelSkeletonIdentity.NormalizeLogicalUnrealPath(
            sourceObjectPath,
            nameof(sourceObjectPath));
        ArgumentNullException.ThrowIfNull(identity);

        root.AddAttribute("FMODEL_TARGET_SKELETON", attribute =>
        {
            WriteEnvelope(attribute, 1);
            attribute.Write(ExtensionVersion);
            attribute.WriteFString(sourceObjectPath);
            WriteIdentity(attribute, identity);
        });
    }

    private static void WriteEnvelope(FArchiveWriter archive, int arraySize)
    {
        archive.Write(ExtensionVersion);
        archive.Write(arraySize);
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
