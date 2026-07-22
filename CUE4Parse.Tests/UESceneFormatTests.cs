using System.Buffers.Binary;
using System.Text;
using CUE4Parse_Conversion.UEScene;

namespace CUE4Parse.Tests;

public class UESceneFormatTests
{
    [Fact]
    public void WritesExactV1HeaderAndCrc()
    {
        var bytes = UESceneWriter.WritePhaseOne(UESceneFixture.EmptyScene(timestamp: 0));

        Assert.Equal("UESCENE\0", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal((ushort) 1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)));
        Assert.Equal((ushort) 0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)));
        Assert.Equal((uint) 0, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        Assert.Equal((uint) 44, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal((ulong) 44, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    [Fact]
    public void WritesExactAlignedDirectoryEntries()
    {
        var bytes = UESceneWriter.WritePhaseOne(UESceneFixture.StaticScene(timestamp: 123));

        Assert.Equal((uint) 2, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal((ulong) bytes.Length, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)));
        AssertDirectoryEntry(bytes, 44, "META", expectedRecordCount: 1);
        AssertDirectoryEntry(bytes, 84, "STRS", expectedRecordCount: 7);
    }

    [Fact]
    public void SerializesOptionalSourceIdsAsZeroAndRequiredMetadataIdsAsOneBased()
    {
        var withOptionalSources = UESceneWriter.WritePhaseOne(UESceneFixture.StaticScene(timestamp: 123));
        var withoutOptionalSources = UESceneWriter.WritePhaseOne(UESceneFixture.EmptyScene(timestamp: 123));
        var metaOffset = GetMetaOffset(withOptionalSources);
        var strings = ReadStrings(withOptionalSources);

        for (var fieldOffset = 4; fieldOffset <= 24; fieldOffset += 4)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(withOptionalSources.AsSpan(metaOffset + fieldOffset));
            Assert.InRange(id, 1u, checked((uint) strings.Length));
        }

        var sourceGameIndex = Array.IndexOf(strings, "CUE4Parse");
        var sourceEngineIndex = Array.IndexOf(strings, "\u00e4");
        Assert.NotEqual(-1, sourceGameIndex);
        Assert.NotEqual(-1, sourceEngineIndex);
        Assert.Equal((uint) (sourceGameIndex + 1), BinaryPrimitives.ReadUInt32LittleEndian(withOptionalSources.AsSpan(metaOffset + 28)));
        Assert.Equal((uint) (sourceEngineIndex + 1), BinaryPrimitives.ReadUInt32LittleEndian(withOptionalSources.AsSpan(metaOffset + 32)));
        var emptyMetaOffset = GetMetaOffset(withoutOptionalSources);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(withoutOptionalSources.AsSpan(emptyMetaOffset + 28)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(withoutOptionalSources.AsSpan(emptyMetaOffset + 32)));
    }

    [Fact]
    public void RejectsStringsExceedingUtf8ByteLimitAndInvalidUtf16Surrogates()
    {
        Assert.Throws<InvalidDataException>(() => UESceneWriter.WritePhaseOne(UESceneFixture.WithString(new string('a', 65_537))));
        Assert.Throws<InvalidDataException>(() => UESceneWriter.WritePhaseOne(UESceneFixture.WithString("\ud800")));
    }

    [Fact]
    public void PhaseOneWriterRejectsRetiredCameraFeature()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42) with
        {
            Metadata = UESceneFixture.CompleteV1Scene(timestamp: 42).Metadata with
            {
                OptionalFeatures = 1ul << 2
            }
        };

        Assert.Throws<InvalidDataException>(() => UESceneWriter.WritePhaseOne(doc));
    }

    [Fact]
    public void WritesEveryMetaFieldAtItsSchemaOffset()
    {
        var bytes = UESceneWriter.WritePhaseOne(UESceneFixture.StaticScene(timestamp: 123));
        var metaOffset = GetMetaOffset(bytes);
        var strings = ReadStrings(bytes);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset)));
        Assert.Equal((uint) (Array.IndexOf(strings, "uescene") + 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset + 4)));
        Assert.Equal((uint) (Array.IndexOf(strings, "CUE4Parse") + 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset + 8)));
        Assert.Equal((uint) (Array.IndexOf(strings, "z") + 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset + 12)));
        Assert.Equal((uint) (Array.IndexOf(strings, string.Empty) + 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset + 16)));
        Assert.Equal((uint) (Array.IndexOf(strings, "/Game/Maps/Test") + 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset + 20)));
        Assert.Equal((uint) (Array.IndexOf(strings, "/Game/Maps/Test.Test:PersistentLevel") + 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset + 24)));
        Assert.Equal((ulong) 123, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(metaOffset + 36)));
        Assert.Equal(SceneMetadata.UnrealLeftHandedZUp, bytes[metaOffset + 44]);
        Assert.Equal(SceneMetadata.Centimeter, bytes[metaOffset + 45]);
        Assert.Equal(SceneMetadata.LocalToParent, bytes[metaOffset + 46]);
        Assert.Equal(SceneMetadata.Xyzw, bytes[metaOffset + 47]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metaOffset + 48)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(metaOffset + 52)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(metaOffset + 60)));
    }

    [Fact]
    public void SortsAndDeduplicatesUtf8StringsAndWritesMetadataWithoutReadingClock()
    {
        var first = UESceneWriter.WritePhaseOne(UESceneFixture.StaticScene(timestamp: 123));
        var second = UESceneWriter.WritePhaseOne(UESceneFixture.StaticScene(timestamp: 123));

        Assert.Equal(first, second);
        Assert.Equal(0xcbf43926u, Crc32IsoHdlc.Compute("123456789"u8));

        var strings = ReadStrings(first);
        var metaOffset = GetMetaOffset(first);
        Assert.Equal(new[] { string.Empty, "/Game/Maps/Test", "/Game/Maps/Test.Test:PersistentLevel", "CUE4Parse", "uescene", "z", "\u00e4" }, strings);
        Assert.Equal((uint) 1, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(metaOffset)));
        Assert.Equal((ulong) 123, BinaryPrimitives.ReadUInt64LittleEndian(first.AsSpan(metaOffset + 36)));
        Assert.Throws<InvalidDataException>(() => UESceneWriter.WritePhaseOne(UESceneFixture.WithString("a\0b")));
        Assert.Throws<InvalidDataException>(() => UESceneWriter.WritePhaseOne(UESceneFixture.WithString(null!)));
    }

    [Fact]
    public void WritesAndReadsCompleteV1SemanticDocument()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 1000);
        var bytes = UESceneWriter.Write(doc);

        Assert.Equal("UESCENE\0", Encoding.ASCII.GetString(bytes, 0, 8));
        var dirCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
        Assert.Equal(10u, dirCount);

        // No trailing padding after last chunk: file_size equals last payload end.
        Assert.Equal((ulong) bytes.Length, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)));
        var lastEnd = 0ul;
        for (var i = 0; i < dirCount; i++)
        {
            var off = 44 + i * 40;
            var chunkOff = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(off + 8));
            var stored = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(off + 16));
            lastEnd = Math.Max(lastEnd, chunkOff + stored);
        }
        Assert.Equal((ulong) bytes.Length, lastEnd);

        var read = UESceneReader.Read(bytes);
        Assert.Equal(doc.Metadata.ExportUnixTimeMsUtc, read.Metadata.ExportUnixTimeMsUtc);
        Assert.Equal(doc.Metadata.GeneratorName, read.Metadata.GeneratorName);
        Assert.Equal(doc.Assets!.Count, read.Assets!.Count);
        Assert.Equal(doc.Actors!.Count, read.Actors!.Count);
        Assert.Equal(doc.Components!.Count, read.Components!.Count);
        Assert.Equal(doc.Transforms!.Count, read.Transforms!.Count);
        Assert.Equal(doc.MaterialOverrides!.Count, read.MaterialOverrides!.Count);
        Assert.Equal(doc.Diagnostics!.Count, read.Diagnostics!.Count);
        Assert.Equal(doc.BundleEntries!.Count, read.BundleEntries!.Count);
        Assert.Equal(doc.InstanceSets!.Count, read.InstanceSets!.Count);
        Assert.Equal(doc.InstanceTransforms!.Count, read.InstanceTransforms!.Count);
    }

    [Fact]
    public void RoundTripsNonzeroCanonicalStringRefs()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        var read = UESceneReader.Read(bytes);

        var sm = read.Assets!.First(a => a.Kind == AssetKind.StaticMesh);
        Assert.Equal("/Game/Meshes/SM_Chair", sm.CanonicalUnrealPath);
        Assert.Equal("SM_Chair", sm.DisplayName);
        Assert.Equal("Models/SM_Chair.uemodel", sm.LogicalUri);
        Assert.Equal("application/x-uemodel", sm.Mime);
        Assert.NotEqual(0u, (uint) sm.CanonicalUnrealPath.Length);

        var lod0 = read.Lods!.Skip((int) sm.LodStart).Take((int) sm.LodCount).First();
        Assert.Equal("Models/SM_Chair_LOD0.uemodel", lod0.LogicalUri);

        var bndxModel = read.BundleEntries!.Single(e => e.OwnerAssetId == sm.AssetId);
        Assert.Equal(uint.MaxValue, bndxModel.LodIndex);
        Assert.Equal(sm.LogicalUri, bndxModel.LogicalUri);
        Assert.Equal(sm.Mime, bndxModel.Mime);

        var tex = read.Assets!.First(a => a.Kind == AssetKind.Texture);
        var bndxTex = read.BundleEntries!.First(e => e.OwnerAssetId == tex.AssetId);
        Assert.Equal(tex.LogicalUri, bndxTex.LogicalUri);

        var diag = read.Diagnostics![0];
        Assert.Equal("mesh export warning", diag.Message);
        Assert.Equal("/Game/Meshes/SM_Chair", diag.SourceObjectPath);
        Assert.False(string.IsNullOrEmpty(diag.Message));

        // All resolved IDs are in bounds: reader already enforced; re-check string table non-empty semantic strings present.
        Assert.Contains(sm.CanonicalUnrealPath, read.StringTable!);
        Assert.Contains(diag.Message, read.StringTable!);
    }

    [Fact]
    public void WritesDeterministicBytesForSameDocument()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var a = UESceneWriter.Write(doc);
        var b = UESceneWriter.Write(doc);
        Assert.Equal(a, b);
    }

    [Fact]
    public void WritesReadsFixedLiteChunkAndOptionalFeature()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var component = doc.Components![0];
        var transform = doc.Transforms![2];
        var light = new LightRecord(0xEEEE_0000_0000_0001, actor.ActorId, component.ComponentId, transform.TransformId,
            "KeyLight", "UDirectionalLightComponent", "/Game/Maps/Test.Test:PersistentLevel.BP_Actor:KeyLight",
            LightType.Directional, LightColorEncoding.Rgba8, ELightUnits.Lux, 255, 240, 200, 255,
            LightFlags.CastsShadows, 12.5f, 0, 0, 0, 0, 0, 0, 0);
        doc = doc with { Lights = new List<LightRecord> { light } };

        var bytes = UESceneWriter.Write(doc);
        var liteEntry = FindChunkEntryOff(bytes, "LITE");
        var liteOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(liteOffset)));
        Assert.Equal(120ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 16)));

        var read = UESceneReader.Read(bytes);
        Assert.NotEqual(0ul, read.Metadata.OptionalFeatures & SceneMetadata.LightRecordsBit);
        var output = Assert.Single(read.Lights!);
        Assert.Equal(LightType.Directional, output.LightType);
        Assert.Equal(255, output.ColorR);
        Assert.Equal(12.5f, output.Intensity);
    }

    [Fact]
    public void LiteIsAbsentAndOptionalBitClearedWithoutLights()
    {
        var read = UESceneReader.Read(UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42)));
        Assert.Empty(read.Lights!);
        Assert.Equal(0ul, read.Metadata.OptionalFeatures & SceneMetadata.LightRecordsBit);
    }

    [Fact]
    public void RejectsRetiredCameraFeatureBit()
    {
        var document = UESceneFixture.CompleteV1Scene(timestamp: 42) with
        {
            Metadata = UESceneFixture.CompleteV1Scene(timestamp: 42).Metadata with { OptionalFeatures = 1ul << 2 }
        };

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(document));
    }

    [Fact]
    public void ReaderRejectsRetiredCameraDeclarations()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var metaEntry = FindChunkEntryOff(bytes, "META");
        var metaOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(metaEntry + 8)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(metaOffset + 60), SceneMetadata.RetiredCameraRecordsBit);
        RecomputeChunkAndHeaderCrc(bytes, metaEntry);
        Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));

        bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var diagEntry = FindChunkEntryOff(bytes, "DIAG");
        "CAMR"u8.CopyTo(bytes.AsSpan(diagEntry));
        Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
    }

    [Fact]
    public void ReaderRejectsRetiredCameraFeatureBit()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var metaEntry = FindChunkEntryOff(bytes, "META");
        var metaOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(metaEntry + 8)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(metaOffset + 60), 1ul << 2);
        RecomputeChunkAndHeaderCrc(bytes, metaEntry);

        var exception = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("CAMR", exception.Message);
    }

    [Fact]
    public void ReaderRejectsRetiredCameraChunk()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var diagEntry = FindChunkEntryOff(bytes, "DIAG");
        "CAMR"u8.CopyTo(bytes.AsSpan(diagEntry, 4));

        var exception = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("CAMR", exception.Message);
    }

    [Fact]
    public void ReaderRejectsLiteFeatureAndChunkMismatchAndInvalidRecord()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var transform = doc.Transforms![0];
        doc = doc with
        {
            Lights = new List<LightRecord>
            {
                new(81, actor.ActorId, 0, transform.TransformId, "Point", "UPointLightComponent", "/Game/Maps/Test.Point",
                    LightType.Point, LightColorEncoding.Rgba8, ELightUnits.Lumens, 1, 2, 3, 255,
                    LightFlags.HasAttenuation | LightFlags.UsesInverseSquaredFalloff, 1, 100, 0, 0, 0, 0, 0, 0)
            }
        };
        var bytes = UESceneWriter.Write(doc);
        var liteEntry = FindChunkEntryOff(bytes, "LITE");
        var metaEntry = FindChunkEntryOff(bytes, "META");
        var metaOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(metaEntry + 8)));

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(metaOffset + 60), 0);
        RecomputeChunkAndHeaderCrc(bytes, metaEntry);
        var mismatch = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("LITE presence", mismatch.Message);

        bytes = UESceneWriter.Write(doc);
        liteEntry = FindChunkEntryOff(bytes, "LITE");
        var liteOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 8)));
        bytes[liteOffset + 4 + 44] = 0;
        RecomputeChunkAndHeaderCrc(bytes, liteEntry);
        var invalid = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("enum", invalid.Message, StringComparison.OrdinalIgnoreCase);

        bytes = UESceneWriter.Write(doc);
        liteEntry = FindChunkEntryOff(bytes, "LITE");
        liteOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 8)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(liteOffset), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(liteEntry + 36), 0);
        RecomputeChunkAndHeaderCrc(bytes, liteEntry);
        var empty = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("nonempty", empty.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriterRejectsInvalidLightTypeSpecificParametersAndCanonicalizesForeignKeys()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var component = doc.Components![0];
        var transform = doc.Transforms![2];
        var valid = new LightRecord(82, actor.ActorId, component.ComponentId, transform.TransformId,
            "Spot", "USpotLightComponent", "/Game/Maps/Test.Spot", LightType.Spot, LightColorEncoding.Rgba8, ELightUnits.Candelas,
            255, 255, 255, 255, LightFlags.HasAttenuation | LightFlags.HasSpotAngles, 2, 500, 8, 10, 30, 0, 0, 0);
        var invalid = valid with { RectWidth = 1 };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc with { Lights = new List<LightRecord> { invalid } }));

        var baseline = UESceneWriter.Write(doc with { Lights = new List<LightRecord> { valid } });
        var remapped = RemapAllIds(doc) with
        {
            Lights = new List<LightRecord>
            {
                valid with { ActorId = valid.ActorId + 0xBBBB_0000_0000_0000UL, ParentComponentId = valid.ParentComponentId + 0xCCCC_0000_0000_0000UL, TransformId = valid.TransformId + 0xDDDD_0000_0000_0000UL }
            }
        };
        Assert.Equal(baseline, UESceneWriter.Write(remapped));
    }

    [Fact]
    public void WritesReadsAllLightTypeSpecificParametersCanonically()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var component = doc.Components![0];
        var transform = doc.Transforms![2];
        var directional = new LightRecord(101, actor.ActorId, component.ComponentId, transform.TransformId,
            "Directional", "UDirectionalLightComponent", "/Game/Maps/Test.Directional", LightType.Directional, LightColorEncoding.Rgba8, ELightUnits.Lux,
            1, 2, 3, 255, LightFlags.CastsShadows, 1, 0, 0, 0, 0, 0, 0, 0) with { LightSourceAngle = 0.5f, LightSourceSoftAngle = 0.25f };
        var point = directional with { LightId = 102, Name = "Point", ClassPath = "UPointLightComponent", SourceObjectPath = "/Game/Maps/Test.Point", LightType = LightType.Point,
            Flags = LightFlags.HasAttenuation | LightFlags.UsesInverseSquaredFalloff, AttenuationRadius = 100, LightSourceAngle = 0, LightSourceSoftAngle = 0,
            SourceRadius = 2, SoftSourceRadius = 1, SourceLength = 3 };
        var spot = point with { LightId = 103, Name = "Spot", ClassPath = "USpotLightComponent", SourceObjectPath = "/Game/Maps/Test.Spot", LightType = LightType.Spot,
            Flags = LightFlags.HasAttenuation | LightFlags.HasSpotAngles, InnerConeAngleDegrees = 10, OuterConeAngleDegrees = 20, SourceRadius = 4, SoftSourceRadius = 2, SourceLength = 6 };
        var rect = directional with { LightId = 104, Name = "Rect", ClassPath = "URectLightComponent", SourceObjectPath = "/Game/Maps/Test.Rect", LightType = LightType.Rect,
            Flags = LightFlags.HasRectDimensions, RectWidth = 20, RectHeight = 10, LightSourceAngle = 0, LightSourceSoftAngle = 0, BarnDoorAngle = 45, BarnDoorLength = 5 };

        var bytes = UESceneWriter.Write(doc with { Lights = new List<LightRecord> { rect, spot, point, directional } });
        var liteEntry = FindChunkEntryOff(bytes, "LITE");
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 8))))));
        Assert.Equal(468ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 16)));

        var lights = UESceneReader.Read(bytes).Lights!;
        Assert.Equal((0.5f, 0.25f), (lights.Single(light => light.LightType == LightType.Directional).LightSourceAngle, lights.Single(light => light.LightType == LightType.Directional).LightSourceSoftAngle));
        Assert.Equal((2f, 1f, 3f), (lights.Single(light => light.LightType == LightType.Point).SourceRadius, lights.Single(light => light.LightType == LightType.Point).SoftSourceRadius, lights.Single(light => light.LightType == LightType.Point).SourceLength));
        Assert.Equal((4f, 2f, 6f), (lights.Single(light => light.LightType == LightType.Spot).SourceRadius, lights.Single(light => light.LightType == LightType.Spot).SoftSourceRadius, lights.Single(light => light.LightType == LightType.Spot).SourceLength));
        Assert.Equal((45f, 5f), (lights.Single(light => light.LightType == LightType.Rect).BarnDoorAngle, lights.Single(light => light.LightType == LightType.Rect).BarnDoorLength));
        Assert.Equal(bytes, UESceneWriter.Write(UESceneReader.Read(bytes)));
    }

    [Fact]
    public void RetainsRawInverseSquareFalloffExponentAndPermitsRectAttenuation()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var transform = doc.Transforms![2];
        var point = new LightRecord(108, actor.ActorId, 0, transform.TransformId,
            "Point", "UPointLightComponent", "/Game/Maps/Test.Point", LightType.Point, LightColorEncoding.Rgba8, ELightUnits.Lumens,
            1, 2, 3, 255, LightFlags.HasAttenuation | LightFlags.UsesInverseSquaredFalloff, 1, 100, 8, 0, 0, 0, 0, 0);
        var spot = point with { LightId = 109, Name = "Spot", ClassPath = "USpotLightComponent", SourceObjectPath = "/Game/Maps/Test.Spot",
            LightType = LightType.Spot, Flags = LightFlags.HasAttenuation | LightFlags.HasSpotAngles | LightFlags.UsesInverseSquaredFalloff,
            InnerConeAngleDegrees = 10, OuterConeAngleDegrees = 20 };
        var rect = point with { LightId = 110, Name = "Rect", ClassPath = "URectLightComponent", SourceObjectPath = "/Game/Maps/Test.Rect",
            LightType = LightType.Rect, Flags = LightFlags.HasAttenuation | LightFlags.HasRectDimensions, RectWidth = 20, RectHeight = 10,
            FalloffExponent = 0 };

        var read = UESceneReader.Read(UESceneWriter.Write(doc with { Lights = new List<LightRecord> { point, spot, rect } }));

        Assert.Equal(8f, read.Lights!.Single(light => light.LightType == LightType.Point).FalloffExponent);
        Assert.Equal(8f, read.Lights.Single(light => light.LightType == LightType.Spot).FalloffExponent);
        Assert.True((read.Lights.Single(light => light.LightType == LightType.Rect).Flags & LightFlags.HasAttenuation) != 0);
    }

    [Fact]
    public void WritesAndReadsUnsupportedLightDiagnosticCodeNine()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var diagnostic = new DiagnosticRecord(2, 2, UESceneDiagnosticCodes.UnsupportedLight, 0, actor.ActorId,
            "/Game/Maps/Test.Test:PersistentLevel.Sky", "Unsupported light component type 'USkyLightComponent'.", 0, 0, 0);

        var read = UESceneReader.Read(UESceneWriter.Write(doc with { Diagnostics = new List<DiagnosticRecord> { diagnostic } }));

        Assert.Equal(UESceneDiagnosticCodes.UnsupportedLight, Assert.Single(read.Diagnostics!).Code);
    }

    [Fact]
    public void RejectsCrossActorLightParentAndRectInverseSquare()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var otherActorComponent = doc.Components!.First(component => component.ActorId == doc.Actors[1].ActorId);
        var transform = doc.Transforms![2];
        var light = new LightRecord(105, actor.ActorId, otherActorComponent.ComponentId, transform.TransformId,
            "Point", "UPointLightComponent", "/Game/Maps/Test.Point", LightType.Point, LightColorEncoding.Rgba8, ELightUnits.Lumens,
            1, 2, 3, 255, LightFlags.HasAttenuation, 1, 100, 0, 0, 0, 0, 0, 0);
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc with { Lights = new List<LightRecord> { light } }));

        var rect = light with { ParentComponentId = 0, LightType = LightType.Rect, Flags = LightFlags.HasRectDimensions | LightFlags.UsesInverseSquaredFalloff, AttenuationRadius = 0, RectWidth = 1, RectHeight = 1 };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc with { Lights = new List<LightRecord> { rect } }));
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc with { Lights = new List<LightRecord> { light with { LightSourceAngle = 1 } } }));
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc with { Lights = new List<LightRecord> { light with { BarnDoorAngle = 1 } } }));
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc with { Lights = new List<LightRecord> { rect with { Flags = LightFlags.HasRectDimensions, SourceRadius = 1 } } }));
    }

    [Fact]
    public void ReaderRejectsLiteNegativeZeroAndNoncanonicalOrdering()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var actor = doc.Actors![0];
        var transform = doc.Transforms![2];
        var first = new LightRecord(106, actor.ActorId, 0, transform.TransformId, "A", "UPointLightComponent", "/Game/Maps/Test.A", LightType.Point, LightColorEncoding.Rgba8, ELightUnits.Lumens,
            1, 2, 3, 255, LightFlags.HasAttenuation, 1, 100, 0, 0, 0, 0, 0, 0);
        var second = first with { LightId = 107, Name = "B", SourceObjectPath = "/Game/Maps/Test.B" };
        var bytes = UESceneWriter.Write(doc with { Lights = new List<LightRecord> { first, second } });
        var liteEntry = FindChunkEntryOff(bytes, "LITE");
        var liteOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 8)));

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(liteOffset + 4 + 56), 0x8000_0000);
        RecomputeChunkAndHeaderCrc(bytes, liteEntry);
        Assert.Contains("negative zero", Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes)).Message, StringComparison.OrdinalIgnoreCase);

        bytes = UESceneWriter.Write(doc with { Lights = new List<LightRecord> { first, second } });
        liteEntry = FindChunkEntryOff(bytes, "LITE");
        liteOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(liteEntry + 8)));
        const int lightRecordSize = 116;
        var record = bytes.AsSpan(liteOffset + 4, lightRecordSize).ToArray();
        bytes.AsSpan(liteOffset + 4 + lightRecordSize, lightRecordSize).CopyTo(bytes.AsSpan(liteOffset + 4));
        record.CopyTo(bytes.AsSpan(liteOffset + 4 + lightRecordSize));
        RecomputeChunkAndHeaderCrc(bytes, liteEntry);
        Assert.Contains("canonical order", Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsNegativeZeroOutsideLite()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var xfrmEntry = FindChunkEntryOff(bytes, "XFRM");
        var xfrmOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(xfrmEntry + 8)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(xfrmOffset + 4 + 8), 0x8000_0000_0000_0000);
        RecomputeChunkAndHeaderCrc(bytes, xfrmEntry);
        Assert.Contains("negative zero", Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes)).Message, StringComparison.OrdinalIgnoreCase);

        bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var asstEntry = FindChunkEntryOff(bytes, "ASST");
        var asstOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(asstEntry + 8)));
        var assetCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(asstOffset)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(asstOffset + 8 + assetCount * 84 + 48), 0x8000_0000_0000_0000);
        RecomputeChunkAndHeaderCrc(bytes, asstEntry);
        Assert.Contains("negative zero", Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes)).Message, StringComparison.OrdinalIgnoreCase);

        bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var instEntry = FindChunkEntryOff(bytes, "INST");
        var instOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(instEntry + 8)));
        var setCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(instOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(instOffset + 8 + checked((int) setCount * 32)), 0x8000_0000);
        RecomputeChunkAndHeaderCrc(bytes, instEntry);
        Assert.Contains("negative zero", Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsLodRangeZeroAndOne()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        var read = UESceneReader.Read(bytes);

        var staticAsset = read.Assets!.First(a => a.Kind == AssetKind.StaticMesh);
        Assert.Equal(2u, staticAsset.LodCount);
        var slice = read.Lods!.Skip((int) staticAsset.LodStart).Take((int) staticAsset.LodCount).ToList();
        Assert.Equal(2, slice.Count);
        Assert.Equal(0u, slice[0].LodIndex);
        Assert.Equal(1u, slice[1].LodIndex);
    }

    [Fact]
    public void ValidatesActorCycle()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var a = doc.Actors![0];
        var b = doc.Actors[1];
        doc.Actors.Clear();
        doc.Actors.Add(a with { ParentActorId = b.ActorId });
        doc.Actors.Add(b with { ParentActorId = a.ActorId });

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void ValidatesCrossActorComponentParent()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var comp = doc.Components![0];
        var otherActor = doc.Actors![1];
        doc.Components[0] = comp with
        {
            ActorId = otherActor.ActorId,
            ParentComponentId = doc.Components.First(c => c.ActorId == doc.Actors[0].ActorId).ComponentId
        };

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void PreservesSparseSourceLodIndex()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        doc.Lods!.Clear();
        doc.Lods.Add(UESceneFixture.MakeLod(1, "Models/SM_Chair_LOD1.uemodel", UESceneFixture.Sha(2)));
        doc.Assets![0] = doc.Assets[0] with { LodStart = 0, LodCount = 1 };

        var parsed = UESceneReader.Read(UESceneWriter.Write(doc));
        Assert.Equal(1u, Assert.Single(parsed.Lods!).LodIndex);
    }

    [Fact]
    public void ValidatesInstanceFeatureMismatch()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var ismComp = doc.Components!.First(c => c.ComponentKind == ComponentKind.InstancedStaticMesh);
        doc.InstanceSets!.Clear();
        doc.InstanceTransforms!.Clear();
        doc.Components[doc.Components.IndexOf(ismComp)] = ismComp with { InstanceSetId = 0 };

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void ValidatesSharedInstanceSetRejected()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        // Force HISM to share ISM's set — must fail.
        var setId = doc.InstanceSets![0].InstanceSetId;
        var hism = doc.Components!.First(c => c.ComponentKind == ComponentKind.HierarchicalInstancedStaticMesh);
        doc.Components[doc.Components.IndexOf(hism)] = hism with { InstanceSetId = setId };
        doc.InstanceSets.RemoveAll(s => s.ComponentId == hism.ComponentId);

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void ReaderRejectsUnreferencedInstanceSetWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var instEntry = FindChunkEntryOff(bytes, "INST");
        var instOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(instEntry + 8)));

        // Repoint the second set at the static component. The first ISM set remains valid and referenced.
        var compEntry = FindChunkEntryOff(bytes, "COMP");
        var compOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(compEntry + 8)));
        var staticComponentId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(compOffset + 4));
        const int instanceSetRecordSize = 32;
        var secondSet = instOffset + 8 + instanceSetRecordSize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(secondSet + 8), staticComponentId);
        RecomputeChunkAndHeaderCrc(bytes, instEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Instance set owner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsOverlappingAndOrphanLodRangesWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var asstEntry = FindChunkEntryOff(bytes, "ASST");
        var asstOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(asstEntry + 8)));
        const int assetRecordSize = 84;

        // Turn the unreferenced texture into a second static asset claiming the first asset's valid LOD range.
        var thirdAsset = asstOffset + 8 + assetRecordSize * 2;
        bytes[thirdAsset + 8] = (byte) AssetKind.StaticMesh;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(thirdAsset + 76), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(thirdAsset + 80), 2);
        RecomputeChunkAndHeaderCrc(bytes, asstEntry);

        var overlap = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("LOD table ranges overlap", overlap.Message, StringComparison.OrdinalIgnoreCase);

        // Leave the second LOD outside the only static asset's range; no LOD record may be orphaned.
        bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        asstEntry = FindChunkEntryOff(bytes, "ASST");
        asstOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(asstEntry + 8)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(asstOffset + 8 + 80), 1);
        RecomputeChunkAndHeaderCrc(bytes, asstEntry);

        var orphan = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("unowned", orphan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatesDirectoryOverlap()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), (ulong) bytes.Length * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
    }

    [Fact]
    public void ReaderAcceptsValidNoncanonicalDirectoryOffset()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
        var directoryLength = checked((int) directoryCount * 40);

        // A four-byte gap after the header moves the directory to 48 while
        // retaining the already aligned canonical chunk offsets at 448.
        Array.Copy(bytes, 44, bytes, 48, directoryLength);
        Array.Clear(bytes, 44, 4);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        Assert.Equal(42ul, UESceneReader.Read(bytes).Metadata.ExportUnixTimeMsUtc);
    }

    [Fact]
    public void ValidatesBadCrc()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        bytes[40] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
    }

    [Fact]
    public void ValidatesUnknownRequiredFeature()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        doc = doc with { Metadata = doc.Metadata with { RequiredFeatures = 0xFFFFFFFF } };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void ValidatesBadOpaqueEnvelope()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        var matoEntryOff = FindChunkEntryOff(bytes, "MATO");
        var matoOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(matoEntryOff + 8)));
        var matoStored = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(matoEntryOff + 16)));
        // Corrupt blob body (not CRC path): flip last payload byte then recompute chunk CRC so reader hits envelope validation.
        bytes[matoOffset + matoStored - 1] ^= 0xFF;
        var payload = bytes.AsSpan(matoOffset, matoStored);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(matoEntryOff + 32), Crc32IsoHdlc.Compute(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
    }

    [Fact]
    public void RejectsPayloadRecordCountMutationWithRecomputedCrc()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        var actrOff = FindChunkEntryOff(bytes, "ACTR");
        var chunkOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrOff + 8)));
        var stored = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrOff + 16)));
        // Mutate first payload count without changing directory record_count; recompute CRC so validation not CRC.
        var mutatedCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkOffset)) + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(chunkOffset), mutatedCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOff + 32), Crc32IsoHdlc.Compute(bytes.AsSpan(chunkOffset, stored)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
    }

    [Fact]
    public void RejectsMissingRequiredChunk()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        // Rename DIAG fourcc to DEBG (printable unknown) so DIAG is missing; recompute CRCs.
        var diagEntry = FindChunkEntryOff(bytes, "DIAG");
        "DEBG"u8.CopyTo(bytes.AsSpan(diagEntry));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Missing required chunk", ex.Message);
    }

    [Fact]
    public void RejectsTooLargeSecondaryCountSafely()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        var asstEntry = FindChunkEntryOff(bytes, "ASST");
        var chunkOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(asstEntry + 8)));
        var stored = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(asstEntry + 16)));
        // Secondary lod_count set huge; size formula must fail without allocating huge arrays.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(chunkOffset + 4), 0x7FFF_FF00u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(asstEntry + 32), Crc32IsoHdlc.Compute(bytes.AsSpan(chunkOffset, stored)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        Assert.ThrowsAny<Exception>(() => UESceneReader.Read(bytes));
    }

    [Fact]
    public void RejectsMissingOrMismatchedBndx()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        // Remove texture BNDX primary.
        var texId = doc.Assets!.First(a => a.Kind == AssetKind.Texture).AssetId;
        doc.BundleEntries!.RemoveAll(e => e.OwnerAssetId == texId);
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));

        doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var entry = doc.BundleEntries![0];
        doc.BundleEntries[0] = entry with { ByteLength = entry.ByteLength + 1 };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void RejectsPerLodStaticMeshModelBndx()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var mesh = doc.Assets!.Single(a => a.Kind == AssetKind.StaticMesh);
        var lod = doc.Lods!.Single(l => l.LodIndex == 0);
        doc.BundleEntries!.Clear();
        doc.BundleEntries.Add(new BundleEntry(
            (byte) BundleResourceKind.Model, LocationMode.Bundle, Availability.Available,
            lod.LogicalUri, mesh.AssetId, 0, lod.Sha256, lod.ByteLength, mesh.Mime, 0, 0));

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));

        doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        doc.BundleEntries!.RemoveAll(entry => entry.OwnerAssetId == doc.Assets!.Single(a => a.Kind == AssetKind.StaticMesh).AssetId);

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void WriterRejectsChildComponentAsActorRoot()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var child = doc.Components![1];
        doc.Components[1] = child with { ParentComponentId = doc.Components[0].ComponentId };
        doc.Actors![0] = doc.Actors[0] with { RootComponentId = child.ComponentId };

        var ex = Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
        Assert.Contains("root component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriterRejectsTextureExtraModelPrimaryBndx()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var texture = doc.BundleEntries!.Single(e => e.ResourceKind == (byte) BundleResourceKind.Texture);
        doc.BundleEntries.Add(texture with { ResourceKind = (byte) BundleResourceKind.Model });

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void AllowsUnavailableOptionalNonComponentAssetWithExactDiagnostic()
    {
        var doc = UESceneFixture.WithOptionalTextureAvailability();

        var read = UESceneReader.Read(UESceneWriter.Write(doc));

        var texture = read.Assets!.Single(a => a.Kind == AssetKind.Texture);
        Assert.Equal(Availability.Optional, texture.Availability);
        Assert.DoesNotContain(read.BundleEntries!, e => e.OwnerAssetId == texture.AssetId);
        Assert.Contains(read.Diagnostics!, d => d.SubjectKind == 1 && d.SubjectId == texture.AssetId);
    }

    [Fact]
    public void RejectsUnavailableAssetWithoutExactDiagnostic()
    {
        var doc = UESceneFixture.WithOptionalTextureAvailability();
        doc.Diagnostics!.Clear();
        var texture = doc.Assets!.Single();
        doc.Assets[0] = texture with { DiagnosticCount = 0 };

        var ex = Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
        Assert.Contains("availability", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsComponentReferencingUnavailableStaticMesh()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var mesh = doc.Assets!.Single(a => a.Kind == AssetKind.StaticMesh);
        doc.Assets[doc.Assets.IndexOf(mesh)] = mesh with { Availability = Availability.Missing };

        var ex = Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
        Assert.Contains("available static mesh", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsMissingAssetDiagnosticWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.WithOptionalTextureAvailability());
        var asstEntry = FindChunkEntryOff(bytes, "ASST");
        var asstOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(asstEntry + 8)));

        // The asset diagnostic must be represented by its owned ASST range.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(asstOffset + 8 + 72), 0);
        RecomputeChunkAndHeaderCrc(bytes, asstEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Asset diagnostic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsComponentReferencingUnavailableStaticMeshWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var asstEntry = FindChunkEntryOff(bytes, "ASST");
        var asstOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(asstEntry + 8)));

        bytes[asstOffset + 8 + 10] = (byte) Availability.Missing;
        RecomputeChunkAndHeaderCrc(bytes, asstEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("available static mesh", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsMismatchedAndEmbeddedBndxWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var bndxEntry = FindChunkEntryOff(bytes, "BNDX");
        var bndxOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bndxEntry + 8)));

        bytes[bndxOffset + 4 + 2] = (byte) Availability.Optional;
        RecomputeChunkAndHeaderCrc(bytes, bndxEntry);

        var mismatch = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("availability", mismatch.Message, StringComparison.OrdinalIgnoreCase);

        bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        bndxEntry = FindChunkEntryOff(bytes, "BNDX");
        bndxOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bndxEntry + 8)));
        bytes[bndxOffset + 4 + 1] = (byte) LocationMode.Embedded;
        RecomputeChunkAndHeaderCrc(bytes, bndxEntry);

        var embedded = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Embedded", embedded.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsOutOfRangeBndxLodWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var bndxEntry = FindChunkEntryOff(bytes, "BNDX");
        var bndxOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bndxEntry + 8)));

        // First BNDX record is the static mesh primary; per-LOD entries are forbidden.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bndxOffset + 4 + 16), 0);
        RecomputeChunkAndHeaderCrc(bytes, bndxEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Per-LOD BNDX", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsTextureExtraModelPrimaryBndxWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var bndxEntry = FindChunkEntryOff(bytes, "BNDX");
        var bndxOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bndxEntry + 8)));

        // Reassign the optional MaterialJson row as a Model row owned by the texture.
        var textureOwnerId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bndxOffset + 4 + 3 * 72 + 8));
        bytes[bndxOffset + 4 + 2 * 72] = (byte) BundleResourceKind.Model;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bndxOffset + 4 + 2 * 72 + 8), textureOwnerId);
        RecomputeChunkAndHeaderCrc(bytes, bndxEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("BNDX", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsDuplicateMatoComponentSlotWithValidChecksums()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var first = doc.MaterialOverrides![0];
        doc.MaterialOverrides.Add(first with { SlotOrdinal = 1 });
        doc.Components![0] = doc.Components[0] with { MaterialOverrideCount = 2 };
        var bytes = UESceneWriter.Write(doc);
        var matoEntry = FindChunkEntryOff(bytes, "MATO");
        var matoOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(matoEntry + 8)));

        // Second record uses the same component; duplicate the first record's slot ordinal.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(matoOffset + 8 + 56 + 8), 0);
        RecomputeChunkAndHeaderCrc(bytes, matoEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Duplicate MATO", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsForeignActorComponentRangeWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var actrEntry = FindChunkEntryOff(bytes, "ACTR");
        var actrOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 8)));

        // Give actor 1 actor 2's component while keeping actor ranges disjoint.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOffset + 4 + 44), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOffset + 4 + 48), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOffset + 4 + 64 + 44), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOffset + 4 + 64 + 48), 2);
        RecomputeChunkAndHeaderCrc(bytes, actrEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Actor component range has foreign", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsZeroActorRootWithComponentsAndValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var actrEntry = FindChunkEntryOff(bytes, "ACTR");
        var actrOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 8)));

        // ACTR record: root_component_id@28 within each 64-byte record.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(actrOffset + 4 + 28), 0);
        RecomputeChunkAndHeaderCrc(bytes, actrEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Actor root component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsForeignActorRootWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var actrEntry = FindChunkEntryOff(bytes, "ACTR");
        var actrOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 8)));
        var compEntry = FindChunkEntryOff(bytes, "COMP");
        var compOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(compEntry + 8)));

        // Point actor 1 at actor 2's root component.
        var foreignRootId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(compOffset + 4 + 88 * 2));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(actrOffset + 4 + 28), foreignRootId);
        RecomputeChunkAndHeaderCrc(bytes, actrEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Actor root component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsMissingActorRootWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var actrEntry = FindChunkEntryOff(bytes, "ACTR");
        var actrOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 8)));

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(actrOffset + 4 + 28), ulong.MaxValue);
        RecomputeChunkAndHeaderCrc(bytes, actrEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Actor root component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsRootForActorWithoutComponentsAndValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var actrEntry = FindChunkEntryOff(bytes, "ACTR");
        var actrOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 8)));

        // Reassign all component records to actor 1 so actor 2 has a zero-length range.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOffset + 4 + 48), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOffset + 4 + 64 + 44), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrOffset + 4 + 64 + 48), 0);
        RecomputeChunkAndHeaderCrc(bytes, actrEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("without components has a root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsForeignComponentMatoRangeWithValidChecksums()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var secondComponent = doc.Components![1];
        doc.MaterialOverrides!.Add(doc.MaterialOverrides[0] with { ComponentId = secondComponent.ComponentId });
        doc.Components[1] = secondComponent with { MaterialOverrideStart = 1, MaterialOverrideCount = 1 };
        var bytes = UESceneWriter.Write(doc);
        var compEntry = FindChunkEntryOff(bytes, "COMP");
        var compOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(compEntry + 8)));

        // Swap the two non-empty MATO ranges so each component points at the other's override.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(compOffset + 4 + 56), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(compOffset + 4 + 60), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(compOffset + 4 + 88 + 56), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(compOffset + 4 + 88 + 60), 1);
        RecomputeChunkAndHeaderCrc(bytes, compEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Component MATO range has foreign", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsUnownedInstanceTransformWithValidChecksums()
    {
        var bytes = UESceneWriter.Write(UESceneFixture.CompleteV1Scene(timestamp: 42));
        var instEntry = FindChunkEntryOff(bytes, "INST");
        var instOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(instEntry + 8)));

        // Shrink the first set consistently, leaving transform index 1 uncovered by either set.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(instOffset + 8 + 16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(instOffset + 8 + 28), 1);
        RecomputeChunkAndHeaderCrc(bytes, instEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("Instance transform table contains unowned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnknownEnumAndNonzeroActorFlags()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        doc.Actors![0] = doc.Actors[0] with { ActorFlags = 1 };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));

        doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        doc.Components![0] = doc.Components[0] with { VisibilityFlags = 0x100 };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void RejectsAlternateNanAndTruncatedSha()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var badNan = BitConverter.UInt32BitsToSingle(0x7F800001); // non-canonical NaN/inf-ish
        doc.Lods![0] = doc.Lods[0] with { SourceScreenSize = badNan };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));

        doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        doc.Assets![0] = doc.Assets[0] with { Sha256 = new byte[16] };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void SetsRequiredBit1OnlyWhenMatoParameterPayloadPresent()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        var read = UESceneReader.Read(bytes);
        Assert.Equal(SceneMetadata.InstanceSetsBit | SceneMetadata.MatoProvenanceBit, read.Metadata.RequiredFeatures & SceneMetadata.KnownRequiredBits);

        // Clear payload flag and empty blob — bit1 must clear even though overrides remain.
        doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        doc.MaterialOverrides![0] = doc.MaterialOverrides[0] with { Flags = 0, ParameterPayloadOffset = 0, ParameterPayloadLength = 0 };
        doc = doc with { MatoBlob = Array.Empty<byte>() };
        bytes = UESceneWriter.Write(doc);
        read = UESceneReader.Read(bytes);
        Assert.Equal(0ul, read.Metadata.RequiredFeatures & SceneMetadata.MatoProvenanceBit);
        Assert.Equal(SceneMetadata.InstanceSetsBit, read.Metadata.RequiredFeatures & SceneMetadata.InstanceSetsBit);
    }

    [Fact]
    public void SkipsUnknownPrintableOptionalZeroFlagChunks()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);

        var origDirCount = 10;
        var newDirCount = 11;
        var origChunksEnd = Align8(44 + origDirCount * 40);
        var newChunksStart = Align8(44 + newDirCount * 40);
        var shift = newChunksStart - origChunksEnd;

        // Find last chunk end in original (no trailing pad).
        var origLastEnd = 0;
        for (var i = 0; i < origDirCount; i++)
        {
            var srcOff = 44 + i * 40;
            var o = (int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(srcOff + 8));
            var s = (int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(srcOff + 16));
            origLastEnd = Math.Max(origLastEnd, o + s);
        }

        var extraChunkPayload = 8;
        var testChunkOff = Align8(newChunksStart + (origLastEnd - origChunksEnd));
        var newLen = testChunkOff + extraChunkPayload;
        var newBytes = new byte[newLen];

        "UESCENE\0"u8.CopyTo(newBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(newBytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(newBytes.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(16), 44);
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(20), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(28), (uint) newDirCount);
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(32), (ulong) newLen);

        for (var i = 0; i < origDirCount; i++)
        {
            var srcOff = 44 + i * 40;
            var dstOff = 44 + i * 40;
            bytes.AsSpan(srcOff, 40).CopyTo(newBytes.AsSpan(dstOff));
            var origChunkOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(srcOff + 8));
            BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(dstOff + 8), origChunkOffset + (ulong) shift);
        }

        var testEntryOff = 44 + 10 * 40;
        "TEST"u8.CopyTo(newBytes.AsSpan(testEntryOff));
        BinaryPrimitives.WriteUInt16LittleEndian(newBytes.AsSpan(testEntryOff + 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(newBytes.AsSpan(testEntryOff + 6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(testEntryOff + 8), (ulong) testChunkOff);
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(testEntryOff + 16), (ulong) extraChunkPayload);
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(testEntryOff + 24), (ulong) extraChunkPayload);
        var testPayload = new byte[extraChunkPayload];
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(testEntryOff + 32), Crc32IsoHdlc.Compute(testPayload));
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(testEntryOff + 36), 0);

        Array.Copy(bytes, origChunksEnd, newBytes, newChunksStart, origLastEnd - origChunksEnd);
        testPayload.CopyTo(newBytes.AsSpan(testChunkOff));
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(40), Crc32IsoHdlc.Compute(newBytes.AsSpan(0, 40)));

        var read = UESceneReader.Read(newBytes);
        Assert.Equal(doc.Metadata.ExportUnixTimeMsUtc, read.Metadata.ExportUnixTimeMsUtc);
    }

    [Fact]
    public void RejectsTotalFixedRecordsOverTenMillion()
    {
        var doc = UESceneFixture.EmptyScene(timestamp: 0);
        var diagnostics = new List<DiagnosticRecord>();
        for (var i = 0; i < 10_000_001; i++)
            diagnostics.Add(new DiagnosticRecord(1, 0, 1, 0, 0, "", "error", 0, 0, 0));
        doc = doc with { Diagnostics = diagnostics };

        var ex = Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
        Assert.Contains("10,000,000", ex.Message);
    }

    [Fact]
    public void ReaderRejectsTotalFixedRecordsOverTenMillionBeforeAllocation()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);

        var diagEntry = FindChunkEntryOff(bytes, "DIAG");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(diagEntry + 36), 10_000_001u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("10,000,000", ex.Message);
    }

    [Fact]
    public void CanonicalOutputIsByteIdenticalForPermutedInput()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var original = UESceneWriter.Write(doc);

        var permuted = BuildPermutedDocument(doc);
        var permutedBytes = UESceneWriter.Write(permuted);

        // Find first differing byte for diagnostics
        var minLen = Math.Min(original.Length, permutedBytes.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (original[i] != permutedBytes[i])
            {
                Assert.Fail($"First difference at byte {i}: expected 0x{original[i]:X2}, got 0x{permutedBytes[i]:X2}");
            }
        }
        Assert.Equal(original.Length, permutedBytes.Length);
        Assert.Equal(original, permutedBytes);

        var read = UESceneReader.Read(permutedBytes);
        Assert.Equal(doc.Metadata.ExportUnixTimeMsUtc, read.Metadata.ExportUnixTimeMsUtc);
        Assert.Equal(doc.Assets!.Count, read.Assets!.Count);
        Assert.Equal(doc.Actors!.Count, read.Actors!.Count);
        Assert.Equal(doc.Components!.Count, read.Components!.Count);
    }

    [Fact]
    public void RejectsUnownedInstanceTransformForSingleSet()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var ism = doc.Components!.First(c => c.ComponentKind == ComponentKind.InstancedStaticMesh);
        doc = doc with
        {
            Components = doc.Components.Select(c => c.ComponentKind == ComponentKind.HierarchicalInstancedStaticMesh
                ? c with { ComponentKind = ComponentKind.StaticMesh, InstanceSetId = 0 }
                : c).ToList(),
        };
        doc.InstanceSets!.RemoveAll(s => s.ComponentId != ism.ComponentId);
        doc.InstanceTransforms!.Insert(0, new InstanceTransform(99, 0, 0, 0, 0, 0, 1, 1, 1, 1));
        doc.InstanceSets[0] = doc.InstanceSets[0] with { TransformStart = 1, TransformCount = 2, InstanceCount = 2 };

        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
    }

    [Fact]
    public void CanonicalizesSingleInstanceSetAtZero()
    {
        var original = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var ism = original.Components!.First(c => c.ComponentKind == ComponentKind.InstancedStaticMesh);
        var oneSet = original.InstanceSets!.Single(s => s.ComponentId == ism.ComponentId);
        var transforms = original.InstanceTransforms!.Skip((int) oneSet.TransformStart).Take((int) oneSet.TransformCount).ToList();
        var expected = original with
        {
            Components = original.Components.Select(c => c.ComponentKind == ComponentKind.HierarchicalInstancedStaticMesh
                ? c with { ComponentKind = ComponentKind.StaticMesh, InstanceSetId = 0 }
                : c).ToList(),
            InstanceSets = new List<InstanceSetRecord> { oneSet with { TransformStart = 0 } },
            InstanceTransforms = transforms,
        };
        var bytes = UESceneWriter.Write(expected);
        var read = UESceneReader.Read(bytes);

        Assert.Equal(UESceneWriter.Write(expected), bytes);
        Assert.Equal(0u, read.InstanceSets!.Single().TransformStart);
        Assert.Equal(transforms, read.InstanceTransforms);
    }

    [Fact]
    public void CanonicalizesDiagnosticStringsByUtf8Bytes()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var assetId = doc.Assets![0].AssetId;
        const string privateUse = "\ue000";
        const string supplementary = "\U00010000";
        var diagnostics = new List<DiagnosticRecord>
        {
            new(1, 1, 1, 0, assetId, "", privateUse, 0, 0, 0),
            new(1, 1, 1, 0, assetId, "", supplementary, 0, 0, 0),
        };

        var utf8Ordered = doc with { Diagnostics = diagnostics };
        var reversed = doc with { Diagnostics = diagnostics.AsEnumerable().Reverse().ToList() };
        var bytes = UESceneWriter.Write(reversed);
        var read = UESceneReader.Read(bytes);

        Assert.Equal(UESceneWriter.Write(utf8Ordered), bytes);
        Assert.Equal(new[] { privateUse, supplementary }, read.Diagnostics!.Select(d => d.Message));
    }

    [Fact]
    public void RecomputesDiagnosticRangesForAllOwningSubjectKindsWithReverseInput()
    {
        var baseDoc = UESceneFixture.CompleteV1Scene(timestamp: 99);
        var mato = baseDoc.MaterialOverrides![0];
        var matoSubject = UESceneWriter.MaterialOverrideSubjectId(mato.ComponentId, mato.SlotOrdinal);
        var bndx = baseDoc.BundleEntries!.First(e => e.OwnerAssetId == baseDoc.Assets![2].AssetId);
        var bndxSubject = UESceneWriter.BundleEntrySubjectId(bndx.ResourceKind, bndx.LogicalUri, bndx.OwnerAssetId, bndx.LodIndex);

        // Deliberately reverse-of-canonical subject ordering: BNDX, MATO, InstanceSet, Component, Actor, Asset, Scene.
        var diagnostics = new List<DiagnosticRecord>
        {
            new(2, 6, 8, 0, bndxSubject, "/Game/Textures/T_Diffuse", "bndx diag", 0, 0, 0),
            new(2, 5, 7, 0, matoSubject, "/Game/Materials/M_Basic", "mato diag", 0, 0, 0),
            new(1, 4, 6, 0, baseDoc.InstanceSets![0].InstanceSetId, "", "instset diag", 0, 0, 0),
            new(3, 3, 5, 0, baseDoc.Components![0].ComponentId, "/Game/Maps/Test.Test:PersistentLevel.BP_Actor:StaticMeshComponent", "comp diag", 0, 0, 0),
            new(2, 2, 4, 0, baseDoc.Actors![1].ActorId, "/Game/Maps/Test.Test:PersistentLevel.BP_Actor_2", "actor2 diag", 0, 0, 0),
            new(1, 2, 3, 0, baseDoc.Actors![0].ActorId, "/Game/Maps/Test.Test:PersistentLevel.BP_Actor", "actor1 diag", 0, 0, 0),
            new(1, 1, 2, 0, baseDoc.Assets![0].AssetId, "/Game/Meshes/SM_Chair", "asset diag", 0, 0, 0),
            new(1, 0, 1, 0, 0, "", "scene diag", 0, 0, 0),
        };

        var unsorted = baseDoc with { Diagnostics = diagnostics };
        var sortedEquivalent = baseDoc with
        {
            Diagnostics = diagnostics
                .OrderBy(d => d.SubjectKind)
                .ThenBy(d => d.SubjectId)
                .ThenBy(d => d.Severity)
                .ThenBy(d => d.Code)
                .ThenBy(d => d.SourceObjectPath, StringComparer.Ordinal)
                .ThenBy(d => d.Message, StringComparer.Ordinal)
                .ToList()
        };

        var unsortedBytes = UESceneWriter.Write(unsorted);
        var sortedBytes = UESceneWriter.Write(sortedEquivalent);
        Assert.Equal(sortedBytes, unsortedBytes);

        var read = UESceneReader.Read(unsortedBytes);
        Assert.Equal(8, read.Diagnostics!.Count);

        // Canonical order: Scene, Asset, Actor*, Component, InstanceSet, MATO, BNDX.
        Assert.Equal(0, read.Diagnostics[0].SubjectKind);
        Assert.Equal(1, read.Diagnostics[1].SubjectKind);

        var sm = read.Assets!.First(a => a.CanonicalUnrealPath == "/Game/Meshes/SM_Chair");
        Assert.Equal(1u, sm.DiagnosticCount);
        Assert.Equal(1, read.Diagnostics[(int) sm.DiagnosticStart].SubjectKind);
        Assert.Equal(sm.AssetId, read.Diagnostics[(int) sm.DiagnosticStart].SubjectId);

        var actor1 = read.Actors!.First(a => a.SourceObjectPath == "/Game/Maps/Test.Test:PersistentLevel.BP_Actor");
        Assert.Equal(1u, actor1.DiagnosticCount);
        Assert.Equal(2, read.Diagnostics[(int) actor1.DiagnosticStart].SubjectKind);
        Assert.Equal(actor1.ActorId, read.Diagnostics[(int) actor1.DiagnosticStart].SubjectId);

        var actor2 = read.Actors!.First(a => a.SourceObjectPath == "/Game/Maps/Test.Test:PersistentLevel.BP_Actor_2");
        Assert.Equal(1u, actor2.DiagnosticCount);
        Assert.Equal(actor2.ActorId, read.Diagnostics[(int) actor2.DiagnosticStart].SubjectId);

        var comp = read.Components!.First(c => c.SourceObjectPath == "/Game/Maps/Test.Test:PersistentLevel.BP_Actor:StaticMeshComponent");
        Assert.Equal(1u, comp.DiagnosticCount);
        Assert.Equal(3, read.Diagnostics[(int) comp.DiagnosticStart].SubjectKind);
        Assert.Equal(comp.ComponentId, read.Diagnostics[(int) comp.DiagnosticStart].SubjectId);

        var ismComp = read.Components!.First(c => c.SourceObjectPath == "/Game/Maps/Test.Test:PersistentLevel.BP_Actor:InstancedStaticMeshComponent");
        var setId = read.InstanceSets!.First(s => s.ComponentId == ismComp.ComponentId).InstanceSetId;
        Assert.Contains(read.Diagnostics, d => d.SubjectKind == 4 && d.SubjectId == setId);

        var matoRead = read.MaterialOverrides![0];
        Assert.Equal(1u, matoRead.DiagnosticCount);
        Assert.Equal(5, read.Diagnostics[(int) matoRead.DiagnosticStart].SubjectKind);
        var expectedMatoSubject = UESceneWriter.MaterialOverrideSubjectId(matoRead.ComponentId, matoRead.SlotOrdinal);
        Assert.Equal(expectedMatoSubject, read.Diagnostics[(int) matoRead.DiagnosticStart].SubjectId);

        var bndxRead = read.BundleEntries!.First(e => e.LogicalUri == bndx.LogicalUri);
        Assert.Equal(1u, bndxRead.DiagnosticCount);
        Assert.Equal(6, read.Diagnostics[(int) bndxRead.DiagnosticStart].SubjectKind);
        var expectedBndxSubject = UESceneWriter.BundleEntrySubjectId(bndxRead.ResourceKind, bndxRead.LogicalUri, bndxRead.OwnerAssetId, bndxRead.LodIndex);
        Assert.Equal(expectedBndxSubject, read.Diagnostics[(int) bndxRead.DiagnosticStart].SubjectId);

        // BNDX semantic mapping still valid after range rewrite.
        Assert.Equal(bndx.LogicalUri, bndxRead.LogicalUri);
        Assert.Equal(bndx.ByteLength, bndxRead.ByteLength);
    }

    [Fact]
    public void ReaderRejectsZeroOverrideMaterialWithoutParameterPayload()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        // Keep HAS_PARAMETER_PAYLOAD so META bit1 stays consistent, but zero override id is only legal with bit0.
        doc.MaterialOverrides![0] = doc.MaterialOverrides[0] with
        {
            OverrideMaterialAssetId = 0,
            Flags = 0,
            ParameterPayloadOffset = 0,
            ParameterPayloadLength = 0
        };
        doc = doc with { MatoBlob = Array.Empty<byte>() };
        Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));

        // Build valid bytes then mutate MATO override id to 0 while keeping flags=0 (no payload).
        doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        // Clear payload first so we can set flags=0 legally with nonzero override.
        doc.MaterialOverrides![0] = doc.MaterialOverrides[0] with
        {
            Flags = 0,
            ParameterPayloadOffset = 0,
            ParameterPayloadLength = 0
        };
        doc = doc with { MatoBlob = Array.Empty<byte>() };
        var bytes = UESceneWriter.Write(doc);
        var matoEntry = FindChunkEntryOff(bytes, "MATO");
        var matoOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(matoEntry + 8)));
        var matoStored = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(matoEntry + 16)));
        // First record: override_material_asset_id at record+24 (after component_id@0, slot@8, flags@12, base@16).
        var record0 = matoOffset + 8;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(record0 + 24), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(matoEntry + 32), Crc32IsoHdlc.Compute(bytes.AsSpan(matoOffset, matoStored)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("override material", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsDanglingActorParent()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        var actrEntry = FindChunkEntryOff(bytes, "ACTR");
        var actrOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 8)));
        var actrStored = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 16)));
        // First actor parent_actor_id at record+8.
        var record0 = actrOffset + 4;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(record0 + 8), 0xDEAD_BEEF_CAFE_BABEUL);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrEntry + 32), Crc32IsoHdlc.Compute(bytes.AsSpan(actrOffset, actrStored)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("parent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsOverlappingActorDiagnosticRanges()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        // Two actor diagnostics so ranges are non-empty and can be forced to overlap.
        var actorId1 = doc.Actors![0].ActorId;
        var actorId2 = doc.Actors![1].ActorId;
        doc = doc with
        {
            Diagnostics = new List<DiagnosticRecord>
            {
                new(1, 2, 1, 0, actorId1, "/Game/Maps/Test.Test:PersistentLevel.BP_Actor", "a1", 0, 0, 0),
                new(1, 2, 2, 0, actorId2, "/Game/Maps/Test.Test:PersistentLevel.BP_Actor_2", "a2", 0, 0, 0),
            }
        };
        var bytes = UESceneWriter.Write(doc);
        var actrEntry = FindChunkEntryOff(bytes, "ACTR");
        var actrOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 8)));
        var actrStored = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(actrEntry + 16)));
        // After canonical sort both actors own one diag each; force both to start=0 count=1.
        // ACTR record: diagnostic_start@56, diagnostic_count@60 within each 64-byte record.
        for (var i = 0; i < 2; i++)
        {
            var rec = actrOffset + 4 + i * 64;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(rec + 56), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(rec + 60), 1);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(actrEntry + 32), Crc32IsoHdlc.Compute(bytes.AsSpan(actrOffset, actrStored)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriterRejectsOverlengthUriAndSourceObjectPath()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var overUri = "Models/" + new string('u', 4090); // >4096 UTF-8 with prefix
        Assert.True(Encoding.UTF8.GetByteCount(overUri) > 4096);
        doc.Assets![2] = doc.Assets[2] with { LogicalUri = overUri };
        // Keep BNDX in sync so we hit URI validation rather than BNDX mismatch first.
        var texId = doc.Assets[2].AssetId;
        var bndxIdx = doc.BundleEntries!.FindIndex(e => e.OwnerAssetId == texId);
        doc.BundleEntries[bndxIdx] = doc.BundleEntries[bndxIdx] with { LogicalUri = overUri };
        var uriEx = Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
        Assert.Contains("URI", uriEx.Message, StringComparison.OrdinalIgnoreCase);

        doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var overPath = "/Game/" + new string('p', 16380);
        Assert.True(Encoding.UTF8.GetByteCount(overPath) > 16384);
        doc.Actors![0] = doc.Actors[0] with { SourceObjectPath = overPath };
        var pathEx = Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
        Assert.Contains("source object path", pathEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriterRejectsBundleSchemeUri()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var mesh = doc.Assets!.First(a => a.Kind == AssetKind.StaticMesh);
        var invalidUri = "bundle://Models/SM_Chair.uemodel";
        doc.Assets[doc.Assets.IndexOf(mesh)] = mesh with { LogicalUri = invalidUri };
        var entryIndex = doc.BundleEntries!.FindIndex(e => e.OwnerAssetId == mesh.AssetId);
        doc.BundleEntries[entryIndex] = doc.BundleEntries[entryIndex] with { LogicalUri = invalidUri };

        var ex = Assert.Throws<InvalidDataException>(() => UESceneWriter.Write(doc));
        Assert.Contains("URI", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsBundleSchemeUriWithValidChecksums()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        const string validUri = "Models/SM_Chair.uemodel";
        const string invalidUri = "bundle://XChair.uemodel";
        Assert.Equal(validUri.Length, invalidUri.Length);
        // Write the root-relative fixture, then replace its equal-length STRS value so parsing reaches reader validation.
        var bytes = UESceneWriter.Write(doc);
        var strsEntry = FindChunkEntryOff(bytes, "STRS");
        var strsOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(strsEntry + 8)));
        var strings = ReadStrings(bytes);
        var uriIndex = Array.IndexOf(strings, validUri);
        Assert.True(uriIndex >= 0);
        var offset = strsOffset + 4;
        for (var i = 0; i < uriIndex; i++)
            offset += 4 + Encoding.UTF8.GetByteCount(strings[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint) invalidUri.Length);
        Encoding.UTF8.GetBytes(invalidUri).CopyTo(bytes.AsSpan(offset + 4));
        RecomputeChunkAndHeaderCrc(bytes, strsEntry);

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(bytes));
        Assert.Contains("URI", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsOverlengthResolvedSourceObjectPathWithValidChecksums()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var bytes = UESceneWriter.Write(doc);
        // Expand STRS with an overlength source path and repoint actor source_object_path_str to it.
        // Simpler approach: mutate STRS entry for an existing short path to a long one, recompute STRS CRC + header CRC.
        // Instead, replace actor SourceObjectPath string bytes if present; if not large enough, rebuild via mutation of string table.
        var strings = ReadStrings(bytes);
        var overPath = "/Game/" + new string('q', 16380);
        Assert.True(Encoding.UTF8.GetByteCount(overPath) > 16384);

        // Rebuild file: write with a max-legal path then mutate STRS content length in-place is hard;
        // write a custom file by taking valid bytes and swapping STRS payload with expanded table.
        doc.Actors![0] = doc.Actors[0] with { SourceObjectPath = "short_path_marker_xyz" };
        bytes = UESceneWriter.Write(doc);
        strings = ReadStrings(bytes);
        var markerIndex = Array.IndexOf(strings, "short_path_marker_xyz");
        Assert.True(markerIndex >= 0);

        // Reconstruct STRS with overlength replacement, preserving 8-byte alignment of later chunks.
        strings[markerIndex] = overPath;
        var strsEntry = FindChunkEntryOff(bytes, "STRS");
        var oldStrsOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(strsEntry + 8)));
        var oldStrsStored = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(strsEntry + 16)));
        var newStrsCore = BuildStrsPayload(strings);
        var oldNext = Align8(oldStrsOffset + oldStrsStored);
        var newNext = Align8(oldStrsOffset + newStrsCore.Length);
        var delta = newNext - oldNext;
        var newBytes = new byte[bytes.Length + delta];
        Array.Copy(bytes, 0, newBytes, 0, oldStrsOffset);
        newStrsCore.CopyTo(newBytes.AsSpan(oldStrsOffset));
        // Inter-chunk pad zeros already present between oldStrsOffset+newStrsCore.Length and newNext.
        Array.Copy(bytes, oldNext, newBytes, newNext, bytes.Length - oldNext);

        var dirCount = BinaryPrimitives.ReadUInt32LittleEndian(newBytes.AsSpan(28));
        for (var i = 0; i < dirCount; i++)
        {
            var off = 44 + i * 40;
            var chunkOff = BinaryPrimitives.ReadUInt64LittleEndian(newBytes.AsSpan(off + 8));
            if ((int) chunkOff >= oldNext)
                BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(off + 8), chunkOff + (ulong) delta);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(strsEntry + 16), (ulong) newStrsCore.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(strsEntry + 24), (ulong) newStrsCore.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(strsEntry + 32), Crc32IsoHdlc.Compute(newStrsCore));
        BinaryPrimitives.WriteUInt64LittleEndian(newBytes.AsSpan(32), (ulong) newBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(newBytes.AsSpan(40), Crc32IsoHdlc.Compute(newBytes.AsSpan(0, 40)));

        var ex = Assert.Throws<InvalidDataException>(() => UESceneReader.Read(newBytes));
        Assert.Contains("source object path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildStrsPayload(string[] strings)
    {
        var enc = new UTF8Encoding(false, true);
        var parts = strings.Select(s => enc.GetBytes(s)).ToArray();
        var size = 4 + parts.Sum(p => 4 + p.Length);
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint) strings.Length);
        var o = 4;
        foreach (var p in parts)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(o), (uint) p.Length);
            o += 4;
            p.CopyTo(bytes.AsSpan(o));
            o += p.Length;
        }
        return bytes;
    }

    private static UESceneDocument BuildPermutedDocument(UESceneDocument original)
    {
        // Reverse actor order
        var actors = original.Actors!.AsEnumerable().Reverse().ToList();
        // Reverse components within each actor, then reverse actor-group order
        var compsByActor = original.Components!.GroupBy(c => c.ActorId)
            .ToDictionary(g => g.Key, g => g.Reverse().ToList());
        var components = new List<ComponentRecord>();
        foreach (var actor in actors)
        {
            if (compsByActor.TryGetValue(actor.ActorId, out var ac))
                components.AddRange(ac);
        }
        // Adjust actor ComponentStart/Count
        var adjActors = new List<ActorRecord>();
        var cOff = 0;
        foreach (var actor in actors)
        {
            if (compsByActor.TryGetValue(actor.ActorId, out var ac))
            {
                adjActors.Add(actor with { ComponentStart = (uint) cOff, ComponentCount = (uint) ac.Count });
                cOff += ac.Count;
            }
            else
                adjActors.Add(actor with { ComponentStart = 0, ComponentCount = 0 });
        }
        // Reverse material overrides to follow component reversal
        var matoByComp = original.MaterialOverrides!.GroupBy(o => o.ComponentId)
            .ToDictionary(g => g.Key, g => g.Reverse().ToList());
        var materialOverrides = new List<MaterialOverrideRecord>();
        foreach (var comp in components)
        {
            if (matoByComp.TryGetValue(comp.ComponentId, out var mos))
                materialOverrides.AddRange(mos);
        }
        var adjComps = new List<ComponentRecord>();
        var mOff = 0;
        foreach (var comp in components)
        {
            if (matoByComp.TryGetValue(comp.ComponentId, out var mos))
            {
                adjComps.Add(comp with { MaterialOverrideStart = (uint) mOff, MaterialOverrideCount = (uint) mos.Count });
                mOff += mos.Count;
            }
            else
                adjComps.Add(comp with { MaterialOverrideStart = 0, MaterialOverrideCount = 0 });
        }
        // LODs: do NOT reverse — LodIndex is identity, not orderable. Keep original.
        var lods = original.Lods!.ToList();
        var adjAssets = new List<AssetRecord>();
        var lOff = 0;
        foreach (var asset in original.Assets!)
        {
            if (asset.Kind == AssetKind.StaticMesh)
            {
                var count = asset.LodCount;
                adjAssets.Add(asset with { LodStart = (uint) lOff, LodCount = count });
                lOff += (int) count;
            }
            else
                adjAssets.Add(asset with { LodStart = 0, LodCount = 0 });
        }
        // Reverse instance sets and rebuild transforms
        var instSets = original.InstanceSets!.AsEnumerable().Reverse().ToList();
        var idToSet = original.InstanceSets!.ToDictionary(s => s.InstanceSetId);
        var newInstXforms = new List<InstanceTransform>();
        foreach (var s in instSets)
        {
            var orig = idToSet[s.InstanceSetId];
            newInstXforms.AddRange(original.InstanceTransforms!.Skip((int) orig.TransformStart).Take((int) orig.TransformCount));
        }
        var adjInstSets = new List<InstanceSetRecord>();
        var tOff = 0;
        foreach (var s in instSets)
        {
            var orig = idToSet[s.InstanceSetId];
            adjInstSets.Add(s with { TransformStart = (uint) tOff, TransformCount = orig.TransformCount });
            tOff += (int) orig.TransformCount;
        }
        // Reverse transforms
        var transforms = original.Transforms!.AsEnumerable().Reverse().ToList();
        // Reverse diagnostics
        var diagnostics = original.Diagnostics!.AsEnumerable().Reverse().ToList();
        // Adjust diagnostic ranges for actors
        var diagBySubject = diagnostics.Select((d, i) => (d, i))
            .GroupBy(x => (x.d.SubjectKind, x.d.SubjectId))
            .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        var adjActors2 = new List<ActorRecord>();
        foreach (var actor in adjActors)
        {
            var key = ((byte) 2, actor.ActorId);
            if (diagBySubject.TryGetValue(key, out var indices))
                adjActors2.Add(actor with { DiagnosticStart = (uint) indices[0], DiagnosticCount = (uint) indices.Count });
            else
                adjActors2.Add(actor with { DiagnosticStart = 0, DiagnosticCount = 0 });
        }
        var adjComps2 = new List<ComponentRecord>();
        foreach (var comp in adjComps)
        {
            var key = ((byte) 3, comp.ComponentId);
            if (diagBySubject.TryGetValue(key, out var indices))
                adjComps2.Add(comp with { DiagnosticStart = (uint) indices[0], DiagnosticCount = (uint) indices.Count });
            else
                adjComps2.Add(comp with { DiagnosticStart = 0, DiagnosticCount = 0 });
        }
        // Reverse bundle entries
        var bundleEntries = original.BundleEntries!.AsEnumerable().Reverse().ToList();

        return original with
        {
            Assets = adjAssets,
            Lods = lods,
            Actors = adjActors2,
            Components = adjComps2,
            Transforms = transforms,
            InstanceSets = adjInstSets,
            InstanceTransforms = newInstXforms,
            MaterialOverrides = materialOverrides,
            Diagnostics = diagnostics,
            BundleEntries = bundleEntries,
        };
    }

    private static string[] ReadStrings(byte[] bytes)
    {
        const int stringsDirectoryOffset = 84;
        var offset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(stringsDirectoryOffset + 8)));
        var count = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
        var strings = new string[count];
        offset += 4;
        for (var index = 0; index < count; index++)
        {
            var length = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            offset += 4;
            strings[index] = new UTF8Encoding(false, true).GetString(bytes, offset, length);
            offset += length;
        }
        return strings;
    }

    private static void AssertDirectoryEntry(byte[] bytes, int entryOffset, string expectedFourCc, uint expectedRecordCount)
    {
        Assert.Equal(expectedFourCc, Encoding.ASCII.GetString(bytes, entryOffset, 4));
        Assert.Equal((ushort) 1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryOffset + 4)));
        Assert.Equal((ushort) 0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryOffset + 6)));

        var chunkOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entryOffset + 8));
        var storedLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entryOffset + 16));
        var decodedLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entryOffset + 24));
        Assert.Equal(0ul, chunkOffset % 8);
        Assert.Equal(storedLength, decodedLength);
        Assert.Equal(expectedRecordCount, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 36)));
        Assert.Equal(Crc32IsoHdlc.Compute(bytes.AsSpan(checked((int) chunkOffset), checked((int) storedLength))), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 32)));
    }

    private static int GetMetaOffset(byte[] bytes) => checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(52)));

    private static int FindChunkEntryOff(byte[] bytes, string fourCc)
    {
        var dirCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
        for (var i = 0; i < dirCount; i++)
        {
            var off = 44 + i * 40;
            if (Encoding.ASCII.GetString(bytes, off, 4) == fourCc)
                return off;
        }
        throw new InvalidOperationException($"Chunk {fourCc} not found");
    }

    private static void RecomputeChunkAndHeaderCrc(byte[] bytes, int chunkEntryOffset)
    {
        var chunkOffset = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(chunkEntryOffset + 8)));
        var storedLength = checked((int) BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(chunkEntryOffset + 16)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(chunkEntryOffset + 32), Crc32IsoHdlc.Compute(bytes.AsSpan(chunkOffset, storedLength)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), Crc32IsoHdlc.Compute(bytes.AsSpan(0, 40)));
    }

    private static int Align8(int value) => (value + 7) & ~7;

    [Fact]
    public void CanonicalOutputIgnoresCallerSuppliedPersistentIds()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);
        var baseline = UESceneWriter.Write(doc);

        var remapped = RemapAllIds(doc);
        var remappedBytes = UESceneWriter.Write(remapped);

        Assert.Equal(baseline, remappedBytes);
    }

    [Fact]
    public void CanonicalizationDoesNotMutateCallerDocument()
    {
        var doc = UESceneFixture.CompleteV1Scene(timestamp: 42);

        // Snapshot all IDs and ranges
        var assetIds = doc.Assets!.Select(a => a.AssetId).ToList();
        var actorIds = doc.Actors!.Select(a => a.ActorId).ToList();
        var compIds = doc.Components!.Select(c => c.ComponentId).ToList();
        var xfrmIds = doc.Transforms!.Select(t => t.TransformId).ToList();
        var setIds = doc.InstanceSets!.Select(s => s.InstanceSetId).ToList();
        var assetLodStarts = doc.Assets!.Select(a => a.LodStart).ToList();
        var assetLodCounts = doc.Assets!.Select(a => a.LodCount).ToList();
        var actorCompStarts = doc.Actors!.Select(a => a.ComponentStart).ToList();
        var actorCompCounts = doc.Actors!.Select(a => a.ComponentCount).ToList();
        var compMatoStarts = doc.Components!.Select(c => c.MaterialOverrideStart).ToList();
        var compMatoCounts = doc.Components!.Select(c => c.MaterialOverrideCount).ToList();

        UESceneWriter.Write(doc);

        Assert.Equal(assetIds, doc.Assets!.Select(a => a.AssetId).ToList());
        Assert.Equal(actorIds, doc.Actors!.Select(a => a.ActorId).ToList());
        Assert.Equal(compIds, doc.Components!.Select(c => c.ComponentId).ToList());
        Assert.Equal(xfrmIds, doc.Transforms!.Select(t => t.TransformId).ToList());
        Assert.Equal(setIds, doc.InstanceSets!.Select(s => s.InstanceSetId).ToList());
        Assert.Equal(assetLodStarts, doc.Assets!.Select(a => a.LodStart).ToList());
        Assert.Equal(assetLodCounts, doc.Assets!.Select(a => a.LodCount).ToList());
        Assert.Equal(actorCompStarts, doc.Actors!.Select(a => a.ComponentStart).ToList());
        Assert.Equal(actorCompCounts, doc.Actors!.Select(a => a.ComponentCount).ToList());
        Assert.Equal(compMatoStarts, doc.Components!.Select(c => c.MaterialOverrideStart).ToList());
        Assert.Equal(compMatoCounts, doc.Components!.Select(c => c.MaterialOverrideCount).ToList());
    }

    [Fact]
    public void CanonicalAssetSortPreservesLodOwnership()
    {
        var md = new SceneMetadata("Test", "1.0", "", "/Game/Maps/Test", "/Game/Maps/Test", null, null, 0);

        // Two SM assets in reverse alphabetical order with distinct LODs
        var assetZ = new AssetRecord(
            1, AssetKind.StaticMesh, LocationMode.Bundle, Availability.Missing,
            "/Game/Meshes/Z_Chair", "Z", "Models/z.uemodel", "application/octet-stream",
            UESceneFixture.Sha(1), 100, 0, 0, 0, 2);
        var assetA = new AssetRecord(
            2, AssetKind.StaticMesh, LocationMode.Bundle, Availability.Missing,
            "/Game/Meshes/A_Chair", "A", "Models/a.uemodel", "application/octet-stream",
            UESceneFixture.Sha(2), 100, 0, 0, 2, 2);

        var lodZ0 = UESceneFixture.MakeLod(0, "Models/z_lod0.uemodel", UESceneFixture.Sha(10));
        var lodZ1 = UESceneFixture.MakeLod(1, "Models/z_lod1.uemodel", UESceneFixture.Sha(11));
        var lodA0 = UESceneFixture.MakeLod(0, "Models/a_lod0.uemodel", UESceneFixture.Sha(20));
        var lodA1 = UESceneFixture.MakeLod(1, "Models/a_lod1.uemodel", UESceneFixture.Sha(21));

        var doc = new UESceneDocument(md,
            Assets: new() { assetZ, assetA },
            Lods: new() { lodZ0, lodZ1, lodA0, lodA1 },
            Diagnostics: new() {
                new(1, 1, 1, 0, assetZ.AssetId, assetZ.CanonicalUnrealPath, "mesh unavailable", 0, 0, 0),
                new(1, 1, 1, 0, assetA.AssetId, assetA.CanonicalUnrealPath, "mesh unavailable", 0, 0, 0),
            });

        var bytes = UESceneWriter.Write(doc);
        var read = UESceneReader.Read(bytes);

        Assert.Equal(2, read.Assets!.Count);
        var first = read.Assets[0];
        var second = read.Assets[1];
        Assert.Equal("/Game/Meshes/A_Chair", first.CanonicalUnrealPath);
        Assert.Equal("/Game/Meshes/Z_Chair", second.CanonicalUnrealPath);

        // A_Chair should have A's LODs
        var firstLods = read.Lods!.Skip((int) first.LodStart).Take((int) first.LodCount).ToList();
        Assert.Equal(2, firstLods.Count);
        Assert.Equal("Models/a_lod0.uemodel", firstLods[0].LogicalUri);
        Assert.Equal("Models/a_lod1.uemodel", firstLods[1].LogicalUri);

        // Z_Chair should have Z's LODs
        var secondLods = read.Lods!.Skip((int) second.LodStart).Take((int) second.LodCount).ToList();
        Assert.Equal(2, secondLods.Count);
        Assert.Equal("Models/z_lod0.uemodel", secondLods[0].LogicalUri);
        Assert.Equal("Models/z_lod1.uemodel", secondLods[1].LogicalUri);
    }

    private static UESceneDocument RemapAllIds(UESceneDocument original)
    {
        const ulong assetOff = 0xAAAA_0000_0000_0000UL;
        const ulong actorOff = 0xBBBB_0000_0000_0000UL;
        const ulong compOff = 0xCCCC_0000_0000_0000UL;
        const ulong xfrmOff = 0xDDDD_0000_0000_0000UL;
        const ulong setOff = 0xEEEE_0000_0000_0000UL;

        var assets = original.Assets!.Select(a => a with { AssetId = a.AssetId + assetOff }).ToList();
        var actors = original.Actors!.Select(a => a with
        {
            ActorId = a.ActorId + actorOff,
            ParentActorId = a.ParentActorId == 0 ? 0 : a.ParentActorId + actorOff,
            RootComponentId = a.RootComponentId == 0 ? 0 : a.RootComponentId + compOff,
            LocalTransformId = a.LocalTransformId + xfrmOff,
        }).ToList();
        var components = original.Components!.Select(c => c with
        {
            ComponentId = c.ComponentId + compOff,
            ActorId = c.ActorId + actorOff,
            ParentComponentId = c.ParentComponentId == 0 ? 0 : c.ParentComponentId + compOff,
            LocalTransformId = c.LocalTransformId + xfrmOff,
            MeshAssetId = c.MeshAssetId + assetOff,
            InstanceSetId = c.InstanceSetId == 0 ? 0 : c.InstanceSetId + setOff,
        }).ToList();
        var transforms = original.Transforms!.Select(t => t with { TransformId = t.TransformId + xfrmOff }).ToList();
        var instanceSets = original.InstanceSets!.Select(s => s with
        {
            InstanceSetId = s.InstanceSetId + setOff,
            ComponentId = s.ComponentId + compOff,
        }).ToList();
        var materialOverrides = original.MaterialOverrides!.Select(m => m with
        {
            ComponentId = m.ComponentId + compOff,
            BaseMaterialAssetId = m.BaseMaterialAssetId == 0 ? 0 : m.BaseMaterialAssetId + assetOff,
            OverrideMaterialAssetId = m.OverrideMaterialAssetId == 0 ? 0 : m.OverrideMaterialAssetId + assetOff,
        }).ToList();
        var bundleEntries = original.BundleEntries!.Select(e => e with
        {
            OwnerAssetId = e.OwnerAssetId + assetOff,
        }).ToList();
        var diagnostics = original.Diagnostics!.Select(d => d with
        {
            SubjectId = d.SubjectKind switch
            {
                0 => 0,
                1 => d.SubjectId + assetOff,
                2 => d.SubjectId + actorOff,
                3 => d.SubjectId + compOff,
                4 => d.SubjectId + setOff,
                _ => d.SubjectId, // 5/6 recomputed by Write
            }
        }).ToList();

        return original with
        {
            Assets = assets,
            Actors = actors,
            Components = components,
            Transforms = transforms,
            InstanceSets = instanceSets,
            MaterialOverrides = materialOverrides,
            Diagnostics = diagnostics,
            BundleEntries = bundleEntries,
        };
    }

    private static class UESceneFixture
    {
        public static byte[] Sha(byte seed)
        {
            var b = new byte[32];
            for (var i = 0; i < 32; i++)
                b[i] = (byte) (seed + i);
            return b;
        }

        public static UESceneDocument EmptyScene(ulong timestamp) => new(new SceneMetadata(
            "CUE4Parse",
            "1.0.0",
            "test",
            "/Game/Maps/Test",
            "/Game/Maps/Test.Test:PersistentLevel",
            null,
            null,
            timestamp));

        public static UESceneDocument StaticScene(ulong timestamp) => new(new SceneMetadata(
            "CUE4Parse",
            "z",
            string.Empty,
            "/Game/Maps/Test",
            "/Game/Maps/Test.Test:PersistentLevel",
            "CUE4Parse",
            "\u00e4",
            timestamp));

        public static UESceneDocument WithString(string value) => new(new SceneMetadata(
            value,
            "1.0.0",
            "test",
            "/Game/Maps/Test",
            "/Game/Maps/Test.Test:PersistentLevel",
            null,
            null,
            0));

        public static MeshLodRecord MakeLod(uint index, string uri, byte[] sha) => new(
            index, uri, sha, 1024 + index,
            0.0, 0.0, 0.0, 100.0, 100.0, 100.0, 1.0f, 0);

        public static UESceneDocument CompleteV1Scene(ulong timestamp)
        {
            var md = new SceneMetadata(
                "CUE4Parse", "2.0.0", "test-build",
                "/Game/Maps/Test", "/Game/Maps/Test.Test:PersistentLevel",
                "TestGame", "5.4",
                timestamp);

            var assetId1 = 0x1111_1111_1111_1111UL;
            var assetId2 = 0x2222_2222_2222_2222UL;
            var assetId3 = 0x3333_3333_3333_3333UL;

            var actorId1 = 0xAAAA_0000_0000_0001UL;
            var actorId2 = 0xAAAA_0000_0000_0002UL;

            var compId1 = 0xBBBB_0000_0000_0001UL;
            var compId2 = 0xBBBB_0000_0000_0002UL;
            var compId3 = 0xBBBB_0000_0000_0003UL;

            var xformId1 = 0xCCCC_0000_0000_0001UL;
            var xformId2 = 0xCCCC_0000_0000_0002UL;
            var xformId3 = 0xCCCC_0000_0000_0003UL;
            var xformId4 = 0xCCCC_0000_0000_0004UL;
            var xformId5 = 0xCCCC_0000_0000_0005UL;

            var setId1 = 0xDDDD_0000_0000_0001UL;
            var setId2 = 0xDDDD_0000_0000_0002UL;

            var smSha = Sha(1);
            var matSha = Sha(2);
            var texSha = Sha(3);
            var lod0Sha = Sha(10);
            var lod1Sha = Sha(11);

            const string smPath = "/Game/Meshes/SM_Chair";
            const string smDisplay = "SM_Chair";
            const string smUri = "Models/SM_Chair.uemodel";
            const string smMime = "application/x-uemodel";
            const string lod0Uri = "Models/SM_Chair_LOD0.uemodel";
            const string lod1Uri = "Models/SM_Chair_LOD1.uemodel";
            const string matPath = "/Game/Materials/M_Basic";
            const string matDisplay = "M_Basic";
            const string matUri = "Materials/M_Basic.json";
            const string matMime = "application/json";
            const string texPath = "/Game/Textures/T_Diffuse";
            const string texDisplay = "T_Diffuse";
            const string texUri = "Textures/T_Diffuse.png";
            const string texMime = "image/png";
            const string diagMsg = "mesh export warning";
            const string diagSrc = "/Game/Meshes/SM_Chair";

            var overrides = new List<MaterialOverrideRecord> {
                new(compId1, 0, 1, assetId2, assetId2, 0, 8, 0, 0, 0),
            };
            var matoBlob = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(matoBlob, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(matoBlob.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(matoBlob.AsSpan(4), 0);

            var diagnostics = new List<DiagnosticRecord> {
                new(1, 1, 1, 0, assetId1, diagSrc, diagMsg, 0, 0, 0),
            };
            var diagBlob = Array.Empty<byte>();

            var lods = new List<MeshLodRecord> {
                MakeLod(0, lod0Uri, lod0Sha),
                MakeLod(1, lod1Uri, lod1Sha),
            };

            return new UESceneDocument(
                md,
                Assets: new List<AssetRecord> {
                    new(assetId1, AssetKind.StaticMesh, LocationMode.Bundle, Availability.Available,
                        smPath, smDisplay, smUri, smMime, smSha, 4096, 0, 1, 0, 2),
                    new(assetId2, AssetKind.Material, LocationMode.Bundle, Availability.Available,
                        matPath, matDisplay, matUri, matMime, matSha, 512, 0, 0, 0, 0),
                    new(assetId3, AssetKind.Texture, LocationMode.Bundle, Availability.Available,
                        texPath, texDisplay, texUri, texMime, texSha, 2048, 0, 0, 0, 0),
                },
                Lods: lods,
                Actors: new List<ActorRecord> {
                    new(actorId1, 0, "BP_Actor", "UBlueprintGeneratedClass",
                        "/Game/Maps/Test.Test:PersistentLevel.BP_Actor",
                        compId1, xformId1, 0, 2, 0, 0, 0),
                    new(actorId2, 0, "BP_Actor_2", "UBlueprintGeneratedClass",
                        "/Game/Maps/Test.Test:PersistentLevel.BP_Actor_2",
                        compId3, xformId2, 2, 1, 0, 0, 0),
                },
                Components: new List<ComponentRecord> {
                    new(compId1, actorId1, 0, ComponentKind.StaticMesh,
                        "StaticMeshComponent", "UStaticMeshComponent",
                        "/Game/Maps/Test.Test:PersistentLevel.BP_Actor:StaticMeshComponent",
                        xformId3, assetId1, 0, 1, 0, VisibilityFlags.Visible, 0, 0),
                    new(compId2, actorId1, 0, ComponentKind.InstancedStaticMesh,
                        "InstancedStaticMeshComponent", "UInstancedStaticMeshComponent",
                        "/Game/Maps/Test.Test:PersistentLevel.BP_Actor:InstancedStaticMeshComponent",
                        xformId4, assetId1, 0, 0, setId1, VisibilityFlags.Visible, 0, 0),
                    new(compId3, actorId2, 0, ComponentKind.HierarchicalInstancedStaticMesh,
                        "HierarchicalInstancedStaticMeshComponent", "UHierarchicalInstancedStaticMeshComponent",
                        "/Game/Maps/Test.Test:PersistentLevel.BP_Actor_2:HierarchicalInstancedStaticMeshComponent",
                        xformId5, assetId1, 0, 0, setId2, VisibilityFlags.Visible, 0, 0),
                },
                Transforms: new List<TransformRecord> {
                    new(xformId1, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0),
                    new(xformId2, 100, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0),
                    new(xformId3, 0, 0, 50, 0, 0, 0, 1, 1, 1, 1, 0),
                    new(xformId4, 10, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0),
                    new(xformId5, 20, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0),
                },
                InstanceSets: new List<InstanceSetRecord> {
                    new(setId1, compId2, 2, 0, 0, 2),
                    new(setId2, compId3, 1, 0, 2, 1),
                },
                InstanceTransforms: new List<InstanceTransform> {
                    new(5, 0, 0, 0, 0, 0, 1, 1, 1, 1),
                    new(10, 0, 0, 0, 0, 0, 1, 1, 1, 1),
                    new(15, 0, 0, 0, 0, 0, 1, 1, 1, 1),
                },
                MaterialOverrides: overrides,
                MatoBlob: matoBlob,
                Diagnostics: diagnostics,
                DiagBlob: diagBlob,
                BundleEntries: new List<BundleEntry> {
                    // SM uses one primary Model resource containing all LODs.
                    new((byte) BundleResourceKind.Model, LocationMode.Bundle, Availability.Available,
                        smUri, assetId1, uint.MaxValue, smSha, 4096, smMime, 0, 0),
                    // MaterialJson optional — include one matching primary
                    new((byte) BundleResourceKind.MaterialJson, LocationMode.Bundle, Availability.Available,
                        matUri, assetId2, uint.MaxValue, matSha, 512, matMime, 0, 0),
                    // Texture primary
                    new((byte) BundleResourceKind.Texture, LocationMode.Bundle, Availability.Available,
                        texUri, assetId3, uint.MaxValue, texSha, 2048, texMime, 0, 0),
                });
        }

        public static UESceneDocument WithOptionalTextureAvailability()
        {
            const ulong textureId = 0x4444_4444_4444_4444UL;
            return new UESceneDocument(
                EmptyScene(42).Metadata,
                Assets: new List<AssetRecord> {
                    new(textureId, AssetKind.Texture, LocationMode.Bundle, Availability.Optional,
                        "/Game/Textures/T_Optional", "T_Optional", "Textures/T_Optional.png", "image/png",
                        Sha(4), 0, 0, 1, 0, 0),
                },
                Diagnostics: new List<DiagnosticRecord> {
                    new(1, 1, 1, 0, textureId, "/Game/Textures/T_Optional", "optional texture unavailable", 0, 0, 0),
                });
        }
    }
}
