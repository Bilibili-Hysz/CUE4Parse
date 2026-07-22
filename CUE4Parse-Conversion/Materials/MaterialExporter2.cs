using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using CUE4Parse_Conversion.UEScene;
using Newtonsoft.Json;

namespace CUE4Parse_Conversion.Materials
{
    public struct MaterialData
    {
        public Dictionary<string, string> Textures;
        public CMaterialParams2 Parameters;
    }

    public class MaterialExporter2 : ExporterBase
    {
        private readonly string _internalFilePath;
        private readonly string _canonicalUnrealObjectPath;
        private readonly MaterialData _materialData;
        private MaterialLinkExportResult? _cachedResult;

        public MaterialExporter2(ExporterOptions options)
        {
            Options = options;
            _internalFilePath = string.Empty;
            _canonicalUnrealObjectPath = string.Empty;
            _materialData = new MaterialData
            {
                Textures = new Dictionary<string, string>(),
                Parameters = new CMaterialParams2()
            };
        }

        public MaterialExporter2(UUnrealMaterial? unrealMaterial, ExporterOptions options) : this(options)
        {
            if (unrealMaterial == null) return;
            _internalFilePath = (unrealMaterial.Owner?.Provider?.FixPath(unrealMaterial.Owner.Name) ??
                                  unrealMaterial.Name).SubstringBeforeLast('.');
            _canonicalUnrealObjectPath = unrealMaterial.GetPathName();

            unrealMaterial.GetParams(_materialData.Parameters, Options.MaterialFormat);
            foreach ((string key, UUnrealMaterial value) in _materialData.Parameters.Textures)
            {
                _materialData.Textures[key] = value.GetPathName();
            }
        }

        public sealed class TextureExportResult
        {
            public string UnrealObjectPath { get; init; } = string.Empty;
            public string Uri { get; init; } = string.Empty;
            public string Mime { get; init; } = string.Empty;
            public byte[] Bytes { get; init; } = [];
        }

        public sealed class MaterialLinkExportResult
        {
            public string JsonPath { get; init; } = string.Empty;
            public IReadOnlyList<TextureExportResult> Textures { get; init; } = [];
            public IReadOnlyList<string> SkippedTextures { get; init; } = [];
        }

        public MaterialLinkExportResult GetMaterialLinkExportResult()
        {
            if (_cachedResult != null)
                return _cachedResult;

            var textures = new Dictionary<string, TextureExportResult>();
            var skipped = new List<string>();
            foreach (var texture in _materialData.Parameters.Textures.Values.OfType<UTexture2D>())
            {
                var objectPath = texture.GetPathName();
                if (textures.ContainsKey(objectPath)) continue;
                try
                {
                    if (texture.Decode(Options.Platform) is not { } bitmap) { skipped.Add(objectPath); continue; }
                    var bytes = bitmap.Encode(Options.TextureFormat, Options.ExportHdrTexturesAsHdr, out var ext);
                    if (bytes.Length == 0) { skipped.Add(objectPath); continue; }
                    textures[objectPath] = new TextureExportResult { UnrealObjectPath = objectPath, Uri = (texture.Owner?.Provider?.FixPath(texture.Owner.Name) ?? texture.Name).SubstringBeforeLast('.') + "." + ext, Mime = Mime(ext), Bytes = bytes };
                }
                catch (Exception)
                {
                    skipped.Add(objectPath);
                }
            }
            _cachedResult = new MaterialLinkExportResult { JsonPath = _internalFilePath + ".json", Textures = textures.Values.ToList(), SkippedTextures = skipped };
            return _cachedResult;
        }

        /// <summary>
        /// Populates a ResourceManifest with this material's texture resources.
        /// Textures are decoded exactly once (cached) and contributed to the manifest.
        /// Call this when the caller wants ordered resource-first write lifecycle.
        /// </summary>
    public void PopulateManifest(ResourceManifest manifest, MaterialLinkMode mode, string modelPath, string? bundleRoot = null, MaterialExportLayout layout = MaterialExportLayout.PreserveHierarchy)
        {
            var result = GetMaterialLinkExportResult();
            foreach (var texture in result.Textures)
            {
                var isExternal = mode != MaterialLinkMode.Embedded;
                var targetPath = Options.UESceneResourceUriPolicy?.Allocate(texture.UnrealObjectPath, UESceneResourceKind.Texture, Path.GetExtension(texture.Uri))
                    ?? BuildTargetPath(mode, layout, TrimLogicalRoot(texture.Uri), MaterialExportResource.Texture, texture.UnrealObjectPath);
                manifest.AddOrGet(texture.UnrealObjectPath, MaterialLinkBuilder.BuildUri(mode, modelPath, targetPath, bundleRoot, layout), texture.Mime, texture.Bytes, isExternal, targetPath);
            }
            if (mode != MaterialLinkMode.Embedded)
            {
                var json = JsonConvert.SerializeObject(_materialData, Formatting.Indented);
                var jsonTargetPath = Options.UESceneResourceUriPolicy?.Allocate(MaterialIdentity(), UESceneResourceKind.Material, ".json")
                    ?? BuildTargetPath(mode, layout, TrimLogicalRoot(result.JsonPath), MaterialExportResource.Material, _internalFilePath);
                var jsonUri = MaterialLinkBuilder.BuildUri(mode, modelPath, jsonTargetPath, bundleRoot, layout);
                manifest.AddOrGet("material:" + MaterialIdentity(),
                    jsonUri,
                    "application/json", System.Text.Encoding.UTF8.GetBytes(json), true, jsonTargetPath);
            }
        }

