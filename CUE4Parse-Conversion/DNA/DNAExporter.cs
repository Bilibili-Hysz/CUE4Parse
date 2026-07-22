using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.Utils;

namespace CUE4Parse_Conversion.DNA;

public class DNAExporter : ExporterBase
{
    private readonly UDNAAsset _dnaAsset;

    public DNAExporter(UDNAAsset dnaAsset, ExporterOptions options) : base(dnaAsset, options)
    {
        _dnaAsset = dnaAsset;
    }

    public override bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath)
    {
        var exportSavePath = GetExportSavePath();
        if (!string.IsNullOrEmpty(_dnaAsset.DnaFileName))
        {
            var exportName = Path.GetFileNameWithoutExtension(_dnaAsset.DnaFileName);
            exportSavePath = exportSavePath.SubstringBeforeWithLast('/') + exportName;
        }
        else
        {
            exportSavePath = exportSavePath.Replace(':', '_');
        }
        savedFilePath = FixAndCreatePath(baseDirectory, exportSavePath, "dna");
        label = $"DNA export for '{_dnaAsset.GetPathName()}'";

        if (_dnaAsset.DNAData is null || _dnaAsset.DNAData.Value.Length == 0)
        {
            label = "No DNA data to export.";
            return false;
        }

        try
        {
            File.WriteAllBytes(savedFilePath, _dnaAsset.DNAData.Value);
            return File.Exists(savedFilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            label = $"Failed to export DNA: {e.Message}";
            return false;
        }
    }

    public override bool TryWriteToZip(out byte[] zipFile)
    {
        throw new NotImplementedException();
    }

    public override void AppendToZip()
    {
        throw new NotImplementedException();
    }
}
