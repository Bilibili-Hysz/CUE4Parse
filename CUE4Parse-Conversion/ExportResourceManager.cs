using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;

namespace CUE4Parse_Conversion;

/// <summary>
/// Coalesces deferred package loads without turning the session into a package cache.
/// A package is held weakly after loading; active exporters provide the only strong
/// leases, so package data can be reclaimed naturally between task waves.
/// </summary>
internal sealed class ExportResourceManager
{
    private readonly object _gate = new();
    private readonly Dictionary<PackageKey, PackageSlot> _packages = new(PackageKeyComparer.Instance);

    public async Task<UObject> ResolveExportAsync(
        IFileProvider provider,
        GameFile packageFile,
        int exportIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(packageFile);
        cancellationToken.ThrowIfCancellationRequested();

        PackageSlot slot;
        lock (_gate)
        {
            var key = new PackageKey(provider, packageFile.Path);
            if (!_packages.TryGetValue(key, out slot!))
            {
                slot = new PackageSlot(provider, packageFile);
                _packages.Add(key, slot);
            }
        }

        var package = await slot.GetPackageAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return package.GetExport(exportIndex) ??
               throw new InvalidOperationException($"Package '{package.Name}' no longer has export index {exportIndex}.");
    }

    public void Clear()
    {
        lock (_gate)
            _packages.Clear();
    }

    private readonly record struct PackageKey(IFileProvider Provider, string Path);

    private sealed class PackageKeyComparer : IEqualityComparer<PackageKey>
    {
        public static PackageKeyComparer Instance { get; } = new();

        public bool Equals(PackageKey x, PackageKey y) =>
            ReferenceEquals(x.Provider, y.Provider) &&
            string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(PackageKey obj) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Provider), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
    }

    private sealed class PackageSlot(IFileProvider provider, GameFile packageFile)
    {
        private readonly object _gate = new();
        private WeakReference<IPackage>? _package;
        private Task<IPackage>? _loadTask;

        public Task<IPackage> GetPackageAsync(CancellationToken cancellationToken)
        {
            Task<IPackage> loadTask;
            TaskCompletionSource<IPackage>? loadCompletion = null;
            lock (_gate)
            {
                if (_package?.TryGetTarget(out var package) == true)
                    return Task.FromResult(package);

                if (_loadTask is null)
                {
                    // Publish the shared task before starting the provider load. An
                    // async provider is allowed to complete synchronously; assigning
                    // LoadAndRememberAsync() directly could otherwise write its
                    // completed Task back after the method already cleared _loadTask.
                    loadCompletion = new TaskCompletionSource<IPackage>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _loadTask = loadCompletion.Task;
                }

                loadTask = _loadTask;
            }

            if (loadCompletion is not null)
                _ = LoadAndRememberAsync(loadCompletion);

            return loadTask.WaitAsync(cancellationToken);
        }

        private async Task LoadAndRememberAsync(TaskCompletionSource<IPackage> completion)
        {
            try
            {
                var package = await provider.LoadPackageAsync(packageFile).ConfigureAwait(false);
                lock (_gate)
                    _package = new WeakReference<IPackage>(package);
                completion.TrySetResult(package);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loadTask, completion.Task))
                        _loadTask = null;
                }
            }
        }
    }
}
