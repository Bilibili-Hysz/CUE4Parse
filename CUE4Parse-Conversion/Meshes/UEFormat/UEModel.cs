using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Animations.UEFormat;
using CUE4Parse_Conversion.Meshes.UEFormat.Collision;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using CUE4Parse_Conversion.UEFormat.Structs;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.Meshes.UEFormat;

public class UEModel : UEFormatExport
{
    protected override string Identifier { get; set; } = "UEMODEL";

    public UEModel(string name, CStaticMesh mesh, FPackageIndex bodySetupLazy, ExporterOptions options, MaterialLinkMode materialLinkMode = MaterialLinkMode.None, FModelMaterialLinks? materialLinks = null) : base(name, options)
    {
        using (var lodChunk = new FDataChunk("LODS"))
        {
            for (var lodIdx = 0; lodIdx < mesh.LODs.Count; lodIdx++)
            {
                var lod = mesh.LODs[lodIdx];
                if (lod.SkipLod) continue;

                using var subLodChunk = new FStaticDataChunk($"LOD{lodIdx}");
                SerializeStaticMeshData(subLodChunk, lod.Verts, lod.Indices.Value, lod.VertexColors, lod.ExtraVertexColors, lod.Sections.Value, lod.ExtraUV.Value, materialLinks);
                subLodChunk.Serialize(lodChunk);

                lodChunk.Count++;

                if (options.LodFormat == ELodFormat.FirstLod) break;
            }

            lodChunk.Serialize(Ar);
        }

        if (bodySetupLazy.TryLoad<UBodySetup>(out var bodySetup) && bodySetup.AggGeom?.ConvexElems is { } convexElems)
        {
            using var collisionChunk = new FDataChunk("COLLISION", convexElems.Length);
            foreach (var convexElem in convexElems)
            {
                var collision = new FConvexMeshCollision(convexElem);
                collision.Serialize(collisionChunk);
            }
            collisionChunk.Serialize(Ar);
        }
        FModelMaterialLinkWriter.Write(Ar, materialLinkMode, materialLinks);
    }

    public UEModel(string name, CSkeletalMesh mesh, FPackageIndex[]? morphTargets, FPackageIndex[] sockets, FPackageIndex skeletonLazy, FPackageIndex physicsAssetLazy, ExporterOptions options, MaterialLinkMode materialLinkMode = MaterialLinkMode.None, FModelMaterialLinks? materialLinks = null) : base(name, options)
    {
        if (!skeletonLazy.TryLoad<USkeleton>(out var skeleton))
            throw new ArgumentException($"Skeletal mesh '{name}' has no resolvable skeleton.", nameof(skeletonLazy));

        using (var lodChunk = new FDataChunk("LODS"))
        {
            for (var lodIdx = 0; lodIdx < mesh.LODs.Count; lodIdx++)
            {
                var lod = mesh.LODs[lodIdx];
                if (lod.SkipLod) continue;

                using var subLodChunk = new FStaticDataChunk($"LOD{lodIdx}");
                SerializeStaticMeshData(subLodChunk, lod.Verts, lod.Indices.Value, lod.VertexColors, lod.ExtraVertexColors, lod.Sections.Value, lod.ExtraUV.Value, materialLinks);
                SerializeSkeletalMeshData(subLodChunk, lod.Verts, morphTargets, lod.LODIndex);
                subLodChunk.Serialize(lodChunk);

                lodChunk.Count++;

                if (options.LodFormat == ELodFormat.FirstLod) break;
            }

            lodChunk.Serialize(Ar);
        }

        using (var skeletonChunk = new FDataChunk("SKELETON", 1))
        {
            SerializeSkeletonData(skeletonChunk, skeleton, mesh.RefSkeleton, sockets, []);

            skeletonChunk.Serialize(Ar);
        }

        /*if (physicsAssetLazy.TryLoad(out UPhysicsAsset physicsAsset))
        {
            using var physicsChunk = new FDataChunk("PHYSICS", 1);

            SerializePhysicsData(physicsChunk, physicsAsset);

            physicsChunk.Serialize(Ar);
        }*/
        FModelMaterialLinkWriter.Write(Ar, materialLinkMode, materialLinks);
        FModelSkeletonExtensionWriter.WriteModelIdentity(Ar, FModelSkeletonIdentity.FromSkeleton(skeleton));
    }