        public FModelMaterialLinks BuildMaterialLinks(MaterialLinkMode mode, string modelPath, int slotIndex, string? slotName = null, string? bundleRoot = null, ResourceManifest? manifest = null, MaterialExportLayout layout = MaterialExportLayout.PreserveHierarchy)
        {
            var result = GetMaterialLinkExportResult();
            var textures = result.Textures.ToDictionary(texture => texture.UnrealObjectPath, StringComparer.Ordinal);
            var parameters = new List<MaterialLinkSourceParameter>();
            foreach (var (name, texture) in _materialData.Parameters.Textures)
            {
                var path = texture.GetPathName();
            if (textures.TryGetValue(path, out var export))
            {
                var resource = manifest?.FindByPath(path);
                parameters.Add(MaterialLinkSourceParameter.Texture(name, path, resource?.RelativeUri ?? export.Uri, resource?.Mime ?? export.Mime, resource?.Bytes.ToArray() ?? export.Bytes));
            }
            }
            parameters.AddRange(_materialData.Parameters.Scalars.Select(parameter => MaterialLinkSourceParameter.Scalar(parameter.Key, parameter.Value)));
            parameters.AddRange(_materialData.Parameters.Colors.Select(parameter => MaterialLinkSourceParameter.Vector(parameter.Key, parameter.Value.R, parameter.Value.G, parameter.Value.B, parameter.Value.A)));
            var materialResource = manifest?.FindByPath("material:" + MaterialIdentity());
            var materialJsonUri = materialResource?.RelativeUri ?? result.JsonPath;
            return MaterialLinkBuilder.Build(mode, modelPath, materialJsonUri, _internalFilePath, parameters, bundleRoot, slotIndex, slotName, materialResource != null);
        }

        private static string BuildTargetPath(MaterialLinkMode mode, MaterialExportLayout layout, string path, MaterialExportResource resource, string identity) =>
            mode == MaterialLinkMode.Bundle && layout == MaterialExportLayout.Categorized
                ? MaterialLinkBuilder.BuildResourcePath(layout, path, resource, identity)
                : path;

        private string TrimLogicalRoot(string path) => !string.IsNullOrEmpty(Options.UESceneProjectName)
            ? UESceneProjectExportUri.FromFixedPath(path, Options.UESceneProjectName, Path.GetExtension(path))
            : path;

        private string MaterialIdentity() => string.IsNullOrEmpty(_canonicalUnrealObjectPath) ? _internalFilePath : _canonicalUnrealObjectPath;

        private static string Mime(string ext) => ext.ToLowerInvariant() switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "tga" => "image/x-tga",
            "hdr" => "image/vnd.radiance",
            _ => "application/octet-stream"
        };
        public override bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath)
        {
            label = string.Empty;
            savedFilePath = string.Empty;

            savedFilePath = FixAndCreatePath(baseDirectory, _internalFilePath, "json");
            WriteAtomic(savedFilePath, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_materialData, Formatting.Indented)));
            label = Path.GetFileName(savedFilePath);

            // Reuse cached decode results to avoid double decode
            var result = GetMaterialLinkExportResult();
            foreach (var texture in result.Textures)
            {
                var texturePath = FixAndCreatePath(baseDirectory, texture.Uri);
                var textureDir = Path.GetDirectoryName(texturePath);
                if (textureDir != null) Directory.CreateDirectory(textureDir);
                WriteAtomic(texturePath, texture.Bytes);
            }

            return true;
        }

        private static void WriteAtomic(string targetPath, byte[] bytes)
        {
            var tempPath = targetPath + ".tmp-" + Path.GetRandomFileName();
            try
            {
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
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
