using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;

namespace CUE4Parse_Conversion;

public sealed class ExportSession(Action<StreamingLevelFilterArgs, CancellationToken>? streamingLevelFilter = null) : INotifyPropertyChanged, IDisposable
{
    private const int MaxActiveTaskCount = 64;

    internal readonly Action<StreamingLevelFilterArgs, CancellationToken>? _streamingLevelFilter = streamingLevelFilter;
    internal readonly SemaphoreSlim _streamingLevelFilterLock = new(1, 1);

    /// <summary>Optional host-provided mesh-format extension. Null preserves upstream behavior.</summary>
    public IExportSessionExtension? Extension { get; init; }

    /// <summary>Stable identity shared by Export Session and FModel Print feedback.</summary>
    public string OperationId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Maximum active exporters. The scheduler caps the effective value at 64 because
    /// mesh decoding, texture encoding and compression may each own large buffers.
    /// Pending built-in work is represented by lightweight deferred descriptors.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    private DirectoryInfo? _baseDirectory;
    internal DirectoryInfo BaseDirectory => _baseDirectory ?? throw new InvalidOperationException("Session is not currently running.");

    private ExportOptions? _options;
    internal ExportOptions Options => _options ?? throw new InvalidOperationException("Session is not currently running.");

    private readonly ExportTaskQueue _taskQueue = new();
    private readonly ExportResourceManager _resourceManager = new();
    private readonly object _lifecycleLock = new();
    private readonly AsyncLocal<int> _exporterExecutionDepth = new();

    public int TotalQueued => _taskQueue.OutstandingCount;
    public bool HasQueuedItems => TotalQueued > 0;

    private int _running;
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    private int _disposed;

    /// <summary>
    /// Queues a built-in exporter for an Unreal object. Provider-backed package
    /// exports are held weakly and reloaded by the session resource manager if they
    /// are collected before a worker starts. Use <see cref="Add(ExporterBase)"/> when
    /// the exact in-memory exporter state must remain alive.
    /// </summary>
    public ExportSession Add(UObject export)
    {
        ArgumentNullException.ThrowIfNull(export);
        EnsureSupportedExportType(export);
        AddOrGet(DeferredObjectExporter.TryCreate(export) ?? CreateExporter(export));
        return this;
    }

    private static void EnsureSupportedExportType(UObject export)
    {
        if (export is UTexture
            or UMaterialInterface
            or USkeletalMesh
            or UStaticMesh
            or USkeleton
            or UPoseAsset
            or UAnimationAsset
            or UDNAAsset
            or UWorld
            or ALandscapeProxy
            or ULandscapeComponent
            or USplineMeshComponent)
        {
            return;
        }

        throw new NotSupportedException($"Could not create exporter for export of type '{export.GetType().Name}'.");
    }

    internal static ExporterBase CreateExporter(UObject export)
    {
        return export switch
        {
            UTexture texture => new TextureExporter(texture),
            UMaterialInterface material => new MaterialExporter(material),
            USkeletalMesh skeletalMesh => new SkeletalMeshExporter(skeletalMesh),
            UStaticMesh staticMesh => new StaticMeshExporter(staticMesh),
            USkeleton skeleton => new SkeletonExporter(skeleton),
            UPoseAsset poseAsset => new PoseAssetExporter(poseAsset),
            UAnimationAsset animation => new AnimationExporter(animation),
            UDNAAsset dna => new DnaExporter(dna),
            UWorld world => new WorldExporter(world),
            ALandscapeProxy landscape => new LandscapeMeshExporter(landscape),
            ULandscapeComponent landscape => new LandscapeMeshExporter2(landscape),
            USplineMeshComponent spline => new SplineMeshExporter(spline),
            _ => throw new NotSupportedException($"Could not create exporter for export of type '{export.GetType().Name}'.")
        };
    }

    public ExportSession Add(ExporterBase exporter)
    {
        AddOrGet(exporter);
        return this;
    }

    /// <summary>
    /// Queues an exporter and returns the canonical exporter for its ObjectPath.
    /// Projection callers may receive their supplied exporter as a non-queued view
    /// when that path is already running or completed. A pending deferred descriptor
    /// is promoted to the concrete exporter so projection and the later disk write
    /// can share the same cached output bytes.
    /// </summary>
    public ExporterBase AddOrGet(ExporterBase exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ThrowIfDisposed();

        exporter._session = this;
        var isDependency = IsRunning && _exporterExecutionDepth.Value > 0;
        var canonical = _taskQueue.AddOrGet(exporter, isDependency, out var added, out var promoted);
        canonical._session = this;

        if (added)
        {
            SafeOnPropertyChanged(nameof(TotalQueued));
            SafeOnPropertyChanged(nameof(HasQueuedItems));
            canonical.Log.Debug(isDependency ? "Queued export dependency" : "Queued for export");
        }
        else if (promoted)
        {
            canonical.Log.Debug("Promoted deferred export task for output projection");
        }

        return canonical;
    }

