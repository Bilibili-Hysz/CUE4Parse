using System.Buffers.Binary;
using System.Text;

namespace CUE4Parse_Conversion.UEScene;

public static class UESceneReader
{
    private const int HeaderSize = 44;
    private const int DirectoryEntrySize = 40;
    private const long MaxFileSize = 2L * 1024 * 1024 * 1024;
    private const long MaxChunkSize = 1L * 1024 * 1024 * 1024;
    private const long MaxTotalFixedRecords = 10_000_000;
    private const int MaxUriUtf8Bytes = 4096;
    private const int MaxSourceObjectPathUtf8Bytes = 16384;
    private const uint CanonicalQuietNanBits = 0x7FC00000;
    private const uint RetiredCameraFourCc = 0x524D4143;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Dictionary<uint, string> KnownFourCc = new()
    {
        [0x4154454D] = "META",
        [0x53525453] = "STRS",
        [0x54535341] = "ASST",
        [0x52544341] = "ACTR",
        [0x504D4F43] = "COMP",
        [0x4D524658] = "XFRM",
        [0x4F54414D] = "MATO",
        [0x47414944] = "DIAG",
        [0x58444E42] = "BNDX",
        [0x54534E49] = "INST",
        [0x4554494C] = "LITE",
    };

    private static readonly string[] RequiredBaseChunks =
    {
        "META", "STRS", "ASST", "ACTR", "COMP", "XFRM", "MATO", "DIAG", "BNDX"
    };

    public static UESceneDocument Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("File too small for header.");
        if ((long) data.Length > MaxFileSize)
            throw new InvalidDataException("File exceeds 2 GiB limit.");

