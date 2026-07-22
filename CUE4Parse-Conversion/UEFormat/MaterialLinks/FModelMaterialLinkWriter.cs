using CUE4Parse_Conversion.UEFormat.Structs;
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.UEFormat.MaterialLinks;

public enum MaterialLinkMode
{
    None,
    Relative,
    Bundle,
    Embedded
}

public sealed class FModelMaterialLinks
{
    public List<FModelMaterialSlot> Slots { get; init; } = [];
    public List<FModelPbrInterface> PbrInterfaces { get; } = [];
}

public enum FModelPbrRole : byte
{
    BaseColor = 1,
    BaseColorFactor,
    Normal,
    Emissive,
    EmissiveColorFactor,
    EmissiveStrength,
    RoughnessMin,
    RoughnessMax,
    Roughness,
    Ao,
    AoColorBoost,
    AoStrength
}

public sealed class FModelPbrInterface
{
    public int SlotIndex { get; init; }
    public int ParameterIndex { get; init; }
    public FModelPbrRole Role { get; init; }
}

public sealed class FModelMaterialSlot
{
    public int SlotIndex { get; init; }
    public string SlotName { get; init; } = string.Empty;
    public string UnrealMaterialPath { get; init; } = string.Empty;
    public string MaterialJsonUri { get; init; } = string.Empty;
    public List<FModelMaterialParameter> Parameters { get; init; } = [];
}

public sealed class FModelMaterialParameter
{
    private FModelMaterialParameter(string name, string semantic, FModelTextureReference texture)
    {
        Name = name;
        Semantic = semantic;
        TextureValue = texture;
        Kind = 1;
    }

    private FModelMaterialParameter(string name, string semantic, float scalar)
    {
        Name = name;
        Semantic = semantic;
        ScalarValue = scalar;
        Kind = 2;
    }

    private FModelMaterialParameter(string name, string semantic, float x, float y, float z, float w)
    {
        Name = name;
        Semantic = semantic;
        VectorValue = [x, y, z, w];
        Kind = 3;
    }

    public string Name { get; }
    public string Semantic { get; }
    public byte Kind { get; }
    public FModelTextureReference? TextureValue { get; }
    public float ScalarValue { get; }
    public float[]? VectorValue { get; }

    public static FModelMaterialParameter Texture(string name, string semantic, FModelTextureReference texture) => new(name, semantic, texture);
    public static FModelMaterialParameter Scalar(string name, string semantic, float value) => new(name, semantic, value);
    public static FModelMaterialParameter Vector(string name, string semantic, float x, float y, float z, float w) => new(name, semantic, x, y, z, w);
}

public sealed class FModelTextureReference
{
    public string UnrealObjectPath { get; init; } = string.Empty;
    public string Uri { get; init; } = string.Empty;
    public string Mime { get; init; } = string.Empty;
    public byte[] ImageBytes { get; init; } = [];
    public byte ColorSpace { get; init; }
    public byte Packing { get; init; }
    public int UvSet { get; init; }
}

public static class FModelMaterialLinkWriter
{
    private const byte ExtensionVersion = 1;

