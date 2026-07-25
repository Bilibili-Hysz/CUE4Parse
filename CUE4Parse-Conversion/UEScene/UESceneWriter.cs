using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CUE4Parse_Conversion.UEScene;

public static class UESceneWriter
{
    private const int HeaderSize = 44;
    private const int DirectoryEntrySize = 40;
    private const long MaxFileSize = 2L * 1024 * 1024 * 1024;
    private const long MaxChunkSize = 1L * 1024 * 1024 * 1024;
    private const long MaxTotalFixedRecords = 10_000_000;
    private const int MaxUriUtf8Bytes = 4096;
    private const int MaxSourceObjectPathUtf8Bytes = 16384;
    private const uint MetaFourCc = 0x4154454D;
    private const uint StringsFourCc = 0x53525453;
    private const uint AsstFourCc = 0x54535341;
    private const uint ActrFourCc = 0x52544341;
    private const uint CompFourCc = 0x504D4F43;
    private const uint XfrmFourCc = 0x4D524658;
    private const uint MatoFourCc = 0x4F54414D;
    private const uint DiagFourCc = 0x47414944;
    private const uint BndxFourCc = 0x58444E42;
    private const uint InstFourCc = 0x54534E49;
    private const uint LiteFourCc = 0x4554494C;
    private const uint CanonicalQuietNanBits = 0x7FC00000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Deterministic DIAG subject_id for a MATO record (SubjectKind=5).</summary>
    public static ulong MaterialOverrideSubjectId(ulong componentId, uint slotOrdinal)
    {
        Span<byte> identity = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(identity, componentId);
        BinaryPrimitives.WriteUInt32LittleEndian(identity[8..], slotOrdinal);
        return DeriveSubjectId("mato", identity);
    }

    /// <summary>Deterministic DIAG subject_id for a BNDX record (SubjectKind=6).</summary>
    public static ulong BundleEntrySubjectId(byte resourceKind, string logicalUri, ulong ownerAssetId, uint lodIndex)
    {
        var uriUtf8 = StrictUtf8.GetBytes(logicalUri ?? string.Empty);
        Span<byte> identity = stackalloc byte[1 + 8 + 4 + uriUtf8.Length];
        identity[0] = resourceKind;
        BinaryPrimitives.WriteUInt64LittleEndian(identity[1..], ownerAssetId);
        BinaryPrimitives.WriteUInt32LittleEndian(identity[9..], lodIndex);
        uriUtf8.CopyTo(identity[13..]);
        return DeriveSubjectId("bndx", identity);
    }

    private static UESceneDocument DeepClone(UESceneDocument doc) => doc with
    {
        StringTable = doc.StringTable?.ToList(),
        Assets = doc.Assets?.Select(a => a with { Sha256 = a.Sha256?.ToArray()! }).ToList(),
        Lods = doc.Lods?.Select(l => l with { Sha256 = l.Sha256?.ToArray()! }).ToList(),
        Actors = doc.Actors?.ToList(),
        Components = doc.Components?.ToList(),
        Transforms = doc.Transforms?.ToList(),
        Lights = doc.Lights?.ToList(),
        InstanceSets = doc.InstanceSets?.ToList(),
        InstanceTransforms = doc.InstanceTransforms?.ToList(),
        MaterialOverrides = doc.MaterialOverrides?.ToList(),
        MatoBlob = doc.MatoBlob?.ToArray(),
        Diagnostics = doc.Diagnostics?.ToList(),
        DiagBlob = doc.DiagBlob?.ToArray(),
        BundleEntries = doc.BundleEntries?.Select(e => e with { Sha256 = e.Sha256?.ToArray()! }).ToList(),
    };

