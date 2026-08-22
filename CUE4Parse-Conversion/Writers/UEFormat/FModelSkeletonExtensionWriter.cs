using System;
using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion.Writers.UEFormat.Structs;

namespace CUE4Parse_Conversion.Writers.UEFormat;

public static class FModelSkeletonExtensionWriter
{
    // This is the semantic payload version. The containing V10 attribute has
    // a separate envelope version written by the helpers below.
    private const byte IdentityPayloadVersion = 1;
    private const byte AttributeEnvelopeVersion = 1;

    public static void WriteModelIdentity(
        FDataAttributeSet root,
        FModelSkeletonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(identity);

        root.AddAttribute("FMODEL_SKELETON_IDENTITY", attribute =>
        {
            WriteEnvelope(attribute, 1);
            attribute.Write(IdentityPayloadVersion);
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
            attribute.Write(IdentityPayloadVersion);
            attribute.WriteFString(sourceObjectPath);
            WriteIdentity(attribute, identity);
        });
    }

    private static void WriteEnvelope(FArchiveWriter archive, int arraySize)
    {
        archive.Write(AttributeEnvelopeVersion);
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
