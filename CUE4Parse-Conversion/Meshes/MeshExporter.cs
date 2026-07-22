using CUE4Parse_Conversion.DNA;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Meshes.glTF;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Meshes.UEFormat;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using CUE4Parse_Conversion.UEScene;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Writers;
using CUE4Parse.Utils;
using Serilog;

namespace CUE4Parse_Conversion.Meshes
{
    public class MeshExporter : ExporterBase
    {
        public List<Mesh> MeshLods = [];
        public readonly List<(uint SourceLodIndex, float SourceScreenSize)> DeclaredLods = [];
        public readonly List<UESceneProducedMaterialSlot> EffectiveMaterialSlots = [];
        public readonly List<DNAExporter> DNAAssets = [];

        internal MeshExporter(IEnumerable<IExporter> meshLods, IEnumerable<IExporter>? dnaAssets = null, ExporterOptions? options = null)
            : base()
        {
            Options = options ?? new ExporterOptions();
            ValidateBundleConfiguration();
            MeshLods = meshLods.OfType<Mesh>().ToList();
            _writeMeshLods = meshLods.ToList();
            _writeDnaAssets = dnaAssets?.ToList() ?? [];
        }

        private readonly List<IExporter>? _writeMeshLods;
        private readonly List<IExporter>? _writeDnaAssets;

        public MeshExporter(USkeleton originalSkeleton, ExporterOptions options) : base(originalSkeleton, options)
        {
            ValidateBundleConfiguration();
            MeshLods = new List<Mesh>();

            if (!originalSkeleton.TryConvert(out var bones, out _) || bones.Count == 0)
            {
                Log.Warning($"Skeleton '{ExportName}' has no bone");
                return;
            }

            using var Ar = new FArchiveWriter();
            string ext;
            switch (Options.MeshFormat)
            {
                case EMeshFormat.ActorX:
                    ext = "pskx";
                    new ActorXMesh(bones, originalSkeleton.Sockets, Options).Save(Ar);
                    break;
                case EMeshFormat.UEFormat:
                    ext = "uemodel";
                    new UEModel(originalSkeleton.Name, originalSkeleton, bones, originalSkeleton.Sockets, originalSkeleton.VirtualBones, Options).Save(Ar);
                    break;
                case EMeshFormat.Gltf2:
                case EMeshFormat.OBJ:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(Options.MeshFormat), Options.MeshFormat, null);
            }

            MeshLods.Add(new Mesh($"{GetExportSavePath()}.{ext}", Ar.GetBuffer(), new List<MaterialExporter2>()));
        }

        public MeshExporter(UStaticMesh originalMesh, ExporterOptions options) : this(originalMesh, null, options){}

        public MeshExporter(UStaticMesh originalMesh, USplineMeshComponent? splineMeshComponent, ExporterOptions options) : base(originalMesh, options)
            => ExportStaticMesh(originalMesh, splineMeshComponent, options, null, null);

        public MeshExporter(UStaticMesh originalMesh, ExporterOptions options, IReadOnlyList<UMaterialInterface?>? overrideMaterials,
            string? logicalModelPath) : base(originalMesh, options)
            => ExportStaticMesh(originalMesh, null, options, overrideMaterials, logicalModelPath);

