using System.Runtime.CompilerServices;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Landscape;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.PoseAsset;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using CUE4Parse_Conversion.UEFormat.Enums;
using CUE4Parse_Conversion.UEScene;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.Utils;

namespace CUE4Parse_Conversion
{
    public enum MaterialExportLayout
    {
        PreserveHierarchy,
        Categorized
    }

    public struct ExporterOptions
    {
        public ELodFormat LodFormat;
        public EMeshFormat MeshFormat;
        public ENaniteMeshFormat NaniteMeshFormat;
        public EAnimFormat AnimFormat;
        public EPoseFormat PoseFormat;
        public EMaterialFormat MaterialFormat;
        public ETextureFormat TextureFormat;
        public EFileCompressionFormat CompressionFormat;
        public ETexturePlatform Platform;
        public ESocketFormat SocketFormat;
        public bool ExportMorphTargets;
        public bool ExportMaterials;
        public bool ExportHdrTexturesAsHdr;
        public MaterialLinkMode MaterialLinkMode;
        public string? MaterialLinkBundleRoot;
        public string? UESceneProjectName;
        public UESceneResourceUriPolicy? UESceneResourceUriPolicy;
        public MaterialExportLayout MaterialExportLayout;

        public ExporterOptions()
        {
            LodFormat = ELodFormat.FirstLod;
            MeshFormat = EMeshFormat.ActorX;
            NaniteMeshFormat = ENaniteMeshFormat.OnlyNaniteLOD;
            AnimFormat = EAnimFormat.ActorX;
            MaterialFormat = EMaterialFormat.AllLayersNoRef;
            TextureFormat = ETextureFormat.Png;
            CompressionFormat = EFileCompressionFormat.None;
            Platform = ETexturePlatform.DesktopMobile;
            SocketFormat = ESocketFormat.Bone;
            ExportMorphTargets = true;
            ExportMaterials = true;
            ExportHdrTexturesAsHdr = true;
            MaterialLinkMode = MaterialLinkMode.None;
            MaterialLinkBundleRoot = null;
            UESceneProjectName = null;
            UESceneResourceUriPolicy = null;
            MaterialExportLayout = MaterialExportLayout.PreserveHierarchy;
        }
    }

    public interface IExporter
    {
        public bool TryWriteToDir(DirectoryInfo directoryInfo, out string label, out string savedFileName);
        public bool TryWriteToZip(out byte[] zipFile);
        public void AppendToZip();
    }

    public abstract class ExporterBase : IExporter
    {
        protected readonly string PackagePath;
        protected readonly string ExportName;
        public ExporterOptions Options;

        protected ExporterBase()
        {
            PackagePath = string.Empty;
            ExportName = string.Empty;
            Options = new ExporterOptions();
        }

        protected ExporterBase(UObject export, ExporterOptions options)
        {
            var p = export.Owner?.Name ?? export.GetPathName();

            // this should use FixPath in order to get correct package path, as in game project
            PackagePath = (export.Owner?.Provider?.FixPath(p) ?? p).SubstringBeforeLast('.');
            ExportName = export.Name;
            Options = options;
        }

        // Test seam for platforms where creating a directory link requires elevated privileges.
        internal Func<string, FileAttributes?>? PathAttributes { get; set; }

        public abstract bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath);
        public abstract bool TryWriteToZip(out byte[] zipFile);
        public abstract void AppendToZip();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected string GetExportSavePath()
        {
            return GetExportSavePath(PackagePath, ExportName);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string GetExportSavePath(string packagePath, string exportName)
        {
            var path = packagePath.SubstringAfterLast('/').Equals(exportName, StringComparison.InvariantCulture) ? packagePath : packagePath + '/' + exportName;
            return path[0] == '/' ? path[1..] : path;
        }

        protected string FixAndCreatePath(DirectoryInfo baseDirectory, string fullPath, string? ext = null)
        {
            if (fullPath.StartsWith('/')) fullPath = fullPath[1..];
            var combined = Path.Combine(baseDirectory.FullName, fullPath) + (ext != null ? $".{ext.ToLower()}" : "");
            var ret = Path.GetFullPath(combined);
            // Path containment: ensure the result stays within the base directory
            if (!ret.StartsWith(baseDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !ret.Equals(baseDirectory.FullName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Path escape detected: '{combined}' resolves outside base directory '{baseDirectory.FullName}'.");
            var directory = Path.GetDirectoryName(ret) ?? baseDirectory.FullName;
            EnsureSafeDirectoryAncestors(baseDirectory.FullName, directory);
            Directory.CreateDirectory(directory);
            EnsureSafeDirectoryAncestors(baseDirectory.FullName, directory);
            return ret;
        }

        private void EnsureSafeDirectoryAncestors(string rootPath, string targetPath)
        {
            EnsureNotReparsePoint(rootPath);
            var current = rootPath;
            foreach (var segment in Path.GetRelativePath(rootPath, targetPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment is "" or ".") continue;
                current = Path.Combine(current, segment);
                if (Directory.Exists(current)) EnsureNotReparsePoint(current);
            }
        }

        private void EnsureNotReparsePoint(string path)
        {
            var attributes = PathAttributes?.Invoke(path);
            if (attributes is null && Directory.Exists(path)) attributes = File.GetAttributes(path);
            if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Export target traverses reparse point '{path}'.");
        }
    }

    public class Exporter
    {
        private readonly ExporterBase _exporterBase;

        public Exporter(UObject export) : this(export, new ExporterOptions()) { }
        public Exporter(UObject export, ExporterOptions options)
        {
            if (UsesBundleOutputRoot(export, options))
                MaterialLinkBuilder.ValidateBundleRoot(options.MaterialLinkBundleRoot);

            _exporterBase = export switch
            {
                UAnimSequence animSequence => new AnimExporter(animSequence, options),
                UAnimMontage animMontage => new AnimExporter(animMontage, options),
                UAnimComposite animComposite => new AnimExporter(animComposite, options),
                UPoseAsset poseAsset => new PoseAssetExporter(poseAsset, options),
                UMaterialInterface material => new MaterialExporter2(material, options),
                USkeletalMesh skeletalMesh => new MeshExporter(skeletalMesh, options),
                USkeleton skeleton => new MeshExporter(skeleton, options),
                UStaticMesh staticMesh => new MeshExporter(staticMesh, options),
                ALandscapeProxy landscape => new LandscapeExporter(landscape, null, options),
                _ => throw new NotSupportedException($"export of '{export.GetType()}' is not supported yet.")
            };
        }

        public static bool UsesBundleOutputRoot(UObject export, ExporterOptions options) => UsesBundleOutputRoot(export.GetType(), options);

        public static bool UsesBundleOutputRoot(Type exportType, ExporterOptions options) =>
            options.MaterialLinkMode == MaterialLinkMode.Bundle &&
            options.MeshFormat == EMeshFormat.UEFormat &&
            (typeof(USkeletalMesh).IsAssignableFrom(exportType) ||
             typeof(USkeleton).IsAssignableFrom(exportType) ||
             typeof(UStaticMesh).IsAssignableFrom(exportType));

        public static DirectoryInfo GetOutputDirectory(UObject export, ExporterOptions options, string modelDirectory) =>
            new(UsesBundleOutputRoot(export, options) ? options.MaterialLinkBundleRoot! : modelDirectory);

        public bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath) =>
            _exporterBase.TryWriteToDir(baseDirectory, out label, out savedFilePath);

        public bool TryWriteToZip(out byte[] zipFile) => _exporterBase.TryWriteToZip(out zipFile);

        public void AppendToZip() => _exporterBase.AppendToZip();
    }
}