    public bool Remove(string objectPath)
    {
        ThrowIfDisposed();
        if (IsRunning || !_taskQueue.Remove(objectPath))
            return false;

        SafeOnPropertyChanged(nameof(TotalQueued));
        SafeOnPropertyChanged(nameof(HasQueuedItems));
        return true;
    }

    public void Clear()
    {
        ThrowIfDisposed();
        if (IsRunning)
            throw new InvalidOperationException("Cannot clear an ExportSession while it is running.");

        ClearQueueCore();
        SafeOnPropertyChanged(nameof(TotalQueued));
        SafeOnPropertyChanged(nameof(HasQueuedItems));
    }

    /// <summary>
    /// Keeps a running session open while a host streams roots into it. The lease
    /// must be opened before <see cref="RunAsync"/> can observe an empty queue and
    /// must be disposed when production is complete. Exporters continue to use the
    /// regular Add methods; this lease owns only producer lifetime.
    /// </summary>
    public IDisposable OpenProducer()
    {
        ThrowIfDisposed();
        return _taskQueue.OpenProducer();
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(
        string baseDirectory,
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(options);
        if (MaxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), "Export parallelism must be at least one.");

        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (IsRunning)
                throw new InvalidOperationException("Session is already running.");

            _baseDirectory = new DirectoryInfo(baseDirectory);
            _options = options;
            _taskQueue.BeginRun();
            Volatile.Write(ref _running, 1);
        }

        var results = new ConcurrentQueue<ExportResult>();
        var completed = 0;
        try
        {
            SafeOnPropertyChanged(nameof(IsRunning));

            var workerCount = Math.Min(MaxActiveTaskCount, MaxDegreeOfParallelism);
            var workers = new Task[workerCount];
            for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
            {
                // Exporter conversion performs substantial synchronous work before
                // its first asynchronous file write. Starting workers on the pool is
                // required so a streaming host can continue producing queue roots.
                workers[workerIndex] = Task.Run(() => WorkerAsync(ct), CancellationToken.None);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            var stillQueued = TotalQueued;
            var count = Volatile.Read(ref completed);

            try
            {
                _taskQueue.EndRunAndClear();
                _resourceManager.Clear();
            }
            finally
            {
                // Reset state before invoking host callbacks. A broken UI/progress
                // subscriber must never leave active options, paths or queue roots.
                _options = null;
                _baseDirectory = null;
                Volatile.Write(ref _running, 0);

                SafeOnPropertyChanged(nameof(TotalQueued));
                SafeOnPropertyChanged(nameof(HasQueuedItems));
                SafeOnPropertyChanged(nameof(IsRunning));
                SafeReport(progress, new ExportProgress(count, count + stillQueued));
            }
        }

        return [.. results];

        async Task WorkerAsync(CancellationToken token)
        {
            while (await _taskQueue.DequeueAsync(token).ConfigureAwait(false) is { } lease)
            {
                ExportResult? result = null;
                var previousDepth = _exporterExecutionDepth.Value;
                _exporterExecutionDepth.Value = previousDepth + 1;
                try
                {
                    result = await lease.Exporter.ExportAsync(token).ConfigureAwait(false);
                    results.Enqueue(result);
                }
                finally
                {
                    _exporterExecutionDepth.Value = previousDepth;
                    var stillQueued = _taskQueue.Complete(lease);
                    SafeOnPropertyChanged(nameof(TotalQueued));
                    SafeOnPropertyChanged(nameof(HasQueuedItems));

                    if (result is not null)
                    {
                        var count = Interlocked.Increment(ref completed);
                        SafeReport(progress, new ExportProgress(count, count + stillQueued, result));
                    }
                }
            }
        }
    }

    internal Task<UObject> ResolveDeferredExportAsync(
        IFileProvider provider,
        GameFile packageFile,
        int exportIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _resourceManager.ResolveExportAsync(provider, packageFile, exportIndex, cancellationToken);
    }

    internal string ResolveOutputPath(string savePath, string ext, string? nameSuffix = null)
    {
        ThrowIfDisposed();
        var fullPath = Path.Combine(BaseDirectory.FullName, savePath) + nameSuffix + '.' + ext.ToLower();
        var dir = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"Cannot determine directory for path: {fullPath}");
        Directory.CreateDirectory(dir);
        return fullPath.Replace('/', '\\');
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ClearQueueCore()
    {
        _taskQueue.Clear();
        _resourceManager.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ExportSession));
    }

    private void SafeOnPropertyChanged([CallerMemberName] string? name = null)
    {
        try
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "ExportSession PropertyChanged subscriber failed for {PropertyName}", name);
        }
    }

    private static void SafeReport(IProgress<ExportProgress>? progress, ExportProgress value)
    {
        if (progress is null) return;
        try
        {
            progress.Report(value);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "ExportSession progress subscriber failed");
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (IsRunning)
                throw new InvalidOperationException("Cannot dispose an ExportSession while it is running.");

            Volatile.Write(ref _disposed, 1);
            _taskQueue.Dispose();
            _resourceManager.Clear();
        }

        _options = null;
        _baseDirectory = null;
        PropertyChanged = null;
        _streamingLevelFilterLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