    public static void Write(FArchiveWriter archive, MaterialLinkMode mode = MaterialLinkMode.None, FModelMaterialLinks? links = null)
    {
        if (mode == MaterialLinkMode.None || links?.Slots.Count is not > 0) return;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));

        var textures = mode == MaterialLinkMode.Embedded ? CollectEmbeddedTextures(links) : [];
        Validate(links, mode);
        using var extensionArchive = new FArchiveWriter();
        using (var materialsChunk = new FDataChunk("FMODEL_MATERIALS", links.Slots.Count))
        {
            materialsChunk.Write(ExtensionVersion);
            materialsChunk.Write(links.Slots.Count);
            foreach (var slot in links.Slots)
            {
                materialsChunk.Write(slot.SlotIndex);
                materialsChunk.WriteFString(slot.SlotName);
                materialsChunk.WriteFString(slot.UnrealMaterialPath);
                materialsChunk.WriteFString(slot.MaterialJsonUri);
                materialsChunk.Write(slot.Parameters.Count);
                foreach (var parameter in slot.Parameters)
                {
                    materialsChunk.WriteFString(parameter.Name);
                    materialsChunk.Write(parameter.Kind);
                    materialsChunk.WriteFString(parameter.Semantic);
                    WriteParameter(materialsChunk, parameter, mode, textures);
                }
            }
            materialsChunk.Serialize(extensionArchive);
        }

        if (textures.Count > 0)
        {
            using var texturesChunk = new FDataChunk("FMODEL_TEXTURES", textures.Count);
            texturesChunk.Write(ExtensionVersion);
            texturesChunk.Write(textures.Count);
            foreach (var texture in textures)
            {
                texturesChunk.Write(texture.Id);
                texturesChunk.WriteFString(texture.Reference.UnrealObjectPath);
                texturesChunk.WriteFString(texture.Reference.Mime);
                texturesChunk.Write(texture.Reference.ImageBytes.Length);
                texturesChunk.Write(texture.Reference.ImageBytes);
            }
            texturesChunk.Serialize(extensionArchive);
        }

        WritePbrInterfaces(extensionArchive, links);
        archive.Write(extensionArchive.GetBuffer());
    }

    private static void WritePbrInterfaces(FArchiveWriter archive, FModelMaterialLinks links)
    {
        if (links.PbrInterfaces.Count == 0) return;
        using var interfacesChunk = new FDataChunk("FMODEL_PBR_INTERFACES", links.PbrInterfaces.Count);
        interfacesChunk.Write((byte) 2);
        interfacesChunk.Write(links.PbrInterfaces.Count);
        foreach (var role in links.PbrInterfaces)
        {
            interfacesChunk.Write(role.SlotIndex);
            interfacesChunk.Write(role.ParameterIndex);
            interfacesChunk.Write((byte) role.Role);
        }
        interfacesChunk.Serialize(archive);
    }

    private static void Validate(FModelMaterialLinks links, MaterialLinkMode mode)
    {
        var slots = new Dictionary<int, FModelMaterialSlot>();
        foreach (var slot in links.Slots)
        {
            if (slot.SlotIndex < 0) throw new ArgumentException("Material slot index must be non-negative.", nameof(links));
            if (!slots.TryAdd(slot.SlotIndex, slot)) throw new ArgumentException("Material slot indexes must be unique.", nameof(links));
            ValidateLogicalUri(slot.MaterialJsonUri, nameof(slot.MaterialJsonUri), mode, allowRelativeTraversal: false);

            foreach (var parameter in slot.Parameters)
            {
                ValidateParameter(parameter, mode);
            }
        }

        var roles = new HashSet<(int SlotIndex, FModelPbrRole Role)>();
        foreach (var pbrInterface in links.PbrInterfaces)
        {
            if (!slots.TryGetValue(pbrInterface.SlotIndex, out var slot)) throw new ArgumentException("PBR interface references an undefined material slot.", nameof(links));
            if (pbrInterface.ParameterIndex < 0 || pbrInterface.ParameterIndex >= slot.Parameters.Count) throw new ArgumentException("PBR interface parameter index is invalid.", nameof(links));
            if (!Enum.IsDefined(pbrInterface.Role)) throw new ArgumentException("PBR interface role is invalid.", nameof(links));
            if (!roles.Add((pbrInterface.SlotIndex, pbrInterface.Role))) throw new ArgumentException("PBR interface slot and role pairs must be unique.", nameof(links));
        }
    }

    private static void ValidateParameter(FModelMaterialParameter parameter, MaterialLinkMode mode)
    {
        switch (parameter.Kind)
        {
            case 1:
                if (parameter.TextureValue is null) throw new ArgumentException("Texture parameter is missing its texture reference.", nameof(parameter));
                ValidateLogicalUri(parameter.TextureValue.Uri, nameof(parameter.TextureValue.Uri), mode, allowRelativeTraversal: true);
                break;
            case 2:
                break;
            case 3:
                if (parameter.VectorValue?.Length != 4) throw new ArgumentException("Vector parameter must contain four values.", nameof(parameter));
                break;
            default:
                throw new ArgumentException($"Unsupported FModel material parameter kind: {parameter.Kind}", nameof(parameter));
        }
    }

    private static void ValidateLogicalUri(string value, string parameterName, MaterialLinkMode mode, bool allowRelativeTraversal)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0 || value.IndexOf('\\') >= 0 || value[0] == '/' || IsDrivePath(value))
            throw new ArgumentException("Logical URI must be a non-rooted slash-separated path.", parameterName);

        if (value.Split('/').Any(segment => segment is "" or "."))
            throw new ArgumentException("Logical URI contains an empty or traversal segment.", parameterName);
        if (!allowRelativeTraversal && mode == MaterialLinkMode.Relative && value.StartsWith("../", StringComparison.Ordinal) && value.Split('/').Skip(1).All(segment => segment != ".."))
            return;
        if (!allowRelativeTraversal && value.Split('/').Any(segment => segment == ".."))
            throw new ArgumentException("Bundle URI contains a traversal segment.", parameterName);
    }


    private static bool IsDrivePath(string value) => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static void WriteParameter(FArchiveWriter archive, FModelMaterialParameter parameter, MaterialLinkMode mode, List<EmbeddedTexture> textures)
    {
        switch (parameter.Kind)
        {
            case 1:
                var texture = parameter.TextureValue ?? throw new InvalidOperationException("Texture parameter is missing its texture reference.");
                archive.Write(LocationMode(mode));
                archive.WriteFString(texture.Uri);
                archive.Write(mode == MaterialLinkMode.Embedded ? FindEmbeddedTextureId(textures, texture) : -1);
                archive.WriteFString(texture.UnrealObjectPath);
                archive.Write(texture.ColorSpace);
                archive.Write(texture.Packing);
                archive.Write(texture.UvSet);
                break;
            case 2:
                archive.Write(parameter.ScalarValue);
                break;
            case 3:
                var vector = parameter.VectorValue ?? throw new InvalidOperationException("Vector parameter is missing its value.");
                foreach (var value in vector) archive.Write(value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported FModel material parameter kind: {parameter.Kind}");
        }
    }

    private static byte LocationMode(MaterialLinkMode mode) => mode switch
    {
        MaterialLinkMode.Relative => 1,
        MaterialLinkMode.Bundle => 2,
        MaterialLinkMode.Embedded => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static List<EmbeddedTexture> CollectEmbeddedTextures(FModelMaterialLinks links)
    {
        var textures = new List<EmbeddedTexture>();
        foreach (var texture in links.Slots.SelectMany(slot => slot.Parameters).Where(parameter => parameter.Kind == 1).Select(parameter => parameter.TextureValue!))
        {
            if (texture.ImageBytes.Length == 0) throw new ArgumentException("Embedded resource requires non-empty bytes.", nameof(links));
            if (textures.Any(existing => existing.Matches(texture))) continue;
            textures.Add(new EmbeddedTexture(textures.Count, texture));
        }
        return textures;
    }

    private static int FindEmbeddedTextureId(List<EmbeddedTexture> textures, FModelTextureReference texture)
    {
        var embedded = textures.FirstOrDefault(candidate => candidate.Matches(texture));
        return embedded?.Id ?? -1;
    }

    private sealed class EmbeddedTexture(int id, FModelTextureReference reference)
    {
        public int Id { get; } = id;
        public FModelTextureReference Reference { get; } = reference;

        public bool Matches(FModelTextureReference other) =>
            StringComparer.Ordinal.Equals(Reference.UnrealObjectPath, other.UnrealObjectPath) &&
            Reference.ImageBytes.AsSpan().SequenceEqual(other.ImageBytes);
    }
}
