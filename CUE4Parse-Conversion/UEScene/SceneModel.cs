namespace CUE4Parse_Conversion.UEScene;

public enum AssetKind : byte { StaticMesh = 1, Material = 2, Texture = 3 }
public enum LocationMode : byte { Relative = 1, Bundle = 2, Embedded = 3 }
public enum Availability : byte { Available = 1, Missing = 2, Failed = 3, Optional = 4 }
public enum ComponentKind : byte { StaticMesh = 1, InstancedStaticMesh = 2, HierarchicalInstancedStaticMesh = 3 }
public enum BundleResourceKind : byte { Model = 1, MaterialJson = 2, Texture = 3 }
public enum LightType : byte { Directional = 1, Point = 2, Spot = 3, Rect = 4 }
public enum LightColorEncoding : byte { Rgba8 = 1 }
public enum ELightUnits : byte { Unitless = 0, Candelas = 1, Lumens = 2, Ev = 3, Lux = 4 }

public static class VisibilityFlags
{
    public const uint Visible = 1u;
    public const uint AllowedMask = Visible;
}

public sealed record UESceneDocument(
    SceneMetadata Metadata,
    List<string>? StringTable = null,
    List<AssetRecord>? Assets = null,
    List<MeshLodRecord>? Lods = null,
    List<ActorRecord>? Actors = null,
    List<ComponentRecord>? Components = null,
    List<TransformRecord>? Transforms = null,
    List<LightRecord>? Lights = null,
    List<InstanceSetRecord>? InstanceSets = null,
    List<InstanceTransform>? InstanceTransforms = null,
    List<MaterialOverrideRecord>? MaterialOverrides = null,
    byte[]? MatoBlob = null,
    List<DiagnosticRecord>? Diagnostics = null,
    byte[]? DiagBlob = null,
    List<BundleEntry>? BundleEntries = null);

public sealed record SceneMetadata(
    string GeneratorName,
    string GeneratorVersion,
    string GeneratorBuild,
    string SourceWorldPath,
    string PersistentLevelPath,
    string? SourceGame,
    string? SourceEngine,
    ulong ExportUnixTimeMsUtc,
    ulong RequiredFeatures = 0,
    ulong OptionalFeatures = 0)
{
    public const byte UnrealLeftHandedZUp = 1;
    public const byte Centimeter = 1;
    public const byte LocalToParent = 1;
    public const byte Xyzw = 1;
    public const ulong InstanceSetsBit = 1ul;
    public const ulong MatoProvenanceBit = 2ul;
    public const ulong KnownRequiredBits = InstanceSetsBit | MatoProvenanceBit;
    public const ulong GameExportTreeRootBit = 1ul << 0;
    public const ulong LightRecordsBit = 1ul << 1;
    public const ulong RetiredCameraRecordsBit = 1ul << 2;
}

public sealed record AssetRecord(
    ulong AssetId,
    AssetKind Kind,
    LocationMode Location,
    Availability Availability,
    string CanonicalUnrealPath,
    string DisplayName,
    string LogicalUri,
    string Mime,
    byte[] Sha256,
    ulong ByteLength,
    uint DiagnosticStart,
    uint DiagnosticCount,
    uint LodStart,
    uint LodCount);

public sealed record MeshLodRecord(
    uint LodIndex,
    string LogicalUri,
    byte[] Sha256,
    ulong ByteLength,
    double BoundsMinX, double BoundsMinY, double BoundsMinZ,
    double BoundsMaxX, double BoundsMaxY, double BoundsMaxZ,
    float SourceScreenSize,
    uint Reserved);

public sealed record ActorRecord(
    ulong ActorId,
    ulong ParentActorId,
    string Name,
    string ClassPath,
    string SourceObjectPath,
    ulong RootComponentId,
    ulong LocalTransformId,
    uint ComponentStart,
    uint ComponentCount,
    uint ActorFlags,
    uint DiagnosticStart,
    uint DiagnosticCount);

