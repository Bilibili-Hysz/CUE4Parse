using CUE4Parse_Conversion.Materials;
using CUE4Parse.UE4.Assets.Exports.Material;
using System.Security.Cryptography;
using System.Text;

namespace CUE4Parse_Conversion.UEFormat.MaterialLinks;

public enum MaterialExportResource
{
    Texture,
    Material
}

public sealed class MaterialLinkSourceParameter
{
    private MaterialLinkSourceParameter(string name, string unrealTexturePath, string textureUri, string mime, byte[] imageBytes, bool isMaterialized)
    {
        Name = name;
        UnrealTexturePath = unrealTexturePath;
        TextureUri = textureUri;
        Mime = mime;
        ImageBytes = imageBytes;
        IsMaterialized = isMaterialized;
        Kind = 1;
    }

    private MaterialLinkSourceParameter(string name, float scalar)
    {
        Name = name;
        ScalarValue = scalar;
        Kind = 2;
    }

    private MaterialLinkSourceParameter(string name, float x, float y, float z, float w)
    {
        Name = name;
        VectorValue = [x, y, z, w];
        Kind = 3;
    }

    public string Name { get; }
    public byte Kind { get; }
    public string UnrealTexturePath { get; } = string.Empty;
    public string TextureUri { get; } = string.Empty;
    public string Mime { get; } = string.Empty;
    public byte[] ImageBytes { get; } = [];
    public bool IsMaterialized { get; }
    public float ScalarValue { get; }
    public float[]? VectorValue { get; }

    public static MaterialLinkSourceParameter Texture(string name, string unrealTexturePath, string textureUri, string mime, byte[] imageBytes, bool isMaterialized = true) => new(name, unrealTexturePath, textureUri, mime, imageBytes, isMaterialized);
    public static MaterialLinkSourceParameter Scalar(string name, float value) => new(name, value);
    public static MaterialLinkSourceParameter Vector(string name, float x, float y, float z, float w) => new(name, x, y, z, w);
}

public static class MaterialLinkBuilder
{
    public static string NormalizeUnrealMaterialPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path = path.Trim();
        if (path.StartsWith("Game/", StringComparison.Ordinal) || path.StartsWith("Engine/", StringComparison.Ordinal))
            path = "/" + path;
        if (path.Length < 4 || !path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return path;

        var slash = path.LastIndexOf('/');
        if (slash <= 1) return path;
        var packageSegments = path[1..slash].Split('/');
        if (packageSegments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".." || segment.Contains('.')))
            return path;

