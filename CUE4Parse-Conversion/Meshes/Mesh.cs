using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.UEFormat;

namespace CUE4Parse_Conversion.Meshes
{
    public class Mesh : ExporterBase
    {
        public readonly string FileName;
        public readonly byte[] FileData;
        public readonly List<MaterialExporter2> Materials;

        /// <summary>
        /// Optional manifest of external resource files (textures, JSON) that must
        /// be written before the model file. When set, resources are written first
        /// and the model is only published if all resources succeed (atomic publish).
        /// When null, legacy behavior applies (materials written sequentially).
        /// </summary>
        public ResourceManifest? ExportManifest { get; init; }

        // Test-only seam for exercising rollback after a prior sidecar was promoted.
        internal Func<int, bool>? FinalResourcePromotionFailure { get; init; }

        public Mesh(string fileName, byte[] fileData, List<MaterialExporter2> materials)
        {
            FileName = fileName;
            FileData = fileData;
            Materials = materials;
        }

        public override bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath)
        {
            label = string.Empty;
            savedFilePath = string.Empty;
            var externalResources = ExportManifest?.Resources.Where(resource => resource.IsExternal).ToArray() ?? [];
            if (FileData.Length <= 0)
            {
                if (externalResources.Length > 0)
                {
                    var failedModelPath = FixAndCreatePath(baseDirectory, FileName);
                    InvalidateModel(failedModelPath);
                }
                return false;
            }

            if (externalResources.Length > 0)
            {
                string operationFinalPath;
                Dictionary<ResourceEntry, string> finalResourcePaths;
                try
                {
                    operationFinalPath = FixAndCreatePath(baseDirectory, FileName);
                    finalResourcePaths = externalResources.ToDictionary(
                        resource => resource,
                        resource => FixAndCreatePath(baseDirectory, resource.PhysicalTargetPath));
                }
                catch
                {
                    return false;
                }
                if (!InvalidateModel(operationFinalPath)) return false;

                DirectoryInfo? stagingDirectory = null;
                var replacedResources = new List<(string FinalPath, string? BackupPath)>();
                try
                {
                    stagingDirectory = Directory.CreateDirectory(Path.Combine(baseDirectory.FullName, ".ueformat-stage-" + Path.GetRandomFileName()));
                    var stagedResources = new List<(string StagedPath, string FinalPath)>();
                    foreach (var resource in ExportManifest!.Resources)
                    {
                        if (!resource.IsExternal)
                        {
                            ExportManifest.MarkWritten(resource.UnrealObjectPath);
                            continue;
                        }

                        if (resource.Bytes.Count == 0)
                        {
                            ExportManifest.MarkFailed(resource.UnrealObjectPath);
                            return false;
                        }

                        var finalResourcePath = finalResourcePaths[resource];
                        var stagedResourcePath = FixAndCreatePath(stagingDirectory, resource.PhysicalTargetPath);
                        var bytes = resource.Bytes.ToArray();
                        File.WriteAllBytes(stagedResourcePath, bytes);
                        if (!File.ReadAllBytes(stagedResourcePath).SequenceEqual(bytes))
                            throw new IOException($"Failed to verify staged resource '{resource.PhysicalTargetPath}'.");
                        stagedResources.Add((stagedResourcePath, finalResourcePath));
                    }

                    var stagedModelPath = FixAndCreatePath(stagingDirectory, FileName);
                    File.WriteAllBytes(stagedModelPath, FileData);
                    if (!File.ReadAllBytes(stagedModelPath).SequenceEqual(FileData))
                        throw new IOException($"Failed to verify staged model '{FileName}'.");

                    var rollbackDirectory = Directory.CreateDirectory(Path.Combine(stagingDirectory.FullName, ".rollback"));
                    for (var promotionIndex = 0; promotionIndex < stagedResources.Count; promotionIndex++)
                    {
                        var (stagedPath, finalResourcePath) = stagedResources[promotionIndex];
                        string? backupPath = null;
                        if (File.Exists(finalResourcePath))
                        {
                            backupPath = FixAndCreatePath(rollbackDirectory,
                                Path.GetRelativePath(baseDirectory.FullName, finalResourcePath));
                            File.Move(finalResourcePath, backupPath);
                        }

                        replacedResources.Add((finalResourcePath, backupPath));
                        if (FinalResourcePromotionFailure?.Invoke(promotionIndex) == true)
                            throw new IOException($"Failed to promote staged resource '{finalResourcePath}'.");
                        File.Move(stagedPath, finalResourcePath, overwrite: true);
                    }
                    foreach (var resource in externalResources)
                    {
                        ExportManifest.MarkWritten(resource.UnrealObjectPath);
                    }

                    File.Move(stagedModelPath, operationFinalPath, overwrite: true);
                    savedFilePath = operationFinalPath;
                    label = Path.GetFileName(operationFinalPath);
                    return true;
                }
                catch
                {
                    foreach (var (finalResourcePath, backupPath) in replacedResources.AsEnumerable().Reverse())
                    {
                        try
                        {
                            if (backupPath is null)
                                File.Delete(finalResourcePath);
                            else
                                File.Move(backupPath, finalResourcePath, overwrite: true);
                        }
                        catch
                        {
                            // Continue restoring the remaining originals even if one rollback fails.
                        }
                    }
                    foreach (var resource in externalResources.Where(resource => ExportManifest!.GetStatus(resource.UnrealObjectPath) == WriteStatus.Pending))
                        ExportManifest!.MarkFailed(resource.UnrealObjectPath);
                    InvalidateModel(operationFinalPath);
                    return false;
                }
                finally
                {
                    if (stagingDirectory?.Exists == true)
                    {
                        try
                        {
                            stagingDirectory.Delete(true);
                        }
                        catch
                        {
                            // Publication outcome is authoritative; abandoned staging is never a final export.
                        }
                    }
                }
            }

            if (ExportManifest is { Resources.Count: > 0 })
            {
                foreach (var resource in ExportManifest.Resources)
                    ExportManifest.MarkWritten(resource.UnrealObjectPath);
            }
            else
            {
                // Legacy path: write materials via sequential loop
                foreach (var material in Materials)
                {
                    if (!material.TryWriteToDir(baseDirectory, out _, out _))
                        return false;
                }
            }

            // --- Phase 2: Write model to temp file, then rename atomically ---
            var finalPath = FixAndCreatePath(baseDirectory, FileName);
            var finalDir = Path.GetDirectoryName(finalPath);
            if (finalDir != null) Directory.CreateDirectory(finalDir);
            var tempPath = Path.Combine(finalDir ?? baseDirectory.FullName, Path.GetRandomFileName());
            try
            {
                File.WriteAllBytes(tempPath, FileData);
                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                return false;
            }

            savedFilePath = finalPath;
            label = Path.GetFileName(finalPath);
            return true;
        }

        public override bool TryWriteToZip(out byte[] zipFile)
        {
            throw new NotImplementedException();
        }

        public override void AppendToZip()
        {
            throw new NotImplementedException();
        }

        private static bool InvalidateModel(string targetPath)
        {
            try
            {
                File.Delete(targetPath);
                return !File.Exists(targetPath);
            }
            catch
            {
                return false;
            }
        }
    }
}
