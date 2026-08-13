using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using CUE4Parse_Conversion.Writers.UEFormat;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Writers;
 using CUE4Parse.UE4.Writers;
using System;

namespace CUE4Parse_Conversion.Formats.Animations;

public sealed class UEFormatAnimFormat(
    string? sourceAnimationPath = null,
    FModelSkeletonIdentity? targetSkeleton = null)
    : IAnimExportFormat
{
    public string DisplayName => "UEFormat (ueanim)";

    public IReadOnlyList<ExportFile> Build(string objectName, ExportOptions options, CAnimSet animSet)
    {
        var results = new List<ExportFile>(animSet.Sequences.Count);
        for (var i = 0; i < results.Capacity; i++)
        {
            using var ar = new FArchiveWriter();
            if (sourceAnimationPath is null || targetSkeleton is null)
                throw new InvalidOperationException("Regular UEFormat animation export requires skeleton identity.");

            new UEAnim(objectName, animSet, i, sourceAnimationPath, targetSkeleton, options).Save(ar);

            var suffix = i == 0 ? "" : $"_SEQ{i}";
            results.Add(new ExportFile("ueanim", ar.GetBuffer(), suffix));
        }

        return results;
    }

    public IReadOnlyList<ExportFile> BuildAnimStreamable(string objectName, ExportOptions options, UAnimStreamable animStreamable)
    {
        using var ar = new FArchiveWriter();
        new UEAnim(objectName, animStreamable, options).Save(ar);
        return [new ExportFile("ueanim", ar.GetBuffer())];
    }
}
