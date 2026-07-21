namespace CUE4Parse_Conversion.UEFormat;

public sealed class ResourceManifest
{
    private readonly List<ResourceEntry> _resources = [];
    private IReadOnlyList<ResourceEntry>? _sealedResources;
    private readonly Dictionary<string, int> _pathIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _uriIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _physicalTargetIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<int, WriteStatus> _statuses = [];

    public IReadOnlyList<ResourceEntry> Resources => _sealedResources ?? _resources.AsReadOnly();
    public bool IsSealed { get; private set; }

    public bool AllWritten => _resources.Count > 0 && _resources.All(r => _statuses[r.Id] == WriteStatus.Written);
    public bool HasFailures => _resources.Any(r => _statuses[r.Id] == WriteStatus.Failed);

    public ResourceEntry AddOrGet(string unrealObjectPath, string relativeUri, string mime, byte[] bytes, bool isExternal, string? physicalTargetPath = null)
    {
        if (IsSealed) throw new InvalidOperationException("The resource manifest is sealed.");
        var resolvedPhysicalTargetPath = physicalTargetPath ?? relativeUri;
        if (_pathIndex.TryGetValue(unrealObjectPath, out var existing))
        {
            var existingEntry = _resources[existing];
            if (existingEntry.RelativeUri != relativeUri || existingEntry.PhysicalTargetPath != resolvedPhysicalTargetPath ||
                existingEntry.Mime != mime || existingEntry.IsExternal != isExternal || !existingEntry.Bytes.SequenceEqual(bytes))
                throw new InvalidDataException($"Conflicting resource declaration for '{unrealObjectPath}'.");
            return existingEntry;
        }

        ThrowIfClaimed(_uriIndex, relativeUri, unrealObjectPath, "URI");
        ThrowIfClaimed(_physicalTargetIndex, resolvedPhysicalTargetPath, unrealObjectPath, "physical target");

        var entry = new ResourceEntry(_resources.Count, unrealObjectPath, relativeUri, mime, bytes, isExternal, physicalTargetPath);
        _resources.Add(entry);
        _statuses[entry.Id] = WriteStatus.Pending;
        _pathIndex[unrealObjectPath] = entry.Id;
        _uriIndex[relativeUri] = entry.Id;
        _physicalTargetIndex[resolvedPhysicalTargetPath] = entry.Id;
        return entry;
    }

    public void Seal()
    {
        if (IsSealed) return;
        _sealedResources = _resources.ToArray();
        IsSealed = true;
    }

    public WriteStatus GetStatus(string unrealObjectPath) =>
        _pathIndex.TryGetValue(unrealObjectPath, out var index) ? _statuses[index] : throw new KeyNotFoundException(unrealObjectPath);

    public void MarkWritten(string unrealObjectPath)
    {
        _statuses[GetRequiredIndex(unrealObjectPath)] = WriteStatus.Written;
    }

    public void MarkFailed(string unrealObjectPath)
    {
        _statuses[GetRequiredIndex(unrealObjectPath)] = WriteStatus.Failed;
    }

    public ResourceEntry? FindByPath(string unrealObjectPath)
    {
        return _pathIndex.TryGetValue(unrealObjectPath, out var index) ? _resources[index] : null;
    }

    private int GetRequiredIndex(string unrealObjectPath) =>
        _pathIndex.TryGetValue(unrealObjectPath, out var index) ? index : throw new KeyNotFoundException(unrealObjectPath);

    private void ThrowIfClaimed(Dictionary<string, int> index, string key, string unrealObjectPath, string kind)
    {
        if (index.TryGetValue(key, out var existing))
            throw new InvalidDataException(
                $"Resource {kind} '{key}' is already claimed by '{_resources[existing].UnrealObjectPath}', not '{unrealObjectPath}'.");
    }
}
