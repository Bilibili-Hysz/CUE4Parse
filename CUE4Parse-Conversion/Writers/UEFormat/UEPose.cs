using System;
using System.Collections.Generic;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using CUE4Parse_Conversion.Writers.UEFormat.Structs;
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.Writers.UEFormat;

public sealed class UEPose : UEFormatExport
{
    protected override string Identifier => "UEPOSE";

    public UEPose(string name, string objectPath, CPoseAsset poseAsset, ExportOptions options)
        : this(name, objectPath, poseAsset, options, null, null)
    {
    }

    /// <summary>
    /// Writes a standard V10 pose asset and optionally adds the FModel target
    /// skeleton provenance attribute to its root attribute set.
    /// </summary>
    public UEPose(
        string name,
        string objectPath,
        CPoseAsset poseAsset,
        ExportOptions options,
        string? sourcePosePath,
        FModelSkeletonIdentity? targetSkeleton)
        : base(name, objectPath, options)
    {
        if ((sourcePosePath is null) != (targetSkeleton is null))
            throw new ArgumentException("FModel pose provenance requires both source path and target skeleton identity.");

        WriteRoot(root =>
        {
            root.AddAttribute("POSES", attr => attr.WriteArray(poseAsset.Poses, WritePose));
            root.AddAttribute("CURVES", attr => attr.WriteArray(poseAsset.CurveNames, attr.WriteFString));

            if (sourcePosePath is not null)
                FModelSkeletonExtensionWriter.WriteTargetPose(root, sourcePosePath, targetSkeleton!);
        });
    }

    private static void WritePose(FArchiveWriter writer, CPoseData pose)
    {
        writer.WriteFString(pose.PoseName);

        writer.WriteArray(pose.Keys, (writer, key) =>
        {
            writer.WriteFString(key.BoneName);
            key.Location.Serialize(writer);
            key.Rotation.Serialize(writer);
            key.Scale.Serialize(writer);
        });

        var influences = new List<KeyValuePair<int, float>>();
        for (var curveIndex = 0; curveIndex < pose.CurveData.Length; curveIndex++)
        {
            var curveValue = pose.CurveData[curveIndex];
            if (Math.Abs(curveValue) < 0.001f) continue;
            influences.Add(new KeyValuePair<int, float>(curveIndex, curveValue));
        }

        writer.WriteArray(influences, (writer, kvp) =>
        {
            writer.Write(kvp.Key);
            writer.Write(kvp.Value);
        });
    }
}