public sealed record ComponentRecord(
    ulong ComponentId,
    ulong ActorId,
    ulong ParentComponentId,
    ComponentKind ComponentKind,
    string Name,
    string ClassPath,
    string SourceObjectPath,
    ulong LocalTransformId,
    ulong MeshAssetId,
    uint MaterialOverrideStart,
    uint MaterialOverrideCount,
    ulong InstanceSetId,
    uint VisibilityFlags,
    uint DiagnosticStart,
    uint DiagnosticCount);

public sealed record TransformRecord(
    ulong TransformId,
    double TranslationX, double TranslationY, double TranslationZ,
    float RotationX, float RotationY, float RotationZ, float RotationW,
    float ScaleX, float ScaleY, float ScaleZ,
    uint Reserved);

public static class LightFlags
{
    public const uint CastsShadows = 1u;
    public const uint HasAttenuation = 2u;
    public const uint HasSpotAngles = 4u;
    public const uint HasRectDimensions = 8u;
    public const uint UsesInverseSquaredFalloff = 16u;
    public const uint AllowedMask = CastsShadows | HasAttenuation | HasSpotAngles | HasRectDimensions | UsesInverseSquaredFalloff;
}

public sealed record LightRecord(
    ulong LightId,
    ulong ActorId,
    ulong ParentComponentId,
    ulong TransformId,
    string Name,
    string ClassPath,
    string SourceObjectPath,
    LightType LightType,
    LightColorEncoding ColorEncoding,
    ELightUnits LightUnits,
    byte ColorR,
    byte ColorG,
    byte ColorB,
    byte ColorA,
    uint Flags,
    float Intensity,
    float AttenuationRadius,
    float FalloffExponent,
    float InnerConeAngleDegrees,
    float OuterConeAngleDegrees,
    float RectWidth,
    float RectHeight,
    uint Reserved,
    float LightSourceAngle = 0,
    float LightSourceSoftAngle = 0,
    float SourceRadius = 0,
    float SoftSourceRadius = 0,
    float SourceLength = 0,
    float BarnDoorAngle = 0,
    float BarnDoorLength = 0);

public sealed record InstanceSetRecord(
    ulong InstanceSetId,
    ulong ComponentId,
    uint InstanceCount,
    uint Encoding,
    uint TransformStart,
    uint TransformCount);

public sealed record InstanceTransform(
    float TranslationX, float TranslationY, float TranslationZ,
    float RotationX, float RotationY, float RotationZ, float RotationW,
    float ScaleX, float ScaleY, float ScaleZ);

public sealed record MaterialOverrideRecord(
    ulong ComponentId,
    uint SlotOrdinal,
    uint Flags,
    ulong BaseMaterialAssetId,
    ulong OverrideMaterialAssetId,
    ulong ParameterPayloadOffset,
    uint ParameterPayloadLength,
    uint DiagnosticStart,
    uint DiagnosticCount,
    uint Reserved);

public sealed record DiagnosticRecord(
    byte Severity,
    byte SubjectKind,
    ushort Code,
    uint Flags,
    ulong SubjectId,
    string SourceObjectPath,
    string Message,
    ulong DetailsOffset,
    uint DetailsLength,
    uint Reserved);

public sealed record BundleEntry(
    byte ResourceKind,
    LocationMode Location,
    Availability Availability,
    string LogicalUri,
    ulong OwnerAssetId,
    uint LodIndex,
    byte[] Sha256,
    ulong ByteLength,
    string Mime,
    uint DiagnosticStart,
    uint DiagnosticCount);

public sealed class SceneStringTable
{
    private readonly IReadOnlyList<string> _strings;

    internal SceneStringTable(IReadOnlyList<string> strings)
    {
        _strings = strings;
    }

    public IReadOnlyList<string> Strings => _strings;

    public uint GetId(string value)
    {
        for (var index = 0; index < _strings.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(_strings[index], value)) return checked((uint) index + 1);
        }
        throw new InvalidOperationException("The string is not in this table.");
    }

    public uint GetOptionalId(string? value) => value is null ? 0u : GetId(value);
}
