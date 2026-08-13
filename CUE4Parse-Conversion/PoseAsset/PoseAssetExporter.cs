using CUE4Parse_Conversion.PoseAsset.UEFormat;
using CUE4Parse_Conversion.Animations.UEFormat;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.PoseAsset;

public class PoseAssetExporter : ExporterBase
{
    
    public PoseAsset? PoseAsset { get; private set; }

    public PoseAssetExporter(UPoseAsset poseAsset, ExporterOptions options) : base(poseAsset, options)
    {
        if (!poseAsset.TryConvert(out var convertedPoseAsset))
        {
            Log.Warning("PoseAsset '{ExportName}' failed to convert", ExportName);
            return;
        }

        using var Ar = new FArchiveWriter();
        if (!poseAsset.Skeleton.TryLoad<USkeleton>(out var skeleton))
            throw new ArgumentException($"Pose asset '{poseAsset.GetPathName()}' has no resolvable target skeleton.", nameof(poseAsset));

        var targetSkeleton = FModelSkeletonIdentity.FromSkeleton(skeleton);
        string ext;
        switch (Options.PoseFormat)
        {
            case EPoseFormat.UEFormat:
                ext = "uepose";
                new UEPose(poseAsset.Name, convertedPoseAsset, poseAsset.GetPathName(), targetSkeleton, Options).Save(Ar);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Options.PoseFormat), Options.PoseFormat, null);
        }

        PoseAsset = new PoseAsset($"{GetExportSavePath()}.{ext}", Ar.GetBuffer());
    }

    public override bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath)
    {
        if (PoseAsset is null)
        {
            label = string.Empty;
            savedFilePath = string.Empty;
            return false;
        }

        return PoseAsset.TryWriteToDir(baseDirectory, out label, out savedFilePath);
    }

    public override bool TryWriteToZip(out byte[] zipFile)
    {
        throw new NotImplementedException();
    }

    public override void AppendToZip()
    {
        throw new NotImplementedException();
    }
}
