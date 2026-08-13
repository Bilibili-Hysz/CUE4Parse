using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Formats.Animations;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse_Conversion.Writers.UEFormat;

namespace CUE4Parse_Conversion.Exporters;

public sealed class AnimationExporter(UAnimationAsset animation) : ExporterBase(animation)
{
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        Log.Debug("Converting animation to {Format}", Session.Options.MeshFormat);

        if (Session.Options.MeshFormat == EMeshFormat.UEFormat && animation is UAnimStreamable streamable)
        {
            return new UEFormatAnimFormat().BuildAnimStreamable(ObjectName, Session.Options, streamable);
        }

        var animSet = animation.ConvertAnims();
        var format = GetAnimFormat(Session.Options.MeshFormat, animSet.Skeleton);
        return format.Build(ObjectName, Session.Options, animSet);
    }

    private IAnimExportFormat GetAnimFormat(EMeshFormat format, USkeleton skeleton) => format switch
    {
        EMeshFormat.ActorX => new ActorXAnimFormat(),
        EMeshFormat.UEFormat => new UEFormatAnimFormat(animation.GetPathName(), FModelSkeletonIdentity.FromSkeleton(skeleton)),
        EMeshFormat.USD => new UsdAnimFormat(),
        _ => throw new NotSupportedException($"Animation export does not support format {format}. Available formats: {string.Join(", ", "ActorX", "UEFormat", "USD")}"),
    };
}