        private void ExportStaticMesh(UStaticMesh originalMesh, USplineMeshComponent? splineMeshComponent, ExporterOptions options,
            IReadOnlyList<UMaterialInterface?>? overrideMaterials, string? plannedLogicalModelPath)
        {
            ValidateBundleConfiguration();
            MeshLods = new List<Mesh>();

            if (!originalMesh.TryConvert(splineMeshComponent, out var convertedMesh, options.NaniteMeshFormat, options.LodFormat) || convertedMesh.LODs.Count == 0)
            {
                Log.Logger.Warning($"Mesh '{ExportName}' has no LODs");
                return;
            }

            for (var sourceLodIndex = 0; sourceLodIndex < convertedMesh.LODs.Count; sourceLodIndex++)
            {
                var lod = convertedMesh.LODs[sourceLodIndex];
                if (lod.SkipLod) continue;
                DeclaredLods.Add((checked((uint)sourceLodIndex), lod.ScreenSize));
                if (options.LodFormat == ELodFormat.FirstLod) break;
            }

            var path = GetExportSavePath();
            if (splineMeshComponent != null) {
                path += string.Concat("-", splineMeshComponent.GetMeshId().AsSpan(0, 6));
            }

            if (Options.MeshFormat == EMeshFormat.UEFormat)
            {
                using var ueModelArchive = new FArchiveWriter();
                var includedLods = convertedMesh.LODs.Select((lod, sourceLodIndex) => (lod, sourceLodIndex))
                    .Where(entry => !entry.lod.SkipLod).ToList();
                if (Options.LodFormat == ELodFormat.AllLods)
                    ValidateUeFormatMaterialTopology(includedLods.Select(entry =>
                        (checked((uint) entry.sourceLodIndex), entry.lod.GetSectionMaterialIndices())).ToList());
                var materialLod = includedLods[0].lod;
                var materialSlots = materialLod.GetMaterialsWithSlotIndices(options, overrideMaterials);
                var materials = materialSlots.Select(slot => slot.Exporter).ToList();
                var mode = Options.MaterialLinkMode;
                var logicalModelPath = plannedLogicalModelPath ?? (mode == MaterialLinkMode.Bundle
                    ? Options.UESceneResourceUriPolicy?.Allocate(originalMesh.GetPathName(), UESceneResourceKind.Model, ".uemodel") ?? MaterialLinkBuilder.BuildModelPath(Options.MaterialExportLayout, TrimLogicalRoot($"{path}.uemodel"), originalMesh.GetPathName())
                    : $"{path}.uemodel");
                var modelPath = mode == MaterialLinkMode.Bundle ? logicalModelPath : $"{path}.uemodel";
                ResourceManifest? manifest = null;
                if (mode != MaterialLinkMode.None)
                {
                    manifest = new ResourceManifest();
                    foreach (var (_, exporter) in materialSlots)
                    {
                        exporter.PopulateManifest(manifest, mode, logicalModelPath, Options.MaterialLinkBundleRoot, Options.MaterialExportLayout);
                    }
                    manifest.Seal();
                }

                var materialLinks = BuildMaterialLinks(materialSlots, logicalModelPath, manifest);
                foreach (var (sourceMaterialIndex, sectionIndex) in materialLod.GetSectionMaterialIndices())
                {
                    var link = materialLinks.Slots.FirstOrDefault(slot => slot.SlotIndex == sectionIndex);
                    if (link is not null)
                        EffectiveMaterialSlots.Add(new UESceneProducedMaterialSlot(
                            checked((uint)sourceMaterialIndex), checked((uint)sectionIndex), link.UnrealMaterialPath));
                }
                new UEModel(originalMesh.Name, convertedMesh, originalMesh.BodySetup, Options, mode, materialLinks).Save(ueModelArchive);
                MeshLods.Add(new Mesh(mode == MaterialLinkMode.Bundle ? logicalModelPath : modelPath, ueModelArchive.GetBuffer(), materials) { ExportManifest = manifest });
                return;
            }

            var i = -1;
            foreach (var lod in convertedMesh.LODs)
            {
                i++;
                if (lod.SkipLod)
                {
                    Log.Warning($"LOD {i} in mesh '{ExportName}' should be skipped");
                    continue;
                }

                using var Ar = new FArchiveWriter();
                var materialExports = options.ExportMaterials ? new List<MaterialExporter2>() : null;
                string ext;
                switch (Options.MeshFormat)
                {
                    case EMeshFormat.ActorX:
                        ext = "pskx";
                        new ActorXMesh(lod, materialExports, originalMesh.Sockets, Options).Save(Ar);
                        break;
                    case EMeshFormat.Gltf2:
                        ext = "glb";
                        new Gltf(ExportName, lod, materialExports, Options).Save(Options.MeshFormat, Ar);
                        break;
                    case EMeshFormat.OBJ:
                        ext = "obj";
                        new Gltf(ExportName, lod, materialExports, Options).Save(Options.MeshFormat, Ar);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Options.MeshFormat), Options.MeshFormat, null);
                }

                if (Options.LodFormat == ELodFormat.FirstLod)
                {
                    MeshLods.Add(new Mesh($"{path}.{ext}", Ar.GetBuffer(), materialExports ?? new List<MaterialExporter2>()));
                    break;
                }

