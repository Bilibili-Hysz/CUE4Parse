using System.Linq;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using CUE4Parse.UE4.Objects.Engine.Animation;

namespace CUE4Parse_Conversion.PoseAsset;

public static class PoseAssetConverter
{
    public static bool TryConvert(this UPoseAsset poseAsset, out CPoseAsset convertedPoseAsset)
    {
        convertedPoseAsset = new CPoseAsset();

        if (!poseAsset.bAdditivePose) return false;

        var poseContainer = poseAsset.PoseContainer;
        var poseDatas = poseContainer.Poses;
        if (poseDatas is null || poseDatas.Length == 0) return false;

        var poseNames = poseContainer.GetPoseNames().ToArray();
        if (poseNames.Length == 0 || poseNames.Length != poseDatas.Length) return false;

        var boneTrackNames = poseContainer.Tracks;
        if (boneTrackNames.Length == 0) return false;

        if (poseDatas.Any(pose => pose is null || pose.LocalSpacePose is null || pose.CurveData is null))
            return false;

        convertedPoseAsset.CurveNames = [..poseAsset.PoseContainer.Curves.Select(curve => curve.CurveName.Text)];

        // create initial poses
        for (var poseIndex = 0; poseIndex < poseDatas.Length; poseIndex++)
        {
            var poseData = poseDatas[poseIndex];
            convertedPoseAsset.Poses.Add(new CPoseData
            {
                PoseName = poseNames[poseIndex],
                CurveData = poseData.CurveData
            });
        }

        if (poseContainer.TrackPoseInfluenceIndices is { } trackBoneInfluences)
        {
            if (boneTrackNames.Length != trackBoneInfluences.Length) return false;

            for (var boneIndex = 0; boneIndex < trackBoneInfluences.Length; boneIndex++)
            {
                var boneInfluences = trackBoneInfluences[boneIndex];
                if (boneInfluences is null) continue;
                if (boneInfluences.Influences is null) return false;

                var boneName = boneTrackNames[boneIndex];
                foreach (var influence in boneInfluences.Influences)
                {
                    if (influence is null
                        || influence.PoseIndex < 0
                        || influence.PoseIndex >= poseDatas.Length
                        || influence.BoneTransformIndex < 0
                        || influence.BoneTransformIndex >= poseDatas[influence.PoseIndex].LocalSpacePose.Length)
                        return false;

                    var targetTransform = poseDatas[influence.PoseIndex].LocalSpacePose[influence.BoneTransformIndex];
                    if (!targetTransform.Rotation.IsNormalized) targetTransform.Rotation.Normalize();

                    var targetPose = convertedPoseAsset.Poses[influence.PoseIndex];
                    targetPose.Keys.Add(new CPoseKey(
                        boneName.Text,
                        targetTransform.Translation,
                        targetTransform.Rotation,
                        targetTransform.Scale3D
                    ));
                }
            }
        }
        else
        {
            for (var poseIndex = 0; poseIndex < poseDatas.Length; poseIndex++)
            {
                var targetPose = convertedPoseAsset.Poses[poseIndex];
                var poseData = poseDatas[poseIndex];
                foreach (var (trackIndex, transformIndex) in poseData.TrackToBufferIndex)
                {
                    if (trackIndex < 0
                        || trackIndex >= boneTrackNames.Length
                        || transformIndex < 0
                        || transformIndex >= poseData.LocalSpacePose.Length)
                        return false;

                    var transform = poseData.LocalSpacePose[transformIndex];
                    if (!transform.Rotation.IsNormalized)
                        transform.Rotation.Normalize();

                    targetPose.Keys.Add(new CPoseKey(
                        boneTrackNames[trackIndex].Text,
                        transform.Translation,
                        transform.Rotation,
                        transform.Scale3D
                    ));
                }
            }
        }

        return true;
    }
}