        if (!data[..8].SequenceEqual("UESCENE\0"u8))
            throw new InvalidDataException("Invalid magic.");
        var major = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        if (major != 1 || minor != 0)
            throw new InvalidDataException($"Unsupported version {major}.{minor}.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != 0)
            throw new InvalidDataException("Nonzero header flags.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != HeaderSize)
            throw new InvalidDataException("Unexpected header size.");
        var directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[20..]);
        var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        if (directoryCount > 64)
            throw new InvalidDataException("Directory count exceeds 64.");
        var fileSize = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
        if (fileSize != (ulong) data.Length)
            throw new InvalidDataException("File size mismatch.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[40..]) != Crc32IsoHdlc.Compute(data[..40]))
            throw new InvalidDataException("Header CRC mismatch.");

        var dirEnd64 = checked(directoryOffset + directoryCount * (ulong) DirectoryEntrySize);
        if (directoryOffset < HeaderSize || dirEnd64 > (ulong) data.Length || dirEnd64 > int.MaxValue)
            throw new InvalidDataException("Directory extends beyond file.");
        var dirEnd = (int) dirEnd64;

        var chunks = new List<ChunkInfo>();
        var seenFourCc = new HashSet<uint>();
        var intervals = new List<(ulong start, ulong end)>();
        for (var i = 0; i < directoryCount; i++)
        {
            var entryOff = checked((int) directoryOffset + i * DirectoryEntrySize);
            var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[entryOff..]);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(data[(entryOff + 4)..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(entryOff + 6)..]);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(data[(entryOff + 8)..]);
            var storedLength = BinaryPrimitives.ReadUInt64LittleEndian(data[(entryOff + 16)..]);
            var decodedLength = BinaryPrimitives.ReadUInt64LittleEndian(data[(entryOff + 24)..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(data[(entryOff + 32)..]);
            var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(entryOff + 36)..]);

            if (!IsPrintableFourCc(fourCc))
                throw new InvalidDataException("Non-printable FourCC.");
            if (storedLength > (ulong) MaxChunkSize)
                throw new InvalidDataException("Chunk exceeds 1 GiB limit.");
            var chunkEnd = checked(offset + storedLength);
            if (offset < (ulong) HeaderSize)
                throw new InvalidDataException($"Chunk offset {offset} overlaps header.");
            if (offset < (ulong) dirEnd && chunkEnd > directoryOffset)
                throw new InvalidDataException("Chunk overlaps directory.");
            if (chunkEnd > fileSize)
                throw new InvalidDataException("Chunk extends beyond file.");
            if (offset % 8 != 0)
                throw new InvalidDataException("Chunk offset not 8-byte aligned.");
            if (flags != 0)
                throw new InvalidDataException("Nonzero flags for chunk.");
            if (storedLength != decodedLength)
                throw new InvalidDataException("Stored != decoded length.");

            var chunkData = data.Slice(checked((int) offset), checked((int) storedLength));
            if (Crc32IsoHdlc.Compute(chunkData) != crc)
                throw new InvalidDataException("CRC mismatch for chunk.");

            foreach (var (s, e) in intervals)
            {
                if (offset < e && chunkEnd > s)
                    throw new InvalidDataException("Overlapping chunks.");
            }
            intervals.Add((offset, chunkEnd));

            var isKnown = KnownFourCc.TryGetValue(fourCc, out var name);
            if (fourCc == RetiredCameraFourCc)
                throw new InvalidDataException("CAMR camera records are retired in UEScene V1.");
            if (isKnown)
            {
                if (!seenFourCc.Add(fourCc))
                    throw new InvalidDataException($"Duplicate chunk {name}.");
                if (version != 1)
                    throw new InvalidDataException($"Chunk {name} version {version} != 1.");
            }

            chunks.Add(new ChunkInfo(fourCc, name, offset, storedLength, recordCount, isKnown));
        }

        // §10 global total fixed-record cap — must precede any payload allocation.
        checked
        {
            long totalFixedRecords = 0;
            foreach (var chunk in chunks.Where(c => c.Name is not null and not ("META" or "STRS")))
                totalFixedRecords += chunk.RecordCount;
            if (totalFixedRecords > MaxTotalFixedRecords)
                throw new InvalidDataException($"Total declared fixed records exceeds {MaxTotalFixedRecords:N0}.");
        }

        var chunkDict = new Dictionary<string, ChunkInfo>();
        foreach (var chunk in chunks)
        {
            if (chunk.IsKnown && chunk.Name != null)
                chunkDict[chunk.Name] = chunk;
        }

        foreach (var required in RequiredBaseChunks)
        {
            if (!chunkDict.ContainsKey(required))
                throw new InvalidDataException($"Missing required chunk {required}.");
        }

        // STRS exact consume + 65536 per-string cap
        var strsChunk = chunkDict["STRS"];
        var strsData = data.Slice(checked((int) strsChunk.Offset), checked((int) strsChunk.Length));
        if (strsData.Length < 4) throw new InvalidDataException("STRS too small.");
        var stringCount = BinaryPrimitives.ReadUInt32LittleEndian(strsData);
        if (stringCount != strsChunk.RecordCount)
            throw new InvalidDataException("STRS directory record_count mismatch.");
        if (stringCount > 1_000_000)
            throw new InvalidDataException("String count exceeds limit.");
        var strsStrings = new string[stringCount];
        var strsOff = 4;
        for (var i = 0; i < stringCount; i++)
        {
            if (strsOff + 4 > strsData.Length) throw new InvalidDataException("STRS truncated.");
            var strLen = BinaryPrimitives.ReadUInt32LittleEndian(strsData[strsOff..]);
            strsOff += 4;
            if (strLen > 65_536) throw new InvalidDataException("String exceeds 65,536 UTF-8 bytes.");
            if (strsOff + strLen > strsData.Length) throw new InvalidDataException("STRS truncated string.");
            try { strsStrings[i] = StrictUtf8.GetString(strsData.Slice(strsOff, checked((int) strLen))); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8 in STRS.", ex); }
            if (strsStrings[i].IndexOf('\0') >= 0) throw new InvalidDataException("STRS contains NUL.");
            strsOff = checked(strsOff + (int) strLen);
        }
        if (strsOff != strsData.Length)
            throw new InvalidDataException("STRS did not exactly consume payload.");

        // META
        var metaChunk = chunkDict["META"];
        var metaData = data.Slice(checked((int) metaChunk.Offset), checked((int) metaChunk.Length));
        if (metaData.Length != 68) throw new InvalidDataException("META payload size mismatch.");
        var metaRecordCount = BinaryPrimitives.ReadUInt32LittleEndian(metaData);
        if (metaRecordCount != 1 || metaChunk.RecordCount != 1)
            throw new InvalidDataException("META record_count != 1.");
        var metaOff = 4;
        var formatNameStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var genNameStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var genVersionStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var genBuildStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var worldPathStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var levelPathStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var sourceGameStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var sourceEngineStr = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var exportTime = BinaryPrimitives.ReadUInt64LittleEndian(metaData[metaOff..]); metaOff += 8;
        var coord = metaData[metaOff++];
        var unit = metaData[metaOff++];
        var xformSpace = metaData[metaOff++];
        var quatOrder = metaData[metaOff++];
        var reserved0 = BinaryPrimitives.ReadUInt32LittleEndian(metaData[metaOff..]); metaOff += 4;
        var requiredFeatures = BinaryPrimitives.ReadUInt64LittleEndian(metaData[metaOff..]); metaOff += 8;
        var optionalFeatures = BinaryPrimitives.ReadUInt64LittleEndian(metaData[metaOff..]); metaOff += 8;
        if (metaOff != 68) throw new InvalidDataException("META did not exactly consume payload.");
        if (coord != SceneMetadata.UnrealLeftHandedZUp || unit != SceneMetadata.Centimeter
            || xformSpace != SceneMetadata.LocalToParent || quatOrder != SceneMetadata.Xyzw)
            throw new InvalidDataException("META coordinate contract invalid.");
        if (reserved0 != 0) throw new InvalidDataException("META reserved0 nonzero.");
        if ((requiredFeatures & ~SceneMetadata.KnownRequiredBits) != 0)
            throw new InvalidDataException("Unknown required feature bits.");
        if ((optionalFeatures & SceneMetadata.RetiredCameraRecordsBit) != 0)
            throw new InvalidDataException("CAMR camera records are retired in UEScene V1.");
        if (ResolveStr(strsStrings, formatNameStr) != "uescene")
            throw new InvalidDataException("META format_name_str must be uescene.");

        var hasInstanceBit = (requiredFeatures & SceneMetadata.InstanceSetsBit) != 0;
        if (hasInstanceBit != chunkDict.ContainsKey("INST"))
            throw new InvalidDataException("INST presence must match META required_features bit0.");
        if (!hasInstanceBit && chunkDict.ContainsKey("INST"))
            throw new InvalidDataException("INST present without INSTANCE_SETS bit.");
        var hasLightBit = (optionalFeatures & SceneMetadata.LightRecordsBit) != 0;
        if (hasLightBit != chunkDict.ContainsKey("LITE"))
            throw new InvalidDataException("LITE presence must match META optional_features bit1.");

        var metadata = new SceneMetadata(
            ResolveStr(strsStrings, genNameStr),
            ResolveStr(strsStrings, genVersionStr),
            ResolveStr(strsStrings, genBuildStr),
            ResolveStr(strsStrings, worldPathStr),
            ResolveStr(strsStrings, levelPathStr),
            sourceGameStr == 0 ? null : ResolveStr(strsStrings, sourceGameStr),
            sourceEngineStr == 0 ? null : ResolveStr(strsStrings, sourceEngineStr),
            exportTime,
            requiredFeatures,
            optionalFeatures);

        // ASST
        var asstChunk = chunkDict["ASST"];
        var asstData = data.Slice(checked((int) asstChunk.Offset), checked((int) asstChunk.Length));
        if (asstData.Length < 8) throw new InvalidDataException("ASST too small.");
        var assetCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(asstData));
        var lodCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(asstData[4..]));
        if ((uint) assetCount != asstChunk.RecordCount)
            throw new InvalidDataException("ASST directory record_count mismatch.");
        CheckCount(assetCount, 1_000_000, "assets");
        CheckCount(lodCount, 64 * Math.Max(1, assetCount), "lods");
        var expectedAsst = checked(8L + assetCount * 84L + lodCount * 104L);
        if (asstData.Length != expectedAsst)
            throw new InvalidDataException("ASST payload size mismatch.");
        var assets = new List<AssetRecord>(assetCount);
        var asstOff = 8;
        for (var i = 0; i < assetCount; i++)
        {
            var r = asstData[asstOff..];
            var assetId = BinaryPrimitives.ReadUInt64LittleEndian(r);
            var kindB = r[8];
            var locB = r[9];
            var availB = r[10];
            if (r[11] != 0) throw new InvalidDataException("ASST reserved nonzero.");
            if (kindB is < 1 or > 3) throw new InvalidDataException("Unknown asset kind.");
            if (locB is < 1 or > 3) throw new InvalidDataException("Unknown location mode.");
            if (locB == (byte) LocationMode.Embedded) throw new InvalidDataException("Embedded location rejected in V1.");
            if (availB is < 1 or > 4) throw new InvalidDataException("Unknown availability.");
            var sha = RequireShaCopy(r[28..60]);
            assets.Add(new AssetRecord(
                assetId, (AssetKind) kindB, (LocationMode) locB, (Availability) availB,
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[12..])),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[16..])),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[20..])),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[24..])),
                sha,
                BinaryPrimitives.ReadUInt64LittleEndian(r[60..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[68..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[72..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[76..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[80..])));
            asstOff += 84;
        }
        var lods = new List<MeshLodRecord>(lodCount);
        for (var i = 0; i < lodCount; i++)
        {
            var r = asstData[asstOff..];
            for (var fieldOffset = 48; fieldOffset <= 88; fieldOffset += 8)
                RejectNegativeZero64(r[fieldOffset..], "LOD bounds");
            RejectNegativeZero32(r[96..], "LOD screen size");
            var sha = RequireShaCopy(r[8..40]);
            var screen = BinaryPrimitives.ReadSingleLittleEndian(r[96..]);
            ValidateScreenSize(screen);
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(r[100..]);
            if (reserved != 0) throw new InvalidDataException("LOD reserved nonzero.");
            var bMinX = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[48..]));
            var bMinY = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[56..]));
            var bMinZ = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[64..]));
            var bMaxX = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[72..]));
            var bMaxY = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[80..]));
            var bMaxZ = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[88..]));
            if (!double.IsFinite(bMinX) || !double.IsFinite(bMinY) || !double.IsFinite(bMinZ)
                || !double.IsFinite(bMaxX) || !double.IsFinite(bMaxY) || !double.IsFinite(bMaxZ))
                throw new InvalidDataException("LOD bounds non-finite.");
            if (bMinX > bMaxX || bMinY > bMaxY || bMinZ > bMaxZ)
                throw new InvalidDataException("LOD bounds min > max.");
            lods.Add(new MeshLodRecord(
                BinaryPrimitives.ReadUInt32LittleEndian(r),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[4..])),
                sha,
                BinaryPrimitives.ReadUInt64LittleEndian(r[40..]),
                bMinX, bMinY, bMinZ, bMaxX, bMaxY, bMaxZ, screen, reserved));
            asstOff += 104;
        }
        if (asstOff != asstData.Length) throw new InvalidDataException("ASST did not exactly consume payload.");

        // ACTR
        var actrChunk = chunkDict["ACTR"];
        var actrData = data.Slice(checked((int) actrChunk.Offset), checked((int) actrChunk.Length));
        if (actrData.Length < 4) throw new InvalidDataException("ACTR too small.");
        var actorCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(actrData));
        if ((uint) actorCount != actrChunk.RecordCount)
            throw new InvalidDataException("ACTR directory record_count mismatch.");
        CheckCount(actorCount, 1_000_000, "actors");
        if (actrData.Length != checked(4 + actorCount * 64))
            throw new InvalidDataException("ACTR payload size mismatch.");
        var actors = new List<ActorRecord>(actorCount);
        var actrOff = 4;
        for (var i = 0; i < actorCount; i++)
        {
            var r = actrData[actrOff..];
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(r[52..]);
            if (flags != 0) throw new InvalidDataException("Actor flags must be zero.");
            actors.Add(new ActorRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(r),
                BinaryPrimitives.ReadUInt64LittleEndian(r[8..]),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[16..])),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[20..])),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[24..])),
                BinaryPrimitives.ReadUInt64LittleEndian(r[28..]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[36..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[44..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[48..]),
                flags,
                BinaryPrimitives.ReadUInt32LittleEndian(r[56..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[60..])));
            actrOff += 64;
        }

        // COMP
        var compChunk = chunkDict["COMP"];
        var compData = data.Slice(checked((int) compChunk.Offset), checked((int) compChunk.Length));
        if (compData.Length < 4) throw new InvalidDataException("COMP too small.");
        var compCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(compData));
        if ((uint) compCount != compChunk.RecordCount)
            throw new InvalidDataException("COMP directory record_count mismatch.");
        CheckCount(compCount, 4_000_000, "components");
        if (compData.Length != checked(4 + compCount * 88))
            throw new InvalidDataException("COMP payload size mismatch.");
        var components = new List<ComponentRecord>(compCount);
        var compOff = 4;
        for (var i = 0; i < compCount; i++)
        {
            var r = compData[compOff..];
            var kindB = r[24];
            if (kindB is < 1 or > 3) throw new InvalidDataException("Unknown component kind.");
            if (r[25] != 0 || r[26] != 0 || r[27] != 0) throw new InvalidDataException("COMP reserved0 nonzero.");
            var vis = BinaryPrimitives.ReadUInt32LittleEndian(r[72..]);
            if ((vis & ~VisibilityFlags.AllowedMask) != 0)
                throw new InvalidDataException("Visibility flags outside allowed mask.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(r[84..]) != 0)
                throw new InvalidDataException("COMP reserved1 nonzero.");
            components.Add(new ComponentRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(r),
                BinaryPrimitives.ReadUInt64LittleEndian(r[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[16..]),
                (ComponentKind) kindB,
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[28..])),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[32..])),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[36..])),
                BinaryPrimitives.ReadUInt64LittleEndian(r[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[48..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[56..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[60..]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[64..]),
                vis,
                BinaryPrimitives.ReadUInt32LittleEndian(r[76..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[80..])));
            compOff += 88;
        }

        // XFRM
        var xfrmChunk = chunkDict["XFRM"];
        var xfrmData = data.Slice(checked((int) xfrmChunk.Offset), checked((int) xfrmChunk.Length));
        if (xfrmData.Length < 4) throw new InvalidDataException("XFRM too small.");
        var transformCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(xfrmData));
        if ((uint) transformCount != xfrmChunk.RecordCount)
            throw new InvalidDataException("XFRM directory record_count mismatch.");
        CheckCount(transformCount, 5_000_000, "transforms");
        if (xfrmData.Length != checked(4 + transformCount * 64))
            throw new InvalidDataException("XFRM payload size mismatch.");
        var transforms = new List<TransformRecord>(transformCount);
        var xfrmOff = 4;
        for (var i = 0; i < transformCount; i++)
        {
            var r = xfrmData[xfrmOff..];
            for (var fieldOffset = 8; fieldOffset <= 24; fieldOffset += 8)
                RejectNegativeZero64(r[fieldOffset..], "XFRM");
            for (var fieldOffset = 32; fieldOffset <= 56; fieldOffset += 4)
                RejectNegativeZero32(r[fieldOffset..], "XFRM");
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(r[60..]);
            if (reserved != 0) throw new InvalidDataException("XFRM reserved nonzero.");
            var t = new TransformRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(r),
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[8..])),
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[16..])),
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r[24..])),
                BinaryPrimitives.ReadSingleLittleEndian(r[32..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[36..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[40..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[44..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[48..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[52..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[56..]),
                reserved);
            ValidateFiniteTrs(t.TranslationX, t.TranslationY, t.TranslationZ, t.RotationX, t.RotationY, t.RotationZ, t.RotationW, t.ScaleX, t.ScaleY, t.ScaleZ, t.TransformId.ToString());
            transforms.Add(t);
            xfrmOff += 64;
        }

        // INST
        var instanceSets = new List<InstanceSetRecord>();
        var instanceTransforms = new List<InstanceTransform>();
        if (chunkDict.TryGetValue("INST", out var instChunk))
        {
            var instData = data.Slice(checked((int) instChunk.Offset), checked((int) instChunk.Length));
            if (instData.Length < 8) throw new InvalidDataException("INST too small.");
            var setCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(instData));
            var xformCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(instData[4..]));
            if ((uint) setCount != instChunk.RecordCount)
                throw new InvalidDataException("INST directory record_count mismatch.");
            CheckCount(setCount, 1_000_000, "instance sets");
            CheckCount(xformCount, 50_000_000, "instance transforms");
            var expectedInst = checked(8L + setCount * 32L + xformCount * 40L);
            if (instData.Length != expectedInst)
                throw new InvalidDataException("INST payload size mismatch.");
            var instOff = 8;
            for (var i = 0; i < setCount; i++)
            {
                var r = instData[instOff..];
                var encoding = BinaryPrimitives.ReadUInt32LittleEndian(r[20..]);
                if (encoding != 0) throw new InvalidDataException("INST encoding must be 0.");
                var instCount = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
                var tCount = BinaryPrimitives.ReadUInt32LittleEndian(r[28..]);
                if (instCount != tCount) throw new InvalidDataException("INST count != transformCount.");
                instanceSets.Add(new InstanceSetRecord(
                    BinaryPrimitives.ReadUInt64LittleEndian(r),
                    BinaryPrimitives.ReadUInt64LittleEndian(r[8..]),
                    instCount, encoding,
                    BinaryPrimitives.ReadUInt32LittleEndian(r[24..]),
                    tCount));
                instOff += 32;
            }
            for (var i = 0; i < xformCount; i++)
            {
                var r = instData[instOff..];
                for (var fieldOffset = 0; fieldOffset < 40; fieldOffset += 4)
                    RejectNegativeZero32(r[fieldOffset..], "INST");
                var t = new InstanceTransform(
                    BinaryPrimitives.ReadSingleLittleEndian(r),
                    BinaryPrimitives.ReadSingleLittleEndian(r[4..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[8..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[12..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[16..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[20..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[24..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[28..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[32..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[36..]));
                ValidateFiniteTrs(t.TranslationX, t.TranslationY, t.TranslationZ, t.RotationX, t.RotationY, t.RotationZ, t.RotationW, t.ScaleX, t.ScaleY, t.ScaleZ, $"instance[{i}]");
                instanceTransforms.Add(t);
                instOff += 40;
            }
            if (instOff != instData.Length) throw new InvalidDataException("INST did not exactly consume payload.");
        }

        // LITE: fixed 116-byte records, present only when optional bit1 is set.
        var lights = new List<LightRecord>();
        if (chunkDict.TryGetValue("LITE", out var liteChunk))
        {
            var liteData = data.Slice(checked((int) liteChunk.Offset), checked((int) liteChunk.Length));
            if (liteData.Length < 4) throw new InvalidDataException("LITE too small.");
            var lightCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(liteData));
            if ((uint) lightCount != liteChunk.RecordCount) throw new InvalidDataException("LITE directory record_count mismatch.");
            if (lightCount == 0) throw new InvalidDataException("LITE must be nonempty when present.");
            CheckCount(lightCount, 1_000_000, "lights");
            if (liteData.Length != checked(4 + lightCount * 116)) throw new InvalidDataException("LITE payload size mismatch.");
            var liteOff = 4;
            for (var i = 0; i < lightCount; i++)
            {
                var r = liteData[liteOff..];
                if (r[47] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(r[84..]) != 0)
                    throw new InvalidDataException("LITE reserved nonzero.");
                foreach (var floatOffset in new[] { 56, 60, 64, 68, 72, 76, 80, 88, 92, 96, 100, 104, 108, 112 })
                    if (BinaryPrimitives.ReadUInt32LittleEndian(r[floatOffset..]) == 0x8000_0000)
                        throw new InvalidDataException("LITE negative zero float is noncanonical.");
                lights.Add(new LightRecord(
                    BinaryPrimitives.ReadUInt64LittleEndian(r), BinaryPrimitives.ReadUInt64LittleEndian(r[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(r[16..]), BinaryPrimitives.ReadUInt64LittleEndian(r[24..]),
                    ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[32..])),
                    ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[36..])),
                    ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[40..])),
                    (LightType) r[44], (LightColorEncoding) r[45], (ELightUnits) r[46],
                    r[48], r[49], r[50], r[51], BinaryPrimitives.ReadUInt32LittleEndian(r[52..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[56..]), BinaryPrimitives.ReadSingleLittleEndian(r[60..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[64..]), BinaryPrimitives.ReadSingleLittleEndian(r[68..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[72..]), BinaryPrimitives.ReadSingleLittleEndian(r[76..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[80..]), BinaryPrimitives.ReadUInt32LittleEndian(r[84..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[88..]), BinaryPrimitives.ReadSingleLittleEndian(r[92..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[96..]), BinaryPrimitives.ReadSingleLittleEndian(r[100..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[104..]), BinaryPrimitives.ReadSingleLittleEndian(r[108..]),
                    BinaryPrimitives.ReadSingleLittleEndian(r[112..])));
                liteOff += 116;
            }
        }

        // MATO
        var matoChunk = chunkDict["MATO"];
        var matoData = data.Slice(checked((int) matoChunk.Offset), checked((int) matoChunk.Length));
        if (matoData.Length < 8) throw new InvalidDataException("MATO too small.");
        var overrideCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(matoData));
        var blobLen = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(matoData[4..]));
        if ((uint) overrideCount != matoChunk.RecordCount)
            throw new InvalidDataException("MATO directory record_count mismatch.");
        CheckCount(overrideCount, 4_000_000, "overrides");
        if (blobLen > 64 * 1024 * 1024) throw new InvalidDataException("MATO blob exceeds 64 MiB.");
        var expectedMato = checked(8L + overrideCount * 56L + blobLen);
        if (matoData.Length != expectedMato)
            throw new InvalidDataException("MATO payload size mismatch.");
        var materialOverrides = new List<MaterialOverrideRecord>(overrideCount);
        var matoOff = 8;
        for (var i = 0; i < overrideCount; i++)
        {
            var r = matoData[matoOff..];
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(r[12..]);
            if ((flags & ~1u) != 0) throw new InvalidDataException("MATO flags only bit0 allowed.");
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(r[52..]);
            if (reserved != 0) throw new InvalidDataException("MATO reserved nonzero.");
            var pOff = BinaryPrimitives.ReadUInt64LittleEndian(r[32..]);
            var pLen = BinaryPrimitives.ReadUInt32LittleEndian(r[40..]);
            if ((flags & 1u) == 0 && (pOff != 0 || pLen != 0))
                throw new InvalidDataException("MATO clear HAS_PARAMETER_PAYLOAD requires zero offset/length.");
            materialOverrides.Add(new MaterialOverrideRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(r),
                BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
                flags,
                BinaryPrimitives.ReadUInt64LittleEndian(r[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[24..]),
                pOff, pLen,
                BinaryPrimitives.ReadUInt32LittleEndian(r[44..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[48..]),
                reserved));
            matoOff += 56;
        }
        var matoBlob = new byte[blobLen];
        matoData.Slice(matoOff, blobLen).CopyTo(matoBlob);
        foreach (var o in materialOverrides.Where(o => (o.Flags & 1u) != 0))
            ValidateOpaqueEnvelope(matoBlob, o.ParameterPayloadOffset, o.ParameterPayloadLength, "MATO");
        if ((requiredFeatures & SceneMetadata.MatoProvenanceBit) != 0
            && materialOverrides.All(o => (o.Flags & 1u) == 0))
            throw new InvalidDataException("META bit1 set without MATO parameter payload flag.");
        if ((requiredFeatures & SceneMetadata.MatoProvenanceBit) == 0
            && materialOverrides.Any(o => (o.Flags & 1u) != 0))
            throw new InvalidDataException("MATO parameter payload present without META bit1.");

        // DIAG
        var diagChunk = chunkDict["DIAG"];
        var diagData = data.Slice(checked((int) diagChunk.Offset), checked((int) diagChunk.Length));
        if (diagData.Length < 8) throw new InvalidDataException("DIAG too small.");
        var diagCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(diagData));
        var dBlobLen = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(diagData[4..]));
        if ((uint) diagCount != diagChunk.RecordCount)
            throw new InvalidDataException("DIAG directory record_count mismatch.");
        CheckCount(diagCount, 1_000_000, "diagnostics");
        if (dBlobLen > 64 * 1024 * 1024) throw new InvalidDataException("DIAG blob exceeds 64 MiB.");
        var expectedDiag = checked(8L + diagCount * 40L + dBlobLen);
        if (diagData.Length != expectedDiag)
            throw new InvalidDataException("DIAG payload size mismatch.");
        var diagnostics = new List<DiagnosticRecord>(diagCount);
        var diagOff = 8;
        for (var i = 0; i < diagCount; i++)
        {
            var r = diagData[diagOff..];
            var severity = r[0];
            var subjectKind = r[1];
            var code = BinaryPrimitives.ReadUInt16LittleEndian(r[2..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
            var subjectId = BinaryPrimitives.ReadUInt64LittleEndian(r[8..]);
            var detailsOff = BinaryPrimitives.ReadUInt64LittleEndian(r[24..]);
            var detailsLen = BinaryPrimitives.ReadUInt32LittleEndian(r[32..]);
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(r[36..]);
            if (severity is < 1 or > 3) throw new InvalidDataException("Diagnostic severity out of range.");
            if (subjectKind > 6) throw new InvalidDataException("Diagnostic subject_kind out of range.");
            if (code is < 1 or > 10) throw new InvalidDataException("Diagnostic code out of range.");
            if ((flags & ~1u) != 0) throw new InvalidDataException("DIAG flags only bit0 allowed.");
            if (reserved != 0) throw new InvalidDataException("DIAG reserved nonzero.");
            if (subjectKind == 0 && subjectId != 0) throw new InvalidDataException("Scene diagnostic has nonzero subject_id.");
            if (subjectKind != 0 && subjectId == 0) throw new InvalidDataException("Non-scene diagnostic has zero subject_id.");
            if ((flags & 1u) == 0 && (detailsOff != 0 || detailsLen != 0))
                throw new InvalidDataException("DIAG clear HAS_DETAILS requires zero offset/length.");
            var msg = ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[20..]));
            var src = ResolveOptionalStr(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[16..]));
            diagnostics.Add(new DiagnosticRecord(severity, subjectKind, code, flags, subjectId, src, msg, detailsOff, detailsLen, reserved));
            diagOff += 40;
        }
        var diagBlob = new byte[dBlobLen];
        diagData.Slice(diagOff, dBlobLen).CopyTo(diagBlob);
        foreach (var d in diagnostics.Where(d => (d.Flags & 1u) != 0))
            ValidateOpaqueEnvelope(diagBlob, d.DetailsOffset, d.DetailsLength, "DIAG");

        // BNDX
        var bndxChunk = chunkDict["BNDX"];
        var bndxData = data.Slice(checked((int) bndxChunk.Offset), checked((int) bndxChunk.Length));
        if (bndxData.Length < 4) throw new InvalidDataException("BNDX too small.");
        var entryCount = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(bndxData));
        if ((uint) entryCount != bndxChunk.RecordCount)
            throw new InvalidDataException("BNDX directory record_count mismatch.");
        if (bndxData.Length != checked(4 + entryCount * 72))
            throw new InvalidDataException("BNDX payload size mismatch.");
        var bundleEntries = new List<BundleEntry>(entryCount);
        var bndxOff = 4;
        for (var i = 0; i < entryCount; i++)
        {
            var r = bndxData[bndxOff..];
            var resKind = r[0];
            var locB = r[1];
            var availB = r[2];
            if (r[3] != 0) throw new InvalidDataException("BNDX reserved nonzero.");
            if (resKind is < 1 or > 3) throw new InvalidDataException("Unknown BNDX resource_kind.");
            if (locB is < 1 or > 3) throw new InvalidDataException("Unknown BNDX location.");
            if (locB == (byte) LocationMode.Embedded) throw new InvalidDataException("Embedded BNDX location rejected in V1.");
            if (availB is < 1 or > 4) throw new InvalidDataException("Unknown BNDX availability.");
            var sha = RequireShaCopy(r[20..52]);
            bundleEntries.Add(new BundleEntry(
                resKind, (LocationMode) locB, (Availability) availB,
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[4..])),
                BinaryPrimitives.ReadUInt64LittleEndian(r[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[16..]),
                sha,
                BinaryPrimitives.ReadUInt64LittleEndian(r[52..]),
                ResolveStrRequired(strsStrings, BinaryPrimitives.ReadUInt32LittleEndian(r[60..])),
                BinaryPrimitives.ReadUInt32LittleEndian(r[64..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[68..])));
            bndxOff += 72;
        }

        // Post-read structural validation (graph / ranges / BNDX / instance rules)
        ValidateGraph(assets, lods, actors, components, transforms, instanceSets, instanceTransforms, materialOverrides, diagnostics, bundleEntries, hasInstanceBit);
        ValidateUniqueNonzero(lights.Select(light => light.LightId), "Light");
        UESceneWriter.ValidateLights(lights, actors, components, transforms);
        ValidateCanonicalLightOrder(lights, actors, components, transforms);

        return new UESceneDocument(
            metadata,
            StringTable: strsStrings.ToList(),
            Assets: assets,
            Lods: lods,
            Actors: actors,
            Components: components,
            Transforms: transforms,
            Lights: lights,
            InstanceSets: instanceSets,
            InstanceTransforms: instanceTransforms,
            MaterialOverrides: materialOverrides,
            MatoBlob: matoBlob,
            Diagnostics: diagnostics,
            DiagBlob: diagBlob,
            BundleEntries: bundleEntries);
    }

    private static void ValidateCanonicalLightOrder(
        List<LightRecord> lights, List<ActorRecord> actors, List<ComponentRecord> components, List<TransformRecord> transforms)
    {
        var actorOrder = actors.Select((actor, index) => (actor.ActorId, index)).ToDictionary(value => value.ActorId, value => value.index);
        var componentOrder = components.Select((component, index) => (component.ComponentId, index)).ToDictionary(value => value.ComponentId, value => value.index);
        var transformOrder = transforms.Select((transform, index) => (transform.TransformId, index)).ToDictionary(value => value.TransformId, value => value.index);
        var previous = (-1, -1, -1, 0UL);
        foreach (var light in lights)
        {
            var current = (actorOrder[light.ActorId], light.ParentComponentId == 0 ? -1 : componentOrder[light.ParentComponentId], transformOrder[light.TransformId], light.LightId);
            if (Comparer<(int actor, int component, int transform, ulong id)>.Default.Compare(previous, current) > 0)
                throw new InvalidDataException("LITE records are not in canonical order.");
            previous = current;
        }
    }

    private static void ValidateGraph(
        List<AssetRecord> assets, List<MeshLodRecord> lods,
        List<ActorRecord> actors, List<ComponentRecord> components,
        List<TransformRecord> transforms,
        List<InstanceSetRecord> instanceSets, List<InstanceTransform> instanceTransforms,
        List<MaterialOverrideRecord> materialOverrides,
        List<DiagnosticRecord> diagnostics, List<BundleEntry> bundleEntries,
        bool hasInstanceBit)
    {
        ValidateUniqueNonzero(assets.Select(a => a.AssetId), "Asset");
        ValidateUniqueNonzero(actors.Select(a => a.ActorId), "Actor");
        ValidateUniqueNonzero(components.Select(c => c.ComponentId), "Component");
        ValidateUniqueNonzero(transforms.Select(t => t.TransformId), "Transform");
        ValidateUniqueNonzero(instanceSets.Select(s => s.InstanceSetId), "InstanceSet");

        foreach (var asset in assets)
        {
            if (asset.Kind == AssetKind.StaticMesh)
            {
                if ((ulong) asset.LodStart + asset.LodCount > (ulong) lods.Count)
                    throw new InvalidDataException("Asset LOD table range out of bounds.");
                var slice = lods.Skip(checked((int) asset.LodStart)).Take(checked((int) asset.LodCount)).ToList();
                if (slice.Select(lod => lod.LodIndex).Distinct().Count() != slice.Count)
                    throw new InvalidDataException("LOD source indices must be unique within an asset range.");
            }
            else if (asset.LodCount != 0 || asset.LodStart != 0)
            {
                throw new InvalidDataException("Non-static-mesh asset has non-zero LOD range.");
            }
        }
        var ownedLods = new bool[lods.Count];
        foreach (var asset in assets.Where(a => a.Kind == AssetKind.StaticMesh))
        {
            for (var i = 0; i < asset.LodCount; i++)
            {
                var index = checked((int) asset.LodStart + i);
                if (ownedLods[index])
                    throw new InvalidDataException("Asset LOD table ranges overlap.");
                ownedLods[index] = true;
            }
        }
        if (ownedLods.Any(owned => !owned))
            throw new InvalidDataException("LOD table contains unowned records.");

        var transformIds = transforms.Select(t => t.TransformId).ToHashSet();
        foreach (var actor in actors)
        {
            if (actor.LocalTransformId == 0 || !transformIds.Contains(actor.LocalTransformId))
                throw new InvalidDataException("Actor local_transform_id invalid.");
            if ((ulong) actor.ComponentStart + actor.ComponentCount > (ulong) components.Count)
                throw new InvalidDataException("Actor component range out of bounds.");
            if ((ulong) actor.DiagnosticStart + actor.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException("Actor diagnostic range out of bounds.");
            ValidateSourceObjectPathField(actor.SourceObjectPath, "Actor");
        }
        var componentsById = components.ToDictionary(component => component.ComponentId);
        foreach (var actor in actors)
        {
            if (actor.ComponentCount == 0)
            {
                if (actor.RootComponentId != 0)
                    throw new InvalidDataException("Actor without components has a root component.");
            }
            else if (actor.RootComponentId == 0
                     || !componentsById.TryGetValue(actor.RootComponentId, out var root)
                     || root.ActorId != actor.ActorId
                     || root.ParentComponentId != 0)
            {
                throw new InvalidDataException("Actor root component invalid.");
            }
        }
        ValidateNonOverlappingRanges(actors.Select(a => (a.ComponentStart, a.ComponentCount)), "Actor component");
        ValidateExactOwnership(
            actors,
            components,
            actor => actor.ActorId,
            component => component.ActorId,
            actor => (actor.ComponentStart, actor.ComponentCount),
            "Actor component");
        // Actor parent nonzero must resolve; ValidateAcyclic rejects dangling parents.
        ValidateAcyclic(actors.Select(a => (a.ActorId, a.ParentActorId)), "Actor");

        foreach (var comp in components)
        {
            if (comp.LocalTransformId == 0 || !transformIds.Contains(comp.LocalTransformId))
                throw new InvalidDataException("Component local_transform_id invalid.");
            if (actors.All(a => a.ActorId != comp.ActorId))
                throw new InvalidDataException("Component references missing actor.");
            if (!assets.Any(a => a.AssetId == comp.MeshAssetId
                    && a.Kind == AssetKind.StaticMesh
                    && a.Availability == Availability.Available))
                throw new InvalidDataException("Component MeshAssetId must be an available static mesh.");
            if ((ulong) comp.MaterialOverrideStart + comp.MaterialOverrideCount > (ulong) materialOverrides.Count)
                throw new InvalidDataException("Component MATO range out of bounds.");
            if ((ulong) comp.DiagnosticStart + comp.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException("Component diagnostic range out of bounds.");
            ValidateSourceObjectPathField(comp.SourceObjectPath, "Component");
            if (comp.ParentComponentId != 0)
            {
                var parent = components.FirstOrDefault(c => c.ComponentId == comp.ParentComponentId);
                if (parent is null || parent.ActorId != comp.ActorId)
                    throw new InvalidDataException("Component parent invalid or cross-actor.");
            }
        }
        ValidateNonOverlappingRanges(components.Select(c => (c.MaterialOverrideStart, c.MaterialOverrideCount)), "Component MATO");
        ValidateExactOwnership(
            components,
            materialOverrides,
            component => component.ComponentId,
            mato => mato.ComponentId,
            component => (component.MaterialOverrideStart, component.MaterialOverrideCount),
            "Component MATO");
        ValidateAcyclic(components.Select(c => (c.ComponentId, c.ParentComponentId)), "Component");

        var hasIsmHism = components.Any(c => c.ComponentKind is ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh);
        if (hasIsmHism != hasInstanceBit)
            throw new InvalidDataException("INSTANCE_SETS bit must match ISM/HISM presence.");
        if (hasIsmHism && instanceSets.Count == 0)
            throw new InvalidDataException("ISM/HISM without instance sets.");
        if (!hasIsmHism && instanceSets.Count > 0)
            throw new InvalidDataException("Instance sets without ISM/HISM.");

        var setsById = instanceSets.ToDictionary(s => s.InstanceSetId);
        var usedSets = new HashSet<ulong>();
        foreach (var comp in components)
        {
            if (comp.ComponentKind is ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh)
            {
                if (comp.InstanceSetId == 0 || !setsById.TryGetValue(comp.InstanceSetId, out var set))
                    throw new InvalidDataException("ISM/HISM missing unique instance set.");
                if (!usedSets.Add(comp.InstanceSetId))
                    throw new InvalidDataException("Instance set shared by multiple components.");
                if (set.ComponentId != comp.ComponentId)
                    throw new InvalidDataException("Instance set owner mismatch.");
            }
            else if (comp.InstanceSetId != 0)
            {
                throw new InvalidDataException("Static component has instance set.");
            }
        }
        foreach (var set in instanceSets)
        {
            var owner = components.FirstOrDefault(c => c.ComponentId == set.ComponentId);
            if (owner is null || owner.ComponentKind is not (ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh)
                || owner.InstanceSetId != set.InstanceSetId)
                throw new InvalidDataException("Instance set owner mismatch.");
            if ((ulong) set.TransformStart + set.TransformCount > (ulong) instanceTransforms.Count)
                throw new InvalidDataException("Instance transform range out of bounds.");
        }
        ValidateNonOverlappingRanges(instanceSets.Select(s => (s.TransformStart, s.TransformCount)), "Instance transform");
        ValidateCompleteRangeCoverage(instanceSets.Select(s => (s.TransformStart, s.TransformCount)), instanceTransforms.Count, "Instance transform");

        var matoKeys = new HashSet<(ulong componentId, uint slotOrdinal)>();
        foreach (var mato in materialOverrides)
        {
            if (!matoKeys.Add((mato.ComponentId, mato.SlotOrdinal)))
                throw new InvalidDataException("Duplicate MATO component/slot key.");
            if (components.All(c => c.ComponentId != mato.ComponentId))
                throw new InvalidDataException("MATO component missing.");
            if (mato.BaseMaterialAssetId != 0 && assets.All(a => a.AssetId != mato.BaseMaterialAssetId || a.Kind != AssetKind.Material))
                throw new InvalidDataException("MATO base material invalid.");
            if (mato.OverrideMaterialAssetId != 0 && assets.All(a => a.AssetId != mato.OverrideMaterialAssetId || a.Kind != AssetKind.Material))
                throw new InvalidDataException("MATO override material invalid.");
            // Same as writer: zero override material requires parameter payload (flags bit0).
            if (mato.OverrideMaterialAssetId == 0 && (mato.Flags & 1u) == 0)
                throw new InvalidDataException("Zero override material requires parameter payload.");
            if ((ulong) mato.DiagnosticStart + mato.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException("MATO diagnostic range out of bounds.");
        }

        // BNDX mapping
        var keys = new HashSet<(byte, string, ulong, uint)>();
        foreach (var e in bundleEntries)
        {
            if (!keys.Add((e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex)))
                throw new InvalidDataException("Duplicate BNDX key.");
            if (assets.All(a => a.AssetId != e.OwnerAssetId))
                throw new InvalidDataException("BNDX owner missing.");
            if (e.LodIndex != uint.MaxValue)
                throw new InvalidDataException("Per-LOD BNDX entries are invalid in V1.");
            ValidateUriField(e.LogicalUri, "BNDX");
            if ((ulong) e.DiagnosticStart + e.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException("BNDX diagnostic range out of bounds.");
        }
        foreach (var asset in assets)
        {
            if (asset.Availability != Availability.Available) continue;
            var ownedEntries = bundleEntries.Where(e => e.OwnerAssetId == asset.AssetId).ToList();
            if (asset.Kind == AssetKind.StaticMesh)
            {
                if (ownedEntries.Count != 1 || ownedEntries[0].ResourceKind != (byte) BundleResourceKind.Model
                    || ownedEntries[0].LodIndex != uint.MaxValue)
                    throw new InvalidDataException("Available static mesh requires exactly one primary Model BNDX.");
                MatchBndx(ownedEntries[0], asset.LogicalUri, asset.Sha256, asset.ByteLength, asset.Mime, asset.Location, asset.Availability);
            }
            else if (asset.Kind == AssetKind.Material)
            {
                if (ownedEntries.Any(e => e.ResourceKind != (byte) BundleResourceKind.MaterialJson || e.LodIndex != uint.MaxValue))
                    throw new InvalidDataException("Material has invalid BNDX record.");
                if (ownedEntries.Count > 1) throw new InvalidDataException("Material duplicate BNDX primary.");
                if (ownedEntries.Count == 1)
                    MatchBndx(ownedEntries[0], asset.LogicalUri, asset.Sha256, asset.ByteLength, asset.Mime, asset.Location, asset.Availability);
            }
            else
            {
                var kind = asset.Kind == AssetKind.Texture ? (byte) BundleResourceKind.Texture : (byte) BundleResourceKind.Model;
                if (ownedEntries.Count != 1 || ownedEntries[0].ResourceKind != kind || ownedEntries[0].LodIndex != uint.MaxValue)
                    throw new InvalidDataException("Available non-SM ASST missing BNDX primary.");
                MatchBndx(ownedEntries[0], asset.LogicalUri, asset.Sha256, asset.ByteLength, asset.Mime, asset.Location, asset.Availability);
            }
        }
        foreach (var asset in assets.Where(a => a.Availability != Availability.Available))
        {
            if (bundleEntries.Any(e => e.OwnerAssetId == asset.AssetId))
                throw new InvalidDataException("Unavailable asset has BNDX.");
        }

        // Type-specific string byte limits on resolved semantic fields.
        foreach (var asset in assets)
        {
            ValidateUriField(asset.LogicalUri, "Asset");
            if ((ulong) asset.DiagnosticStart + asset.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException("Asset diagnostic range out of bounds.");
        }
        foreach (var lod in lods)
            ValidateUriField(lod.LogicalUri, "LOD");
        foreach (var d in diagnostics)
        {
            if (!string.IsNullOrEmpty(d.SourceObjectPath))
                ValidateSourceObjectPathField(d.SourceObjectPath, "DIAG");
        }

        ValidateDiagnosticOwnership(assets, actors, components, instanceSets, materialOverrides, bundleEntries, diagnostics);
    }

    private static void ValidateDiagnosticOwnership(
        List<AssetRecord> assets,
        List<ActorRecord> actors,
        List<ComponentRecord> components,
        List<InstanceSetRecord> instanceSets,
        List<MaterialOverrideRecord> materialOverrides,
        List<BundleEntry> bundleEntries,
        List<DiagnosticRecord> diagnostics)
    {
        ValidateNonOverlappingRanges(assets.Select(a => (a.DiagnosticStart, a.DiagnosticCount)), "Asset diagnostic");
        ValidateNonOverlappingRanges(actors.Select(a => (a.DiagnosticStart, a.DiagnosticCount)), "Actor diagnostic");
        ValidateNonOverlappingRanges(components.Select(c => (c.DiagnosticStart, c.DiagnosticCount)), "Component diagnostic");
        ValidateNonOverlappingRanges(materialOverrides.Select(o => (o.DiagnosticStart, o.DiagnosticCount)), "MATO diagnostic");
        ValidateNonOverlappingRanges(bundleEntries.Select(e => (e.DiagnosticStart, e.DiagnosticCount)), "BNDX diagnostic");

        foreach (var asset in assets)
        {
            var slice = diagnostics.Skip(checked((int) asset.DiagnosticStart)).Take(checked((int) asset.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 1 || d.SubjectId != asset.AssetId)
                    throw new InvalidDataException("Asset diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 1 && d.SubjectId == asset.AssetId);
            if (owned != asset.DiagnosticCount)
                throw new InvalidDataException("Asset diagnostic count mismatch.");
            if (asset.Availability != Availability.Available && owned == 0)
                throw new InvalidDataException("Asset availability requires an exact asset diagnostic.");
        }

        foreach (var actor in actors)
        {
            var slice = diagnostics.Skip(checked((int) actor.DiagnosticStart)).Take(checked((int) actor.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 2 || d.SubjectId != actor.ActorId)
                    throw new InvalidDataException("Actor diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 2 && d.SubjectId == actor.ActorId);
            if (owned != actor.DiagnosticCount)
                throw new InvalidDataException("Actor diagnostic count mismatch.");
        }

        foreach (var comp in components)
        {
            var slice = diagnostics.Skip(checked((int) comp.DiagnosticStart)).Take(checked((int) comp.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 3 || d.SubjectId != comp.ComponentId)
                    throw new InvalidDataException("Component diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 3 && d.SubjectId == comp.ComponentId);
            if (owned != comp.DiagnosticCount)
                throw new InvalidDataException("Component diagnostic count mismatch.");
        }

        foreach (var d in diagnostics.Where(d => d.SubjectKind == 4))
        {
            if (instanceSets.All(s => s.InstanceSetId != d.SubjectId))
                throw new InvalidDataException("InstanceSet diagnostic subject missing.");
        }

        foreach (var mato in materialOverrides)
        {
            var expectedId = UESceneWriter.MaterialOverrideSubjectId(mato.ComponentId, mato.SlotOrdinal);
            var slice = diagnostics.Skip(checked((int) mato.DiagnosticStart)).Take(checked((int) mato.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 5 || d.SubjectId != expectedId)
                    throw new InvalidDataException("MATO diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 5 && d.SubjectId == expectedId);
            if (owned != mato.DiagnosticCount)
                throw new InvalidDataException("MATO diagnostic count mismatch.");
        }

        foreach (var entry in bundleEntries)
        {
            var expectedId = UESceneWriter.BundleEntrySubjectId(entry.ResourceKind, entry.LogicalUri, entry.OwnerAssetId, entry.LodIndex);
            var slice = diagnostics.Skip(checked((int) entry.DiagnosticStart)).Take(checked((int) entry.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 6 || d.SubjectId != expectedId)
                    throw new InvalidDataException("BNDX diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 6 && d.SubjectId == expectedId);
            if (owned != entry.DiagnosticCount)
                throw new InvalidDataException("BNDX diagnostic count mismatch.");
        }

        foreach (var d in diagnostics)
        {
            switch (d.SubjectKind)
            {
                case 0:
                    break;
                case 1:
                    if (assets.All(a => a.AssetId != d.SubjectId))
                        throw new InvalidDataException("Asset diagnostic subject missing.");
                    break;
                case 2:
                    if (actors.All(a => a.ActorId != d.SubjectId))
                        throw new InvalidDataException("Actor diagnostic subject missing.");
                    break;
                case 3:
                    if (components.All(c => c.ComponentId != d.SubjectId))
                        throw new InvalidDataException("Component diagnostic subject missing.");
                    break;
                case 4:
                    break;
                case 5:
                    if (!materialOverrides.Any(o => UESceneWriter.MaterialOverrideSubjectId(o.ComponentId, o.SlotOrdinal) == d.SubjectId))
                        throw new InvalidDataException("MATO diagnostic subject missing.");
                    break;
                case 6:
                    if (!bundleEntries.Any(e => UESceneWriter.BundleEntrySubjectId(e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex) == d.SubjectId))
                        throw new InvalidDataException("BNDX diagnostic subject missing.");
                    break;
            }
        }
    }

    private static void ValidateUriField(string value, string context)
    {
        if (value is null) throw new InvalidDataException($"{context} URI cannot be null.");
        byte[] utf8;
        try { utf8 = StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException($"{context} URI must be valid UTF-8.", ex); }
        if (utf8.Length > MaxUriUtf8Bytes)
            throw new InvalidDataException($"{context} URI exceeds {MaxUriUtf8Bytes} UTF-8 bytes.");
        if (value.Length == 0 || value.Contains('\\') || value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || value.Contains(':')
            || (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '/' || value[2] == '\\')))
            throw new InvalidDataException($"{context} URI has invalid syntax.");
        foreach (var segment in value.Split('/'))
            if (segment.Length == 0 || segment is "." or "..")
                throw new InvalidDataException($"{context} URI has invalid segment.");
    }

    private static void ValidateSourceObjectPathField(string value, string context)
    {
        if (value is null) throw new InvalidDataException($"{context} source object path cannot be null.");
        byte[] utf8;
        try { utf8 = StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException($"{context} source object path must be valid UTF-8.", ex); }
        if (utf8.Length > MaxSourceObjectPathUtf8Bytes)
            throw new InvalidDataException($"{context} source object path exceeds {MaxSourceObjectPathUtf8Bytes} UTF-8 bytes.");
    }

    private static void MatchBndx(BundleEntry entry, string uri, byte[] sha, ulong length, string mime, LocationMode location, Availability availability)
    {
        if (!StringComparer.Ordinal.Equals(entry.LogicalUri, uri)
            || !entry.Sha256.AsSpan().SequenceEqual(sha)
            || entry.ByteLength != length
            || !StringComparer.Ordinal.Equals(entry.Mime, mime)
            || entry.Location != location
            || entry.Availability != availability)
            throw new InvalidDataException("BNDX URI/hash/length/MIME/mode/availability mismatch.");
    }

    private static void ValidateOpaqueEnvelope(byte[] blob, ulong offset, uint length, string name)
    {
        if (length < 8) throw new InvalidDataException($"{name} HAS payload but length < 8.");
        if (offset + length > (ulong) blob.Length) throw new InvalidDataException($"{name} envelope out of blob bounds.");
        var off = checked((int) offset);
        var envVer = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off));
        var envKind = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 2));
        var envDataLen = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 4));
        if (envVer != 1) throw new InvalidDataException($"{name} opaque envelope version != 1.");
        if (envKind != 0) throw new InvalidDataException($"{name} opaque envelope kind != 0.");
        if (envDataLen != length - 8) throw new InvalidDataException($"{name} opaque envelope data_length mismatch.");
        if (envDataLen > 1u * 1024 * 1024) throw new InvalidDataException($"{name} opaque envelope exceeds 1 MiB.");
    }

    private static void ValidateScreenSize(float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        if (bits == CanonicalQuietNanBits) return;
        if (!float.IsFinite(value))
            throw new InvalidDataException("source_screen_size must be finite or exact canonical NaN 0x7FC00000.");
    }

    private static void RejectNegativeZero64(ReadOnlySpan<byte> value, string field)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(value) == 0x8000_0000_0000_0000)
            throw new InvalidDataException($"{field} negative zero is noncanonical.");
    }

    private static void RejectNegativeZero32(ReadOnlySpan<byte> value, string field)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(value) == 0x8000_0000)
            throw new InvalidDataException($"{field} negative zero is noncanonical.");
    }

    private static void ValidateFiniteTrs(
        double tx, double ty, double tz,
        float rx, float ry, float rz, float rw,
        float sx, float sy, float sz,
        string id)
    {
        if (!double.IsFinite(tx) || !double.IsFinite(ty) || !double.IsFinite(tz))
            throw new InvalidDataException($"Transform {id} non-finite translation.");
        if (!float.IsFinite(rx) || !float.IsFinite(ry) || !float.IsFinite(rz) || !float.IsFinite(rw))
            throw new InvalidDataException($"Transform {id} non-finite rotation.");
        var qLen = MathF.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
        if (MathF.Abs(qLen - 1.0f) > 1e-4f)
            throw new InvalidDataException($"Transform {id} quaternion not normalized.");
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(sz))
            throw new InvalidDataException($"Transform {id} non-finite scale.");
    }

    private static byte[] RequireShaCopy(ReadOnlySpan<byte> src)
    {
        if (src.Length != 32) throw new InvalidDataException("SHA-256 must be exactly 32 bytes.");
        var sha = new byte[32];
        src.CopyTo(sha);
        return sha;
    }

    private static void CheckCount(int count, int max, string name)
    {
        if (count < 0 || count > max) throw new InvalidDataException($"{name} count exceeds limit.");
    }

    private static bool IsPrintableFourCc(uint fourCc)
    {
        for (var i = 0; i < 4; i++)
        {
            var b = (byte) ((fourCc >> (8 * i)) & 0xFF);
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    private static void ValidateUniqueNonzero(IEnumerable<ulong> ids, string name)
    {
        var seen = new HashSet<ulong>();
        foreach (var id in ids)
        {
            if (id == 0) throw new InvalidDataException($"{name} zero ID.");
            if (!seen.Add(id)) throw new InvalidDataException($"{name} duplicate ID.");
        }
    }

    private static void ValidateNonOverlappingRanges(IEnumerable<(uint start, uint count)> ranges, string name)
    {
        var occupied = new HashSet<uint>();
        foreach (var (start, count) in ranges)
        {
            for (uint i = 0; i < count; i++)
            {
                var idx = checked(start + i);
                if (!occupied.Add(idx))
                    throw new InvalidDataException($"{name} ranges overlap.");
            }
        }
    }

    private static void ValidateExactOwnership<TOwner, TItem>(
        IEnumerable<TOwner> owners,
        IReadOnlyList<TItem> items,
        Func<TOwner, ulong> ownerId,
        Func<TItem, ulong> itemOwnerId,
        Func<TOwner, (uint start, uint count)> range,
        string name)
    {
        foreach (var owner in owners)
        {
            var id = ownerId(owner);
            var (start, count) = range(owner);
            for (uint i = 0; i < count; i++)
            {
                if (itemOwnerId(items[checked((int) (start + i))]) != id)
                    throw new InvalidDataException($"{name} range has foreign item.");
            }
            if (items.Count(item => itemOwnerId(item) == id) != count)
                throw new InvalidDataException($"{name} ownership count mismatch.");
        }
    }

    private static void ValidateCompleteRangeCoverage(IEnumerable<(uint start, uint count)> ranges, int itemCount, string name)
    {
        var covered = new bool[itemCount];
        foreach (var (start, count) in ranges)
        {
            for (uint i = 0; i < count; i++)
                covered[checked((int) (start + i))] = true;
        }
        if (covered.Any(isCovered => !isCovered))
            throw new InvalidDataException($"{name} table contains unowned records.");
    }

    private static void ValidateAcyclic(IEnumerable<(ulong id, ulong parentId)> nodes, string tableName)
    {
        var parentMap = nodes.ToDictionary(n => n.id, n => n.parentId);
        foreach (var start in parentMap.Keys)
        {
            var visited = new HashSet<ulong>();
            var current = start;
            while (true)
            {
                if (!visited.Add(current))
                    throw new InvalidDataException($"{tableName} cycle detected involving ID {current}.");
                if (!parentMap.TryGetValue(current, out var parent) || parent == 0) break;
                if (!parentMap.ContainsKey(parent) && parent != 0)
                    throw new InvalidDataException($"{tableName} parent {parent} missing.");
                current = parent;
            }
        }
    }

    private static string ResolveStr(string[] strings, uint id)
    {
        if (id == 0) return string.Empty;
        if (id > strings.Length) throw new InvalidDataException($"String ID {id} out of range.");
        return strings[id - 1];
    }

    private static string ResolveStrRequired(string[] strings, uint id)
    {
        if (id == 0) throw new InvalidDataException("Required string ID is zero.");
        return ResolveStr(strings, id);
    }

    private static string ResolveOptionalStr(string[] strings, uint id) => id == 0 ? string.Empty : ResolveStr(strings, id);

    private sealed record ChunkInfo(uint FourCc, string? Name, ulong Offset, ulong Length, uint RecordCount, bool IsKnown);
}