                MeshLods.Add(new Mesh($"{path}_LOD{i}.{ext}", Ar.GetBuffer(), materialExports ?? new List<MaterialExporter2>()));
            }
        }

        public MeshExporter(USkeletalMesh originalMesh, ExporterOptions options) : base(originalMesh, options)
        {
            ValidateBundleConfiguration();
            MeshLods = new List<Mesh>();

            if (!originalMesh.TryConvert(out var convertedMesh) || convertedMesh.LODs.Count == 0)
            {
                Log.Warning($"Mesh '{ExportName}' has no LODs");
                return;
            }

            if (originalMesh.AssetUserData != null)
            {
                foreach (var userData in originalMesh.AssetUserData)
                {
                    if (userData.TryLoad<UDNAAsset>(out var originalDNA))
                    {
                        var dna = new DNAExporter(originalDNA, options);
                        DNAAssets.Add(dna);
                    }
                }
            }

            var totalSockets = new List<FPackageIndex>();
            if (Options.SocketFormat != ESocketFormat.None)
            {
                totalSockets.AddRange(originalMesh.Sockets);
                if (originalMesh.Skeleton.TryLoad<USkeleton>(out var originalSkeleton))
                {
                    totalSockets.AddRange(originalSkeleton.Sockets);
                }
            }

            if (Options.ExportMorphTargets) originalMesh.PopulateMorphTargetVerticesData();

            if (Options.MeshFormat == EMeshFormat.UEFormat)
            {
                using var ueModelArchive = new FArchiveWriter();
                var materialSlots = convertedMesh.LODs[0].GetMaterialsWithSlotIndices(options);
                var materials = materialSlots.Select(slot => slot.Exporter).ToList();
                var mode = Options.MaterialLinkMode;
                var logicalModelPath = mode == MaterialLinkMode.Bundle
                    ? Options.UESceneResourceUriPolicy?.Allocate(originalMesh.GetPathName(), UESceneResourceKind.Model, ".uemodel") ?? MaterialLinkBuilder.BuildModelPath(Options.MaterialExportLayout, TrimLogicalRoot($"{GetExportSavePath()}.uemodel"), originalMesh.GetPathName())
                    : $"{GetExportSavePath()}.uemodel";
                var modelPath = mode == MaterialLinkMode.Bundle ? logicalModelPath : $"{GetExportSavePath()}.uemodel";
                ResourceManifest? manifest = null;
                if (mode != MaterialLinkMode.None)
                {
                    manifest = new ResourceManifest();
                    foreach (var (_, exporter) in materialSlots)
                    {
                        exporter.PopulateManifest(manifest, mode, logicalModelPath, Options.MaterialLinkBundleRoot, Options.MaterialExportLayout);
                    }
                    manifest.Seal();
                }

                new UEModel(originalMesh.Name, convertedMesh, Options.ExportMorphTargets ? originalMesh.MorphTargets : null, totalSockets.ToArray(), originalMesh.Skeleton, originalMesh.PhysicsAsset, Options, mode, BuildMaterialLinks(materialSlots, logicalModelPath, manifest)).Save(ueModelArchive);
                MeshLods.Add(new Mesh(mode == MaterialLinkMode.Bundle ? logicalModelPath : modelPath, ueModelArchive.GetBuffer(), materials) { ExportManifest = manifest });
                return;
            }

            var lodIndex = 0;
            for (var index = 0; index < convertedMesh.LODs.Count; index++)
            {
                var lod = convertedMesh.LODs[index];
                if (lod.SkipLod)
                {
                    Log.Warning($"LOD {lodIndex} in mesh '{ExportName}' should be skipped");
                    continue;
                }

                using var Ar = new FArchiveWriter();
                var materialExports = options.ExportMaterials ? new List<MaterialExporter2>() : null;
                var ext = "";
                switch (Options.MeshFormat)
                {
                    case EMeshFormat.ActorX:
                        ext = convertedMesh.LODs[lodIndex].NumVerts > 65536 ? "pskx" : "psk";
                        new ActorXMesh(lod, convertedMesh.RefSkeleton, materialExports,
                            Options.ExportMorphTargets ? originalMesh.MorphTargets : null,
                            totalSockets.ToArray(), lod.LODIndex, Options).Save(Ar);
                        break;
                    case EMeshFormat.Gltf2:
                        ext = "glb";
                        new Gltf(ExportName, lod, convertedMesh.RefSkeleton, materialExports, Options,
                            Options.ExportMorphTargets ? originalMesh.MorphTargets : null,
                            lod.LODIndex).Save(Options.MeshFormat, Ar);
                        break;
                    case EMeshFormat.OBJ:
                        ext = "obj";
                        new Gltf(ExportName, lod, convertedMesh.RefSkeleton, materialExports, Options,
                            Options.ExportMorphTargets ? originalMesh.MorphTargets : null,
                            lod.LODIndex).Save(Options.MeshFormat, Ar);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Options.MeshFormat), Options.MeshFormat, null);
                }

                if (Options.LodFormat == ELodFormat.FirstLod)
                {
                    MeshLods.Add(new Mesh($"{GetExportSavePath()}.{ext}", Ar.GetBuffer(), materialExports ?? new List<MaterialExporter2>()));
                    break;
                }
                else
                {
                    MeshLods.Add(new Mesh($"{GetExportSavePath()}_LOD{lodIndex}.{ext}", Ar.GetBuffer(), materialExports ?? new List<MaterialExporter2>()));
                }
                lodIndex++;
            }
        }

        private string TrimLogicalRoot(string path) => !string.IsNullOrEmpty(Options.UESceneProjectName)
            ? UESceneProjectExportUri.FromFixedPath(path, Options.UESceneProjectName, Path.GetExtension(path))
            : path;

        private FModelMaterialLinks BuildMaterialLinks(IEnumerable<(int SlotIndex, MaterialExporter2 Exporter)> materials, string modelPath, ResourceManifest? manifest = null)
        {
            var links = new FModelMaterialLinks();
            foreach (var (slotIndex, exporter) in materials)
            {
                var materialLinks = exporter.BuildMaterialLinks(Options.MaterialLinkMode, modelPath, slotIndex, slotIndex.ToString(), Options.MaterialLinkBundleRoot, manifest, Options.MaterialExportLayout);
                links.Slots.AddRange(materialLinks.Slots);
                links.PbrInterfaces.AddRange(materialLinks.PbrInterfaces);
            }
            return links;
        }

        internal static void ValidateUeFormatMaterialTopology(IReadOnlyList<(uint SourceLodIndex,
            IReadOnlyList<(int SourceMaterialIndex, int SectionIndex)> Topology)> lods)
        {
            if (lods.Count < 2) return;
            var baseline = lods[0].Topology;
            for (var index = 1; index < lods.Count; index++)
            {
                if (!baseline.SequenceEqual(lods[index].Topology))
                    throw new InvalidDataException($"LOD {lods[index].SourceLodIndex} material topology differs from LOD {lods[0].SourceLodIndex}; UEScene V1 requires one model-wide material-link table.");
            }
        }

        private void ValidateBundleConfiguration()
        {
            if (Options.MaterialLinkMode == MaterialLinkMode.Bundle && Options.MeshFormat == EMeshFormat.UEFormat)
                MaterialLinkBuilder.ValidateBundleRoot(Options.MaterialLinkBundleRoot);
        }

        /// <param name="baseDirectory"></param>
        /// <param name="label"></param>
        /// <param name="savedFilePath"></param>
        /// <returns>true if *ALL* lods were successfully exported</returns>
        public override bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath)
        {
            var allSucceeded = true;
            label = string.Empty;
            savedFilePath = string.Empty;
            var meshLods = _writeMeshLods ?? MeshLods.Cast<IExporter>().ToList();
            if (meshLods.Count == 0) return false;

            var dnaAssets = _writeDnaAssets ?? DNAAssets.Cast<IExporter>().ToList();
            for (var i = 0; i < dnaAssets.Count; i++)
            {
                if (!dnaAssets[i].TryWriteToDir(baseDirectory, out label, out _))
                    return false;
            }

            var outText = "LOD ";
            for (var i = 0; i < meshLods.Count; i++)
            {
                if (!meshLods[i].TryWriteToDir(baseDirectory, out label, out savedFilePath))
                {
                    allSucceeded = false;
                    break; // AND semantics: first failure stops the export
                }
                outText += $"{i} ";
            }

            label = outText + $"as '{savedFilePath.SubstringAfterWithLast('.')}' for '{ExportName}'";
            return allSucceeded;
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
}
