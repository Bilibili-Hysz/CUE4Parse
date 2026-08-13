using System.Security.Cryptography;
using System.Text;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;

namespace CUE4Parse_Conversion.UEScene;

/// <summary>Allocates the logical URIs shared by UEScene planning and production.</summary>
public sealed class UESceneResourceUriPolicy
{
    public UESceneResourceLogicalRoot LogicalRoot { get; }

    public UESceneResourceUriPolicy(UESceneResourceLogicalRoot logicalRoot) => LogicalRoot = UESceneExportDestination.NormalizeLogicalRoot(logicalRoot);

    public string Allocate(string canonicalUnrealObjectPath, UESceneResourceKind resource, string extension)
    {
        if (string.IsNullOrWhiteSpace(canonicalUnrealObjectPath))
            throw new InvalidDataException("UEScene resource identity must be a canonical Unreal object path.");
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.' || extension.Length == 1 ||
            extension.IndexOfAny(['/','\\',':']) >= 0)
            throw new InvalidDataException("UEScene resource extension must be a simple dot-prefixed extension.");

        var normalized = canonicalUnrealObjectPath.Trim();
        if (normalized.Contains('\\') || normalized.Contains('\0'))
            throw new ArgumentException("UEScene resource identity must use safe POSIX separators.", nameof(canonicalUnrealObjectPath));

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            // MaterialExporter2 still supplies legacy Game/Engine package spellings
            // for some assets. They remain hash-addressed rather than being silently
            // reinterpreted as a Content URI. Other relative spellings are rejected.
            if (!normalized.StartsWith("Game/", StringComparison.Ordinal) &&
                !normalized.StartsWith("Engine/", StringComparison.Ordinal))
                throw new ArgumentException("UEScene resource identity must be a canonical Unreal object path.", nameof(canonicalUnrealObjectPath));
            if (normalized.Split('/', StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
                throw new ArgumentException("UEScene resource identity contains an unsafe or empty segment.", nameof(canonicalUnrealObjectPath));
        }
        else if (UESceneProjectExportUri.IsContentRootedUnrealPath(normalized))
        {
            return UESceneProjectExportUri.FromUnrealPath(normalized, extension);
        }

        // Engine/plugin mounts without a Content root, and legacy relative mount
        // spellings, remain addressable without pretending to be project Content.
        return "Content/External/" + LastName(normalized) + "-" +
               IdentityHash(normalized) + extension;
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
