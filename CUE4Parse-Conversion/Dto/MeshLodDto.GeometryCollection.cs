using CUE4Parse.UE4.Objects.Chaos.GeometryCollection;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.UObject;

namespace CUE4Parse_Conversion.Dto;

public partial class MeshLodDto<TVertex>
{
    internal static MeshLodDto<MeshVertex> FromRenderData(
        StaticMeshDto owner,
        uint sourceLodIndex,
        FGeometryCollectionMeshResources resources,
        FGeometryCollectionMeshDescription description,
        FGeometryCollection? collection)
    {
        ArgumentNullException.ThrowIfNull(resources.IndexBuffer.Buffer, "LOD has no index buffer");

        var vertexCount = resources.PositionVertexBuffer.Verts.Length;
        RequireLength(resources.StaticMeshVertexBuffer.UV, vertexCount, "Geometry collection UV buffer");

        var extraUvs = new FMeshUVFloat[Math.Max(0, resources.StaticMeshVertexBuffer.NumTexCoords - 1)][];
        var vertices = new MeshVertex[vertexCount];
        for (var i = 0; i < extraUvs.Length; i++)
        {
            extraUvs[i] = new FMeshUVFloat[vertexCount];
        }

        FColor[]? vertexColors = null;
        if (resources.ColorVertexBuffer.Data.Length > 0)
        {
            RequireLength(resources.ColorVertexBuffer.Data, vertexCount, "Geometry collection color buffer");
            vertexColors = new FColor[vertexCount];
            Array.Copy(resources.ColorVertexBuffer.Data, vertexColors, vertexCount);
        }
        else if (collection?.TryGetAttributeValue<FLinearColor>("BoneColor", "Transform", out var boneColors) == true &&
                 resources.BoneMapVertexBuffer.BoneMap.Length > 0)
        {
            RequireLength(resources.BoneMapVertexBuffer.BoneMap, vertexCount, "Geometry collection bone map");
            vertexColors = BuildBoneColors(resources.BoneMapVertexBuffer.BoneMap, boneColors, vertexCount);
        }

        for (var i = 0; i < vertexCount; i++)
        {
            var uv = resources.StaticMeshVertexBuffer.UV[i];
            RequireLength(uv.UV, extraUvs.Length + 1, $"Geometry collection UV channels for vertex {i}");
            vertices[i] = new MeshVertex(
                resources.PositionVertexBuffer.Verts[i],
                uv.Normal[2],
                uv.Normal[0],
                uv.UV[0]);

            for (var channel = 0; channel < extraUvs.Length; channel++)
            {
                extraUvs[channel][i] = uv.UV[channel + 1];
            }
        }

        var sections = new MeshSectionDto[description.Sections.Length];
        for (var i = 0; i < sections.Length; i++)
        {
            sections[i] = new MeshSectionDto(description.Sections[i]);
        }

        return new MeshLodDto<MeshVertex>(
            owner,
            sourceLodIndex,
            resources.IndexBuffer.Buffer,
            vertices,
            sections,
            extraUvs,
            vertexColors);
    }