    public UEModel(string name, USkeleton skeleton, List<CSkelMeshBone> bones, FPackageIndex[] sockets, FVirtualBone[] virtualBones, ExporterOptions options, MaterialLinkMode materialLinkMode = MaterialLinkMode.None, FModelMaterialLinks? materialLinks = null) : base(name, options)
    {
        using (var skeletonChunk = new FDataChunk("SKELETON", 1))
        {
            SerializeSkeletonData(skeletonChunk, skeleton, bones, sockets, virtualBones);

            skeletonChunk.Serialize(Ar);
        }
        FModelMaterialLinkWriter.Write(Ar, materialLinkMode, materialLinks);
        FModelSkeletonExtensionWriter.WriteModelIdentity(Ar, FModelSkeletonIdentity.FromSkeleton(skeleton));
    }

    private void SerializeStaticMeshData(FArchiveWriter archive, IReadOnlyCollection<CMeshVertex> verts, uint[] indices, FColor[]? vertexColors, CVertexColor[]? extraVertexColors, CMeshSection[] sections, FMeshUVFloat[][] extraUVs, FModelMaterialLinks? materialLinks = null)
    {
        using var vertexChunk = new FDataChunk("VERTICES", verts.Count);
        using var normalsChunk = new FDataChunk("NORMALS", verts.Count);
        using var tangentsChunk = new FDataChunk("TANGENTS", verts.Count);

        var mainUVs = new List<FMeshUVFloat>();
        foreach (var vert in verts)
        {
            var position = vert.Position;
            position.Serialize(vertexChunk);

            var normalSign = vert.Normal.W;
            normalsChunk.Write(normalSign); // EUEFormatVersion.SerializeBinormalSign

            var normal = (FVector) vert.Normal;
            normal /= MathF.Sqrt(normal | normal);
            normal.Serialize(normalsChunk);

            var tangent = (FVector) vert.Tangent;
            tangent.Normalize();
            tangent.Serialize(tangentsChunk);

            var uv = vert.UV;
            mainUVs.Add(uv);
        }

        vertexChunk.Serialize(archive);
        normalsChunk.Serialize(archive);
        tangentsChunk.Serialize(archive);

        using (var texCoordsChunk = new FDataChunk("TEXCOORDS"))
        {
            void SerializeUVSet(IEnumerable<FMeshUVFloat> uvSet)
            {
                texCoordsChunk.WriteArray(uvSet, uv =>
                {
                    uv.Serialize(texCoordsChunk);
                });

                texCoordsChunk.Count++;
            }

            SerializeUVSet(mainUVs);
            foreach (var extraUVSet in extraUVs)
            {
                SerializeUVSet(extraUVSet);
            }

            texCoordsChunk.Serialize(archive);
        }

        using (var indexChunk = new FDataChunk("INDICES", indices.Length))
        {
            for (var i = 0; i < indices.Length; i++)
            {
                indexChunk.Write(indices[i]);
            }

            indexChunk.Serialize(archive);
        }

        if (vertexColors?.Length > 0 || extraVertexColors?.Length > 0)
        {
            using var vertexColorChunk = new FDataChunk("VERTEXCOLORS");
            if (vertexColors is not null)
            {
                vertexColorChunk.WriteFString("COL0"); // todo fallback to default name w/ index for other extras, maybe just combine extra vtx col and main?
                vertexColorChunk.WriteArray(vertexColors, (writer, color) => color.Serialize(writer));
                vertexColorChunk.Count++;
            }

            if (extraVertexColors is not null)
            {
                foreach (var extraVertexColor in extraVertexColors)
                {
                    vertexColorChunk.WriteFString(extraVertexColor.Name);
                    vertexColorChunk.WriteArray(extraVertexColor.ColorData, (writer, color) => color.Serialize(writer));
                    vertexColorChunk.Count++;
                }
            }

            vertexColorChunk.Serialize(archive);
        }

        using (var materialChunk = new FDataChunk("MATERIALS", sections.Length))
        {
            for (var sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                var section = sections[sectionIndex];
                var materialLink = materialLinks?.Slots.FirstOrDefault(slot => slot.SlotIndex == sectionIndex);
                var materialPath = materialLink?.UnrealMaterialPath ?? section.Material?.GetPathName() ?? string.Empty;
                var materialName = MaterialDisplayName(materialPath) ?? section.Material?.Name.Text ?? section.MaterialName ?? string.Empty;
                materialChunk.WriteFString(materialName);
                materialChunk.WriteFString(materialPath);

                materialChunk.Write(section.FirstIndex);
                materialChunk.Write(section.NumFaces);
            }

            materialChunk.Serialize(archive);
        }
    }

