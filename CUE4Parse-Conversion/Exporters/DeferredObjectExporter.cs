using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;

namespace CUE4Parse_Conversion.Exporters;

/// <summary>
/// Lightweight descriptor for a built-in, provider-backed UObject export. It keeps
/// only a weak object reference plus the package file/export index needed to reload
/// that object when a worker starts. This prevents a large pending queue from rooting
/// complete package graphs.
/// </summary>
internal sealed class DeferredObjectExporter : ExporterBase
{
    private readonly WeakReference<UObject> _export;
    private readonly IFileProvider _provider;
    private readonly GameFile _packageFile;
    private readonly int _exportIndex;

    private DeferredObjectExporter(UObject export, IFileProvider provider, GameFile packageFile, int exportIndex)
        : base(export)
    {
        _export = new WeakReference<UObject>(export);
        _provider = provider;
        _packageFile = packageFile;
        _exportIndex = exportIndex;
    }

    internal static DeferredObjectExporter? TryCreate(UObject export)
    {
        if (export.Owner is not { Provider: { } provider } owner ||
            !provider.TryGetGameFile(owner.Name, out var packageFile))
            return null;

        for (var exportIndex = 0; exportIndex < owner.ExportsLazy.Length; exportIndex++)
        {
            var candidate = owner.ExportsLazy[exportIndex];
            if (!candidate.IsValueCreated || !ReferenceEquals(candidate.Value, export))
                continue;

            return new DeferredObjectExporter(export, provider, packageFile, exportIndex);
        }

        return null;
    }

    public override async Task<ExportResult> ExportAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_export.TryGetTarget(out var export))
            {
                export = await Session.ResolveDeferredExportAsync(_provider, _packageFile, _exportIndex, ct)
                    .ConfigureAwait(false);
                _export.SetTarget(export);
            }

            var exporter = ExportSession.CreateExporter(export);
            exporter._session = _session;
            return await exporter.ExportAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error(exception, "Failed to load deferred export");
            return ExportResult.Failure(ObjectPath, exception);
        }
    }

    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default) =>
        throw new NotSupportedException("Deferred exporters resolve their concrete exporter at worker execution time.");
}
