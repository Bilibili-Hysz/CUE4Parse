using CUE4Parse_Conversion.Exporters;

namespace CUE4Parse_Conversion;

/// <summary>
/// Owns ExportSession task identity, scheduling and exporter lifetime.
///
/// <para>
/// Queue nodes retain only one exporter descriptor. Built-in UObject work normally
/// uses <see cref="DeferredObjectExporter"/>, whose UObject reference is weak. A
/// custom exporter supplied through <see cref="ExportSession.Add(ExporterBase)"/>
/// remains strongly owned until it starts because its exact in-memory state is part
/// of that API's contract.
/// </para>
///
/// <para>
/// Work discovered by an active exporter is placed in the dependency queue ahead
/// of untouched roots. This prevents projected material/texture buffers from being
/// stranded behind a very large root queue. Only workers hold running exporters;
/// completed entries keep at most a weak canonical reference for de-duplication.
/// </para>
/// </summary>
internal sealed class ExportTaskQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<ExportTaskEntry> _dependencies = new();
    private readonly Queue<ExportTaskEntry> _roots = new();
    private readonly Dictionary<string, ExportTaskEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private TaskCompletionSource _stateChanged = NewSignal();
    private int _outstanding;
    private int _active;
    private int _producerCount;
    private bool _running;
    private bool _disposed;
    private long _generation;

    public int OutstandingCount => Volatile.Read(ref _outstanding);
    public int ActiveCount => Volatile.Read(ref _active);

    public ExporterBase AddOrGet(
        ExporterBase exporter,
        bool isDependency,
        out bool added,
        out bool promoted)
    {
        ArgumentNullException.ThrowIfNull(exporter);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_running && _outstanding == 0 && _producerCount == 0)
                throw new InvalidOperationException("The export queue has already drained and is not accepting new work.");

            if (_entries.TryGetValue(exporter.ObjectPath, out var existing))
            {
                added = false;
                promoted = existing.TryPromoteDeferred(exporter);
                if (isDependency && existing.TryPrioritize())
                {
                    // The original root token remains harmlessly in _roots. This
                    // dependency token will claim the entry first; the stale root
                    // token is skipped because the entry is no longer pending.
                    _dependencies.Enqueue(existing);
                    PulseAll();
                }

                if (promoted)
                    return exporter;

                if (existing.TryGetExporter(out var canonical))
                {
                    // A running deferred descriptor cannot expose the concrete
                    // TextureExporter required by projection. The supplied exporter
                    // is therefore a projection-only view; path de-duplication still
                    // prevents a second disk write.
                    if (canonical is DeferredObjectExporter && exporter is not DeferredObjectExporter)
                        return exporter;
                    return canonical;
                }

                // The path already completed and its canonical exporter was
                // collected. Preserve de-duplication while allowing a short-lived
                // projection-only exporter to inspect deterministic output metadata.
                return exporter;
            }

            var entry = new ExportTaskEntry(exporter);
            _entries.Add(exporter.ObjectPath, entry);
            if (isDependency)
            {
                entry.TryPrioritize();
                _dependencies.Enqueue(entry);
            }
            else
                _roots.Enqueue(entry);

            var outstanding = checked(_outstanding + 1);
            Volatile.Write(ref _outstanding, outstanding);
            added = true;
            promoted = false;
            PulseAll();
            return exporter;
        }
    }

    public bool Remove(string objectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectPath);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_running || !_entries.Remove(objectPath, out var entry) || !entry.TryRemove())
                return false;

            var outstanding = _outstanding - 1;
            Volatile.Write(ref _outstanding, outstanding);
            PulseAll();
            return true;
        }
    }

    public IDisposable OpenProducer()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_running && _outstanding == 0 && _producerCount == 0)
                throw new InvalidOperationException("The export queue has already drained and cannot be reopened.");

            checked { _producerCount++; }
            var generation = _generation;
            PulseAll();
            return new ProducerLease(this, generation);
        }
    }

    public void BeginRun()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_running)
                throw new InvalidOperationException("The export task queue is already running.");

            _running = true;
            PulseAll();
        }
    }

    public async ValueTask<ExportTaskLease?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task stateChanged;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_running)
                    throw new InvalidOperationException("The export task queue is not running.");

                while (TryDequeue(out var entry))
                {
                    if (!entry.TryStart(out var exporter))
                        continue;

                    var active = _active + 1;
                    Volatile.Write(ref _active, active);
                    return new ExportTaskLease(entry, exporter);
                }

                if (_outstanding == 0 && _producerCount == 0)
                    return null;

                stateChanged = _stateChanged.Task;
            }

            await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public int Complete(ExportTaskLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!lease.TryComplete() || !lease.Entry.TryComplete(lease.Exporter))
                throw new InvalidOperationException("An export task lease was completed more than once or no longer owns its queue entry.");

            var active = _active - 1;
            var outstanding = _outstanding - 1;
            Volatile.Write(ref _active, active);
            Volatile.Write(ref _outstanding, outstanding);
            PulseAll();
            return outstanding;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_running)
                throw new InvalidOperationException("Cannot clear the export task queue while it is running.");
            if (_producerCount != 0)
                throw new InvalidOperationException("Cannot clear the export task queue while a producer lease is open.");

            ClearCore();
        }
    }

    public void EndRunAndClear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _running = false;
            ClearCore();
        }
    }

    private bool TryDequeue(out ExportTaskEntry entry)
    {
        if (_dependencies.TryDequeue(out entry!)) return true;
        return _roots.TryDequeue(out entry!);
    }

    private void ReleaseProducer(long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _producerCount == 0)
                return;

            _producerCount--;
            PulseAll();
        }
    }

    private void ClearCore()
    {
        foreach (var entry in _entries.Values)
            entry.Release();
        _dependencies.Clear();
        _roots.Clear();
        _entries.Clear();
        _outstanding = 0;
        _active = 0;
        _producerCount = 0;
        _generation++;
        Volatile.Write(ref _outstanding, 0);
        Volatile.Write(ref _active, 0);
        PulseAll();
    }

    private void PulseAll()
    {
        var previous = _stateChanged;
        _stateChanged = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExportTaskQueue));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            ClearCore();
        }
    }

    internal sealed class ExportTaskLease(ExportTaskEntry entry, ExporterBase exporter)
    {
        private int _completed;
        internal ExportTaskEntry Entry { get; } = entry;
        public ExporterBase Exporter { get; } = exporter;
        internal bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;
    }

    internal sealed class ExportTaskEntry(ExporterBase exporter)
    {
        private ExporterBase? _strongExporter = exporter;
        private WeakReference<ExporterBase>? _weakExporter;
        private ExportTaskState _state = ExportTaskState.Pending;
        private bool _dependencyQueued;

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

        public bool TryPromoteDeferred(ExporterBase replacement)
        {
            if (_state != ExportTaskState.Pending ||
                _strongExporter is not DeferredObjectExporter ||
                replacement is DeferredObjectExporter)
                return false;

            _strongExporter = replacement;
            _weakExporter = null;
            return true;
        }

        public bool TryPrioritize()
        {
            if (_state != ExportTaskState.Pending || _dependencyQueued)
                return false;
            _dependencyQueued = true;
            return true;
        }

        public bool TryStart(out ExporterBase exporter)
        {
            if (_state != ExportTaskState.Pending || _strongExporter is not { } strong)
            {
                exporter = null!;
                return false;
            }

            _state = ExportTaskState.Running;
            exporter = strong;
            return true;
        }

        public bool TryComplete(ExporterBase exporter)
        {
            if (_state != ExportTaskState.Running || !ReferenceEquals(_strongExporter, exporter))
                return false;

            _weakExporter = new WeakReference<ExporterBase>(exporter);
            _strongExporter = null;
            _state = ExportTaskState.Completed;
            return true;
        }

        public bool TryRemove()
        {
            if (_state != ExportTaskState.Pending)
                return false;
            Release();
            return true;
        }

        public void Release()
        {
            _strongExporter = null;
            _weakExporter = null;
            _state = ExportTaskState.Removed;
        }
    }

    private sealed class ProducerLease(ExportTaskQueue owner, long generation) : IDisposable
    {
        private ExportTaskQueue? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseProducer(generation);
        }
    }

    private enum ExportTaskState : byte
    {
        Pending,
        Running,
        Completed,
        Removed
    }
}
