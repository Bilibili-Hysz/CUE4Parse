using System.Security.Cryptography;
using System.Text;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;

namespace CUE4Parse_Conversion.UEScene;

/// <summary>Allocates the logical URIs shared by UEScene planning and production.</summary>
public sealed class UESceneResourceUriPolicy
{
    public UESceneResourceLogicalRoot LogicalRoot { get; }

    public UESceneResourceUriPolicy(UESceneResourceLogicalRoot logicalRoot) => LogicalRoot = logicalRoot;

    public string Allocate(string canonicalUnrealObjectPath, UESceneResourceKind resource, string extension)
    {
        if (string.IsNullOrWhiteSpace(canonicalUnrealObjectPath))
            throw new InvalidDataException("UEScene resource identity must be a canonical Unreal object path.");
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.')
            throw new InvalidDataException("UEScene resource extension must start with a dot.");

        if (LogicalRoot == UESceneResourceLogicalRoot.GameExportTree)
            return canonicalUnrealObjectPath.StartsWith("/Game/", StringComparison.Ordinal)
                ? UESceneProjectExportUri.FromUnrealPath(canonicalUnrealObjectPath, extension)
                : "Content/External/" + LastName(canonicalUnrealObjectPath) + "-" + IdentityHash(canonicalUnrealObjectPath) + extension;

        var existingPath = "Game/" + LastName(canonicalUnrealObjectPath) + extension;
        return resource == UESceneResourceKind.Model
            ? MaterialLinkBuilder.BuildModelPath(MaterialExportLayout.Categorized, existingPath, canonicalUnrealObjectPath)
            : MaterialLinkBuilder.BuildResourcePath(MaterialExportLayout.Categorized, existingPath,
                resource == UESceneResourceKind.Material ? MaterialExportResource.Material : MaterialExportResource.Texture, canonicalUnrealObjectPath);
    }

    public string AllocateVariant(string canonicalUnrealObjectPath, string variantIdentity, UESceneResourceKind resource, string extension)
    {
        var uri = Allocate(canonicalUnrealObjectPath, resource, extension);
        var suffix = "-variant-" + IdentityHash(variantIdentity)[..16];
        return uri[..^extension.Length] + suffix + extension;
    }

    private static string LastName(string path) => path.Split(['/','.'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Resource";
    private static string IdentityHash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
}

public enum UESceneResourceKind { Model, Material, Texture }