    private static ulong DeriveEntityId(string domain, ReadOnlySpan<byte> identityBytes, HashSet<ulong> usedIds)
    {
        var domainUtf8 = Encoding.ASCII.GetBytes(domain);
        Span<byte> prefix = stackalloc byte[10]; // "UESceneV1\0"
        "UESceneV1\0"u8.CopyTo(prefix);
        var total = prefix.Length + domainUtf8.Length + 1 + identityBytes.Length;
        var buf = total <= 256 ? stackalloc byte[total] : new byte[total];
        prefix.CopyTo(buf);
        var o = prefix.Length;
        domainUtf8.CopyTo(buf[o..]);
        o += domainUtf8.Length;
        buf[o++] = 0;
        identityBytes.CopyTo(buf[o..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buf[..total], hash);
        var id = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        if (id != 0 && usedIds.Add(id)) return id;
        for (var ordinal = 1; ordinal < 1024; ordinal++)
        {
            var suffix = Encoding.ASCII.GetBytes(ordinal.ToString());
            var retryTotal = total + 1 + suffix.Length;
            var retry = new byte[retryTotal];
            buf[..total].CopyTo(retry);
            retry[total] = 0;
            suffix.CopyTo(retry.AsSpan(total + 1));
            SHA256.HashData(retry, hash);
            id = BinaryPrimitives.ReadUInt64LittleEndian(hash);
            if (id != 0 && usedIds.Add(id)) return id;
        }
        throw new InvalidDataException($"Unable to derive nonzero unique entity id for domain {domain}.");
    }

    private static void ValidateSourceUniqueness(string domain, IEnumerable<string> identities)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            if (!seen.Add(identity))
                throw new InvalidDataException($"Duplicate {domain} source identity: {identity}");
        }
    }

    private static void ValidateUniqueIds(string domain, IEnumerable<ulong> ids)
    {
        var seen = new HashSet<ulong>();
        foreach (var id in ids)
        {
            if (id == 0 || !seen.Add(id))
                throw new InvalidDataException($"{domain} IDs must be nonzero and unique.");
        }
    }

    private static ulong DeriveSubjectId(string domain, ReadOnlySpan<byte> identityBytes)
    {
        var domainUtf8 = Encoding.ASCII.GetBytes(domain);
        Span<byte> prefix = stackalloc byte[10]; // "UESceneV1\0"
        "UESceneV1\0"u8.CopyTo(prefix);
        var total = prefix.Length + domainUtf8.Length + 1 + identityBytes.Length;
        var buf = total <= 256 ? stackalloc byte[total] : new byte[total];
        prefix.CopyTo(buf);
        var o = prefix.Length;
        domainUtf8.CopyTo(buf[o..]);
        o += domainUtf8.Length;
        buf[o++] = 0;
        identityBytes.CopyTo(buf[o..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buf[..total], hash);
        var id = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        if (id != 0) return id;
        // Collision-with-zero: append ordinal retry starting at 1.
        for (var ordinal = 1; ordinal < 1024; ordinal++)
        {
            var suffix = Encoding.ASCII.GetBytes(ordinal.ToString());
            var retryTotal = total + 1 + suffix.Length;
            var retry = new byte[retryTotal];
            buf[..total].CopyTo(retry);
            retry[total] = 0;
            suffix.CopyTo(retry.AsSpan(total + 1));
            SHA256.HashData(retry, hash);
            id = BinaryPrimitives.ReadUInt64LittleEndian(hash);
            if (id != 0) return id;
        }
        throw new InvalidDataException($"Unable to derive nonzero subject id for domain {domain}.");
    }

    /// <summary>
    /// Writes only the phase-one META and STRS container chunks. This is not a complete UEScene V1 file.
    /// </summary>
    public static byte[] WritePhaseOne(UESceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Metadata);
        ValidateMetadataFeatures(document.Metadata);

        var strings = BuildStringTable(document.Metadata);
        var meta = BuildMetaPayload(document.Metadata, strings);
        var strs = BuildStringsPayload(strings);
        var directoryOffset = HeaderSize;
        var chunksOffset = Align8(directoryOffset + 2 * DirectoryEntrySize);
        var metaOffset = chunksOffset;
        var strsOffset = Align8(metaOffset + meta.Length);
        var fileSize = checked(strsOffset + strs.Length);
        var bytes = new byte[fileSize];

        WriteHeader(bytes, 2, checked((ulong) fileSize));
        WriteDirectoryEntry(bytes.AsSpan(directoryOffset, DirectoryEntrySize), MetaFourCc, metaOffset, meta, 1);
        WriteDirectoryEntry(bytes.AsSpan(directoryOffset + DirectoryEntrySize, DirectoryEntrySize), StringsFourCc, strsOffset, strs, checked((uint) strings.Strings.Count));
        meta.CopyTo(bytes.AsSpan(metaOffset));
        strs.CopyTo(bytes.AsSpan(strsOffset));
        return bytes;
    }

    /// <summary>
    /// Writes the complete V1 semantic document with all chunks.
    /// Derives entity IDs from source identities per §10; never serializes caller-supplied IDs.
    /// </summary>
    public static byte[] Write(UESceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Metadata);

        // §10: Deep clone to protect caller data
        var doc = DeepClone(document);

        var matoBlob = doc.MatoBlob ?? Array.Empty<byte>();
        var diagBlob = doc.DiagBlob ?? Array.Empty<byte>();
        ValidateDocument(doc, matoBlob, diagBlob);

        var assets = doc.Assets ?? new();
        var actors = doc.Actors ?? new();
        var components = doc.Components ?? new();
        var transforms = doc.Transforms ?? new();
        var lights = doc.Lights ?? new();
        var instanceSets = doc.InstanceSets ?? new();
        var materialOverrides = doc.MaterialOverrides ?? new();
        var diagnostics = doc.Diagnostics ?? new();
        var bundleEntries = doc.BundleEntries ?? new();
        var lods = doc.Lods ?? new();

        ValidateUniqueIds("Asset", assets.Select(a => a.AssetId));
        ValidateUniqueIds("Actor", actors.Select(a => a.ActorId));
        ValidateUniqueIds("Component", components.Select(c => c.ComponentId));
        ValidateUniqueIds("Transform", transforms.Select(t => t.TransformId));
        ValidateUniqueIds("Light", lights.Select(l => l.LightId));
        ValidateUniqueIds("Instance set", instanceSets.Select(s => s.InstanceSetId));

        // Capture the caller's complete LOD ownership before IDs or owners can move.
        var lodCaptures = CaptureLodOwnership(assets, lods);

        // §10: Validate source identity uniqueness per domain
        ValidateSourceUniqueness("Asset path", assets.Select(a => a.CanonicalUnrealPath));
        ValidateSourceUniqueness("Actor source path", actors.Select(a => a.SourceObjectPath));
        ValidateSourceUniqueness("Component source path", components.Select(c => c.SourceObjectPath));

        // §10: Derive canonical IDs from source identities
        var assetUsedIds = new HashSet<ulong>();
        var assetOldToNew = new Dictionary<ulong, ulong>();
        foreach (var a in assets.OrderBy(a => Encoding.UTF8.GetBytes(a.CanonicalUnrealPath), BytewiseUtf8Comparer.Instance))
            assetOldToNew[a.AssetId] = DeriveEntityId("asset", Encoding.UTF8.GetBytes(a.CanonicalUnrealPath), assetUsedIds);

        var actorUsedIds = new HashSet<ulong>();
        var actorOldToNew = new Dictionary<ulong, ulong>();
        foreach (var a in actors.OrderBy(a => Encoding.UTF8.GetBytes(a.SourceObjectPath), BytewiseUtf8Comparer.Instance))
            actorOldToNew[a.ActorId] = DeriveEntityId("actor", Encoding.UTF8.GetBytes(a.SourceObjectPath), actorUsedIds);

        var componentUsedIds = new HashSet<ulong>();
        var componentOldToNew = new Dictionary<ulong, ulong>();
        foreach (var c in components.OrderBy(c => Encoding.UTF8.GetBytes(c.SourceObjectPath), BytewiseUtf8Comparer.Instance))
            componentOldToNew[c.ComponentId] = DeriveEntityId("component", Encoding.UTF8.GetBytes(c.SourceObjectPath), componentUsedIds);

        // Transform identity: exactly one owner source object path + NUL + "local".
        var transformOwnerPaths = BuildTransformOwnerPaths(actors, components, lights, transforms);
        var xfrmUsedIds = new HashSet<ulong>();
        var xfrmOldToNew = new Dictionary<ulong, ulong>();
        foreach (var t in transforms.OrderBy(t => Encoding.UTF8.GetBytes(transformOwnerPaths[t.TransformId] + "\0local"), BytewiseUtf8Comparer.Instance))
            xfrmOldToNew[t.TransformId] = DeriveEntityId("xfrm", Encoding.UTF8.GetBytes(transformOwnerPaths[t.TransformId] + "\0local"), xfrmUsedIds);

        // Instance set identity: exactly one owning component source object path.
        var instanceSetOwners = BuildInstanceSetOwners(components, instanceSets);
        var instsetUsedIds = new HashSet<ulong>();
        var instsetOldToNew = new Dictionary<ulong, ulong>();
        foreach (var s in instanceSets.OrderBy(s => Encoding.UTF8.GetBytes(instanceSetOwners[s.InstanceSetId]), BytewiseUtf8Comparer.Instance))
        {
            instsetOldToNew[s.InstanceSetId] = DeriveEntityId("instset", Encoding.UTF8.GetBytes(instanceSetOwners[s.InstanceSetId]), instsetUsedIds);
        }

        // §10: Build MATO/BNDX old subject-ID maps for diagnostic remapping
        var matoOldSubjectToIndex = new Dictionary<ulong, int>();
        for (var i = 0; i < materialOverrides.Count; i++)
        {
            var m = materialOverrides[i];
            matoOldSubjectToIndex[MaterialOverrideSubjectId(m.ComponentId, m.SlotOrdinal)] = i;
        }
        var bndxOldSubjectToIndex = new Dictionary<ulong, int>();
        for (var i = 0; i < bundleEntries.Count; i++)
        {
            var e = bundleEntries[i];
            bndxOldSubjectToIndex[BundleEntrySubjectId(e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex)] = i;
        }

        // §10: Remap all foreign keys
        for (var i = 0; i < assets.Count; i++)
            assets[i] = assets[i] with { AssetId = assetOldToNew[assets[i].AssetId] };

        for (var i = 0; i < actors.Count; i++)
        {
            var a = actors[i];
            actors[i] = a with
            {
                ActorId = actorOldToNew[a.ActorId],
                ParentActorId = a.ParentActorId == 0 ? 0 : actorOldToNew[a.ParentActorId],
                RootComponentId = a.RootComponentId == 0 ? 0 : componentOldToNew[a.RootComponentId],
                LocalTransformId = xfrmOldToNew[a.LocalTransformId],
            };
        }

        for (var i = 0; i < components.Count; i++)
        {
            var c = components[i];
            components[i] = c with
            {
                ComponentId = componentOldToNew[c.ComponentId],
                ActorId = actorOldToNew[c.ActorId],
                ParentComponentId = c.ParentComponentId == 0 ? 0 : componentOldToNew[c.ParentComponentId],
                LocalTransformId = xfrmOldToNew[c.LocalTransformId],
                MeshAssetId = assetOldToNew[c.MeshAssetId],
                InstanceSetId = c.InstanceSetId == 0 ? 0 : instsetOldToNew[c.InstanceSetId],
            };
        }

        for (var i = 0; i < transforms.Count; i++)
            transforms[i] = transforms[i] with { TransformId = xfrmOldToNew[transforms[i].TransformId] };

        for (var i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            lights[i] = light with
            {
                ActorId = actorOldToNew[light.ActorId],
                ParentComponentId = light.ParentComponentId == 0 ? 0 : componentOldToNew[light.ParentComponentId],
                TransformId = xfrmOldToNew[light.TransformId],
            };
        }

        for (var i = 0; i < instanceSets.Count; i++)
        {
            var s = instanceSets[i];
            instanceSets[i] = s with
            {
                InstanceSetId = instsetOldToNew[s.InstanceSetId],
                ComponentId = componentOldToNew[s.ComponentId],
            };
        }

        for (var i = 0; i < materialOverrides.Count; i++)
        {
            var m = materialOverrides[i];
            materialOverrides[i] = m with
            {
                ComponentId = componentOldToNew[m.ComponentId],
                BaseMaterialAssetId = m.BaseMaterialAssetId == 0 ? 0 : assetOldToNew[m.BaseMaterialAssetId],
                OverrideMaterialAssetId = m.OverrideMaterialAssetId == 0 ? 0 : assetOldToNew[m.OverrideMaterialAssetId],
            };
        }

        for (var i = 0; i < bundleEntries.Count; i++)
        {
            var e = bundleEntries[i];
            bundleEntries[i] = e with { OwnerAssetId = assetOldToNew[e.OwnerAssetId] };
        }

        // Remap diagnostics — kinds 1-4 use maps; kinds 5/6 recompute from remapped record identity
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            ulong newSubjectId;
            switch (d.SubjectKind)
            {
                case 0: newSubjectId = 0; break;
                case 1: newSubjectId = assetOldToNew[d.SubjectId]; break;
                case 2: newSubjectId = actorOldToNew[d.SubjectId]; break;
                case 3: newSubjectId = componentOldToNew[d.SubjectId]; break;
                case 4: newSubjectId = instsetOldToNew[d.SubjectId]; break;
                case 5:
                {
                    var matoRec = materialOverrides[matoOldSubjectToIndex[d.SubjectId]];
                    newSubjectId = MaterialOverrideSubjectId(matoRec.ComponentId, matoRec.SlotOrdinal);
                    break;
                }
                case 6:
                {
                    var bndxRec = bundleEntries[bndxOldSubjectToIndex[d.SubjectId]];
                    newSubjectId = BundleEntrySubjectId(bndxRec.ResourceKind, bndxRec.LogicalUri, bndxRec.OwnerAssetId, bndxRec.LodIndex);
                    break;
                }
                default: newSubjectId = d.SubjectId; break;
            }
            diagnostics[i] = d with { SubjectId = newSubjectId };
        }

        // Carry captured caller LOD groups through the asset ID remap; never infer ownership from a moved table position.
        lodCaptures = lodCaptures.ToDictionary(pair => assetOldToNew[pair.Key], pair => pair.Value);

        // Canonicalize transforms and instance transforms
        transforms = CanonicalizeTransforms(transforms);
        var instanceTransforms = CanonicalizeInstanceTransforms(doc.InstanceTransforms ?? new());

        // §10: Sort assets by kind/path/derived ID
        assets = assets.OrderBy(a => (byte) a.Kind)
            .ThenBy(a => a.CanonicalUnrealPath, StringComparer.Ordinal)
            .ThenBy(a => a.AssetId)
            .ToList();

        // §10: Assign LODs from captured ownership after asset sort
        var resultLods = new List<MeshLodRecord>();
        {
            var offset = 0;
            for (var i = 0; i < assets.Count; i++)
            {
                var a = assets[i];
                if (a.Kind != AssetKind.StaticMesh || !lodCaptures.TryGetValue(a.AssetId, out var capturedLods))
                {
                    assets[i] = a with { LodStart = 0, LodCount = 0 };
                    continue;
                }
                var sortedLods = capturedLods.OrderBy(l => l.LodIndex).ToList();
                assets[i] = a with { LodStart = (uint) offset, LodCount = (uint) sortedLods.Count };
                resultLods.AddRange(sortedLods);
                offset += sortedLods.Count;
            }
        }
        lods = resultLods;

        // §10 Canonical ordering — sort every table canonically and recompute dependent ranges.
        actors = CanonicalizeActors(actors);
        components = CanonicalizeComponents(components, actors);
        transforms = SortTransformsByOwner(transforms, actors, components, lights);
        lights = CanonicalizeLights(lights, actors, components, transforms);
        (instanceSets, instanceTransforms) = CanonicalizeInstanceSets(instanceSets, instanceTransforms, components);
        materialOverrides = CanonicalizeMaterialOverrides(materialOverrides, components);
        bundleEntries = CanonicalizeBundleEntries(bundleEntries);
        diagnostics = CanonicalizeDiagnostics(diagnostics);
        RecomputeAllRanges(assets, lods, actors, components, materialOverrides, diagnostics, instanceSets, bundleEntries);
        ValidateDiagnosticOwnership(assets, actors, components, instanceSets, materialOverrides, bundleEntries, diagnostics);

        var hasIsmHism = components.Any(c => c.ComponentKind is ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh);
        var hasMatoPayload = materialOverrides.Any(o => (o.Flags & 1u) != 0);
        var requiredFeatures = document.Metadata.RequiredFeatures;
        if (hasIsmHism) requiredFeatures |= SceneMetadata.InstanceSetsBit;
        else requiredFeatures &= ~SceneMetadata.InstanceSetsBit;
        if (hasMatoPayload) requiredFeatures |= SceneMetadata.MatoProvenanceBit;
        else requiredFeatures &= ~SceneMetadata.MatoProvenanceBit;
        var optionalFeatures = document.Metadata.OptionalFeatures;
        if (lights.Count != 0) optionalFeatures |= SceneMetadata.LightRecordsBit;
        else optionalFeatures &= ~SceneMetadata.LightRecordsBit;
        var metadata = document.Metadata with { RequiredFeatures = requiredFeatures, OptionalFeatures = optionalFeatures };

        var combinedStrings = new List<string>();
        AddMetadataStrings(metadata, combinedStrings);
        AddRecordStrings(assets, lods, actors, components, lights, diagnostics, bundleEntries, combinedStrings);
        var dedupedStrings = combinedStrings
            .GroupBy(s => s, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => StrictUtf8.GetBytes(s), BytewiseUtf8Comparer.Instance)
            .ToList();
        foreach (var s in dedupedStrings) ValidateUtf8String(s);
        var strings = new SceneStringTable(dedupedStrings);

        var meta = BuildMetaPayload(metadata, strings);
        var strs = BuildStringsPayload(strings);
        var asst = BuildAsstPayload(assets, lods, strings);
        var actr = BuildActrPayload(actors, strings);
        var comp = BuildCompPayload(components, strings);
        var xfrm = BuildXfrmPayload(transforms);
        byte[]? lite = lights.Count != 0 ? BuildLitePayload(lights, strings) : null;
        var mato = BuildMatoPayload(materialOverrides, matoBlob);
        var diag = BuildDiagPayload(diagnostics, diagBlob, strings);
        var bndx = BuildBndxPayload(bundleEntries, strings);
        var hasInst = hasIsmHism;
        byte[]? inst = hasInst ? BuildInstPayload(instanceSets, instanceTransforms) : null;

        var chunks = new List<(uint fourCc, byte[] payload, uint recordCount)>
        {
            (MetaFourCc, meta, 1),
            (StringsFourCc, strs, checked((uint) strings.Strings.Count)),
            (AsstFourCc, asst, checked((uint) assets.Count)),
            (ActrFourCc, actr, checked((uint) actors.Count)),
            (CompFourCc, comp, checked((uint) components.Count)),
            (XfrmFourCc, xfrm, checked((uint) transforms.Count)),
            (MatoFourCc, mato, checked((uint) materialOverrides.Count)),
            (DiagFourCc, diag, checked((uint) diagnostics.Count)),
            (BndxFourCc, bndx, checked((uint) bundleEntries.Count)),
        };
        if (inst != null)
            chunks.Add((InstFourCc, inst, checked((uint) instanceSets.Count)));
        if (lite != null)
            chunks.Add((LiteFourCc, lite, checked((uint) lights.Count)));

        foreach (var chunk in chunks)
        {
            if (chunk.payload.LongLength > MaxChunkSize)
                throw new InvalidDataException("Chunk exceeds 1 GiB limit.");
        }

        var directoryOffset = HeaderSize;
        var directoryCount = checked((uint) chunks.Count);
        var chunksStart = Align8(checked(directoryOffset + (int) (directoryCount * DirectoryEntrySize)));
        var currentOffset = chunksStart;
        var chunkOffsets = new int[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            chunkOffsets[i] = currentOffset;
            var next = currentOffset + chunks[i].payload.Length;
            // Pad between chunks only; last payload ends at file size (no trailing padding).
            currentOffset = i + 1 < chunks.Count ? Align8(next) : next;
        }
        var fileSize = currentOffset;
        if ((long) fileSize > MaxFileSize)
            throw new InvalidDataException("File exceeds 2 GiB limit.");

        var bytes = new byte[fileSize];
        WriteHeader(bytes, directoryCount, checked((ulong) fileSize));
        for (var i = 0; i < chunks.Count; i++)
        {
            var entrySpan = bytes.AsSpan(directoryOffset + i * DirectoryEntrySize, DirectoryEntrySize);
            WriteDirectoryEntry(entrySpan, chunks[i].fourCc, chunkOffsets[i], chunks[i].payload, chunks[i].recordCount);
        }
        for (var i = 0; i < chunks.Count; i++)
            chunks[i].payload.CopyTo(bytes.AsSpan(chunkOffsets[i]));
        return bytes;
    }

    private static void ValidateDocument(UESceneDocument document, byte[] matoBlob, byte[] diagBlob)
    {
        var metadata = document.Metadata;
        var assets = document.Assets ?? new();
        var lods = document.Lods ?? new();
        var actors = document.Actors ?? new();
        var components = document.Components ?? new();
        var transforms = document.Transforms ?? new();
        var lights = document.Lights ?? new();
        var instanceSets = document.InstanceSets ?? new();
        var instanceTransforms = document.InstanceTransforms ?? new();
        var materialOverrides = document.MaterialOverrides ?? new();
        var diagnostics = document.Diagnostics ?? new();
        var bundleEntries = document.BundleEntries ?? new();

        ValidateMetadataFeatures(metadata);

        // §10 global total fixed-record cap — must precede per-table limits and any allocation.
        checked
        {
            long totalFixedRecords = (long) assets.Count + actors.Count + components.Count + transforms.Count
                + materialOverrides.Count + diagnostics.Count + bundleEntries.Count + instanceSets.Count + lights.Count;
            if (totalFixedRecords > MaxTotalFixedRecords)
                throw new InvalidDataException($"Total declared fixed records exceeds {MaxTotalFixedRecords:N0}.");
        }

        ValidateLodRangesBeforeSort(assets, lods);
        foreach (var asset in assets)
        {
            if (!Enum.IsDefined(asset.Kind)) throw new InvalidDataException($"Unknown asset kind {(byte) asset.Kind}.");
            if (asset.Location != LocationMode.Bundle)
                throw new InvalidDataException("Canonical writer emits only Bundle location_mode.");
            if (!Enum.IsDefined(asset.Availability)) throw new InvalidDataException($"Unknown availability {(byte) asset.Availability}.");
            RequireSha256(asset.Sha256, $"Asset {asset.AssetId}");
            if (asset.CanonicalUnrealPath is null || asset.DisplayName is null || asset.LogicalUri is null || asset.Mime is null)
                throw new InvalidDataException($"Asset {asset.AssetId} string fields cannot be null.");
            ValidateUriField(asset.LogicalUri, $"Asset {asset.AssetId}");
            if ((ulong) asset.DiagnosticStart + asset.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException($"Asset {asset.AssetId} diagnostic range out of bounds.");
        }

        foreach (var lod in lods)
        {
            RequireSha256(lod.Sha256, "LOD");
            if (lod.LogicalUri is null) throw new InvalidDataException("LOD logical URI cannot be null.");
            ValidateUriField(lod.LogicalUri, "LOD");
            if (lod.Reserved != 0) throw new InvalidDataException("LOD reserved must be zero.");
            ValidateScreenSize(lod.SourceScreenSize);
            if (!double.IsFinite(lod.BoundsMinX) || !double.IsFinite(lod.BoundsMinY) || !double.IsFinite(lod.BoundsMinZ)
                || !double.IsFinite(lod.BoundsMaxX) || !double.IsFinite(lod.BoundsMaxY) || !double.IsFinite(lod.BoundsMaxZ))
                throw new InvalidDataException("LOD bounds non-finite.");
            if (lod.BoundsMinX > lod.BoundsMaxX || lod.BoundsMinY > lod.BoundsMaxY || lod.BoundsMinZ > lod.BoundsMaxZ)
                throw new InvalidDataException("LOD bounds min > max.");
        }

        // LOD table ranges index into Lods; LodIndex retains the source mesh LOD identity.
        foreach (var asset in assets)
        {
            if (asset.Kind == AssetKind.StaticMesh)
            {
                if ((ulong) asset.LodStart + asset.LodCount > (ulong) lods.Count)
                    throw new InvalidDataException($"Asset {asset.AssetId} LOD table range out of bounds.");
                var slice = lods.Skip(checked((int) asset.LodStart)).Take(checked((int) asset.LodCount)).ToList();
                if (slice.Count != asset.LodCount)
                    throw new InvalidDataException($"Asset {asset.AssetId} LOD range incomplete.");
                if (slice.Select(lod => lod.LodIndex).Distinct().Count() != slice.Count)
                    throw new InvalidDataException($"Asset {asset.AssetId} has duplicate source LOD indices.");
            }
            else if (asset.LodCount != 0 || asset.LodStart != 0)
            {
                throw new InvalidDataException($"Non-static-mesh asset {asset.AssetId} has non-zero LOD range.");
            }
        }

        var transformIds = transforms.Select(t => t.TransformId).ToHashSet();
        foreach (var actor in actors)
        {
            if (actor.ActorFlags != 0) throw new InvalidDataException($"Actor {actor.ActorId} flags must be zero in V1.");
            if (actor.Name is null || actor.ClassPath is null || actor.SourceObjectPath is null)
                throw new InvalidDataException($"Actor {actor.ActorId} string fields cannot be null.");
            ValidateSourceObjectPathField(actor.SourceObjectPath, $"Actor {actor.ActorId}");
            if (actor.LocalTransformId == 0 || !transformIds.Contains(actor.LocalTransformId))
                throw new InvalidDataException($"Actor {actor.ActorId} local_transform_id invalid.");
            if (actor.ParentActorId != 0 && actors.All(a => a.ActorId != actor.ParentActorId))
                throw new InvalidDataException($"Actor {actor.ActorId} parent missing.");
            if ((ulong) actor.ComponentStart + actor.ComponentCount > (ulong) components.Count)
                throw new InvalidDataException($"Actor {actor.ActorId} component range out of bounds.");
            if ((ulong) actor.DiagnosticStart + actor.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException($"Actor {actor.ActorId} diagnostic range out of bounds.");
            if (actor.RootComponentId != 0)
            {
                var root = components.FirstOrDefault(c => c.ComponentId == actor.RootComponentId);
                if (root is null || root.ActorId != actor.ActorId || root.ParentComponentId != 0)
                    throw new InvalidDataException($"Actor {actor.ActorId} root component invalid.");
            }
            else if (actor.ComponentCount != 0)
            {
                throw new InvalidDataException($"Actor {actor.ActorId} has components but zero root_component_id.");
            }
        }
        ValidateNonOverlappingRanges(actors.Select(a => (a.ComponentStart, a.ComponentCount)), "Actor component");
        ValidateAcyclic(actors.Select(a => (a.ActorId, a.ParentActorId)), "Actor");

        foreach (var comp in components)
        {
            if (!Enum.IsDefined(comp.ComponentKind)) throw new InvalidDataException($"Unknown component kind {(byte) comp.ComponentKind}.");
            if ((comp.VisibilityFlags & ~VisibilityFlags.AllowedMask) != 0)
                throw new InvalidDataException($"Component {comp.ComponentId} visibility flags outside allowed mask.");
            if (comp.Name is null || comp.ClassPath is null || comp.SourceObjectPath is null)
                throw new InvalidDataException($"Component {comp.ComponentId} string fields cannot be null.");
            ValidateSourceObjectPathField(comp.SourceObjectPath, $"Component {comp.ComponentId}");
            if (comp.LocalTransformId == 0 || !transformIds.Contains(comp.LocalTransformId))
                throw new InvalidDataException($"Component {comp.ComponentId} local_transform_id invalid.");
            if (actors.All(a => a.ActorId != comp.ActorId))
                throw new InvalidDataException($"Component {comp.ComponentId} references missing actor.");
            if (comp.MeshAssetId == 0)
                throw new InvalidDataException($"Component {comp.ComponentId} MeshAssetId must be nonzero static mesh.");
            if (!assets.Any(a => a.AssetId == comp.MeshAssetId
                    && a.Kind == AssetKind.StaticMesh
                    && a.Availability == Availability.Available))
                throw new InvalidDataException($"Component {comp.ComponentId} mesh asset must be an available static mesh.");
            if ((ulong) comp.MaterialOverrideStart + comp.MaterialOverrideCount > (ulong) materialOverrides.Count)
                throw new InvalidDataException($"Component {comp.ComponentId} MATO range out of bounds.");
            if ((ulong) comp.DiagnosticStart + comp.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException($"Component {comp.ComponentId} diagnostic range out of bounds.");
            if (comp.ParentComponentId != 0)
            {
                var parent = components.FirstOrDefault(c => c.ComponentId == comp.ParentComponentId);
                if (parent is null) throw new InvalidDataException($"Component {comp.ComponentId} parent missing.");
                if (parent.ActorId != comp.ActorId)
                    throw new InvalidDataException($"Component {comp.ComponentId} parent belongs to different actor.");
            }
        }
        ValidateNonOverlappingRanges(components.Select(c => (c.MaterialOverrideStart, c.MaterialOverrideCount)), "Component MATO");
        ValidateAcyclic(components.Select(c => (c.ComponentId, c.ParentComponentId)), "Component");

        // Actor component ranges must match exact component ownership.
        foreach (var actor in actors)
        {
            var slice = components.Skip(checked((int) actor.ComponentStart)).Take(checked((int) actor.ComponentCount)).ToList();
            if (slice.Count != actor.ComponentCount)
                throw new InvalidDataException($"Actor {actor.ActorId} component range incomplete.");
            foreach (var c in slice)
            {
                if (c.ActorId != actor.ActorId)
                    throw new InvalidDataException($"Actor {actor.ActorId} component range includes foreign component.");
            }
            var owned = components.Where(c => c.ActorId == actor.ActorId).ToList();
            if (owned.Count != actor.ComponentCount)
                throw new InvalidDataException($"Actor {actor.ActorId} component count does not match owned components.");
        }

        var hasIsmHism = components.Any(c => c.ComponentKind is ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh);
        if (hasIsmHism && instanceSets.Count == 0)
            throw new InvalidDataException("ISM/HISM components exist but no instance sets provided.");
        if (!hasIsmHism && instanceSets.Count > 0)
            throw new InvalidDataException("Instance sets provided but no ISM/HISM components.");

        var setsById = instanceSets.ToDictionary(s => s.InstanceSetId);
        var usedSetIds = new HashSet<ulong>();
        foreach (var comp in components)
        {
            if (comp.ComponentKind is ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh)
            {
                if (comp.InstanceSetId == 0 || !setsById.TryGetValue(comp.InstanceSetId, out var set))
                    throw new InvalidDataException($"ISM/HISM component {comp.ComponentId} missing unique instance set.");
                if (!usedSetIds.Add(comp.InstanceSetId))
                    throw new InvalidDataException($"Instance set {comp.InstanceSetId} shared by multiple components.");
                if (set.ComponentId != comp.ComponentId)
                    throw new InvalidDataException($"InstanceSet {set.InstanceSetId} owner mismatch.");
            }
            else if (comp.InstanceSetId != 0)
            {
                throw new InvalidDataException($"StaticMesh component {comp.ComponentId} has non-zero instance_set_id.");
            }
        }
        foreach (var set in instanceSets)
        {
            if (set.Encoding != 0) throw new InvalidDataException($"InstanceSet {set.InstanceSetId} encoding must be 0.");
            if (set.InstanceCount != set.TransformCount)
                throw new InvalidDataException($"InstanceSet {set.InstanceSetId} count != transformCount.");
            if ((ulong) set.TransformStart + set.TransformCount > (ulong) instanceTransforms.Count)
                throw new InvalidDataException($"InstanceSet {set.InstanceSetId} transform range out of bounds.");
            if (components.All(c => c.ComponentId != set.ComponentId))
                throw new InvalidDataException($"InstanceSet {set.InstanceSetId} component missing.");
            // InstanceSet records have no diagnostic range fields; diagnostics use SubjectKind=4 + InstanceSetId.
        }
        ValidateInstanceTransformOwnership(instanceSets, instanceTransforms.Count);

        foreach (var t in transforms)
        {
            if (t.Reserved != 0) throw new InvalidDataException($"Transform {t.TransformId} reserved must be zero.");
            ValidateFiniteTrs(t.TranslationX, t.TranslationY, t.TranslationZ, t.RotationX, t.RotationY, t.RotationZ, t.RotationW, t.ScaleX, t.ScaleY, t.ScaleZ, t.TransformId.ToString());
        }
        ValidateLights(lights, actors, components, transforms);
        foreach (var (t, i) in instanceTransforms.Select((t, i) => (t, i)))
        {
            ValidateFiniteTrs(t.TranslationX, t.TranslationY, t.TranslationZ, t.RotationX, t.RotationY, t.RotationZ, t.RotationW, t.ScaleX, t.ScaleY, t.ScaleZ, $"instance[{i}]");
        }

        foreach (var mato in materialOverrides)
        {
            if ((mato.Flags & ~1u) != 0) throw new InvalidDataException("MATO flags only bit0 allowed.");
            if (mato.Reserved != 0) throw new InvalidDataException("MATO reserved must be zero.");
            if (components.All(c => c.ComponentId != mato.ComponentId))
                throw new InvalidDataException($"MATO references missing component {mato.ComponentId}.");
            if (mato.BaseMaterialAssetId != 0 && assets.All(a => a.AssetId != mato.BaseMaterialAssetId || a.Kind != AssetKind.Material))
                throw new InvalidDataException($"MATO base material {mato.BaseMaterialAssetId} invalid.");
            if (mato.OverrideMaterialAssetId != 0 && assets.All(a => a.AssetId != mato.OverrideMaterialAssetId || a.Kind != AssetKind.Material))
                throw new InvalidDataException($"MATO override material {mato.OverrideMaterialAssetId} invalid.");
            if ((mato.Flags & 1u) == 0)
            {
                if (mato.ParameterPayloadOffset != 0 || mato.ParameterPayloadLength != 0)
                    throw new InvalidDataException("MATO clear HAS_PARAMETER_PAYLOAD requires zero offset/length.");
            }
            else
            {
                ValidateOpaqueEnvelope(matoBlob, mato.ParameterPayloadOffset, mato.ParameterPayloadLength, "MATO");
            }
            if (mato.OverrideMaterialAssetId == 0 && (mato.Flags & 1u) == 0)
                throw new InvalidDataException("Zero override material requires parameter payload.");
            if ((ulong) mato.DiagnosticStart + mato.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException("MATO diagnostic range out of bounds.");
        }
        if (materialOverrides.Select(o => (o.ComponentId, o.SlotOrdinal)).Distinct().Count() != materialOverrides.Count)
            throw new InvalidDataException("Duplicate MATO (component, slot) pairs.");

        // Component MATO ranges exact ownership.
        foreach (var comp in components)
        {
            var slice = materialOverrides.Skip(checked((int) comp.MaterialOverrideStart)).Take(checked((int) comp.MaterialOverrideCount)).ToList();
            foreach (var o in slice)
            {
                if (o.ComponentId != comp.ComponentId)
                    throw new InvalidDataException($"Component {comp.ComponentId} MATO range includes foreign override.");
            }
            var owned = materialOverrides.Count(o => o.ComponentId == comp.ComponentId);
            if (owned != comp.MaterialOverrideCount)
                throw new InvalidDataException($"Component {comp.ComponentId} MATO count mismatch.");
        }

        foreach (var d in diagnostics)
        {
            if (d.Severity is < 1 or > 3) throw new InvalidDataException($"Diagnostic severity {d.Severity} out of range.");
            if (d.SubjectKind > 6) throw new InvalidDataException($"Diagnostic subject_kind {d.SubjectKind} out of range.");
            if (d.Code is < 1 or > 11) throw new InvalidDataException($"Diagnostic code {d.Code} out of range.");
            if ((d.Flags & ~1u) != 0) throw new InvalidDataException("DIAG flags only bit0 allowed.");
            if (d.Reserved != 0) throw new InvalidDataException("DIAG reserved must be zero.");
            if (d.SubjectKind == 0 && d.SubjectId != 0) throw new InvalidDataException("Scene diagnostic has nonzero subject_id.");
            if (d.SubjectKind != 0 && d.SubjectId == 0) throw new InvalidDataException("Non-scene diagnostic has zero subject_id.");
            if (string.IsNullOrEmpty(d.Message)) throw new InvalidDataException("DIAG message_str required nonzero.");
            if (d.SourceObjectPath is null) throw new InvalidDataException("DIAG source path cannot be null.");
            if (!string.IsNullOrEmpty(d.SourceObjectPath))
                ValidateSourceObjectPathField(d.SourceObjectPath, "DIAG");
            if ((d.Flags & 1u) == 0)
            {
                if (d.DetailsOffset != 0 || d.DetailsLength != 0)
                    throw new InvalidDataException("DIAG clear HAS_DETAILS requires zero offset/length.");
            }
            else
            {
                ValidateOpaqueEnvelope(diagBlob, d.DetailsOffset, d.DetailsLength, "DIAG");
            }
        }

        ValidateBndxMapping(assets, lods, bundleEntries);
        foreach (var e in bundleEntries)
        {
            ValidateUriField(e.LogicalUri, "BNDX");
            if ((ulong) e.DiagnosticStart + e.DiagnosticCount > (ulong) diagnostics.Count)
                throw new InvalidDataException("BNDX diagnostic range out of bounds.");
        }
        // Exact diagnostic ownership is validated after RecomputeAllRanges in Write.

        if (assets.Count > 1_000_000) throw new InvalidDataException("Asset count exceeds limit.");
        if (actors.Count > 1_000_000) throw new InvalidDataException("Actor count exceeds limit.");
        if (components.Count > 4_000_000) throw new InvalidDataException("Component count exceeds limit.");
        if (transforms.Count > 5_000_000) throw new InvalidDataException("Transform count exceeds limit.");
        if (lights.Count > 1_000_000) throw new InvalidDataException("Light count exceeds limit.");
        if (lods.Count > 64L * Math.Max(1, assets.Count)) throw new InvalidDataException("LOD count exceeds limit.");
        if (instanceSets.Count > 1_000_000) throw new InvalidDataException("Instance set count exceeds limit.");
        if (instanceTransforms.Count > 50_000_000) throw new InvalidDataException("Instance transform count exceeds limit.");
        if (materialOverrides.Count > 4_000_000) throw new InvalidDataException("Override count exceeds limit.");
        if (diagnostics.Count > 1_000_000) throw new InvalidDataException("Diagnostic count exceeds limit.");
        if (matoBlob.Length > 64 * 1024 * 1024) throw new InvalidDataException("MATO blob exceeds 64 MiB.");
        if (diagBlob.Length > 64 * 1024 * 1024) throw new InvalidDataException("DIAG blob exceeds 64 MiB.");
    }

    private static void ValidateMetadataFeatures(SceneMetadata metadata)
    {
        if ((metadata.RequiredFeatures & ~SceneMetadata.KnownRequiredBits) != 0)
            throw new InvalidDataException("Unknown required feature bits set.");
        if ((metadata.OptionalFeatures & SceneMetadata.RetiredCameraRecordsBit) != 0)
            throw new InvalidDataException("CAMR camera records are retired in UEScene V1.");
    }

    private static void ValidateBndxMapping(List<AssetRecord> assets, List<MeshLodRecord> lods, List<BundleEntry> bundleEntries)
    {
        var keys = new HashSet<(byte, string, ulong, uint)>();
        foreach (var e in bundleEntries)
        {
            if (e.ResourceKind is < 1 or > 3) throw new InvalidDataException($"BNDX resource_kind {e.ResourceKind} invalid.");
            if (e.Location != LocationMode.Bundle)
                throw new InvalidDataException("Canonical BNDX writer emits only Bundle location_mode.");
            if (!Enum.IsDefined(e.Availability)) throw new InvalidDataException("BNDX availability invalid.");
            RequireSha256(e.Sha256, "BNDX");
            if (e.LogicalUri is null || e.Mime is null) throw new InvalidDataException("BNDX string fields cannot be null.");
            if (!keys.Add((e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex)))
                throw new InvalidDataException("Duplicate BNDX (kind, URI, owner, lod).");
        }

        foreach (var asset in assets)
        {
            if (asset.Availability != Availability.Available) continue;
            var ownedEntries = bundleEntries.Where(e => e.OwnerAssetId == asset.AssetId).ToList();
            if (asset.Kind == AssetKind.StaticMesh)
            {
                if (ownedEntries.Count != 1 || ownedEntries[0].ResourceKind != (byte) BundleResourceKind.Model
                    || ownedEntries[0].LodIndex != uint.MaxValue)
                    throw new InvalidDataException($"Available static mesh {asset.AssetId} requires exactly one primary Model BNDX.");
                MatchBndxToSource(ownedEntries[0], asset.LogicalUri, asset.Sha256, asset.ByteLength, asset.Mime, asset.Location, asset.Availability);
            }
            else
            {
                // SM without LODs and textures require one kind-specific primary; materials may omit theirs.
                var kind = asset.Kind switch
                {
                    AssetKind.StaticMesh => (byte) BundleResourceKind.Model,
                    AssetKind.Material => (byte) BundleResourceKind.MaterialJson,
                    AssetKind.Texture => (byte) BundleResourceKind.Texture,
                    _ => throw new InvalidDataException("Unknown asset kind for BNDX."),
                };
                // MaterialJson is optional: may exist but not necessary for materials when treated optional.
                if (asset.Kind == AssetKind.Material)
                {
                    if (ownedEntries.Any(e => e.ResourceKind != kind || e.LodIndex != uint.MaxValue))
                        throw new InvalidDataException($"Material {asset.AssetId} has invalid BNDX record.");
                    if (ownedEntries.Count > 1)
                        throw new InvalidDataException($"Material {asset.AssetId} has duplicate BNDX primary.");
                    if (ownedEntries.Count == 1)
                        MatchBndxToSource(ownedEntries[0], asset.LogicalUri, asset.Sha256, asset.ByteLength, asset.Mime, asset.Location, asset.Availability);
                    continue;
                }
                if (ownedEntries.Count != 1 || ownedEntries[0].ResourceKind != kind || ownedEntries[0].LodIndex != uint.MaxValue)
                    throw new InvalidDataException($"Available non-SM ASST {asset.AssetId} requires exactly one matching BNDX primary.");
                MatchBndxToSource(ownedEntries[0], asset.LogicalUri, asset.Sha256, asset.ByteLength, asset.Mime, asset.Location, asset.Availability);
            }
        }

        foreach (var asset in assets.Where(a => a.Availability != Availability.Available))
        {
            if (bundleEntries.Any(e => e.OwnerAssetId == asset.AssetId))
                throw new InvalidDataException($"Unavailable asset {asset.AssetId} must have no BNDX records.");
        }

        foreach (var e in bundleEntries)
        {
            if (assets.All(a => a.AssetId != e.OwnerAssetId))
                throw new InvalidDataException($"BNDX owner asset {e.OwnerAssetId} missing.");
            if (e.LodIndex != uint.MaxValue)
                throw new InvalidDataException("Per-LOD BNDX entries are invalid in V1.");
        }
    }

    private static void MatchBndxToSource(BundleEntry entry, string uri, byte[] sha, ulong length, string mime, LocationMode location, Availability availability)
    {
        if (!StringComparer.Ordinal.Equals(entry.LogicalUri, uri)
            || !entry.Sha256.AsSpan().SequenceEqual(sha)
            || entry.ByteLength != length
            || !StringComparer.Ordinal.Equals(entry.Mime, mime)
            || entry.Location != location
            || entry.Availability != availability)
        {
            throw new InvalidDataException("BNDX URI/hash/length/MIME/mode/availability mismatch with source.");
        }
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
            throw new InvalidDataException($"Transform {id} quaternion not normalized: length={qLen}.");
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(sz))
            throw new InvalidDataException($"Transform {id} non-finite scale.");
    }

    private static void RequireSha256(byte[] sha, string context)
    {
        if (sha is null || sha.Length != 32)
            throw new InvalidDataException($"{context} SHA-256 must be exactly 32 bytes.");
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
                    throw new InvalidDataException($"{name} ranges overlap at {idx}.");
            }
        }
    }

    private static void ValidateUniqueNonzero(string tableName, IEnumerable<ulong> ids)
    {
        var seen = new HashSet<ulong>();
        foreach (var id in ids)
        {
            if (id == 0) throw new InvalidDataException($"{tableName} contains zero ID.");
            if (!seen.Add(id)) throw new InvalidDataException($"{tableName} contains duplicate ID {id}.");
        }
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

    private static List<TransformRecord> CanonicalizeTransforms(List<TransformRecord> transforms)
    {
        return transforms.Select(t =>
        {
            var (rx, ry, rz, rw) = CanonicalizeQuat(t.RotationX, t.RotationY, t.RotationZ, t.RotationW);
            return t with
            {
                TranslationX = CanonicalizeNegZero(t.TranslationX),
                TranslationY = CanonicalizeNegZero(t.TranslationY),
                TranslationZ = CanonicalizeNegZero(t.TranslationZ),
                RotationX = rx, RotationY = ry, RotationZ = rz, RotationW = rw,
                ScaleX = CanonicalizeNegZero(t.ScaleX),
                ScaleY = CanonicalizeNegZero(t.ScaleY),
                ScaleZ = CanonicalizeNegZero(t.ScaleZ),
            };
        }).ToList();
    }

    private static List<InstanceTransform> CanonicalizeInstanceTransforms(List<InstanceTransform> transforms)
    {
        return transforms.Select(t =>
        {
            var (rx, ry, rz, rw) = CanonicalizeQuat(t.RotationX, t.RotationY, t.RotationZ, t.RotationW);
            return t with
            {
                TranslationX = CanonicalizeNegZero(t.TranslationX),
                TranslationY = CanonicalizeNegZero(t.TranslationY),
                TranslationZ = CanonicalizeNegZero(t.TranslationZ),
                RotationX = rx, RotationY = ry, RotationZ = rz, RotationW = rw,
                ScaleX = CanonicalizeNegZero(t.ScaleX),
                ScaleY = CanonicalizeNegZero(t.ScaleY),
                ScaleZ = CanonicalizeNegZero(t.ScaleZ),
            };
        }).ToList();
    }

    private static (float x, float y, float z, float w) CanonicalizeQuat(float x, float y, float z, float w)
    {
        var len = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (len == 0) throw new InvalidDataException("Quaternion length is zero.");
        x /= len; y /= len; z /= len; w /= len;
        // Sign: w > 0; if zero, first nonzero of z, y, x positive.
        var flip = false;
        if (w < 0) flip = true;
        else if (w == 0)
        {
            if (z < 0) flip = true;
            else if (z == 0)
            {
                if (y < 0) flip = true;
                else if (y == 0 && x < 0) flip = true;
            }
        }
        if (flip) { x = -x; y = -y; z = -z; w = -w; }
        return (CanonicalizeNegZero(x), CanonicalizeNegZero(y), CanonicalizeNegZero(z), CanonicalizeNegZero(w));
    }

    private static float CanonicalizeNegZero(float v) => v == 0f ? 0f : v;
    private static double CanonicalizeNegZero(double v) => v == 0d ? 0d : v;

    private static void AddMetadataStrings(SceneMetadata metadata, List<string> strings)
    {
        strings.Add("uescene");
        strings.Add(metadata.GeneratorName);
        strings.Add(metadata.GeneratorVersion);
        strings.Add(metadata.GeneratorBuild);
        strings.Add(metadata.SourceWorldPath);
        strings.Add(metadata.PersistentLevelPath);
        if (metadata.SourceGame != null) strings.Add(metadata.SourceGame);
        if (metadata.SourceEngine != null) strings.Add(metadata.SourceEngine);
    }

    private static void AddRecordStrings(
        List<AssetRecord> assets, List<MeshLodRecord> lods,
        List<ActorRecord> actors, List<ComponentRecord> components,
        List<LightRecord> lights,
        List<DiagnosticRecord> diagnostics, List<BundleEntry> bundleEntries,
        List<string> strings)
    {
        foreach (var a in assets)
        {
            strings.Add(a.CanonicalUnrealPath);
            strings.Add(a.DisplayName);
            strings.Add(a.LogicalUri);
            strings.Add(a.Mime);
        }
        foreach (var l in lods) strings.Add(l.LogicalUri);
        foreach (var a in actors)
        {
            strings.Add(a.Name);
            strings.Add(a.ClassPath);
            strings.Add(a.SourceObjectPath);
        }
        foreach (var c in components)
        {
            strings.Add(c.Name);
            strings.Add(c.ClassPath);
            strings.Add(c.SourceObjectPath);
        }
        foreach (var light in lights)
        {
            strings.Add(light.Name);
            strings.Add(light.ClassPath);
            strings.Add(light.SourceObjectPath);
        }
        foreach (var d in diagnostics)
        {
            strings.Add(d.Message);
            if (!string.IsNullOrEmpty(d.SourceObjectPath)) strings.Add(d.SourceObjectPath);
        }
        foreach (var e in bundleEntries)
        {
            strings.Add(e.LogicalUri);
            strings.Add(e.Mime);
        }
    }

    private static void ValidateUtf8String(string value)
    {
        if (value is null) throw new InvalidDataException("Required UEScene strings cannot be null.");
        if (value.IndexOf('\0') >= 0) throw new InvalidDataException("UEScene strings cannot contain NUL.");
        byte[] utf8;
        try { utf8 = StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException exception) { throw new InvalidDataException("UEScene strings must be valid UTF-8.", exception); }
        if (utf8.Length > 65_536) throw new InvalidDataException("UEScene strings cannot exceed 65,536 UTF-8 bytes.");
    }

    private static void ValidateUriField(string value, string context)
    {
        ValidateUtf8String(value);
        var utf8 = StrictUtf8.GetBytes(value);
        if (utf8.Length > MaxUriUtf8Bytes)
            throw new InvalidDataException($"{context} URI exceeds {MaxUriUtf8Bytes} UTF-8 bytes.");
        if (value.Length == 0 || value.Contains('\\') || value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("\\\\", StringComparison.Ordinal) || value.Contains('\0'))
            throw new InvalidDataException($"{context} URI has invalid syntax.");
        if (value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || value.Contains(':')
            || (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '/' || value[2] == '\\')))
            throw new InvalidDataException($"{context} URI has invalid rooted/device syntax.");
        foreach (var segment in value.Split('/'))
            if (segment.Length == 0 || segment is "." or "..")
                throw new InvalidDataException($"{context} URI has invalid segment.");
    }

    private static void ValidateSourceObjectPathField(string value, string context)
    {
        ValidateUtf8String(value);
        var utf8 = StrictUtf8.GetBytes(value);
        if (utf8.Length > MaxSourceObjectPathUtf8Bytes)
            throw new InvalidDataException($"{context} source object path exceeds {MaxSourceObjectPathUtf8Bytes} UTF-8 bytes.");
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
                    throw new InvalidDataException($"Asset {asset.AssetId} diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 1 && d.SubjectId == asset.AssetId);
            if (owned != asset.DiagnosticCount)
                throw new InvalidDataException($"Asset {asset.AssetId} diagnostic count mismatch.");
            if (asset.Availability != Availability.Available && owned == 0)
                throw new InvalidDataException($"Asset {asset.AssetId} availability requires an exact asset diagnostic.");
        }

        foreach (var actor in actors)
        {
            var slice = diagnostics.Skip(checked((int) actor.DiagnosticStart)).Take(checked((int) actor.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 2 || d.SubjectId != actor.ActorId)
                    throw new InvalidDataException($"Actor {actor.ActorId} diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 2 && d.SubjectId == actor.ActorId);
            if (owned != actor.DiagnosticCount)
                throw new InvalidDataException($"Actor {actor.ActorId} diagnostic count mismatch.");
        }

        foreach (var comp in components)
        {
            var slice = diagnostics.Skip(checked((int) comp.DiagnosticStart)).Take(checked((int) comp.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 3 || d.SubjectId != comp.ComponentId)
                    throw new InvalidDataException($"Component {comp.ComponentId} diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 3 && d.SubjectId == comp.ComponentId);
            if (owned != comp.DiagnosticCount)
                throw new InvalidDataException($"Component {comp.ComponentId} diagnostic count mismatch.");
        }

        // InstanceSet has no range fields; only require that SubjectKind=4 subjects resolve.
        foreach (var d in diagnostics.Where(d => d.SubjectKind == 4))
        {
            if (instanceSets.All(s => s.InstanceSetId != d.SubjectId))
                throw new InvalidDataException($"InstanceSet diagnostic subject {d.SubjectId} missing.");
        }

        foreach (var mato in materialOverrides)
        {
            var expectedId = MaterialOverrideSubjectId(mato.ComponentId, mato.SlotOrdinal);
            var slice = diagnostics.Skip(checked((int) mato.DiagnosticStart)).Take(checked((int) mato.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 5 || d.SubjectId != expectedId)
                    throw new InvalidDataException($"MATO ({mato.ComponentId},{mato.SlotOrdinal}) diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 5 && d.SubjectId == expectedId);
            if (owned != mato.DiagnosticCount)
                throw new InvalidDataException($"MATO ({mato.ComponentId},{mato.SlotOrdinal}) diagnostic count mismatch.");
        }

        foreach (var entry in bundleEntries)
        {
            var expectedId = BundleEntrySubjectId(entry.ResourceKind, entry.LogicalUri, entry.OwnerAssetId, entry.LodIndex);
            var slice = diagnostics.Skip(checked((int) entry.DiagnosticStart)).Take(checked((int) entry.DiagnosticCount));
            foreach (var d in slice)
            {
                if (d.SubjectKind != 6 || d.SubjectId != expectedId)
                    throw new InvalidDataException($"BNDX diagnostic range has foreign subject.");
            }
            var owned = diagnostics.Count(d => d.SubjectKind == 6 && d.SubjectId == expectedId);
            if (owned != entry.DiagnosticCount)
                throw new InvalidDataException("BNDX diagnostic count mismatch.");
        }

        // All non-scene diagnostics must resolve to a known subject.
        foreach (var d in diagnostics)
        {
            switch (d.SubjectKind)
            {
                case 0:
                    break;
                case 1:
                    if (assets.All(a => a.AssetId != d.SubjectId))
                        throw new InvalidDataException($"Asset diagnostic subject {d.SubjectId} missing.");
                    break;
                case 2:
                    if (actors.All(a => a.ActorId != d.SubjectId))
                        throw new InvalidDataException($"Actor diagnostic subject {d.SubjectId} missing.");
                    break;
                case 3:
                    if (components.All(c => c.ComponentId != d.SubjectId))
                        throw new InvalidDataException($"Component diagnostic subject {d.SubjectId} missing.");
                    break;
                case 4:
                    break; // checked above
                case 5:
                    if (!materialOverrides.Any(o => MaterialOverrideSubjectId(o.ComponentId, o.SlotOrdinal) == d.SubjectId))
                        throw new InvalidDataException($"MATO diagnostic subject {d.SubjectId} missing.");
                    break;
                case 6:
                    if (!bundleEntries.Any(e => BundleEntrySubjectId(e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex) == d.SubjectId))
                        throw new InvalidDataException($"BNDX diagnostic subject {d.SubjectId} missing.");
                    break;
            }
        }
    }

    private static SceneStringTable BuildStringTable(SceneMetadata metadata)
    {
        var strings = new List<(string Value, byte[] Utf8)>();
        foreach (var (value, optional) in new[]
                 {
                     ("uescene", false), (metadata.GeneratorName, false), (metadata.GeneratorVersion, false), (metadata.GeneratorBuild, false),
                     (metadata.SourceWorldPath, false), (metadata.PersistentLevelPath, false), (metadata.SourceGame, true), (metadata.SourceEngine, true)
                 })
        {
            if (value is null)
            {
                if (optional) continue;
                throw new InvalidDataException("Required UEScene metadata strings cannot be null.");
            }
            ValidateUtf8String(value);
            strings.Add((value, StrictUtf8.GetBytes(value)));
        }

        var values = strings
            .GroupBy(entry => entry.Value, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(entry => entry.Utf8, BytewiseUtf8Comparer.Instance)
            .Select(entry => entry.Value)
            .ToArray();
        return new SceneStringTable(values);
    }

    private static byte[] BuildMetaPayload(SceneMetadata metadata, SceneStringTable strings)
    {
        var bytes = new byte[68];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], strings.GetId("uescene"));
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], strings.GetId(metadata.GeneratorName));
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], strings.GetId(metadata.GeneratorVersion));
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], strings.GetId(metadata.GeneratorBuild));
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], strings.GetId(metadata.SourceWorldPath));
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], strings.GetId(metadata.PersistentLevelPath));
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], metadata.SourceGame is null ? 0 : strings.GetId(metadata.SourceGame));
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], metadata.SourceEngine is null ? 0 : strings.GetId(metadata.SourceEngine));
        BinaryPrimitives.WriteUInt64LittleEndian(span[36..], metadata.ExportUnixTimeMsUtc);
        span[44] = SceneMetadata.UnrealLeftHandedZUp;
        span[45] = SceneMetadata.Centimeter;
        span[46] = SceneMetadata.LocalToParent;
        span[47] = SceneMetadata.Xyzw;
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(span[52..], metadata.RequiredFeatures);
        BinaryPrimitives.WriteUInt64LittleEndian(span[60..], metadata.OptionalFeatures);
        return bytes;
    }

    private static byte[] BuildStringsPayload(SceneStringTable strings)
    {
        var encoded = strings.Strings.Select(StrictUtf8.GetBytes).ToArray();
        var length = checked(4 + encoded.Sum(value => checked(4 + value.Length)));
        var bytes = new byte[length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, checked((uint) encoded.Length));
        var offset = 4;
        foreach (var value in encoded)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], checked((uint) value.Length));
            offset += 4;
            value.CopyTo(span[offset..]);
            offset += value.Length;
        }
        return bytes;
    }

    private static byte[] BuildAsstPayload(List<AssetRecord> assets, List<MeshLodRecord> lods, SceneStringTable strings)
    {
        var totalSize = checked(8 + assets.Count * 84 + lods.Count * 104);
        var bytes = new byte[totalSize];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, checked((uint) assets.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], checked((uint) lods.Count));
        var offset = 8;
        foreach (var asset in assets)
        {
            var r = span[offset..];
            BinaryPrimitives.WriteUInt64LittleEndian(r, asset.AssetId);
            r[8] = (byte) asset.Kind;
            r[9] = (byte) asset.Location;
            r[10] = (byte) asset.Availability;
            r[11] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], strings.GetId(asset.CanonicalUnrealPath));
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], strings.GetId(asset.DisplayName));
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], strings.GetId(asset.LogicalUri));
            BinaryPrimitives.WriteUInt32LittleEndian(r[24..], strings.GetId(asset.Mime));
            asset.Sha256.CopyTo(r[28..]);
            BinaryPrimitives.WriteUInt64LittleEndian(r[60..], asset.ByteLength);
            BinaryPrimitives.WriteUInt32LittleEndian(r[68..], asset.DiagnosticStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[72..], asset.DiagnosticCount);
            BinaryPrimitives.WriteUInt32LittleEndian(r[76..], asset.LodStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[80..], asset.LodCount);
            offset += 84;
        }
        foreach (var lod in lods)
        {
            var r = span[offset..];
            BinaryPrimitives.WriteUInt32LittleEndian(r, lod.LodIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], strings.GetId(lod.LogicalUri));
            lod.Sha256.CopyTo(r[8..]);
            BinaryPrimitives.WriteUInt64LittleEndian(r[40..], lod.ByteLength);
            BinaryPrimitives.WriteInt64LittleEndian(r[48..], BitConverter.DoubleToInt64Bits(CanonicalizeNegZero(lod.BoundsMinX)));
            BinaryPrimitives.WriteInt64LittleEndian(r[56..], BitConverter.DoubleToInt64Bits(CanonicalizeNegZero(lod.BoundsMinY)));
            BinaryPrimitives.WriteInt64LittleEndian(r[64..], BitConverter.DoubleToInt64Bits(CanonicalizeNegZero(lod.BoundsMinZ)));
            BinaryPrimitives.WriteInt64LittleEndian(r[72..], BitConverter.DoubleToInt64Bits(CanonicalizeNegZero(lod.BoundsMaxX)));
            BinaryPrimitives.WriteInt64LittleEndian(r[80..], BitConverter.DoubleToInt64Bits(CanonicalizeNegZero(lod.BoundsMaxY)));
            BinaryPrimitives.WriteInt64LittleEndian(r[88..], BitConverter.DoubleToInt64Bits(CanonicalizeNegZero(lod.BoundsMaxZ)));
            var screen = lod.SourceScreenSize;
            if (BitConverter.SingleToUInt32Bits(screen) != CanonicalQuietNanBits)
                screen = CanonicalizeNegZero(screen);
            BinaryPrimitives.WriteSingleLittleEndian(r[96..], screen);
            BinaryPrimitives.WriteUInt32LittleEndian(r[100..], lod.Reserved);
            offset += 104;
        }
        return bytes;
    }

    private static byte[] BuildActrPayload(List<ActorRecord> actors, SceneStringTable strings)
    {
        var bytes = new byte[checked(4 + actors.Count * 64)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint) actors.Count));
        var offset = 4;
        foreach (var actor in actors)
        {
            var r = bytes.AsSpan(offset);
            BinaryPrimitives.WriteUInt64LittleEndian(r, actor.ActorId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], actor.ParentActorId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], strings.GetId(actor.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], strings.GetId(actor.ClassPath));
            BinaryPrimitives.WriteUInt32LittleEndian(r[24..], strings.GetId(actor.SourceObjectPath));
            BinaryPrimitives.WriteUInt64LittleEndian(r[28..], actor.RootComponentId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[36..], actor.LocalTransformId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[44..], actor.ComponentStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[48..], actor.ComponentCount);
            BinaryPrimitives.WriteUInt32LittleEndian(r[52..], actor.ActorFlags);
            BinaryPrimitives.WriteUInt32LittleEndian(r[56..], actor.DiagnosticStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[60..], actor.DiagnosticCount);
            offset += 64;
        }
        return bytes;
    }

    private static byte[] BuildCompPayload(List<ComponentRecord> components, SceneStringTable strings)
    {
        var bytes = new byte[checked(4 + components.Count * 88)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint) components.Count));
        var offset = 4;
        foreach (var comp in components)
        {
            var r = bytes.AsSpan(offset);
            BinaryPrimitives.WriteUInt64LittleEndian(r, comp.ComponentId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], comp.ActorId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[16..], comp.ParentComponentId);
            r[24] = (byte) comp.ComponentKind;
            r[25] = 0; r[26] = 0; r[27] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(r[28..], strings.GetId(comp.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(r[32..], strings.GetId(comp.ClassPath));
            BinaryPrimitives.WriteUInt32LittleEndian(r[36..], strings.GetId(comp.SourceObjectPath));
            BinaryPrimitives.WriteUInt64LittleEndian(r[40..], comp.LocalTransformId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[48..], comp.MeshAssetId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[56..], comp.MaterialOverrideStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[60..], comp.MaterialOverrideCount);
            BinaryPrimitives.WriteUInt64LittleEndian(r[64..], comp.InstanceSetId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[72..], comp.VisibilityFlags);
            BinaryPrimitives.WriteUInt32LittleEndian(r[76..], comp.DiagnosticStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[80..], comp.DiagnosticCount);
            BinaryPrimitives.WriteUInt32LittleEndian(r[84..], 0);
            offset += 88;
        }
        return bytes;
    }

    private static byte[] BuildXfrmPayload(List<TransformRecord> transforms)
    {
        var bytes = new byte[checked(4 + transforms.Count * 64)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint) transforms.Count));
        var offset = 4;
        foreach (var t in transforms)
        {
            var r = bytes.AsSpan(offset);
            BinaryPrimitives.WriteUInt64LittleEndian(r, t.TransformId);
            BinaryPrimitives.WriteInt64LittleEndian(r[8..], BitConverter.DoubleToInt64Bits(t.TranslationX));
            BinaryPrimitives.WriteInt64LittleEndian(r[16..], BitConverter.DoubleToInt64Bits(t.TranslationY));
            BinaryPrimitives.WriteInt64LittleEndian(r[24..], BitConverter.DoubleToInt64Bits(t.TranslationZ));
            BinaryPrimitives.WriteSingleLittleEndian(r[32..], t.RotationX);
            BinaryPrimitives.WriteSingleLittleEndian(r[36..], t.RotationY);
            BinaryPrimitives.WriteSingleLittleEndian(r[40..], t.RotationZ);
            BinaryPrimitives.WriteSingleLittleEndian(r[44..], t.RotationW);
            BinaryPrimitives.WriteSingleLittleEndian(r[48..], t.ScaleX);
            BinaryPrimitives.WriteSingleLittleEndian(r[52..], t.ScaleY);
            BinaryPrimitives.WriteSingleLittleEndian(r[56..], t.ScaleZ);
            BinaryPrimitives.WriteUInt32LittleEndian(r[60..], t.Reserved);
            offset += 64;
        }
        return bytes;
    }

    // LITE records are fixed-size so no light-specific variable payload is needed.
    private static byte[] BuildLitePayload(List<LightRecord> lights, SceneStringTable strings)
    {
        const int recordSize = 116;
        var bytes = new byte[checked(4 + lights.Count * recordSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint) lights.Count));
        var offset = 4;
        foreach (var light in lights)
        {
            var r = bytes.AsSpan(offset, recordSize);
            BinaryPrimitives.WriteUInt64LittleEndian(r, light.LightId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], light.ActorId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[16..], light.ParentComponentId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[24..], light.TransformId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[32..], strings.GetId(light.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(r[36..], strings.GetId(light.ClassPath));
            BinaryPrimitives.WriteUInt32LittleEndian(r[40..], strings.GetId(light.SourceObjectPath));
            r[44] = (byte) light.LightType;
            r[45] = (byte) light.ColorEncoding;
            r[46] = (byte) light.LightUnits;
            r[47] = 0;
            r[48] = light.ColorR; r[49] = light.ColorG; r[50] = light.ColorB; r[51] = light.ColorA;
            BinaryPrimitives.WriteUInt32LittleEndian(r[52..], light.Flags);
            BinaryPrimitives.WriteSingleLittleEndian(r[56..], light.Intensity);
            BinaryPrimitives.WriteSingleLittleEndian(r[60..], light.AttenuationRadius);
            BinaryPrimitives.WriteSingleLittleEndian(r[64..], light.FalloffExponent);
            BinaryPrimitives.WriteSingleLittleEndian(r[68..], light.InnerConeAngleDegrees);
            BinaryPrimitives.WriteSingleLittleEndian(r[72..], light.OuterConeAngleDegrees);
            BinaryPrimitives.WriteSingleLittleEndian(r[76..], light.RectWidth);
            BinaryPrimitives.WriteSingleLittleEndian(r[80..], light.RectHeight);
            BinaryPrimitives.WriteUInt32LittleEndian(r[84..], 0);
            BinaryPrimitives.WriteSingleLittleEndian(r[88..], light.LightSourceAngle);
            BinaryPrimitives.WriteSingleLittleEndian(r[92..], light.LightSourceSoftAngle);
            BinaryPrimitives.WriteSingleLittleEndian(r[96..], light.SourceRadius);
            BinaryPrimitives.WriteSingleLittleEndian(r[100..], light.SoftSourceRadius);
            BinaryPrimitives.WriteSingleLittleEndian(r[104..], light.SourceLength);
            BinaryPrimitives.WriteSingleLittleEndian(r[108..], light.BarnDoorAngle);
            BinaryPrimitives.WriteSingleLittleEndian(r[112..], light.BarnDoorLength);
            offset += recordSize;
        }
        return bytes;
    }

    private static byte[] BuildInstPayload(List<InstanceSetRecord> sets, List<InstanceTransform> transforms)
    {
        var bytes = new byte[checked(8 + sets.Count * 32 + transforms.Count * 40)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), checked((uint) sets.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint) transforms.Count));
        var offset = 8;
        foreach (var set in sets)
        {
            var r = bytes.AsSpan(offset);
            BinaryPrimitives.WriteUInt64LittleEndian(r, set.InstanceSetId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], set.ComponentId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], set.InstanceCount);
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], set.Encoding);
            BinaryPrimitives.WriteUInt32LittleEndian(r[24..], set.TransformStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[28..], set.TransformCount);
            offset += 32;
        }
        foreach (var t in transforms)
        {
            var r = bytes.AsSpan(offset);
            BinaryPrimitives.WriteSingleLittleEndian(r, t.TranslationX);
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], t.TranslationY);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], t.TranslationZ);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], t.RotationX);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], t.RotationY);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], t.RotationZ);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], t.RotationW);
            BinaryPrimitives.WriteSingleLittleEndian(r[28..], t.ScaleX);
            BinaryPrimitives.WriteSingleLittleEndian(r[32..], t.ScaleY);
            BinaryPrimitives.WriteSingleLittleEndian(r[36..], t.ScaleZ);
            offset += 40;
        }
        return bytes;
    }

    private static byte[] BuildMatoPayload(List<MaterialOverrideRecord> overrides, byte[] blob)
    {
        var bytes = new byte[checked(8 + overrides.Count * 56 + blob.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint) overrides.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint) blob.Length));
        var offset = 8;
        foreach (var o in overrides)
        {
            var r = bytes.AsSpan(offset);
            BinaryPrimitives.WriteUInt64LittleEndian(r, o.ComponentId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], o.SlotOrdinal);
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], o.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(r[16..], o.BaseMaterialAssetId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[24..], o.OverrideMaterialAssetId);
            BinaryPrimitives.WriteUInt64LittleEndian(r[32..], o.ParameterPayloadOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(r[40..], o.ParameterPayloadLength);
            BinaryPrimitives.WriteUInt32LittleEndian(r[44..], o.DiagnosticStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[48..], o.DiagnosticCount);
            BinaryPrimitives.WriteUInt32LittleEndian(r[52..], o.Reserved);
            offset += 56;
        }
        blob.CopyTo(bytes.AsSpan(offset));
        return bytes;
    }

    private static byte[] BuildDiagPayload(List<DiagnosticRecord> diagnostics, byte[] blob, SceneStringTable strings)
    {
        var bytes = new byte[checked(8 + diagnostics.Count * 40 + blob.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint) diagnostics.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint) blob.Length));
        var offset = 8;
        foreach (var d in diagnostics)
        {
            var r = bytes.AsSpan(offset);
            r[0] = d.Severity;
            r[1] = d.SubjectKind;
            BinaryPrimitives.WriteUInt16LittleEndian(r[2..], d.Code);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], d.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], d.SubjectId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], string.IsNullOrEmpty(d.SourceObjectPath) ? 0u : strings.GetId(d.SourceObjectPath));
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], strings.GetId(d.Message));
            BinaryPrimitives.WriteUInt64LittleEndian(r[24..], d.DetailsOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(r[32..], d.DetailsLength);
            BinaryPrimitives.WriteUInt32LittleEndian(r[36..], d.Reserved);
            offset += 40;
        }
        blob.CopyTo(bytes.AsSpan(offset));
        return bytes;
    }

    private static byte[] BuildBndxPayload(List<BundleEntry> entries, SceneStringTable strings)
    {
        var bytes = new byte[checked(4 + entries.Count * 72)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint) entries.Count));
        var offset = 4;
        foreach (var e in entries)
        {
            var r = bytes.AsSpan(offset);
            r[0] = e.ResourceKind;
            r[1] = (byte) e.Location;
            r[2] = (byte) e.Availability;
            r[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], strings.GetId(e.LogicalUri));
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], e.OwnerAssetId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], e.LodIndex);
            e.Sha256.CopyTo(r[20..]);
            BinaryPrimitives.WriteUInt64LittleEndian(r[52..], e.ByteLength);
            BinaryPrimitives.WriteUInt32LittleEndian(r[60..], strings.GetId(e.Mime));
            BinaryPrimitives.WriteUInt32LittleEndian(r[64..], e.DiagnosticStart);
            BinaryPrimitives.WriteUInt32LittleEndian(r[68..], e.DiagnosticCount);
            offset += 72;
        }
        return bytes;
    }

    private static void WriteHeader(Span<byte> bytes, uint directoryCount, ulong fileSize)
    {
        "UESCENE\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[20..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], directoryCount);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], Crc32IsoHdlc.Compute(bytes[..40]));
    }

    private static void WriteDirectoryEntry(Span<byte> entry, uint fourCc, int offset, byte[] payload, uint recordCount)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(entry, fourCc);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], checked((ulong) offset));
        BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], checked((ulong) payload.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(entry[24..], checked((ulong) payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], Crc32IsoHdlc.Compute(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], recordCount);
    }

    private static int Align8(int value) => checked((value + 7) & ~7);

    private sealed class BytewiseUtf8Comparer : IComparer<byte[]>
    {
        public static BytewiseUtf8Comparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return left.AsSpan().SequenceCompareTo(right);
        }
    }

    // ── §10 Canonical ordering helpers ──────────────────────────────────────

    private static List<ActorRecord> CanonicalizeActors(List<ActorRecord> actors)
    {
        if (actors.Count <= 1) return actors;
        var childrenOf = actors.GroupBy(a => a.ParentActorId)
            .ToDictionary(g => g.Key,
                g => g.OrderBy(a => StrictUtf8.GetBytes(a.SourceObjectPath), BytewiseUtf8Comparer.Instance).ToList());
        var result = new List<ActorRecord>(actors.Count);
        void Visit(ulong parentId)
        {
            if (!childrenOf.TryGetValue(parentId, out var children)) return;
            foreach (var child in children)
            {
                result.Add(child);
                Visit(child.ActorId);
            }
        }
        Visit(0);
        return result;
    }

    private static List<ComponentRecord> CanonicalizeComponents(List<ComponentRecord> components, List<ActorRecord> sortedActors)
    {
        if (components.Count <= 1) return components;
        var componentsByActor = components.GroupBy(c => c.ActorId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<ComponentRecord>(components.Count);
        foreach (var actor in sortedActors)
        {
            if (!componentsByActor.TryGetValue(actor.ActorId, out var actorComps) || actorComps.Count == 0) continue;
            var childrenOf = actorComps.GroupBy(c => c.ParentComponentId)
                .ToDictionary(g => g.Key,
                    g => g.OrderBy(c => StrictUtf8.GetBytes(c.SourceObjectPath), BytewiseUtf8Comparer.Instance).ToList());
            void VisitComp(ulong parentId)
            {
                if (!childrenOf.TryGetValue(parentId, out var children)) return;
                foreach (var child in children)
                {
                    result.Add(child);
                    VisitComp(child.ComponentId);
                }
            }
            VisitComp(0);
        }
        return result;
    }

    private static List<TransformRecord> SortTransformsByOwner(List<TransformRecord> transforms, List<ActorRecord> actors, List<ComponentRecord> components, List<LightRecord> lights)
    {
        if (transforms.Count <= 1) return transforms;
        var idToTransform = transforms.ToDictionary(t => t.TransformId);
        var seen = new HashSet<ulong>();
        var orderedIds = new List<ulong>(transforms.Count);
        foreach (var actor in actors)
        {
            if (seen.Add(actor.LocalTransformId))
                orderedIds.Add(actor.LocalTransformId);
        }
        foreach (var comp in components)
        {
            if (seen.Add(comp.LocalTransformId))
                orderedIds.Add(comp.LocalTransformId);
        }
        foreach (var light in lights)
        {
            if (seen.Add(light.TransformId))
                orderedIds.Add(light.TransformId);
        }
        foreach (var t in transforms)
        {
            if (seen.Add(t.TransformId))
                orderedIds.Add(t.TransformId);
        }
        return orderedIds.Select(id => idToTransform[id]).ToList();
    }

    private static List<MeshLodRecord> CanonicalizeLods(List<MeshLodRecord> lods, List<AssetRecord> assets)
    {
        if (lods.Count <= 1) return lods;
        var result = new List<MeshLodRecord>(lods.Count);
        foreach (var asset in assets)
        {
            if (asset.LodCount == 0) continue;
            var assetLods = lods.Skip(checked((int) asset.LodStart)).Take(checked((int) asset.LodCount))
                .OrderBy(l => l.LodIndex).ToList();
            result.AddRange(assetLods);
        }
        var offset = 0;
        for (var i = 0; i < assets.Count; i++)
        {
            var count = assets[i].Kind == AssetKind.StaticMesh ? assets[i].LodCount : 0;
            assets[i] = assets[i] with { LodStart = (uint) offset, LodCount = count };
            offset += checked((int) count);
        }
        return result;
    }

    private static void ValidateLodRangesBeforeSort(List<AssetRecord> assets, List<MeshLodRecord> lods)
    {
        var used = new HashSet<uint>();
        foreach (var asset in assets)
        {
            if (asset.Kind != AssetKind.StaticMesh) continue;
            if ((ulong) asset.LodStart + asset.LodCount > (ulong) lods.Count)
                throw new InvalidDataException($"Asset {asset.AssetId} LOD table range out of bounds.");
            for (uint i = 0; i < asset.LodCount; i++)
                if (!used.Add(asset.LodStart + i))
                    throw new InvalidDataException("Asset LOD table ranges overlap.");
        }
    }

    private static Dictionary<ulong, List<MeshLodRecord>> CaptureLodOwnership(
        List<AssetRecord> assets, List<MeshLodRecord> lods)
    {
        var captures = new Dictionary<ulong, List<MeshLodRecord>>();
        var owned = new bool[lods.Count];
        foreach (var asset in assets)
        {
            if (asset.Kind != AssetKind.StaticMesh) continue;
            if ((ulong) asset.LodStart + asset.LodCount > (ulong) lods.Count)
                throw new InvalidDataException($"Asset {asset.AssetId} LOD table range out of bounds.");

            var captured = new List<MeshLodRecord>(checked((int) asset.LodCount));
            for (var i = 0; i < asset.LodCount; i++)
            {
                var index = checked((int) (asset.LodStart + i));
                if (owned[index]) throw new InvalidDataException("Asset LOD table ranges overlap.");
                owned[index] = true;
                var lod = lods[index];
                captured.Add(lod);
            }
            captures.Add(asset.AssetId, captured);
        }
        if (owned.Any(value => !value))
            throw new InvalidDataException("Every LOD record must be owned by exactly one static mesh asset.");
        return captures;
    }

    private static Dictionary<ulong, string> BuildTransformOwnerPaths(
        List<ActorRecord> actors, List<ComponentRecord> components, List<LightRecord> lights, List<TransformRecord> transforms)
    {
        var ownerPaths = new Dictionary<ulong, string>();
        void AddOwner(ulong transformId, string sourcePath)
        {
            if (transformId == 0 || !ownerPaths.TryAdd(transformId, sourcePath))
                throw new InvalidDataException($"Transform {transformId} must be owned by exactly one actor or component.");
        }
        foreach (var actor in actors) AddOwner(actor.LocalTransformId, actor.SourceObjectPath);
        foreach (var component in components) AddOwner(component.LocalTransformId, component.SourceObjectPath);
        // Legacy documents may share a component transform with a light; only light-only transforms need a new owner.
        foreach (var light in lights)
            if (!ownerPaths.ContainsKey(light.TransformId)) AddOwner(light.TransformId, light.SourceObjectPath);
        ValidateSourceUniqueness("Transform owner", ownerPaths.Values.Select(path => path + "\0local"));
        if (ownerPaths.Count != transforms.Count)
            throw new InvalidDataException("Every transform record must be owned by exactly one actor or component.");
        foreach (var transform in transforms)
            if (!ownerPaths.ContainsKey(transform.TransformId))
                throw new InvalidDataException($"Transform {transform.TransformId} has no owner.");
        return ownerPaths;
    }

    private static Dictionary<ulong, string> BuildInstanceSetOwners(
        List<ComponentRecord> components, List<InstanceSetRecord> instanceSets)
    {
        var componentsById = components.ToDictionary(component => component.ComponentId);
        var owners = new Dictionary<ulong, string>();
        foreach (var set in instanceSets)
        {
            if (!componentsById.TryGetValue(set.ComponentId, out var component)
                || !owners.TryAdd(set.InstanceSetId, component.SourceObjectPath))
            {
                throw new InvalidDataException($"Instance set {set.InstanceSetId} must be owned by exactly one component.");
            }
        }
        ValidateSourceUniqueness("Instance set owner", owners.Values);
        return owners;
    }

    private static (List<InstanceSetRecord> sets, List<InstanceTransform> transforms) CanonicalizeInstanceSets(
        List<InstanceSetRecord> sets, List<InstanceTransform> transforms, List<ComponentRecord> components)
    {
        ValidateInstanceTransformOwnership(sets, transforms.Count);
        var componentOrder = new Dictionary<ulong, int>();
        for (var i = 0; i < components.Count; i++)
            componentOrder[components[i].ComponentId] = i;
        var sortedSets = sets.OrderBy(s =>
            componentOrder.TryGetValue(s.ComponentId, out var order) ? order : int.MaxValue).ToList();
        var idToSet = sets.ToDictionary(s => s.InstanceSetId);
        var newTransforms = new List<InstanceTransform>(transforms.Count);
        foreach (var set in sortedSets)
        {
            var orig = idToSet[set.InstanceSetId];
            newTransforms.AddRange(transforms.Skip(checked((int) orig.TransformStart)).Take(checked((int) orig.TransformCount)));
        }
        var resultSets = new List<InstanceSetRecord>(sortedSets.Count);
        var offset = 0;
        foreach (var set in sortedSets)
        {
            var orig = idToSet[set.InstanceSetId];
            resultSets.Add(set with { TransformStart = (uint) offset, TransformCount = orig.TransformCount });
            offset += checked((int) orig.TransformCount);
        }
        return (resultSets, newTransforms);
    }

    private static void ValidateInstanceTransformOwnership(List<InstanceSetRecord> sets, int transformCount)
    {
        var owned = new bool[transformCount];
        foreach (var set in sets)
        {
            var start = checked((int) set.TransformStart);
            var count = checked((int) set.TransformCount);
            if (start < 0 || count < 0 || start > transformCount - count)
                throw new InvalidDataException($"InstanceSet {set.InstanceSetId} transform range out of bounds.");
            for (var i = start; i < start + count; i++)
            {
                if (owned[i])
                    throw new InvalidDataException("Every instance transform must be owned by exactly one instance set.");
                owned[i] = true;
            }
        }
        if (owned.Any(isOwned => !isOwned))
            throw new InvalidDataException("Every instance transform must be owned by exactly one instance set.");
    }

    private static List<MaterialOverrideRecord> CanonicalizeMaterialOverrides(
        List<MaterialOverrideRecord> overrides, List<ComponentRecord> components)
    {
        if (overrides.Count <= 1) return overrides;
        var componentOrder = new Dictionary<ulong, int>();
        for (var i = 0; i < components.Count; i++)
            componentOrder[components[i].ComponentId] = i;
        return overrides
            .OrderBy(o => componentOrder.TryGetValue(o.ComponentId, out var order) ? order : int.MaxValue)
            .ThenBy(o => o.SlotOrdinal).ToList();
    }

    private static List<LightRecord> CanonicalizeLights(
        List<LightRecord> lights, List<ActorRecord> actors, List<ComponentRecord> components, List<TransformRecord> transforms)
    {
        if (lights.Count <= 1) return lights.Select(CanonicalizeLight).ToList();
        var actorOrder = actors.Select((actor, index) => (actor.ActorId, index)).ToDictionary(x => x.ActorId, x => x.index);
        var componentOrder = components.Select((component, index) => (component.ComponentId, index)).ToDictionary(x => x.ComponentId, x => x.index);
        var transformOrder = transforms.Select((transform, index) => (transform.TransformId, index)).ToDictionary(x => x.TransformId, x => x.index);
        return lights.Select(CanonicalizeLight)
            .OrderBy(light => actorOrder[light.ActorId])
            .ThenBy(light => light.ParentComponentId == 0 ? -1 : componentOrder[light.ParentComponentId])
            .ThenBy(light => transformOrder[light.TransformId])
            .ThenBy(light => light.LightId)
            .ToList();
    }

    private static LightRecord CanonicalizeLight(LightRecord light) => light with
    {
        Intensity = CanonicalizeNegZero(light.Intensity),
        AttenuationRadius = CanonicalizeNegZero(light.AttenuationRadius),
        FalloffExponent = CanonicalizeNegZero(light.FalloffExponent),
        InnerConeAngleDegrees = CanonicalizeNegZero(light.InnerConeAngleDegrees),
        OuterConeAngleDegrees = CanonicalizeNegZero(light.OuterConeAngleDegrees),
        RectWidth = CanonicalizeNegZero(light.RectWidth),
        RectHeight = CanonicalizeNegZero(light.RectHeight),
        LightSourceAngle = CanonicalizeNegZero(light.LightSourceAngle),
        LightSourceSoftAngle = CanonicalizeNegZero(light.LightSourceSoftAngle),
        SourceRadius = CanonicalizeNegZero(light.SourceRadius),
        SoftSourceRadius = CanonicalizeNegZero(light.SoftSourceRadius),
        SourceLength = CanonicalizeNegZero(light.SourceLength),
        BarnDoorAngle = CanonicalizeNegZero(light.BarnDoorAngle),
        BarnDoorLength = CanonicalizeNegZero(light.BarnDoorLength),
        Reserved = 0,
    };

    internal static void ValidateLights(List<LightRecord> lights, List<ActorRecord> actors, List<ComponentRecord> components, List<TransformRecord> transforms)
    {
        var actorIds = actors.Select(actor => actor.ActorId).ToHashSet();
        var componentsById = components.ToDictionary(component => component.ComponentId);
        var transformIds = transforms.Select(transform => transform.TransformId).ToHashSet();
        foreach (var light in lights)
        {
            if (light.Name is null || light.ClassPath is null || light.SourceObjectPath is null)
                throw new InvalidDataException("Light string fields cannot be null.");
            ValidateSourceObjectPathField(light.SourceObjectPath, $"Light {light.LightId}");
            ValidateUtf8String(light.Name);
            ValidateUtf8String(light.ClassPath);
            if (!actorIds.Contains(light.ActorId)) throw new InvalidDataException($"Light {light.LightId} references missing actor.");
            if (light.ParentComponentId != 0 && (!componentsById.TryGetValue(light.ParentComponentId, out var parent) || parent.ActorId != light.ActorId)) throw new InvalidDataException($"Light {light.LightId} parent component missing or belongs to another actor.");
            if (!transformIds.Contains(light.TransformId)) throw new InvalidDataException($"Light {light.LightId} transform missing.");
            if (!Enum.IsDefined(light.LightType) || !Enum.IsDefined(light.ColorEncoding) || !Enum.IsDefined(light.LightUnits))
                throw new InvalidDataException($"Light {light.LightId} enum invalid.");
            if ((light.Flags & ~LightFlags.AllowedMask) != 0 || light.Reserved != 0)
                throw new InvalidDataException($"Light {light.LightId} flags or reserved invalid.");
            var parameters = new[] { light.Intensity, light.AttenuationRadius, light.FalloffExponent, light.InnerConeAngleDegrees, light.OuterConeAngleDegrees, light.RectWidth, light.RectHeight, light.LightSourceAngle, light.LightSourceSoftAngle, light.SourceRadius, light.SoftSourceRadius, light.SourceLength, light.BarnDoorAngle, light.BarnDoorLength };
            if (parameters.Any(value => !float.IsFinite(value) || value < 0)) throw new InvalidDataException($"Light {light.LightId} physical value invalid.");
            var hasAttenuation = (light.Flags & LightFlags.HasAttenuation) != 0;
            var hasSpot = (light.Flags & LightFlags.HasSpotAngles) != 0;
            var hasRect = (light.Flags & LightFlags.HasRectDimensions) != 0;
            var inverseSquared = (light.Flags & LightFlags.UsesInverseSquaredFalloff) != 0;
            if (hasAttenuation && light.LightType is not (LightType.Point or LightType.Spot or LightType.Rect)) throw new InvalidDataException($"Light {light.LightId} attenuation flags/type mismatch.");
            if (!hasAttenuation && light.LightType is LightType.Point or LightType.Spot) throw new InvalidDataException($"Light {light.LightId} attenuation flags/type mismatch.");
            if (hasSpot != (light.LightType == LightType.Spot)) throw new InvalidDataException($"Light {light.LightId} spot flags/type mismatch.");
            if (hasRect != (light.LightType == LightType.Rect)) throw new InvalidDataException($"Light {light.LightId} rect flags/type mismatch.");
            if (inverseSquared && light.LightType is not (LightType.Point or LightType.Spot)) throw new InvalidDataException($"Light {light.LightId} inverse-square flags/type mismatch.");
            if (!hasAttenuation && (light.AttenuationRadius != 0 || light.FalloffExponent != 0)) throw new InvalidDataException($"Light {light.LightId} unused attenuation parameters must be zero.");
            if (!hasSpot && (light.InnerConeAngleDegrees != 0 || light.OuterConeAngleDegrees != 0)) throw new InvalidDataException($"Light {light.LightId} unused spot angles must be zero.");
            if (hasSpot && light.InnerConeAngleDegrees > light.OuterConeAngleDegrees) throw new InvalidDataException($"Light {light.LightId} inner cone exceeds outer cone.");
            if (!hasRect && (light.RectWidth != 0 || light.RectHeight != 0)) throw new InvalidDataException($"Light {light.LightId} unused rect dimensions must be zero.");
            var directional = light.LightType == LightType.Directional;
            var pointOrSpot = light.LightType is LightType.Point or LightType.Spot;
            if (!directional && (light.LightSourceAngle != 0 || light.LightSourceSoftAngle != 0)) throw new InvalidDataException($"Light {light.LightId} unused directional source angles must be zero.");
            if (!pointOrSpot && (light.SourceRadius != 0 || light.SoftSourceRadius != 0 || light.SourceLength != 0)) throw new InvalidDataException($"Light {light.LightId} unused point/spot source dimensions must be zero.");
            if (!hasRect && (light.BarnDoorAngle != 0 || light.BarnDoorLength != 0)) throw new InvalidDataException($"Light {light.LightId} unused rect barn doors must be zero.");
        }
    }

    private static List<DiagnosticRecord> CanonicalizeDiagnostics(List<DiagnosticRecord> diagnostics)
    {
        if (diagnostics.Count <= 1) return diagnostics;
        return diagnostics
            .OrderBy(d => d.SubjectKind)
            .ThenBy(d => d.SubjectId)
            .ThenBy(d => d.Severity)
            .ThenBy(d => d.Code)
            .ThenBy(d => StrictUtf8.GetBytes(d.SourceObjectPath), BytewiseUtf8Comparer.Instance)
            .ThenBy(d => StrictUtf8.GetBytes(d.Message), BytewiseUtf8Comparer.Instance).ToList();
    }

    private static List<BundleEntry> CanonicalizeBundleEntries(List<BundleEntry> bundleEntries)
    {
        if (bundleEntries.Count <= 1) return bundleEntries;
        return bundleEntries
            .OrderBy(entry => entry.ResourceKind)
            .ThenBy(entry => StrictUtf8.GetBytes(entry.LogicalUri), BytewiseUtf8Comparer.Instance)
            .ThenBy(entry => entry.OwnerAssetId)
            .ThenBy(entry => entry.LodIndex)
            .ToList();
    }

    private static void RecomputeAllRanges(
        List<AssetRecord> assets, List<MeshLodRecord> lods,
        List<ActorRecord> actors, List<ComponentRecord> components,
        List<MaterialOverrideRecord> materialOverrides,
        List<DiagnosticRecord> diagnostics,
        List<InstanceSetRecord> instanceSets,
        List<BundleEntry> bundleEntries)
    {
        // Asset.LodStart/LodCount — preserves original per-asset count, reassigns start.
        {
            var lodOff = 0;
            for (var i = 0; i < assets.Count; i++)
            {
                var a = assets[i];
                if (a.Kind != AssetKind.StaticMesh) { assets[i] = a with { LodStart = 0, LodCount = 0 }; continue; }
                var c = a.LodCount;
                assets[i] = a with { LodStart = (uint) lodOff, LodCount = c };
                lodOff += checked((int) c);
            }
        }

        // Actor.ComponentStart/Count
        {
            var byActor = new Dictionary<ulong, List<int>>();
            for (var i = 0; i < components.Count; i++)
            {
                if (!byActor.TryGetValue(components[i].ActorId, out var list)) { list = new(); byActor[components[i].ActorId] = list; }
                list.Add(i);
            }
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (byActor.TryGetValue(a.ActorId, out var list))
                    actors[i] = a with { ComponentStart = (uint) list[0], ComponentCount = (uint) list.Count };
                else
                    actors[i] = a with { ComponentStart = 0, ComponentCount = 0 };
            }
        }

        // Component.MaterialOverrideStart/Count
        {
            var byComp = new Dictionary<ulong, List<int>>();
            for (var i = 0; i < materialOverrides.Count; i++)
            {
                if (!byComp.TryGetValue(materialOverrides[i].ComponentId, out var list)) { list = new(); byComp[materialOverrides[i].ComponentId] = list; }
                list.Add(i);
            }
            for (var i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (byComp.TryGetValue(c.ComponentId, out var list))
                    components[i] = c with { MaterialOverrideStart = (uint) list[0], MaterialOverrideCount = (uint) list.Count };
                else
                    components[i] = c with { MaterialOverrideStart = 0, MaterialOverrideCount = 0 };
            }
        }

        // Diagnostic ownership ranges after DIAG canonical sort for all owning subject types.
        // Scene (0) is unowned. InstanceSet (4) has no range fields on the record.
        // MATO (5) / BNDX (6) use deterministic unique record identity as SubjectId.
        {
            var diagByOwner = new Dictionary<(byte kind, ulong id), List<int>>();
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var d = diagnostics[i];
                if (d.SubjectKind == 0) continue;
                var key = (d.SubjectKind, d.SubjectId);
                if (!diagByOwner.TryGetValue(key, out var list)) { list = new(); diagByOwner[key] = list; }
                list.Add(i);
            }

            for (var i = 0; i < assets.Count; i++)
            {
                var a = assets[i];
                if (diagByOwner.TryGetValue((1, a.AssetId), out var list))
                    assets[i] = a with { DiagnosticStart = (uint) list[0], DiagnosticCount = (uint) list.Count };
                else
                    assets[i] = a with { DiagnosticStart = 0, DiagnosticCount = 0 };
            }

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (diagByOwner.TryGetValue((2, a.ActorId), out var list))
                    actors[i] = a with { DiagnosticStart = (uint) list[0], DiagnosticCount = (uint) list.Count };
                else
                    actors[i] = a with { DiagnosticStart = 0, DiagnosticCount = 0 };
            }

            for (var i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (diagByOwner.TryGetValue((3, c.ComponentId), out var list))
                    components[i] = c with { DiagnosticStart = (uint) list[0], DiagnosticCount = (uint) list.Count };
                else
                    components[i] = c with { DiagnosticStart = 0, DiagnosticCount = 0 };
            }

            // InstanceSet (kind 4): no DiagnosticStart/Count on InstanceSetRecord; identity is InstanceSetId.
            _ = instanceSets;

            for (var i = 0; i < materialOverrides.Count; i++)
            {
                var m = materialOverrides[i];
                var subjectId = MaterialOverrideSubjectId(m.ComponentId, m.SlotOrdinal);
                if (diagByOwner.TryGetValue((5, subjectId), out var list))
                    materialOverrides[i] = m with { DiagnosticStart = (uint) list[0], DiagnosticCount = (uint) list.Count };
                else
                    materialOverrides[i] = m with { DiagnosticStart = 0, DiagnosticCount = 0 };
            }

            for (var i = 0; i < bundleEntries.Count; i++)
            {
                var e = bundleEntries[i];
                var subjectId = BundleEntrySubjectId(e.ResourceKind, e.LogicalUri, e.OwnerAssetId, e.LodIndex);
                if (diagByOwner.TryGetValue((6, subjectId), out var list))
                    bundleEntries[i] = e with { DiagnosticStart = (uint) list[0], DiagnosticCount = (uint) list.Count };
                else
                    bundleEntries[i] = e with { DiagnosticStart = 0, DiagnosticCount = 0 };
            }
        }
    }
}

public static class Crc32IsoHdlc
{
    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }
}