    private static string? MaterialDisplayName(string materialPath)
    {
        if (string.IsNullOrEmpty(materialPath)) return null;
        var separator = materialPath.LastIndexOf('/');
        return separator >= 0 ? materialPath[(separator + 1)..] : materialPath;
    }

    private void SerializeSkeletalMeshData(FArchiveWriter archive, CSkelMeshVertex[] verts, FPackageIndex[]? morphTargets, int lodIndex)
    {
        using (var weightsChunk = new FDataChunk("WEIGHTS"))
        {
            for (var vertexIndex = 0; vertexIndex < verts.Length; vertexIndex++)
            {
                var vert = verts[vertexIndex];

                foreach (var influence in vert.Influences)
                {
                    weightsChunk.Write(influence.Bone);
                    weightsChunk.Write(vertexIndex);
                    weightsChunk.Write(influence.Weight);
                    weightsChunk.Count++;
                }
            }

            weightsChunk.Serialize(archive);
        }

        if (morphTargets is {Length: > 0})
        {
            using var morphTargetsChunk = new FDataChunk("MORPHTARGETS");
            foreach (var morphTarget in morphTargets)
            {
                var morph = morphTarget.Load<UMorphTarget>();
                if (morph?.MorphLODModels is null || lodIndex >= morph.MorphLODModels.Length || morph.MorphLODModels[lodIndex].Vertices.Length == 0) continue;

                var morphLod = morph.MorphLODModels[lodIndex];

                var morphData = new FMorphTarget(morph.Name, morphLod);
                morphData.Serialize(morphTargetsChunk);
                morphTargetsChunk.Count++;
            }
            morphTargetsChunk.Serialize(archive);
        }
    }

    private void SerializeSkeletonData(FArchiveWriter archive, USkeleton? skeleton, List<CSkelMeshBone> bones, FPackageIndex[] sockets, FVirtualBone[] virtualBones)
    {
        using (var metaDataChunk = new FDataChunk("METADATA", 1))
        {
            metaDataChunk.WriteFString(skeleton?.GetPathName() ?? string.Empty);
            metaDataChunk.Serialize(archive);
        }

        using (var boneChunk = new FDataChunk("BONES", bones.Count))
        {
            foreach (var bone in bones)
            {
                var boneName = new FString(bone.Name.Text);
                boneName.Serialize(boneChunk);

                boneChunk.Write(bone.ParentIndex);

                var bonePos = bone.Position;
                bonePos.Serialize(boneChunk);

                var boneRot = bone.Orientation;
                boneRot.Serialize(boneChunk);
            }
            boneChunk.Serialize(archive);
        }

        using (var socketChunk = new FDataChunk("SOCKETS", sockets.Length))
        {
            foreach (var socketObject in sockets)
            {
                var socket = socketObject.Load<USkeletalMeshSocket>();
                if (socket is null) continue;

                socketChunk.WriteFString(socket.SocketName.Text);
                socketChunk.WriteFString(socket.BoneName.Text);

                var bonePos = socket.RelativeLocation;
                bonePos.Serialize(socketChunk);

                var boneRot = socket.RelativeRotation.Quaternion();
                boneRot.Serialize(socketChunk);

                var boneScale = socket.RelativeScale;
                boneScale.Serialize(socketChunk);
            }

            socketChunk.Serialize(archive);
        }

        using (var virtualBoneChunk = new FDataChunk("VIRTUALBONES", virtualBones.Length))
        {
            foreach (var virtualBone in virtualBones)
            {
                virtualBoneChunk.WriteFString(virtualBone.SourceBoneName.Text);
                virtualBoneChunk.WriteFString(virtualBone.TargetBoneName.Text);
                virtualBoneChunk.WriteFString(virtualBone.VirtualBoneName.Text);
            }

            virtualBoneChunk.Serialize(archive);
        }
    }

    private void SerializePhysicsData(FArchiveWriter archive, UPhysicsAsset physicsAsset)
    {
        using (var bodyChunk = new FDataChunk("BODIES", physicsAsset.SkeletalBodySetups.Length))
        {
            foreach (var bodySetupLazy in physicsAsset.SkeletalBodySetups)
            {
                if (!bodySetupLazy.TryLoad<USkeletalBodySetup>(out var bodySetup)) continue;

                var exportBodySetup = new FBodySetup(bodySetup);
                exportBodySetup.Serialize(bodyChunk);
            }

            bodyChunk.Serialize(archive);
        }
    }

}
