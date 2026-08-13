using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.Lights;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using System.Security.Cryptography;

namespace CUE4Parse_Conversion.UEScene;

/// <summary>Builds a semantic scene plan only; publication and resource bytes are deliberately out of scope.</summary>
public sealed class UESceneBundleExporter
{
    private readonly UESceneBundlePlan? _plan;
    private readonly Func<UEScenePlannedResource, CancellationToken, UESceneProducedResource>? _resourceProducer;
    private readonly Func<UEScenePublicationContext, CancellationToken, IReadOnlyList<UEScenePublicationArtifact>>? _publicationArtifactProducer;

    public UESceneBundleExporter() { }
    public UESceneBundleExporter(
        UESceneBundlePlan plan,
        Func<UEScenePlannedResource, CancellationToken, UESceneProducedResource> resourceProducer,
        Func<UEScenePublicationContext, CancellationToken, IReadOnlyList<UEScenePublicationArtifact>>? publicationArtifactProducer = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _resourceProducer = resourceProducer ?? throw new ArgumentNullException(nameof(resourceProducer));
        _publicationArtifactProducer = publicationArtifactProducer;
    }

    // Test-only seam for exercising rollback after a prior target was promoted.
    internal Func<int, bool>? FinalResourcePromotionFailure { get; init; }
    // Test-only seam for cancellation at a resource-promotion boundary.
    internal Action<int>? BeforeFinalResourcePromotion { get; init; }
    // Test-only seam for exercising rollback after all sidecars were promoted.
    internal bool FinalScenePromotionFailure { get; init; }
    // Test-only seams for verifying that recovery failures remain observable.
    internal Func<string, string?, Exception?>? RollbackFailure { get; init; }
    internal Func<DirectoryInfo, Exception?>? StageCleanupFailure { get; init; }
    // Test seam for platforms where creating a junction requires elevated privileges.
    internal Func<string, FileAttributes?>? PathAttributes { get; init; }