    internal static MeshLodDto<MeshVertex> FromArrayCollection(
        StaticMeshDto owner,
        uint sourceLodIndex,
        FManagedArrayCollection collection)
    {
        var verticesGroup = new FName("Vertices");
        var positions = collection.GetAttributeValue<FVector>("Vertex", verticesGroup);
        var normals = collection.GetAttributeValue<FVector>("Normal", verticesGroup);
        var tangentU = collection.GetAttributeValue<FVector>("TangentU", verticesGroup);
        var tangentV = collection.GetAttributeValue<FVector>("TangentV", verticesGroup);
        var uvLayer0 = collection.GetAttributeValue<FVector2D>("UVLayer0", verticesGroup);
        var boneMap = collection.GetAttributeValue<int>("BoneMap", verticesGroup);

        var vertexCount = positions.Length;
        RequireLength(normals, vertexCount, "Geometry collection normals");
        RequireLength(tangentU, vertexCount, "Geometry collection tangent U");
        RequireLength(tangentV, vertexCount, "Geometry collection tangent V");
        RequireLength(uvLayer0, vertexCount, "Geometry collection UV layer 0");

        var vertices = new MeshVertex[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var sign = FVector.DotProduct(FVector.CrossProduct(normals[i], tangentU[i]), tangentV[i]) < 0f ? -1f : 1f;
            vertices[i] = new MeshVertex(
                positions[i],
                new FVector4(normals[i], sign),
                new FVector4(tangentU[i], sign),
                new FMeshUVFloat(uvLayer0[i].X, uvLayer0[i].Y));
        }

        var extraUvLayers = new List<FMeshUVFloat[]>();
        for (var layer = 1; layer <= 2; layer++)
        {
            if (!collection.TryGetAttributeValue<FVector2D>($"UVLayer{layer}", verticesGroup, out var sourceLayer))
                continue;

            RequireLength(sourceLayer, vertexCount, $"Geometry collection UV layer {layer}");
            var targetLayer = new FMeshUVFloat[vertexCount];
            for (var i = 0; i < vertexCount; i++)
                targetLayer[i] = new FMeshUVFloat(sourceLayer[i].X, sourceLayer[i].Y);
            extraUvLayers.Add(targetLayer);
        }

        FColor[]? vertexColors = null;
        if (collection.TryGetAttributeValue<FLinearColor>("Color", verticesGroup, out var colors) && colors.Length > 0)
        {
            RequireLength(colors, vertexCount, "Geometry collection vertex colors");
            vertexColors = new FColor[vertexCount];
            for (var i = 0; i < vertexCount; i++)
                vertexColors[i] = colors[i].ToFColor(true);
        }
        else if (collection.TryGetAttributeValue<FLinearColor>("BoneColor", "Transform", out var boneColors))
        {
            RequireLength(boneMap, vertexCount, "Geometry collection bone map");
            vertexColors = BuildBoneColors(boneMap, boneColors, vertexCount);
        }

        var sourceIndices = collection.GetAttributeValue<FIntVector>("Indices", "Faces");
        var indices = new uint[sourceIndices.Length * 3];
        for (var i = 0; i < sourceIndices.Length; i++)
        {
            var triangle = sourceIndices[i];
            ValidateVertexIndex(triangle.X, vertexCount);
            ValidateVertexIndex(triangle.Y, vertexCount);
            ValidateVertexIndex(triangle.Z, vertexCount);
            indices[i * 3] = (uint) triangle.X;
            indices[i * 3 + 1] = (uint) triangle.Y;
            indices[i * 3 + 2] = (uint) triangle.Z;
        }

        var sourceSections = collection.GetAttributeValue<FGeometryCollectionSection>("Sections", "Material");
        var sections = new MeshSectionDto[sourceSections.Length];
        for (var i = 0; i < sections.Length; i++)
            sections[i] = new MeshSectionDto(sourceSections[i]);

        return new MeshLodDto<MeshVertex>(
            owner,
            sourceLodIndex,
            indices,
            vertices,
            sections,
            extraUvLayers.ToArray(),
            vertexColors);
    }

    private static FColor[] BuildBoneColors(ushort[] boneMap, FLinearColor[] boneColors, int vertexCount) =>
        BuildBoneColors(index => boneMap[index], boneColors, vertexCount);

    private static FColor[] BuildBoneColors(int[] boneMap, FLinearColor[] boneColors, int vertexCount) =>
        BuildBoneColors(index => boneMap[index], boneColors, vertexCount);

    private static FColor[] BuildBoneColors(Func<int, int> getBoneIndex, FLinearColor[] boneColors, int vertexCount)
    {
        var result = new FColor[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var boneIndex = getBoneIndex(i);
            if ((uint) boneIndex >= (uint) boneColors.Length)
                throw new InvalidDataException($"Geometry collection bone index {boneIndex} is outside the bone color table");
            result[i] = boneColors[boneIndex].ToFColor(true);
        }
        return result;
    }

    private static void ValidateVertexIndex(int index, int vertexCount)
    {
        if ((uint) index >= (uint) vertexCount)
            throw new InvalidDataException($"Geometry collection vertex index {index} is outside the vertex buffer");
    }

    private static void RequireLength<T>(T[] values, int required, string field)
    {
        if (values.Length < required)
            throw new InvalidDataException($"{field} has {values.Length} entries but requires at least {required}");
    }
}
