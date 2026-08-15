using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private const int MaxInFlightBatchSize = 64;

    internal readonly Action<StreamingLevelFilterArgs, CancellationToken>? _streamingLevelFilter = streamingLevelFilter;
    internal readonly SemaphoreSlim _streamingLevelFilterLock = new(1, 1);

    /// <summary>Optional host-provided mesh-format extension. Null preserves upstream behavior.</summary>
    public IExportSessionExtension? Extension { get; init; }

    /// <summary>Stable identity shared by Export Session and FModel Print feedback.</summary>
    public string OperationId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Default export parallelism is capped because UE assets are memory-heavy:
    /// mesh decoding, texture mip encoding and UEFormat compression can each keep
    /// several large buffers alive at once. Hosts may opt into a different value
    /// explicitly when they have measured sufficient memory headroom.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    private DirectoryInfo? _baseDirectory;
    internal DirectoryInfo BaseDirectory => _baseDirectory ?? throw new InvalidOperationException("Session is not currently running.");

    private ExportOptions? _options;
    internal ExportOptions Options => _options ?? throw new InvalidOperationException("Session is not currently running.");

    private int _totalQueued;
    public int TotalQueued => Volatile.Read(ref _totalQueued);
    public bool HasQueuedItems => TotalQueued > 0;

    private int _running;
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    private int _disposed;

    /// <summary>
    /// A queue item contains only the de-duplication key. The exporter itself is held
    /// by <see cref="ExporterRegistration"/> while it is pending/running. This is
    /// important for Remove(): a canceled item must be releasable even though the
    /// concurrent queue cannot remove an arbitrary item in-place.
    /// </summary>
    private readonly record struct QueuedExporter(string ObjectPath, long Generation);

    /// <summary>
    /// Keeps a strong reference only while an exporter is pending or running.
    /// Completed exporters are represented by a weak reference so path de-duplication
    /// remains stable without retaining every UObject and package graph until the
    /// whole session finishes.
    /// </summary>
    private sealed class ExporterRegistration(ExporterBase exporter, long generation)
    {
        private ExporterBase? _strongExporter = exporter;
        private WeakReference<ExporterBase>? _weakExporter;

        public long Generation { get; } = generation;

        public bool TryGetExporter(out ExporterBase result)
        {
            if (_strongExporter is { } strong)
            {
                result = strong;
                return true;
            }

            if (_weakExporter?.TryGetTarget(out var weak) == true)
            {
                result = weak;
                return true;
            }

            result = null!;
            return false;
        }

        public bool Matches(ExporterBase exporter)
        {
            return TryGetExporter(out var current) && ReferenceEquals(current, exporter);
        }

        public void MarkCompleted()
        {
            if (_strongExporter is { } strong)
            {
                _weakExporter = new WeakReference<ExporterBase>(strong);
                _strongExporter = null;
            }
        }
    }

    private readonly ConcurrentQueue<QueuedExporter> _roots = new();
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ExporterRegistration> _exporters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueStateLock = new();
    private long _nextQueueGeneration;

    public ExportSession Add(UObject export)
    {
        return export switch
        {
            UTexture texture => Add(new TextureExporter(texture)),
            UMaterialInterface material => Add(new MaterialExporter(material)),
            USkeletalMesh skeletalMesh => Add(new SkeletalMeshExporter(skeletalMesh)),
            UStaticMesh staticMesh => Add(new StaticMeshExporter(staticMesh)),
            USkeleton skeleton => Add(new SkeletonExporter(skeleton)),
            UPoseAsset poseAsset => Add(new PoseAssetExporter(poseAsset)),
            UAnimationAsset animation => Add(new AnimationExporter(animation)),
            UDNAAsset dna => Add(new DnaExporter(dna)),
            UWorld world => Add(new WorldExporter(world)),
            ALandscapeProxy landscape => Add(new LandscapeMeshExporter(landscape)),
            ULandscapeComponent landscape => Add(new LandscapeMeshExporter2(landscape)),
            USplineMeshComponent spline => Add(new SplineMeshExporter(spline)),
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
    /// This lets adapters inspect the same queue-owned exporter that will later write.
    ///
    /// <para>
    /// Once a path has completed, its strong exporter reference is released. If the
    /// weak canonical exporter is still alive it is returned for projection; if it has
    /// already been collected, the supplied exporter is prepared as a short-lived
    /// projection-only instance and is deliberately not queued a second time.
    /// </para>
    /// </summary>
    public ExporterBase AddOrGet(ExporterBase exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        lock (_queueStateLock)
        {
            ThrowIfDisposed();

            if (_exporters.TryGetValue(exporter.ObjectPath, out var existing))
            {
                if (existing.TryGetExporter(out var canonical))
                    return canonical;

                // The path was already exported, but the canonical exporter has
                // been collected. Do not enqueue a duplicate; return this short-lived
                // projection-only instance so callers can still inspect the format.
                exporter._session = this;
                return exporter;
            }

            // TODO: this prevents 2 exporters messing with the same file from being enqueued in the same run (e.g. MeshExporter / RawDataExporter)
            if (!_paths.TryAdd(exporter.ObjectPath, 0))
            {
                // A completed path can remain in the lightweight path registry after
                // its exporter has been released. Preserve de-duplication and return
                // a non-queued instance for projection-only callers.
                exporter._session = this;
                return exporter;
            }

            // Prepare canonical identity, generation and accounting before publishing to the runnable queue.
            exporter._session = this;
            var queuedExporter = new QueuedExporter(exporter.ObjectPath, ++_nextQueueGeneration);
            _exporters[exporter.ObjectPath] = new ExporterRegistration(exporter, queuedExporter.Generation);
            Interlocked.Increment(ref _totalQueued);
            _roots.Enqueue(queuedExporter);
        }

        SafeOnPropertyChanged(nameof(TotalQueued));
        SafeOnPropertyChanged(nameof(HasQueuedItems));
        exporter.Log.Debug("Queued for export");
        return exporter;
    }

    public bool Remove(string objectPath)
    {
        lock (_queueStateLock)
        {
            ThrowIfDisposed();
            // The exporter stays in _roots but will not be processed; the generation
            // check in RunAsync ignores it after the path registration is removed.
            if (IsRunning || !_paths.TryRemove(objectPath, out _))
                return false;

            _exporters.TryRemove(objectPath, out _);
            Interlocked.Decrement(ref _totalQueued);
        }

        SafeOnPropertyChanged(nameof(TotalQueued));
        SafeOnPropertyChanged(nameof(HasQueuedItems));
        return true;
    }

    public void Clear()
    {
        lock (_queueStateLock)
        {
            ThrowIfDisposed();
            if (IsRunning)
                throw new InvalidOperationException("Cannot clear an ExportSession while it is running.");

            ClearQueueCore();
        }

        SafeOnPropertyChanged(nameof(TotalQueued));
        SafeOnPropertyChanged(nameof(HasQueuedItems));
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(string baseDirectory, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        lock (_queueStateLock)
        {
            ThrowIfDisposed();
            if (IsRunning)
                throw new InvalidOperationException("Session is already running.");

            Volatile.Write(ref _running, 1);
        }

        var results = new ConcurrentQueue<ExportResult>();
        var completed = 0;
        var batchSize = GetBatchSize();
        var current = new List<ExporterBase>(batchSize);
        try
        {
            SafeOnPropertyChanged(nameof(IsRunning));
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
            ArgumentNullException.ThrowIfNull(options);
            _baseDirectory = new DirectoryInfo(baseDirectory);
            _options = options;

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct };
            while (true)
            {
                current.Clear();
                while (current.Count < batchSize && _roots.TryDequeue(out var queuedExporter))
                {
                    ct.ThrowIfCancellationRequested();
                    ExporterBase? exporter = null;
                    lock (_queueStateLock)
                    {
                        if (_exporters.TryGetValue(queuedExporter.ObjectPath, out var canonical)
                            && canonical.Generation == queuedExporter.Generation
                            && canonical.TryGetExporter(out var currentExporter))
                        {
                            exporter = currentExporter;
                        }
                    }

                    if (exporter is not null) // null = exporter was removed or superseded
                        current.Add(exporter);
                }

                if (current.Count == 0) break;

                await Parallel.ForEachAsync(current, parallelOptions, Process).ConfigureAwait(false);
                MarkBatchCompleted(current);
            }
        }
        finally // just in case cancellation is requested, we still need to clear things up
        {
            current.Clear();
            var stillQueued = TotalQueued;
            var count = Volatile.Read(ref completed);

            try
            {
                lock (_queueStateLock)
                {
                    ClearQueueCore();
                }
            }
            finally
            {
                // State reset must not be placed after a host callback. A broken
                // progress/UI subscriber must never leave the session running or
                // retain the active options and output directory.
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

        async ValueTask Process(ExporterBase exporter, CancellationToken token)
        {
            var result = await exporter.ExportAsync(token).ConfigureAwait(false);
            results.Enqueue(result);

            var stillQueued = Interlocked.Decrement(ref _totalQueued);
            var count = Interlocked.Increment(ref completed);
            SafeOnPropertyChanged(nameof(TotalQueued));
            SafeOnPropertyChanged(nameof(HasQueuedItems));

            SafeReport(progress, new ExportProgress(count, count + stillQueued, result));
        }
    }

    private int GetBatchSize()
    {
        // Do not materialize the entire session queue into a strong-reference list:
        // the queue may contain thousands of UObject-backed exporters. A small
        // multiple of the worker count keeps the pipeline fed while allowing the
        // completed batch to release its exporter references before the next batch
        // is dequeued. The long calculation also keeps an invalidly large explicit
        // MaxDegreeOfParallelism from overflowing before ParallelOptions validates it.
        var parallelism = Math.Max(1L, MaxDegreeOfParallelism);
        return (int)Math.Min(MaxInFlightBatchSize, parallelism * 2L);
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

    private void MarkBatchCompleted(IReadOnlyList<ExporterBase> exporters)
    {
        lock (_queueStateLock)
        {
            foreach (var exporter in exporters)
            {
                if (_exporters.TryGetValue(exporter.ObjectPath, out var registration)
                    && registration.Matches(exporter))
                {
                    registration.MarkCompleted();
                }
            }
        }
    }

    private void ClearQueueCore()
    {
        _roots.Clear();
        _paths.Clear();
        _exporters.Clear();
        Interlocked.Exchange(ref _totalQueued, 0);
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
        lock (_queueStateLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (IsRunning)
                throw new InvalidOperationException("Cannot dispose an ExportSession while it is running.");

            Volatile.Write(ref _disposed, 1);
            ClearQueueCore();
        }

        _options = null;
        _baseDirectory = null;
        PropertyChanged = null;
        _streamingLevelFilterLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