        var terminal = path[(slash + 1)..];
        var dot = terminal.IndexOf('.');
        if (dot > 0 && dot == terminal.LastIndexOf('.') && dot < terminal.Length - 1 &&
            StringComparer.Ordinal.Equals(terminal[..dot], terminal[(dot + 1)..]))
            return path[..(slash + 1 + dot)];
        return path;
    }

    public static FModelMaterialLinks BuildForMaterialSlots(MaterialLinkMode mode, string modelPath, IEnumerable<(int SlotIndex, MaterialExporter2 Exporter)> materials, string? bundleRoot = null)
    {
        var links = new FModelMaterialLinks();
        foreach (var (slotIndex, exporter) in materials)
        {
            var materialLinks = exporter.BuildMaterialLinks(mode, modelPath, slotIndex, slotIndex.ToString(), bundleRoot);
            links.Slots.AddRange(materialLinks.Slots);
            links.PbrInterfaces.AddRange(materialLinks.PbrInterfaces);
        }
        return links;
    }

    public static FModelMaterialLinks Build(MaterialLinkMode mode, string modelPath, string materialJsonPath, string unrealMaterialPath, IEnumerable<MaterialLinkSourceParameter> sourceParameters, string? bundleRoot = null, int slotIndex = 0, string? slotName = null, bool finalUris = false)
    {
        if (mode == MaterialLinkMode.None) return new FModelMaterialLinks();
        if (mode == MaterialLinkMode.Bundle) ValidateBundleRoot(bundleRoot);
        var sources = sourceParameters.ToList();
        var semantics = Semantics(mode, sources);
        var materialUri = finalUris ? materialJsonPath : BuildUri(mode, modelPath, materialJsonPath, bundleRoot);
        var parameters = new List<FModelMaterialParameter>();
        var emitted = new Dictionary<MaterialLinkSourceParameter, int>();
        foreach (var source in sources)
        {
            var semantic = semantics.TryGetValue(source, out var selectedSemantic) ? selectedSemantic : "custom";
            switch (source.Kind)
            {
                case 1 when source.IsMaterialized && (mode != MaterialLinkMode.Embedded || source.ImageBytes.Length > 0):
                    parameters.Add(FModelMaterialParameter.Texture(source.Name, semantic, new FModelTextureReference
                    {
                        UnrealObjectPath = source.UnrealTexturePath,
                         Uri = finalUris ? source.TextureUri : BuildUri(mode, modelPath, source.TextureUri, bundleRoot),
                        Mime = source.Mime,
                        ImageBytes = source.ImageBytes
                    }));
                    emitted[source] = parameters.Count - 1;
                    break;
                case 2:
                    parameters.Add(FModelMaterialParameter.Scalar(source.Name, semantic, source.ScalarValue));
                    emitted[source] = parameters.Count - 1;
                    break;
                case 3:
                    var vector = source.VectorValue!;
                    parameters.Add(FModelMaterialParameter.Vector(source.Name, semantic, vector[0], vector[1], vector[2], vector[3]));
                    emitted[source] = parameters.Count - 1;
                    break;
            }
        }

        var links = new FModelMaterialLinks { Slots = [new FModelMaterialSlot { SlotIndex = slotIndex, SlotName = slotName ?? slotIndex.ToString(), UnrealMaterialPath = NormalizeUnrealMaterialPath(unrealMaterialPath), MaterialJsonUri = materialUri, Parameters = parameters }] };
        AddPbrInterfaces(links.PbrInterfaces, slotIndex, sources, emitted);
        return links;
    }

    public static string BuildModelPath(MaterialExportLayout layout, string modelPath, string? identity = null)
    {
        var model = Normalize(modelPath);
        if (layout != MaterialExportLayout.Categorized) return model;
        return BuildCategorizedPath("Models", Path.GetFileName(model), identity);
    }

    public static string BuildResourcePath(MaterialExportLayout layout, string resourcePath, MaterialExportResource resource, string? identity = null)
    {
        var path = Normalize(resourcePath);
        if (layout != MaterialExportLayout.Categorized) return path;
        return BuildCategorizedPath(resource == MaterialExportResource.Texture ? "Textures" : "Materials", Path.GetFileName(path), identity);
    }

    public static string BuildUri(MaterialLinkMode mode, string modelPath, string targetPath, string? bundleRoot, MaterialExportLayout layout = MaterialExportLayout.PreserveHierarchy)
    {
        var target = Normalize(targetPath);
        if (mode == MaterialLinkMode.Embedded) return target;
        if (mode == MaterialLinkMode.Relative)
        {
            return RelativeResourceUri.FromOwner(modelPath, target);
        }
        if (mode != MaterialLinkMode.Bundle) throw new ArgumentOutOfRangeException(nameof(mode));
        ValidateBundleRoot(bundleRoot);
        return target;
    }

    public static void ValidateBundleRoot(string? bundleRoot)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot) || !Path.IsPathRooted(bundleRoot))
            throw new ArgumentException("Bundle mode requires an absolute physical bundle root.", nameof(bundleRoot));
    }

    private static string Normalize(string path) =>
        RelativeResourceUri.NormalizeLogicalPath(path, nameof(path));

    private static string BuildCategorizedPath(string category, string filename, string? identity)
    {
        if (string.IsNullOrEmpty(identity)) return category + "/" + filename;

        // Categorized bundles flatten directories, so retain the readable name and add a stable identity suffix.
        var extension = Path.GetExtension(filename);
        var basename = filename[..^extension.Length];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return category + "/" + basename + "-" + hash + extension;
    }

    private static Dictionary<MaterialLinkSourceParameter, string> Semantics(MaterialLinkMode mode, IReadOnlyList<MaterialLinkSourceParameter> sources)
    {
        var textures = sources.Where(source => source.Kind == 1 && source.IsMaterialized && (mode != MaterialLinkMode.Embedded || source.ImageBytes.Length > 0)).ToList();
        var semantics = new Dictionary<MaterialLinkSourceParameter, string>();

        AddTextureSemantic(semantics, textures, "base_color", CMaterialParams2.Diffuse[0], CMaterialParams2.FallbackDiffuse, true);
        AddTextureSemantic(semantics, textures, "normal", CMaterialParams2.Normals[0], CMaterialParams2.FallbackNormals, false);
        AddTextureSemantic(semantics, textures, "emissive", CMaterialParams2.Emissive[0], null, false);

        return semantics;
    }

    private static void AddTextureSemantic(Dictionary<MaterialLinkSourceParameter, string> semantics, IReadOnlyList<MaterialLinkSourceParameter> textures, string semantic, IEnumerable<string> candidates, string? fallback, bool useFirstTexture)
    {
        var selected = SelectTexture(textures, candidates, fallback, useFirstTexture);
        if (selected is not null) semantics[selected] = semantic;
    }

    private static MaterialLinkSourceParameter? SelectTexture(IReadOnlyList<MaterialLinkSourceParameter> textures, IEnumerable<string> candidates, string? fallback, bool useFirstTexture)
    {
        return candidates.Select(candidate => textures.FirstOrDefault(texture => texture.Name == candidate)).FirstOrDefault(texture => texture is not null)
            ?? (fallback is null ? null : textures.FirstOrDefault(texture => texture.Name == fallback))
            ?? (useFirstTexture ? textures.FirstOrDefault() : null);
    }

    private static void AddPbrInterfaces(List<FModelPbrInterface> roles, int slotIndex, IReadOnlyList<MaterialLinkSourceParameter> sources, IReadOnlyDictionary<MaterialLinkSourceParameter, int> emitted)
    {
        void Add(MaterialLinkSourceParameter? source, FModelPbrRole role)
        {
            if (source is not null && emitted.TryGetValue(source, out var index))
                roles.Add(new FModelPbrInterface { SlotIndex = slotIndex, ParameterIndex = index, Role = role });
        }

        var textures = sources.Where(source => source.Kind == 1 && emitted.ContainsKey(source)).ToList();
        var scalars = sources.Where(source => source.Kind == 2 && emitted.ContainsKey(source)).ToList();
        var vectors = sources.Where(source => source.Kind == 3 && emitted.ContainsKey(source)).ToList();
        MaterialLinkSourceParameter? First(IReadOnlyList<MaterialLinkSourceParameter> candidates, params string[] names) => names.Select(name => candidates.FirstOrDefault(source => source.Name == name)).FirstOrDefault(source => source is not null);

        var baseColor = SelectTexture(textures, CMaterialParams2.Diffuse[0], CMaterialParams2.FallbackDiffuse, true);
        var emissive = SelectTexture(textures, CMaterialParams2.Emissive[0], null, false);
        Add(baseColor, FModelPbrRole.BaseColor);
        if (baseColor is not null) Add(First(vectors.Where(vector => vector.VectorValue![3] > 0).ToList(), [.. CMaterialParams2.DiffuseColors[0]]), FModelPbrRole.BaseColorFactor);
        Add(First(textures, [.. CMaterialParams2.Normals[0], CMaterialParams2.FallbackNormals]), FModelPbrRole.Normal);
        Add(emissive, FModelPbrRole.Emissive);
        if (emissive is not null) Add(First(vectors.Where(vector => vector.VectorValue![3] > 0).ToList(), [.. CMaterialParams2.EmissiveColors[0]]), FModelPbrRole.EmissiveColorFactor);
        Add(First(scalars, "emissive mult", "Emissive_Mult", "EmissiveIntensity", "EmissionIntensity") ?? First(vectors, "Emissive Multiplier", "EmissiveMultiplier"), FModelPbrRole.EmissiveStrength);
        Add(First(scalars, "RoughnessMin", "SpecRoughnessMin"), FModelPbrRole.RoughnessMin);
        Add(First(scalars, "RoughnessMax", "SpecRoughnessMax"), FModelPbrRole.RoughnessMax);
        Add(First(scalars, "Rough", "Roughness", "Ro Multiplier", "RO_mul", "Roughness_Mult"), FModelPbrRole.Roughness);

        var ao = First(textures, "M", "AEM", "AO");
        if (ao is not null && !HasUnrealObjectBasename(ao.UnrealTexturePath, "T_BlackMask"))
        {
            Add(ao, FModelPbrRole.Ao);
            if (ao is not null)
            {
                var skinBoost = First(vectors, "Skin Boost Color And Exponent");
                Add(skinBoost, FModelPbrRole.AoColorBoost);
                Add(skinBoost, FModelPbrRole.AoStrength);
            }
        }
    }

    private static bool HasUnrealObjectBasename(string path, string basename)
    {
        var objectPath = path.Trim().Trim('"', '\'');
        var separator = objectPath.LastIndexOf('/');
        var leaf = objectPath[(separator + 1)..];
        var dot = leaf.IndexOf('.');
        return string.Equals(dot >= 0 ? leaf[..dot] : leaf, basename, StringComparison.Ordinal);
    }
}
