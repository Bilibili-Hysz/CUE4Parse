using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using CUE4Parse_Conversion.Writers.UEFormat;
using CUE4Parse.UE4.Writers;
 using CUE4Parse.UE4.Writers;
using System;

namespace CUE4Parse_Conversion.Formats.PoseAsset;

public sealed class UEFormatPoseFormat(
    string? sourcePosePath = null,
    FModelSkeletonIdentity? targetSkeleton = null)
    : IPoseExportFormat
{
    public string DisplayName => "UEFormat (uepose)";

    public ExportFile Build(string objectName, ExportOptions options, CPoseAsset poseAsset)
    {
        using var ar = new FArchiveWriter();
        if (sourcePosePath is null || targetSkeleton is null)
            throw new InvalidOperationException("UEFormat pose export requires skeleton identity.");

        new UEPose(objectName, poseAsset, sourcePosePath, targetSkeleton, options).Save(ar);
        return new ExportFile("uepose", ar.GetBuffer());
    }
}


