using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse_Conversion.Formats.Animations;
using CUE4Parse_Conversion.Formats.Meshes;
using CUE4Parse_Conversion.Formats.PoseAsset;
using CUE4Parse_Conversion.Options;

namespace CUE4Parse_Conversion.Exporters;

/// <summary>
/// Optional host extension for one explicitly selected mesh format.
/// World export and every official non-UEFormat path remain upstream-owned.
/// </summary>
public interface IExportSessionExtension
{
    /// <summary>
    /// Returns a host mesh format only for an explicitly supported request.
    /// Returning null preserves the official mesh-format switch.
    /// </summary>
    IMeshExportFormat? TryCreateMeshFormat(UObject export, EMeshFormat format, ExportSession session);

    /// <summary>
    /// Returns a host animation format only for an explicitly supported request.
    /// The default implementation preserves the official animation switch.
    /// </summary>
    IAnimExportFormat? TryCreateAnimationFormat(UAnimationAsset export, EMeshFormat format, ExportSession session)
        => null;

    /// <summary>
    /// Returns a host pose format only for an explicitly supported request.
    /// The default implementation preserves the official pose switch.
    /// </summary>
    IPoseExportFormat? TryCreatePoseFormat(UPoseAsset export, EMeshFormat format, ExportSession session)
        => null;
}