    public SceneExportResult TryWriteToDir(DirectoryInfo bundleRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleRoot);
        if (_plan is null || _resourceProducer is null)
            throw new InvalidOperationException("A scene plan and resource producer are required for publication.");
        try
        {
            return TryWriteToDestination(new UEScenePublicationDestination(
                Path.Combine(bundleRoot.FullName, "Content", SceneRelativeDirectory(_plan.Document.Metadata.SourceWorldPath), SceneFileName(_plan.Document.Metadata.SourceWorldPath)),
                bundleRoot.FullName,
                UESceneResourceLogicalRoot.GameExportTree, UESceneResourcePublicationPolicy.ReuseIdenticalOnly), cancellationToken);
        }
        catch (Exception ex)
        {
            var failure = ClassifyFailure(ex, false);
            return new(false, null, failure.Message, Failure: failure, StructuredRecoveryErrors: []);
        }
    }

    /// <summary>Publishes a scene to an explicit path while resolving resources below the selected logical root.</summary>
    public SceneExportResult TryWriteToDestination(UEScenePublicationDestination destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_plan is null || _resourceProducer is null)
            throw new InvalidOperationException("A scene plan and resource producer are required for publication.");

        var bundleRoot = new DirectoryInfo(destination.ResourcePhysicalRoot);
        var scenePath = "";
        DirectoryInfo? stage = null;
        var promoted = new List<(string finalPath, string? backupPath)>();
        SceneExportResult result;
        var retainStageForRecovery = false;
        var promotionStarted = false;
        try
        {
            ValidateDestinationLogicalRoot(destination);
            UESceneExportDestination.ValidateCanonicalPlacement(destination.ResourcePhysicalRoot, destination.ScenePhysicalPath);
            EnsureBundleRoot(bundleRoot);
            scenePath = ResolveContainedPath(bundleRoot, destination.ScenePhysicalPath);
            cancellationToken.ThrowIfCancellationRequested();
            var staticAssets = _plan.Document.Assets!.Where(asset => asset.Kind == AssetKind.StaticMesh).ToDictionary(asset => asset.AssetId);
            var planned = _plan.Resources.ToDictionary(resource => resource.CanonicalUnrealPath, StringComparer.Ordinal);
            var producedResources = new Dictionary<string, UESceneProducedResource>(StringComparer.Ordinal);
            foreach (var entry in _plan.Document.BundleEntries!.Where(entry => entry.ResourceKind == (byte) BundleResourceKind.Model))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = staticAssets[entry.OwnerAssetId];
                var resource = planned[asset.CanonicalUnrealPath];
                var produced = _resourceProducer(resource, cancellationToken);
                if (!string.Equals(produced.LogicalUri, resource.LogicalUri, StringComparison.Ordinal))
                    throw new InvalidDataException($"Produced logical URI '{produced.LogicalUri}' does not match planned URI '{resource.LogicalUri}'.");
                producedResources.Add(entry.LogicalUri, produced);
                var bytes = produced.Bytes;
                if (bytes is null || bytes.Length == 0) throw new InvalidDataException($"Required resource '{resource.LogicalUri}' has no bytes.");
                if (produced.Lods.Count == 0) throw new InvalidDataException($"Required resource '{resource.LogicalUri}' has no declared LODs.");
                if (produced.Lods.Select(lod => lod.SourceLodIndex).Distinct().Count() != produced.Lods.Count)
                    throw new InvalidDataException($"Required resource '{resource.LogicalUri}' declares duplicate source LOD indices.");
            }

            ValidateSidecars(producedResources.Values.SelectMany(resource => resource.Sidecars), bundleRoot);
            // Reconciliation may remove mismatched overrides, so validate the original graph first.
            _ = UESceneWriter.Write(_plan.Document);
            var reconciledDocument = ReconcileMaterialOverrideLinks(_plan.Document, producedResources);
            ValidateFinalResources(producedResources.Values.Select(resource =>
                    new UESceneFinalResource((byte) BundleResourceKind.Model, 0, resource.LogicalUri, "", resource.Bytes))
                .Concat(producedResources.Values.SelectMany(resource => resource.Sidecars)
                    .GroupBy(sidecar => sidecar.CanonicalUnrealPath, StringComparer.Ordinal)
                    .Select(group => new UESceneFinalResource((byte) group.First().Kind, 0, group.First().LogicalUri,
                        group.First().Mime, group.First().Bytes))), bundleRoot, scenePath);
            // Freeze every model and its sealed manifest before staging any bundle bytes.
            stage = CreateDirectory(bundleRoot, Path.Combine(bundleRoot.FullName, ".uescene-stage-" + Path.GetRandomFileName()));
            var staged = new Dictionary<string, (string stagedPath, byte[] sha256, ulong length, IReadOnlyList<UESceneProducedLod> lods)>(StringComparer.Ordinal);
            foreach (var entry in _plan.Document.BundleEntries!.Where(entry => entry.ResourceKind == (byte) BundleResourceKind.Model))
            {
                var produced = producedResources[entry.LogicalUri];
                var bytes = produced.Bytes;
                var stagedPath = StagePath(stage, entry.LogicalUri);
                File.WriteAllBytes(stagedPath, bytes);
                var verified = File.ReadAllBytes(stagedPath);
                if (verified.Length != bytes.Length || !SHA256.HashData(verified).SequenceEqual(SHA256.HashData(bytes)))
                    throw new IOException($"Failed to verify staged resource '{entry.LogicalUri}'.");
                staged.Add(entry.LogicalUri, (stagedPath, SHA256.HashData(verified), checked((ulong) verified.Length), produced.Lods));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var document = FinalizeDocument(reconciledDocument, staged, producedResources);
            var finalPlan = CreateFinalPlan(document, producedResources);
            ValidateFinalResources(finalPlan.Resources, bundleRoot, scenePath);
            foreach (var resource in finalPlan.Resources.Where(resource => resource.ResourceKind != (byte) BundleResourceKind.Model))
            {
                var stagedPath = StagePath(stage, resource.LogicalUri);
                File.WriteAllBytes(stagedPath, resource.Bytes);
                var verified = File.ReadAllBytes(stagedPath);
                if (verified.Length != resource.Bytes.Length || !SHA256.HashData(verified).SequenceEqual(SHA256.HashData(resource.Bytes)))
                    throw new IOException($"Failed to verify staged resource '{resource.LogicalUri}'.");
                staged.Add(resource.LogicalUri, (stagedPath, SHA256.HashData(verified), checked((ulong) verified.Length), []));
            }
            // Rebuild all ASST/BNDX facts from the bytes reread from staging.
            document = FinalizeDocument(reconciledDocument, staged, producedResources);
            finalPlan = CreateFinalPlan(document, producedResources);
            var sceneBytes = UESceneWriter.Write(document);
            // The writer assigns canonical serialized IDs. Read the staged wire form before
            // generating additional publication artifacts so their metadata and the returned
            // result describe exactly the same identity graph Blender will import.
            document = UESceneReader.Read(sceneBytes);
            finalPlan = CreateFinalPlan(document, producedResources);
            var stagedScenePath = StagePath(stage, Path.GetRelativePath(bundleRoot.FullName, scenePath));
            File.WriteAllBytes(stagedScenePath, sceneBytes);
            var verifiedScene = File.ReadAllBytes(stagedScenePath);
            if (verifiedScene.Length != sceneBytes.Length || !SHA256.HashData(verifiedScene).SequenceEqual(SHA256.HashData(sceneBytes)))
                throw new IOException("Failed to verify staged scene.");

            var provisionalResult = CreatePublishedResult(scenePath, document, finalPlan);
            var stagedPublicationArtifacts = StagePublicationArtifacts(stage, bundleRoot, scenePath,
                provisionalResult, sceneBytes, finalPlan.Resources, cancellationToken);

            var rollback = CreateDirectory(stage, Path.Combine(stage.FullName, ".rollback"));
            var reusableExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (destination.ResourcePublicationPolicy == UESceneResourcePublicationPolicy.ReuseIdenticalOnly)
            {
                foreach (var item in staged)
                {
                    var finalPath = ResolvePath(bundleRoot, item.Key);
                    if (!File.Exists(finalPath)) continue;
                    if (!SHA256.HashData(File.ReadAllBytes(finalPath)).SequenceEqual(item.Value.sha256))
                        throw new IOException($"Existing resource '{item.Key}' differs from the staged export.");
                    reusableExisting.Add(finalPath);
                }
            }
            var promotionIndex = 0;
            promotionStarted = true;
            foreach (var item in staged)
            {
                BeforeFinalResourcePromotion?.Invoke(promotionIndex);
                cancellationToken.ThrowIfCancellationRequested();
                var finalPath = FinalPath(bundleRoot, item.Key);
                if (reusableExisting.Contains(finalPath))
                {
                    promotionIndex++;
                    continue;
                }
                string? backupPath = null;
                if (File.Exists(finalPath))
                {
                    backupPath = StagePath(rollback, Path.GetRelativePath(bundleRoot.FullName, finalPath));
                    File.Move(finalPath, backupPath);
                }
                promoted.Add((finalPath, backupPath));
                if (FinalResourcePromotionFailure?.Invoke(promotionIndex) == true)
                    throw new IOException($"Failed to promote staged resource '{item.Key}'.");
                File.Move(item.Value.stagedPath, finalPath, true);
                promotionIndex++;
            }

            foreach (var artifact in stagedPublicationArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finalPath = FinalPath(bundleRoot, artifact.RelativePath);
                string? backupPath = null;
                if (File.Exists(finalPath))
                {
                    backupPath = StagePath(rollback, Path.GetRelativePath(bundleRoot.FullName, finalPath));
                    File.Move(finalPath, backupPath);
                }
                promoted.Add((finalPath, backupPath));
                File.Move(artifact.StagedPath, finalPath, true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CreateDirectory(bundleRoot, Path.GetDirectoryName(scenePath)!);
            string? sceneBackupPath = null;
            if (File.Exists(scenePath))
            {
                sceneBackupPath = StagePath(rollback, Path.GetRelativePath(bundleRoot.FullName, scenePath));
                File.Move(scenePath, sceneBackupPath);
            }
            promoted.Add((scenePath, sceneBackupPath));
            if (FinalScenePromotionFailure)
                throw new IOException("Failed to promote staged scene.");
            File.Move(stagedScenePath, scenePath, true);
            document = UESceneReader.Read(File.ReadAllBytes(scenePath));
            finalPlan = CreateFinalPlan(document, producedResources);
            result = provisionalResult;
        }
        catch (Exception ex)
        {
            var recoveryFailures = new List<SceneExportError>();
            foreach (var (finalPath, backupPath) in promoted.AsEnumerable().Reverse())
            {
                try
                {
                    var injectedFailure = RollbackFailure?.Invoke(finalPath, backupPath);
                    if (injectedFailure is not null) throw injectedFailure;
                    if (backupPath is null) File.Delete(finalPath);
                    else File.Move(backupPath, finalPath, true);
                }
                catch (Exception rollbackEx)
                {
                    recoveryFailures.Add(new(SceneExportErrorCode.RollbackFailed,
                        $"Rollback failed for '{finalPath}': {rollbackEx.Message}", rollbackEx, finalPath));
                }
            }
            retainStageForRecovery = recoveryFailures.Count > 0;
            var failure = ClassifyFailure(ex, promotionStarted);
            var error = string.Join(" ", new[] { failure.Message }.Concat(recoveryFailures.Select(item => item.Message)));
            result = new(false, null, error, retainStageForRecovery ? stage?.FullName : null,
                Failure: failure, StructuredRecoveryErrors: recoveryFailures);
        }

        if (stage?.Exists == true && !retainStageForRecovery)
        {
            try
            {
                var injectedFailure = StageCleanupFailure?.Invoke(stage);
                if (injectedFailure is not null) throw injectedFailure;
                stage.Delete(true);
            }
            catch (Exception cleanupEx)
            {
                var cleanupError = new SceneExportError(SceneExportErrorCode.CleanupFailed,
                    $"Staging cleanup failed: {cleanupEx.Message}. Recovery staging retained at '{stage.FullName}'.",
                    cleanupEx, stage.FullName);
                result = result with
                {
                    Error = result.Error is null ? cleanupError.Message : $"{result.Error} {cleanupError.Message}",
                    RecoveryStagePath = stage.FullName,
                    StructuredRecoveryErrors = result.RecoveryErrors.Concat([cleanupError]).ToArray()
                };
            }
        }
        else if (retainStageForRecovery && stage is not null)
            result = result with { RecoveryStagePath = stage.FullName };

        return result;
    }

    private static SceneExportError ClassifyFailure(Exception exception, bool promotionStarted)
    {
        var code = exception switch
        {
            OperationCanceledException => SceneExportErrorCode.Cancelled,
            InvalidDataException or ArgumentException or KeyNotFoundException => SceneExportErrorCode.InvalidData,
            UnauthorizedAccessException => SceneExportErrorCode.AccessDenied,
            IOException ioException when IsDiskFull(ioException) => SceneExportErrorCode.DiskFull,
            _ when promotionStarted => SceneExportErrorCode.PromotionFailed,
            IOException => SceneExportErrorCode.IoFailure,
            _ => SceneExportErrorCode.Unexpected
        };
        return new(code, exception.Message, exception);
    }

    private static bool IsDiskFull(IOException exception)
    {
        var win32Code = exception.HResult & 0xffff;
        return win32Code is 39 or 112;
    }

    private SceneExportResult CreatePublishedResult(string scenePath, UESceneDocument document, UESceneFinalBundlePlan finalPlan) =>
        new(true, scenePath, null, FinalDocument: document, FinalPlan: finalPlan,
            ActorCount: document.Actors?.Count ?? 0,
            ComponentCount: document.Components?.Count ?? 0,
            MeshCount: document.Assets?.Count(asset => asset.Kind == AssetKind.StaticMesh) ?? 0,
            InstanceCount: document.InstanceTransforms?.Count ?? 0,
            DiagnosticCount: document.Diagnostics?.Count ?? 0,
            ResourceCount: finalPlan.Resources.Count);

    private IReadOnlyList<StagedPublicationArtifact> StagePublicationArtifacts(
        DirectoryInfo stage,
        DirectoryInfo bundleRoot,
        string scenePath,
        SceneExportResult result,
        byte[] sceneBytes,
        IReadOnlyList<UESceneFinalResource> finalResources,
        CancellationToken cancellationToken)
    {
        if (_publicationArtifactProducer is null) return [];

        var artifacts = _publicationArtifactProducer(
            new UEScenePublicationContext(result, bundleRoot.FullName, scenePath, sceneBytes), cancellationToken)
            ?? throw new InvalidDataException("UEScene publication artifact producer returned null.");
        var reservedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(scenePath)
        };
        foreach (var resource in finalResources)
            reservedTargets.Add(ResolvePath(bundleRoot, resource.LogicalUri));

        var stagedArtifacts = new List<StagedPublicationArtifact>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.RelativePath))
                throw new InvalidDataException("UEScene publication artifact must have a safe relative path.");
            if (artifact.Bytes is null || artifact.Bytes.Length == 0)
                throw new InvalidDataException($"UEScene publication artifact '{artifact.RelativePath}' has no bytes.");

            var finalPath = ResolvePath(bundleRoot, artifact.RelativePath);
            if (!reservedTargets.Add(finalPath))
                throw new InvalidDataException($"UEScene publication artifact '{artifact.RelativePath}' aliases a scene or resource target.");

            var stagedPath = StagePath(stage, artifact.RelativePath);
            File.WriteAllBytes(stagedPath, artifact.Bytes);
            var verified = File.ReadAllBytes(stagedPath);
            if (verified.Length != artifact.Bytes.Length || !SHA256.HashData(verified).SequenceEqual(SHA256.HashData(artifact.Bytes)))
                throw new IOException($"Failed to verify staged publication artifact '{artifact.RelativePath}'.");
            stagedArtifacts.Add(new(artifact.RelativePath, stagedPath));
        }

        return stagedArtifacts;
    }

    private sealed record StagedPublicationArtifact(string RelativePath, string StagedPath);

    private void ValidateSidecars(IEnumerable<UESceneProducedSidecar> sidecars, DirectoryInfo bundleRoot)
    {
        var frozen = sidecars.ToArray();
        foreach (var group in frozen.GroupBy(ReconciliationIdentity, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Skip(1).Any(sidecar => sidecar.LogicalUri != first.LogicalUri || sidecar.Kind != first.Kind ||
                                              sidecar.Mime != first.Mime || !sidecar.Bytes.SequenceEqual(first.Bytes)))
                throw new InvalidDataException($"Conflicting sidecar declarations for canonical identity '{group.Key}'.");
        }
        foreach (var group in frozen.GroupBy(sidecar => sidecar.LogicalUri, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Skip(1).Any(sidecar => ReconciliationIdentity(sidecar) != ReconciliationIdentity(first) || sidecar.Kind != first.Kind ||
                                              sidecar.Mime != first.Mime || !sidecar.Bytes.SequenceEqual(first.Bytes)))
                throw new InvalidDataException($"Conflicting sidecar declarations for logical URI '{group.Key}'.");
            _ = ResolvePath(bundleRoot, first.LogicalUri);
        }
    }

    private static UESceneDocument ReconcileMaterialOverrideLinks(UESceneDocument document,
        IReadOnlyDictionary<string, UESceneProducedResource> producedResources)
    {
        var assets = document.Assets ?? [];
        var components = (document.Components ?? []).ToList();
        var overrides = (document.MaterialOverrides ?? []).ToList();
        var diagnostics = (document.Diagnostics ?? []).ToList();
        foreach (var producedDiagnostic in producedResources.Values.SelectMany(resource => resource.ExportDiagnostics))
            diagnostics.Add(new(producedDiagnostic.Severity, producedDiagnostic.SubjectKind, producedDiagnostic.Code, 0, 0,
                producedDiagnostic.SourceObjectPath, producedDiagnostic.Message, 0, 0, 0));
        var entries = document.BundleEntries ?? [];
        var assetsById = assets.ToDictionary(asset => asset.AssetId);
        var unlinkedOverrides = new HashSet<int>();
        foreach (var component in components)
        {
            var modelEntry = entries.SingleOrDefault(entry => entry.ResourceKind == (byte)BundleResourceKind.Model &&
                entry.OwnerAssetId == component.MeshAssetId);
            if (modelEntry is null || !producedResources.TryGetValue(modelEntry.LogicalUri, out var produced))
                continue;
            // Legacy/custom producers may expose opaque model bytes. The FModel
            // producer supplies this manifest and is therefore verified strictly.
            if (produced.MaterialSlotMappings is null)
                continue;
            var overrideStart = checked((int) component.MaterialOverrideStart);
            foreach (var offset in Enumerable.Range(overrideStart, checked((int) component.MaterialOverrideCount)))
            {
                var materialOverride = overrides[offset];
                if (materialOverride.OverrideMaterialAssetId == 0) continue;
                var expected = assetsById[materialOverride.OverrideMaterialAssetId].CanonicalUnrealPath;
                var mapped = produced.MaterialSlots.Where(slot => slot.SourceMaterialIndex == materialOverride.SlotOrdinal).ToArray();
                if (mapped.Length != 0 && mapped.All(slot => UnrealAssetIdentityEquals(slot.EffectiveMaterialPath, expected)))
                    continue;

                var producedPaths = mapped.Length == 0
                    ? "(no produced material mapping)"
                    : string.Join(", ", mapped.Select(slot => slot.EffectiveMaterialPath).Distinct(StringComparer.Ordinal));
                unlinkedOverrides.Add(offset);
                diagnostics.Add(new(2, 3, UESceneDiagnosticCodes.MaterialOverrideMismatch, 0, component.ComponentId,
                    component.SourceObjectPath,
                    $"Component {component.ComponentId} material override source slot {materialOverride.SlotOrdinal} declared '{expected}' conflicts with produced material '{producedPaths}' in model '{modelEntry.LogicalUri}'; the scene material reference was removed.",
                    0, 0, 0));
            }
        }
        if (unlinkedOverrides.Count == 0) return document with { Diagnostics = diagnostics };

        overrides = overrides.Where((_, index) => !unlinkedOverrides.Contains(index)).ToList();
        for (var index = 0; index < components.Count; index++)
        {
            var componentId = components[index].ComponentId;
            var owned = overrides.Select((materialOverride, overrideIndex) => (materialOverride, overrideIndex))
                .Where(item => item.materialOverride.ComponentId == componentId).Select(item => item.overrideIndex).ToArray();
            components[index] = components[index] with
            {
                MaterialOverrideStart = owned.Length == 0 ? 0u : checked((uint) owned[0]),
                MaterialOverrideCount = checked((uint) owned.Length)
            };
        }
        return document with { Components = components, MaterialOverrides = overrides, Diagnostics = diagnostics };
    }

    private static bool UnrealAssetIdentityEquals(string left, string right) =>
        TryNormalizeUnrealAssetIdentityForComparison(left, out var normalizedLeft) &&
        TryNormalizeUnrealAssetIdentityForComparison(right, out var normalizedRight) &&
        StringComparer.Ordinal.Equals(normalizedLeft, normalizedRight);

    private static bool TryNormalizeUnrealAssetIdentityForComparison(string path, out string normalized)
    {
        // Planning may retain raw Game/... or Engine/... forms while production emits canonical absolute paths.
        // Keep this normalization local to reconciliation so resource identities remain raw-domain.
        var comparisonPath = path.StartsWith("Game/", StringComparison.Ordinal) ||
            path.StartsWith("Engine/", StringComparison.Ordinal) ? "/" + path : path;
        return TryNormalizeUnrealAssetIdentity(comparisonPath, out normalized);
    }

    private static string NormalizeUnrealAssetIdentity(string path)
    {
        var slash = path.LastIndexOf('/');
        if (path.Length < 4 || path[0] != '/' || path[1] == '/' || slash <= 1)
            return path;

        var packageSegments = path[1..slash].Split('/');
        if (packageSegments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains('.')))
            return path;

        var terminal = path[(slash + 1)..];
        var dot = terminal.IndexOf('.');
        return dot < 0
            ? path
            : dot > 0 && dot == terminal.LastIndexOf('.') && dot < terminal.Length - 1 &&
              StringComparer.Ordinal.Equals(terminal[..dot], terminal[(dot + 1)..])
                ? path[..(slash + 1 + dot)]
                : path;
    }

    private static bool TryNormalizeUnrealAssetIdentity(string path, out string normalized)
    {
        normalized = NormalizeUnrealAssetIdentity(path);
        if (normalized.Length < 4 || normalized[0] != '/' || normalized[1] == '/' || normalized.LastIndexOf('/') <= 1)
            return false;

        var slash = normalized.LastIndexOf('/');
        var packageSegments = normalized[1..slash].Split('/');
        if (packageSegments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains('.')))
            return false;

        var terminal = normalized[(slash + 1)..];
        return terminal.Length > 0 && terminal.IndexOf('.') < 0;
    }

    private static UESceneDocument FinalizeDocument(UESceneDocument source, IReadOnlyDictionary<string, (string stagedPath, byte[] sha256, ulong length, IReadOnlyList<UESceneProducedLod> lods)> staged, IReadOnlyDictionary<string, UESceneProducedResource> producedResources)
    {
        var assets = source.Assets!.ToList();
        var entries = source.BundleEntries!.ToList();
        var diagnostics = source.Diagnostics!.ToList();
        var sidecars = producedResources.Values.SelectMany(resource => resource.Sidecars)
            .GroupBy(ReconciliationIdentity, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var producedMaterialPaths = sidecars
            .Where(sidecar => sidecar.Kind == BundleResourceKind.MaterialJson)
            .Select(ReconciliationIdentity)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            if (asset.Kind == AssetKind.Material && !producedMaterialPaths.Contains(asset.CanonicalUnrealPath))
            {
                assets[index] = asset with { Availability = Availability.Missing };
                diagnostics.Add(new(2, 1, UESceneDiagnosticCodes.MissingMaterialSidecar, 0, asset.AssetId,
                    asset.CanonicalUnrealPath, "Material override metadata was retained, but no material sidecar bytes were staged.", 0, 0, 0));
            }
        }
        var lods = new List<MeshLodRecord>();
        foreach (var index in Enumerable.Range(0, entries.Count).Where(index => entries[index].ResourceKind == (byte) BundleResourceKind.Model))
        {
            var entry = entries[index];
            var facts = staged[entry.LogicalUri];
            entries[index] = entry with { Sha256 = facts.sha256, ByteLength = facts.length };
            var assetIndex = assets.FindIndex(asset => asset.AssetId == entry.OwnerAssetId);
            var lodStart = checked((uint)lods.Count);
            foreach (var lod in facts.lods.OrderBy(lod => lod.SourceLodIndex))
                lods.Add(new MeshLodRecord(lod.SourceLodIndex, entry.LogicalUri, facts.sha256, facts.length,
                    0, 0, 0, 0, 0, 0, lod.SourceScreenSize, lod.Provenance));
            assets[assetIndex] = assets[assetIndex] with { Sha256 = facts.sha256, ByteLength = facts.length, LodStart = lodStart, LodCount = checked((uint)facts.lods.Count) };
        }
        var assetIds = assets.Count == 0 ? 1UL : assets.Max(asset => asset.AssetId) + 1;
        foreach (var sidecar in sidecars)
        {
            var assetKind = sidecar.Kind == BundleResourceKind.MaterialJson ? AssetKind.Material : AssetKind.Texture;
            var canonicalPath = ReconciliationIdentity(sidecar);
            var facts = staged.TryGetValue(sidecar.LogicalUri, out var stagedFacts)
                ? stagedFacts
                : (stagedPath: "", sha256: SHA256.HashData(sidecar.Bytes), length: checked((ulong) sidecar.Bytes.Length), lods: (IReadOnlyList<UESceneProducedLod>) []);
            var sha256 = facts.sha256;
            var length = facts.length;
            var assetIndex = assets.FindIndex(asset => asset.Kind == assetKind && asset.CanonicalUnrealPath == canonicalPath);
            ulong assetId;
            if (assetIndex >= 0)
            {
                assetId = assets[assetIndex].AssetId;
                assets[assetIndex] = assets[assetIndex] with
                {
                    Location = LocationMode.Bundle,
                    Availability = Availability.Available,
                    LogicalUri = sidecar.LogicalUri,
                    Mime = sidecar.Mime,
                    Sha256 = sha256,
                    ByteLength = length
                };
            }
            else
            {
                assetId = assetIds++;
                assets.Add(new(assetId, assetKind, LocationMode.Bundle, Availability.Available, canonicalPath,
                    LastName(canonicalPath), sidecar.LogicalUri, sidecar.Mime, sha256, length, 0, 0, 0, 0));
            }
            entries.Add(new((byte) sidecar.Kind, LocationMode.Bundle, Availability.Available, sidecar.LogicalUri,
                assetId, uint.MaxValue, sha256, length, sidecar.Mime, 0, 0));
        }
        return source with { Assets = assets, Lods = lods, Diagnostics = diagnostics, BundleEntries = entries };
    }

    // ResourceManifest retains its material: namespace; only scene reconciliation uses asset paths.
    private static string ReconciliationIdentity(UESceneProducedSidecar sidecar) =>
        sidecar.Kind == BundleResourceKind.MaterialJson && sidecar.CanonicalUnrealPath.StartsWith("material:", StringComparison.Ordinal)
            ? sidecar.CanonicalUnrealPath["material:".Length..]
            : sidecar.CanonicalUnrealPath;

    private static UESceneFinalBundlePlan CreateFinalPlan(UESceneDocument document, IReadOnlyDictionary<string, UESceneProducedResource> producedResources)
    {
        var resources = new List<UESceneFinalResource>();
        foreach (var entry in document.BundleEntries!)
        {
            if (entry.ResourceKind == (byte) BundleResourceKind.Model)
            {
                var model = producedResources[entry.LogicalUri];
                resources.Add(new(entry.ResourceKind, entry.OwnerAssetId, entry.LogicalUri, entry.Mime, model.Bytes.ToArray()));
                continue;
            }
            var sidecar = producedResources.Values.SelectMany(resource => resource.Sidecars)
                .First(sidecar => sidecar.LogicalUri == entry.LogicalUri && sidecar.Kind == (BundleResourceKind) entry.ResourceKind);
            resources.Add(new(entry.ResourceKind, entry.OwnerAssetId, entry.LogicalUri, entry.Mime, sidecar.Bytes.ToArray()));
        }
        return new(document, resources);
    }

    private void ValidateFinalResources(IEnumerable<UESceneFinalResource> resources, DirectoryInfo bundleRoot, string finalScenePath)
    {
        var pathComparer = StringComparer.OrdinalIgnoreCase;
        var finalSceneTarget = ResolveContainedPath(bundleRoot, finalScenePath);
        EnsureSafeDirectoryAncestors(bundleRoot, finalSceneTarget);
        var targets = new Dictionary<string, UESceneFinalResource>(pathComparer);
        foreach (var resource in resources)
        {
            var target = ResolvePath(bundleRoot, resource.LogicalUri);
            EnsureSafeDirectoryAncestors(bundleRoot, target);
            if (pathComparer.Equals(target, finalSceneTarget))
                throw new InvalidDataException($"Resource target '{resource.LogicalUri}' aliases the final scene target.");
            if (targets.TryGetValue(target, out var existing))
            {
                if (!string.Equals(resource.LogicalUri, existing.LogicalUri, StringComparison.Ordinal))
                    throw new InvalidDataException($"Conflicting final resources target resolved path '{target}'.");
                if (resource.ResourceKind != existing.ResourceKind || resource.Mime != existing.Mime || !resource.Bytes.SequenceEqual(existing.Bytes))
                    throw new InvalidDataException($"Conflicting final resources target resolved path '{target}'.");
                continue;
            }
            targets.Add(target, resource);
        }
    }

    private string FinalPath(DirectoryInfo root, string logicalUri)
    {
        var candidate = ResolvePath(root, logicalUri);
        CreateDirectory(root, Path.GetDirectoryName(candidate)!);
        return candidate;
    }

    private string ResolvePath(DirectoryInfo root, string logicalUri)
    {
        if (string.IsNullOrWhiteSpace(logicalUri) || logicalUri.Contains('\\') || Path.IsPathRooted(logicalUri))
            throw new InvalidDataException("Resource target must be a non-empty relative path.");
        var candidate = Path.GetFullPath(Path.Combine(root.FullName, logicalUri.Replace('/', Path.DirectorySeparatorChar)));
        var rootPath = Path.GetFullPath(root.FullName + Path.DirectorySeparatorChar);
        if (!candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Resource target escapes bundle root.");
        return candidate;
    }

    private string StagePath(DirectoryInfo root, string relativePath)
    {
        var path = ResolveContainedPath(root, relativePath);
        CreateDirectory(root, Path.GetDirectoryName(path)!);
        return path;
    }

    private string ResolveContainedPath(DirectoryInfo root, string path)
    {
        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root.FullName, path));
        var rootPath = Path.GetFullPath(root.FullName + Path.DirectorySeparatorChar);
        if (!candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Target escapes bundle root.");
        return candidate;
    }

    private void EnsureBundleRoot(DirectoryInfo root)
    {
        if (Directory.Exists(root.FullName)) EnsureNotReparsePoint(root.FullName);
        Directory.CreateDirectory(root.FullName);
        EnsureNotReparsePoint(root.FullName);
    }

    private DirectoryInfo CreateDirectory(DirectoryInfo root, string path)
    {
        EnsureSafeDirectoryAncestors(root, path);
        var directory = Directory.CreateDirectory(path);
        EnsureSafeDirectoryAncestors(root, directory.FullName);
        return directory;
    }

    private void EnsureSafeDirectoryAncestors(DirectoryInfo root, string target)
    {
        var rootPath = Path.GetFullPath(root.FullName);
        var targetPath = Path.GetFullPath(target);
        var rootPrefix = rootPath + Path.DirectorySeparatorChar;
        if (!targetPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) && !targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Target escapes bundle root.");

        EnsureNotReparsePoint(rootPath);
        var relative = Path.GetRelativePath(rootPath, targetPath);
        var current = rootPath;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "." or "") continue;
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) EnsureNotReparsePoint(current);
        }
    }

    private void EnsureNotReparsePoint(string path)
    {
        var attributes = PathAttributes?.Invoke(path);
        if (attributes is null && (Directory.Exists(path) || File.Exists(path))) attributes = File.GetAttributes(path);
        if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Bundle target traverses reparse point '{path}'.");
    }

    private static string SceneRelativeDirectory(string sourceWorldPath)
    {
        if (string.IsNullOrWhiteSpace(sourceWorldPath) || sourceWorldPath.Contains('\\') || sourceWorldPath.StartsWith("//", StringComparison.Ordinal) ||
            sourceWorldPath.Contains(':') || sourceWorldPath.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidDataException("Source world path is not a safe logical path.");
        var normalized = sourceWorldPath.TrimStart('/');
        if (normalized.StartsWith("Game/", StringComparison.Ordinal)) normalized = normalized[5..];
        var packagePath = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        var segments = packagePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Source world path does not have a safe directory.");
        return segments.Length == 1 ? string.Empty : Path.Combine(segments[..^1]);
    }

    private static string SceneFileName(string sourceWorldPath)
    {
        if (string.IsNullOrWhiteSpace(sourceWorldPath) || sourceWorldPath.Contains('\\') || sourceWorldPath.StartsWith("//", StringComparison.Ordinal) ||
            sourceWorldPath.Contains(':') || sourceWorldPath.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidDataException("Source world path is not a safe logical path.");
        var name = sourceWorldPath.Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Source world path does not have a safe filename.");
        return name + ".umap.uescene";
    }

    public UESceneBundlePlan BuildPlan(UWorld world, ExporterOptions options,
        UESceneResourceLogicalRoot resourceLogicalRoot = UESceneResourceLogicalRoot.GameExportTree, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();
        var level = world.PersistentLevel.Load<ULevel>();
        var actors = level?.Actors?.Where(a => a is not null).Select(a => a!.Load<AActor>()).Where(a => a is not null)
            .Select(a => ProjectActor(a!)).ToArray() ?? [];
        return BuildPlanningWorld(new UEScenePlanningWorld(world.GetPathName(), level?.GetPathName() ?? "", actors,
            world.StreamingLevels is { Length: > 0 }, level?.WorldPartitionRuntimeCell.AssetPathName.Text is { Length: > 0 }), options, resourceLogicalRoot, token);
    }

    public static UESceneBundlePlan BuildPlan(UEScenePlanningWorld world, ExporterOptions? options = null, CancellationToken token = default) =>
        new UESceneBundleExporter().BuildPlanningWorld(world, options ?? new ExporterOptions(), UESceneResourceLogicalRoot.GameExportTree, token);

    public static UESceneBundlePlan BuildPlan(UEScenePlanningWorld world, UESceneResourceLogicalRoot resourceLogicalRoot,
        ExporterOptions? options = null, CancellationToken token = default) =>
        new UESceneBundleExporter().BuildPlanningWorld(world, options ?? new ExporterOptions(), resourceLogicalRoot, token);

    private UESceneBundlePlan BuildPlanningWorld(UEScenePlanningWorld world, ExporterOptions options,
        UESceneResourceLogicalRoot resourceLogicalRoot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);
        resourceLogicalRoot = UESceneExportDestination.NormalizeLogicalRoot(resourceLogicalRoot);
        token.ThrowIfCancellationRequested();
        var resourceUriPolicy = options.ResourceUriPolicy ?? new UESceneResourceUriPolicy(resourceLogicalRoot);
        if (resourceUriPolicy.LogicalRoot != resourceLogicalRoot)
            throw new InvalidDataException("Supplied UEScene resource URI policy logical root does not match the requested resource logical root.");
        var ids = 1UL;
        ulong Next() => ids++;
        var assets = new List<AssetRecord>();
        var actors = new List<ActorRecord>();
        var components = new List<ComponentRecord>();
        var transforms = new List<TransformRecord>();
        var sets = new List<InstanceSetRecord>();
        var instanceTransforms = new List<InstanceTransform>();
        var overrides = new List<MaterialOverrideRecord>();
        var diagnostics = new List<DiagnosticRecord>();
        var lights = new List<LightRecord>();
        var bndx = new List<BundleEntry>();
        var meshIds = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var meshResources = new List<UEScenePlannedResource>();
        var materialIds = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var actorIds = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var componentIds = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var pendingComponentDiagnostics = new List<(string actorPath, string source, ushort code, string message)>();

        void Diagnostic(byte subjectKind, ulong subjectId, string path, ushort code, string message) => diagnostics.Add(new(2, subjectKind, code, 0, subjectId, path, message, 0, 0, 0));
        if (world.HasStreamingLevels) Diagnostic(0, 0, world.WorldPath, UESceneDiagnosticCodes.StreamingLevels, "Streaming levels are excluded from the persistent-level export.");
        if (world.HasWorldPartition) Diagnostic(0, 0, world.WorldPath, UESceneDiagnosticCodes.WorldPartition, "World Partition content is excluded from the persistent-level export.");

        foreach (var inputActor in world.Actors)
        {
            token.ThrowIfCancellationRequested();
            var actorId = Next();
            actorIds[inputActor.SourceObjectPath] = actorId;
            var actorTransform = Next();
            transforms.Add(ToTransform(actorTransform, inputActor.LocalTransform));
            actors.Add(new(actorId, 0, inputActor.Name, inputActor.ClassPath, inputActor.SourceObjectPath, 0, actorTransform, 0, 0, 0, 0, 0));
        }

        // Freeze supported membership and IDs before following parent links. Source order must not alter the graph.
        var supported = new List<(UEScenePlanningActor actor, UEScenePlanningComponent input, ComponentKind kind, ulong componentId)>();
        foreach (var inputActor in world.Actors)
        {
            foreach (var sourceInput in inputActor.Components)
            {
                var input = sourceInput with
                {
                    OverrideMaterialPaths = NormalizeOverrideMaterialPaths(sourceInput.OverrideMaterialPaths)
                };
                if (input.Light is not null) continue;
                if (!TryKind(input.Kind, out var kind))
                {
                    pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedComponent, $"Unsupported component kind '{input.Kind}'."));
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(input.AttachSocketName))
                {
                    pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedSocket, "Socket attachment is unsupported and was skipped."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(input.MeshPath))
                {
                    pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.MissingMesh, "Static mesh could not be loaded; component was skipped."));
                    continue;
                }
                if (kind is ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh)
                {
                    var instances = new List<UEScenePlanningTransform>(input.InstanceTransforms.Count);
                    foreach (var instance in input.InstanceTransforms)
                    {
                        if (!TryNormalizeInstanceTransform(instance, out var normalized))
                        {
                            pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.InvalidInstanceTransform, "Instanced component has an invalid instance transform and was skipped."));
                            instances.Clear();
                            break;
                        }
                        instances.Add(normalized);
                    }
                    if (instances.Count != input.InstanceTransforms.Count) continue;
                    input = input with { InstanceTransforms = instances };
                }
                var componentId = Next();
                componentIds.Add(input.SourceObjectPath, componentId);
                supported.Add((inputActor, input, kind, componentId));
            }
        }

        foreach (var item in supported.Where(item => HasParentCycle(item.input, item.actor.Components)).ToArray())
        {
            componentIds.Remove(item.input.SourceObjectPath);
            supported.Remove(item);
            pendingComponentDiagnostics.Add((item.actor.SourceObjectPath, item.input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedComponent, "Unsupported parent ancestry contains a cycle; component was skipped."));
        }

        // Lights are scene records, not synthetic components. Their parent may only reference an emitted mesh component.
        foreach (var inputActor in world.Actors)
        {
            foreach (var input in inputActor.Components.Where(component => component.Light is not null))
            {
                var light = input.Light!;
                if (!TryLightType(light.Type, out var lightType))
                {
                    pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedLight,
                        $"Unsupported light component type '{light.Type}'."));
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(input.AttachSocketName))
                {
                    pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedSocket, "Socket attachment is unsupported and was skipped."));
                    continue;
                }
                if (HasParentCycle(input, inputActor.Components))
                {
                    pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedLight,
                        "Unsupported parent ancestry contains a cycle; light was skipped."));
                    continue;
                }
                var transformId = Next();
                transforms.Add(ToTransform(transformId, ComposeUnemittedAncestors(input, inputActor.Components, componentIds)));
                var parentId = FindEmittedAncestor(input, inputActor.Components, componentIds);
                if (parentId == 0 && input.ParentComponentPath is not null)
                    pendingComponentDiagnostics.Add((inputActor.SourceObjectPath, input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedParent,
                        "Unsupported parent ancestry was baked into the local transform."));
                var flags = (light.CastShadows ? LightFlags.CastsShadows : 0u) |
                    (light.Type is "Point" or "Spot" or "Rect" ? LightFlags.HasAttenuation : 0u) |
                    (light.Type == "Spot" ? LightFlags.HasSpotAngles : 0u) |
                    (light.Type == "Rect" ? LightFlags.HasRectDimensions : 0u) |
                    (light.UsesInverseSquaredFalloff && light.Type is "Point" or "Spot" ? LightFlags.UsesInverseSquaredFalloff : 0u);
                lights.Add(new(Next(), actorIds[inputActor.SourceObjectPath], parentId, transformId, input.Name, input.ClassPath,
                    input.SourceObjectPath, lightType, LightColorEncoding.Rgba8, light.LightUnits, light.ColorR, light.ColorG, light.ColorB,
                    light.ColorA, flags, light.Intensity, light.AttenuationRadius, light.FalloffExponent, light.InnerConeAngleDegrees,
                    light.OuterConeAngleDegrees, light.RectWidth, light.RectHeight, 0, light.LightSourceAngle, light.LightSourceSoftAngle,
                    light.SourceRadius, light.SoftSourceRadius, light.SourceLength, light.BarnDoorAngle, light.BarnDoorLength));
            }
        }

        foreach (var item in supported)
        {
            var (inputActor, input, kind, componentId) = item;
            var variant = new UESceneMeshVariantKey(input.MeshPath!, input.OverrideMaterialPaths);
            if (!meshIds.TryGetValue(variant.Identity, out var meshId))
            {
                meshId = Next(); meshIds.Add(variant.Identity, meshId);
                var uri = variant.HasOverrides
                    ? resourceUriPolicy.AllocateVariant(input.MeshPath!, variant.Identity, UESceneResourceKind.Model, ".uemodel")
                    : resourceUriPolicy.Allocate(input.MeshPath!, UESceneResourceKind.Model, ".uemodel");
                var placeholder = new byte[32];
                assets.Add(new(meshId, AssetKind.StaticMesh, LocationMode.Bundle, Availability.Available, variant.Identity, LastName(input.MeshPath!), uri, "application/x-uemodel", placeholder, 0, 0, 0, 0, 0));
                bndx.Add(new((byte)BundleResourceKind.Model, LocationMode.Bundle, Availability.Available, uri, meshId, uint.MaxValue, placeholder, 0, "application/x-uemodel", 0, 0));
                meshResources.Add(new(variant.Identity, uri, "uemodel multi-LOD", false, input.MeshPath!, input.OverrideMaterialPaths));
            }
            var transformId = Next();
            transforms.Add(ToTransform(transformId, ComposeUnemittedAncestors(input, inputActor.Components, componentIds)));
            var instanceSetId = 0UL;
            if (kind is ComponentKind.InstancedStaticMesh or ComponentKind.HierarchicalInstancedStaticMesh)
            {
                instanceSetId = Next();
                var start = checked((uint)instanceTransforms.Count);
                instanceTransforms.AddRange(input.InstanceTransforms.Select(ToInstanceTransform));
                sets.Add(new(instanceSetId, componentId, checked((uint)input.InstanceTransforms.Count), 0, start, checked((uint)input.InstanceTransforms.Count)));
            }
            components.Add(new(componentId, actorIds[inputActor.SourceObjectPath], 0, kind, input.Name, input.ClassPath, input.SourceObjectPath, transformId, meshId, 0, 0, instanceSetId, input.Visible ? VisibilityFlags.Visible : 0, 0, 0));
            foreach (var materialPath in input.OverrideMaterialPaths.Select((path, index) => (path, index)).Where(x => !string.IsNullOrWhiteSpace(x.path)))
            {
                if (!materialIds.TryGetValue(materialPath.path!, out var materialId))
                {
                    materialId = Next();
                    materialIds.Add(materialPath.path!, materialId);
                    var materialUri = resourceUriPolicy.Allocate(materialPath.path!, UESceneResourceKind.Material, ".json");
                    assets.Add(new(materialId, AssetKind.Material, LocationMode.Bundle, Availability.Available, materialPath.path!, LastName(materialPath.path!), materialUri, "application/json", new byte[32], 0, 0, 0, 0, 0));
                }
                overrides.Add(new(componentId, checked((uint)materialPath.index), 0, 0, materialId, 0, 0, 0, 0, 0));
            }
        }
        // Retain only links to emitted supported components; no generic placeholder nodes are invented.
        for (var i = 0; i < components.Count; i++)
        {
            var c = components[i];
            var input = supported.First(x => x.componentId == c.ComponentId);
            var parent = FindEmittedAncestor(input.input, input.actor.Components, componentIds);
            if (parent != 0) components[i] = c with { ParentComponentId = parent };
            else if (input.input.ParentComponentPath is not null) pendingComponentDiagnostics.Add((input.actor.SourceObjectPath, input.input.SourceObjectPath, UESceneDiagnosticCodes.UnsupportedParent, "Unsupported parent ancestry was baked into the local transform."));
        }
        foreach (var d in pendingComponentDiagnostics)
            Diagnostic(2, actorIds[d.actorPath], d.source, d.code, d.message);
        RecomputeRanges(actors, components, overrides, diagnostics);
        var optionalFeatures = resourceLogicalRoot == UESceneResourceLogicalRoot.GameExportTree
            ? SceneMetadata.GameExportTreeRootBit
            : 0;
        var document = new UESceneDocument(new SceneMetadata("CUE4Parse", "planning", "", world.WorldPath, world.PersistentLevelPath, null, null, 0,
                OptionalFeatures: optionalFeatures),
            Assets: assets, Lods: [], Actors: actors, Components: components, Transforms: transforms, Lights: lights.Count == 0 ? null : lights, InstanceSets: sets,
            InstanceTransforms: instanceTransforms, MaterialOverrides: overrides, MatoBlob: [], Diagnostics: diagnostics, DiagBlob: [], BundleEntries: bndx);
        return new UESceneBundlePlan(document, meshResources);
    }

    private void ValidateDestinationLogicalRoot(UEScenePublicationDestination destination)
    {
        var planUsesGameExportTree = (_plan!.Document.Metadata.OptionalFeatures & SceneMetadata.GameExportTreeRootBit) != 0;
        var destinationUsesGameExportTree = UESceneExportDestination.NormalizeLogicalRoot(destination.ResourceLogicalRoot) == UESceneResourceLogicalRoot.GameExportTree;
        if (planUsesGameExportTree != destinationUsesGameExportTree)
            throw new InvalidDataException("UEScene plan metadata logical root does not match the destination resource logical root.");
    }

    public static IReadOnlyList<T> MergeComponentSources<T>(T? root, IEnumerable<T> owned, IEnumerable<T> instance, IEnumerable<T> fallback, Func<T, string> path)
    {
        var result = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(T? component)
        {
            if (component is not null && seen.Add(path(component))) result.Add(component);
        }
        Add(root);
        foreach (var component in owned) Add(component);
        foreach (var component in instance) Add(component);
        foreach (var component in fallback) Add(component);
        return result;
    }

    private static UEScenePlanningActor ProjectActor(AActor actor)
    {
        var root = actor.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex?>("RootComponent")?.Load<USceneComponent>();
        var components = MergeComponentSources(root,
                actor.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("OwnedComponents", []).Select(i => i.Load<USceneComponent>()),
                actor.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("InstanceComponents", []).Select(i => i.Load<USceneComponent>()),
                actor.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("Components", []).Select(i => i.Load<USceneComponent>()),
                c => c.GetPathName())
            .Where(c => c is not null).Select(c => ProjectComponent(c!)).ToArray();
        var actorTransform = root is null ? ToPlanningTransform(FTransform.Identity) : ToPlanningTransform(root.GetRelativeTransform());
        // The root transform belongs to the actor; keeping it on the root node would apply it twice.
        if (root is not null)
        {
            var rootPath = root.GetPathName();
            components = components.Select(c => c.SourceObjectPath == rootPath ? c with { LocalTransform = ToPlanningTransform(FTransform.Identity) } : c).ToArray();
        }
        return new(actor.GetPathName(), actor.Name, actor.ExportType, actorTransform, components);
    }
    private static UEScenePlanningComponent ProjectComponent(USceneComponent component)
    {
        var (kind, mesh, instances) = component switch
        {
            UHierarchicalInstancedStaticMeshComponent hism => ("HierarchicalInstancedStaticMesh", hism.GetLoadedStaticMesh()?.GetPathName(), hism.GetInstances().Select(x => ToPlanningTransform(x.TransformData)).ToArray()),
            UInstancedStaticMeshComponent ism => ("InstancedStaticMesh", ism.GetLoadedStaticMesh()?.GetPathName(), ism.GetInstances().Select(x => ToPlanningTransform(x.TransformData)).ToArray()),
            UStaticMeshComponent sm => ("StaticMesh", sm.GetLoadedStaticMesh()?.GetPathName(), Array.Empty<UEScenePlanningTransform>()),
            _ => (component.GetType().Name, null, Array.Empty<UEScenePlanningTransform>())
        };
        var parent = component.GetAttachParent()?.GetPathName();
        var materials = component.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("OverrideMaterials", [])
            .Select(i => i.Load<UObject>()?.GetPathName()).ToArray();
        return new(component.GetPathName(), component.Name, component.ExportType, kind, ToPlanningTransform(component.GetRelativeTransform()), mesh,
            component.GetOrDefault("bVisible", true), parent, component.GetOrDefault("AttachSocketName", ""), instances, materials,
            ProjectLight(component));
    }
    private static UEScenePlanningLight? ProjectLight(USceneComponent component) => component switch
    {
        UDirectionalLightComponent light => new("Directional", light.LightColor.R, light.LightColor.G, light.LightColor.B, light.LightColor.A,
            light.Intensity, ELightUnits.Unitless, light.CastShadows, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, light.LightSourceAngle, light.LightSourceSoftAngle, 0, 0),
        USpotLightComponent light => LocalLight("Spot", light, light.LightFalloffExponent, light.bUseInverseSquaredFalloff, light.SourceRadius,
            light.SoftSourceRadius, light.SourceLength, light.InnerConeAngle, light.OuterConeAngle),
        UPointLightComponent light => LocalLight("Point", light, light.LightFalloffExponent, light.bUseInverseSquaredFalloff, light.SourceRadius,
            light.SoftSourceRadius, light.SourceLength, 0, 0),
        URectLightComponent light => LocalLight("Rect", light, 0, false, 0, 0, 0, 0, 0, light.SourceWidth, light.SourceHeight,
            light.BarnDoorAngle, light.BarnDoorLength),
        ULightComponentBase light => new(light.GetType().Name, light.LightColor.R, light.LightColor.G, light.LightColor.B, light.LightColor.A,
            light.Intensity, ELightUnits.Unitless, light.CastShadows, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        _ => null
    };
    private static UEScenePlanningLight LocalLight(string type, ULocalLightComponent light, float falloff, bool inverseSquared, float sourceRadius,
        float softSourceRadius, float sourceLength, float innerCone, float outerCone, float rectWidth = 0, float rectHeight = 0, float barnDoorAngle = 0, float barnDoorLength = 0) =>
        new(type, light.LightColor.R, light.LightColor.G, light.LightColor.B, light.LightColor.A, light.Intensity, ToSceneLightUnits(light.GetLightUnits()),
            light.CastShadows, light.AttenuationRadius, falloff, inverseSquared, innerCone, outerCone, rectWidth, rectHeight, sourceRadius,
            softSourceRadius, sourceLength, 0, 0, barnDoorAngle, barnDoorLength);
    private static ELightUnits ToSceneLightUnits(CUE4Parse.UE4.Objects.Engine.ELightUnits units) => units switch
    {
        CUE4Parse.UE4.Objects.Engine.ELightUnits.Candelas => ELightUnits.Candelas,
        CUE4Parse.UE4.Objects.Engine.ELightUnits.Lumens => ELightUnits.Lumens,
        CUE4Parse.UE4.Objects.Engine.ELightUnits.EV => ELightUnits.Ev,
        CUE4Parse.UE4.Objects.Engine.ELightUnits.Nits => ELightUnits.Lux,
        _ => ELightUnits.Unitless
    };
    private static bool TryKind(string name, out ComponentKind kind) { kind = name switch { "StaticMesh" => ComponentKind.StaticMesh, "InstancedStaticMesh" => ComponentKind.InstancedStaticMesh, "HierarchicalInstancedStaticMesh" => ComponentKind.HierarchicalInstancedStaticMesh, _ => 0 }; return kind != 0; }
    private static bool TryLightType(string name, out LightType type) { type = name switch { "Directional" => LightType.Directional, "Point" => LightType.Point, "Spot" => LightType.Spot, "Rect" => LightType.Rect, _ => 0 }; return type != 0; }
    private static UEScenePlanningTransform ComposeUnemittedAncestors(UEScenePlanningComponent component, IReadOnlyList<UEScenePlanningComponent> actorComponents, IReadOnlyDictionary<string, ulong> emitted)
    {
        var result = ToFTransform(component.LocalTransform);
        var byPath = actorComponents.ToDictionary(c => c.SourceObjectPath, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var parent = component.ParentComponentPath;
        while (parent is not null && !emitted.ContainsKey(parent) && visited.Add(parent) && byPath.TryGetValue(parent, out var ancestor))
        {
            result *= ToFTransform(ancestor.LocalTransform);
            parent = ancestor.ParentComponentPath;
        }
        return ToPlanningTransform(result);
    }
    private static ulong FindEmittedAncestor(UEScenePlanningComponent component, IReadOnlyList<UEScenePlanningComponent> actorComponents, IReadOnlyDictionary<string, ulong> emitted)
    {
        var byPath = actorComponents.ToDictionary(c => c.SourceObjectPath, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var parent = component.ParentComponentPath;
        while (parent is not null && visited.Add(parent))
        {
            if (emitted.TryGetValue(parent, out var parentId)) return parentId;
            if (!byPath.TryGetValue(parent, out var ancestor)) break;
            parent = ancestor.ParentComponentPath;
        }
        return 0;
    }
    private static bool HasParentCycle(UEScenePlanningComponent component, IReadOnlyList<UEScenePlanningComponent> actorComponents)
    {
        var byPath = actorComponents.ToDictionary(c => c.SourceObjectPath, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var parent = component.ParentComponentPath;
        while (parent is not null && byPath.TryGetValue(parent, out var ancestor))
        {
            if (!visited.Add(parent)) return true;
            parent = ancestor.ParentComponentPath;
        }
        return false;
    }
    private static bool TryNormalizeInstanceTransform(UEScenePlanningTransform t, out UEScenePlanningTransform normalized)
    {
        normalized = t;
        if (!float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z) || !float.IsFinite(t.Sx) || !float.IsFinite(t.Sy) || !float.IsFinite(t.Sz))
            return false;
        if (!float.IsFinite(t.Rx) || !float.IsFinite(t.Ry) || !float.IsFinite(t.Rz) || !float.IsFinite(t.Rw))
            return false;
        var lengthSquared = t.Rx * t.Rx + t.Ry * t.Ry + t.Rz * t.Rz + t.Rw * t.Rw;
        if (!float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon) return false;
        var inverseLength = 1f / MathF.Sqrt(lengthSquared);
        normalized = t with { Rx = t.Rx * inverseLength, Ry = t.Ry * inverseLength, Rz = t.Rz * inverseLength, Rw = t.Rw * inverseLength };
        return true;
    }
    private static FTransform ToFTransform(UEScenePlanningTransform t) => new(new FQuat(t.Rx, t.Ry, t.Rz, t.Rw), new FVector(t.X, t.Y, t.Z), new FVector(t.Sx, t.Sy, t.Sz));
    private static TransformRecord ToTransform(ulong id, UEScenePlanningTransform t) => new(id, t.X, t.Y, t.Z, t.Rx, t.Ry, t.Rz, t.Rw, t.Sx, t.Sy, t.Sz, 0);
    private static InstanceTransform ToInstanceTransform(UEScenePlanningTransform t) => new(t.X, t.Y, t.Z, t.Rx, t.Ry, t.Rz, t.Rw, t.Sx, t.Sy, t.Sz);
    private static UEScenePlanningTransform ToPlanningTransform(FTransform t) => new(t.Translation.X, t.Translation.Y, t.Translation.Z, t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W, t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z);
    private static IReadOnlyList<string?> NormalizeOverrideMaterialPaths(IReadOnlyList<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Select(path => string.IsNullOrWhiteSpace(path) ? null : MaterialLinkBuilder.NormalizeUnrealMaterialPath(path)).ToArray();
    }

    private static string LastName(string path) => path.Split(['/','.'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
    private static void RecomputeRanges(List<ActorRecord> actors, List<ComponentRecord> components, List<MaterialOverrideRecord> overrides, List<DiagnosticRecord> diagnostics)
    {
        for (var i = 0; i < actors.Count; i++) { var owned = components.Where(c => c.ActorId == actors[i].ActorId).ToList(); var start = owned.Count == 0 ? 0u : checked((uint)components.IndexOf(owned[0])); actors[i] = actors[i] with { RootComponentId = owned.FirstOrDefault(c => c.ParentComponentId == 0)?.ComponentId ?? 0, ComponentStart = start, ComponentCount = checked((uint)owned.Count) }; }
        for (var i = 0; i < components.Count; i++) { var owned = overrides.Where(o => o.ComponentId == components[i].ComponentId).ToList(); components[i] = components[i] with { MaterialOverrideStart = owned.Count == 0 ? 0u : checked((uint)overrides.IndexOf(owned[0])), MaterialOverrideCount = checked((uint)owned.Count) }; }
        for (var i = 0; i < actors.Count; i++) { var owned = diagnostics.Select((d, n) => (d, n)).Where(x => x.d.SubjectKind == 2 && x.d.SubjectId == actors[i].ActorId).Select(x => x.n).ToArray(); actors[i] = actors[i] with { DiagnosticStart = owned.Length == 0 ? 0u : checked((uint)owned[0]), DiagnosticCount = checked((uint)owned.Length) }; }
    }
}

public sealed record ExporterOptions(UESceneResourceUriPolicy? ResourceUriPolicy = null);
public sealed record UESceneBundlePlan(UESceneDocument Document, IReadOnlyList<UEScenePlannedResource> Resources);
public sealed record UEScenePlannedResource(string CanonicalUnrealPath, string LogicalUri, string ResourcePlan, bool HasFinalBytes,
    string? SourceMeshPath = null, IReadOnlyList<string?>? OverrideMaterialPaths = null);
public sealed record UESceneMeshVariantKey(string SourceMeshPath, IReadOnlyList<string?> OverrideMaterialPaths)
{
    private static IReadOnlyList<string?> Normalize(IReadOnlyList<string?> paths) => paths
        .Select(path => string.IsNullOrWhiteSpace(path) ? null : path.Trim()).ToArray();
    public bool HasOverrides => Normalize(OverrideMaterialPaths).Any(path => path is not null);
    public string Identity { get; } = Normalize(OverrideMaterialPaths).Any(path => path is not null)
        ? SourceMeshPath + "#uescene-material-variant-" + Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\u001f", Normalize(OverrideMaterialPaths).Select(path => path ?? "\u0000")))).AsSpan(0, 16)).ToLowerInvariant()
        : SourceMeshPath;
}
public sealed record UESceneProducedLod(uint SourceLodIndex, float SourceScreenSize = 0, uint Provenance = 0);
public sealed record UEScenePublicationDestination(string ScenePhysicalPath, string ResourcePhysicalRoot,
    UESceneResourceLogicalRoot ResourceLogicalRoot,
    UESceneResourcePublicationPolicy ResourcePublicationPolicy = UESceneResourcePublicationPolicy.Overwrite);

/// <summary>Metadata required to stage additional files atomically with a UESCENE publication.</summary>
public sealed record UEScenePublicationContext(SceneExportResult Result, string ResourcePhysicalRoot,
    string ScenePhysicalPath, byte[] SceneBytes);

/// <summary>A contained relative file staged and promoted in the same transaction as the scene.</summary>
public sealed record UEScenePublicationArtifact(string RelativePath, byte[] Bytes);
public sealed record UESceneProducedResource(string LogicalUri, byte[] Bytes, IReadOnlyList<UESceneProducedLod> Lods, IReadOnlyList<UESceneProducedSidecar> Sidecars,
    IReadOnlyList<UESceneProducedMaterialSlot>? MaterialSlotMappings = null,
    IReadOnlyList<UESceneProducedDiagnostic>? Diagnostics = null)
{
    public IReadOnlyList<UESceneProducedMaterialSlot> MaterialSlots => MaterialSlotMappings ?? [];
    public IReadOnlyList<UESceneProducedDiagnostic> ExportDiagnostics => Diagnostics ?? [];
    public static UESceneProducedResource FromMeshExport(string logicalUri, byte[] bytes, IEnumerable<UESceneProducedLod> lods, IEnumerable<UESceneProducedSidecar>? sidecars = null,
        IEnumerable<UESceneProducedMaterialSlot>? materialSlots = null, IEnumerable<UESceneProducedDiagnostic>? diagnostics = null) =>
        new(logicalUri, bytes, lods.ToArray(), (sidecars ?? []).ToArray(), materialSlots?.ToArray(), diagnostics?.ToArray());
    public static UESceneProducedResource FromMeshExporterLods(string meshFileName, byte[] bytes, IEnumerable<(uint SourceLodIndex, float SourceScreenSize)> declaredLods, IEnumerable<UESceneProducedSidecar>? sidecars = null) =>
        new(meshFileName, bytes, declaredLods.Select(lod => new UESceneProducedLod(lod.SourceLodIndex, lod.SourceScreenSize)).ToArray(), (sidecars ?? []).ToArray());
}
public sealed record UESceneProducedMaterialSlot(uint SourceMaterialIndex, uint SectionIndex, string EffectiveMaterialPath);
public sealed record UESceneProducedDiagnostic(byte Severity, byte SubjectKind, ushort Code, string SourceObjectPath, string Message);
public sealed record UESceneProducedSidecar(string CanonicalUnrealPath, BundleResourceKind Kind, string LogicalUri, string Mime, byte[] Bytes);
public sealed record UESceneFinalResource(byte ResourceKind, ulong OwnerAssetId, string LogicalUri, string Mime, byte[] Bytes);
public sealed record UESceneFinalBundlePlan(UESceneDocument Document, IReadOnlyList<UESceneFinalResource> Resources);
public sealed record UEScenePlanningWorld(string WorldPath, string PersistentLevelPath, IReadOnlyList<UEScenePlanningActor> Actors, bool HasStreamingLevels = false, bool HasWorldPartition = false);
public sealed record UEScenePlanningActor(string SourceObjectPath, string Name, string ClassPath, UEScenePlanningTransform LocalTransform, IReadOnlyList<UEScenePlanningComponent> Components);
public sealed record UEScenePlanningComponent(string SourceObjectPath, string Name, string ClassPath, string Kind, UEScenePlanningTransform LocalTransform, string? MeshPath, bool Visible, string? ParentComponentPath, string AttachSocketName, IReadOnlyList<UEScenePlanningTransform> InstanceTransforms, IReadOnlyList<string?> OverrideMaterialPaths, UEScenePlanningLight? Light = null);
public sealed record UEScenePlanningLight(string Type, byte ColorR, byte ColorG, byte ColorB, byte ColorA, float Intensity, ELightUnits LightUnits,
    bool CastShadows, float AttenuationRadius, float FalloffExponent, bool UsesInverseSquaredFalloff, float InnerConeAngleDegrees,
    float OuterConeAngleDegrees, float RectWidth, float RectHeight, float SourceRadius, float SoftSourceRadius, float SourceLength,
    float LightSourceAngle, float LightSourceSoftAngle, float BarnDoorAngle, float BarnDoorLength);
public sealed record UEScenePlanningTransform(float X, float Y, float Z, float Rx, float Ry, float Rz, float Rw, float Sx, float Sy, float Sz);
public enum SceneExportErrorCode
{
    Cancelled,
    InvalidData,
    AccessDenied,
    DiskFull,
    PromotionFailed,
    RollbackFailed,
    CleanupFailed,
    IoFailure,
    Unexpected
}
public sealed record SceneExportError(SceneExportErrorCode Code, string Message, Exception Exception, string? TargetPath = null);
public sealed record SceneExportResult(
    bool Success,
    string? ScenePath,
    string? Error,
    string? RecoveryStagePath = null,
    UESceneDocument? FinalDocument = null,
    UESceneFinalBundlePlan? FinalPlan = null,
    int ActorCount = 0,
    int ComponentCount = 0,
    int MeshCount = 0,
    int InstanceCount = 0,
    int DiagnosticCount = 0,
    int ResourceCount = 0,
    SceneExportError? Failure = null,
    IReadOnlyList<SceneExportError>? StructuredRecoveryErrors = null)
{
    public IReadOnlyList<SceneExportError> RecoveryErrors => StructuredRecoveryErrors ?? [];
}
public static class UESceneDiagnosticCodes { public const ushort StreamingLevels = 1, WorldPartition = 2, MissingMesh = 3, UnsupportedComponent = 4, UnsupportedParent = 5, UnsupportedSocket = 6, InvalidInstanceTransform = 7, MissingMaterialSidecar = 8, UnsupportedLight = 9, MaterialOverrideMismatch = 10, LodMaterialTopologyFallback = 11; }
