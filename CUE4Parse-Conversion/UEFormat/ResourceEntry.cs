namespace CUE4Parse_Conversion.UEFormat;
using System.Collections.ObjectModel;

public sealed class ResourceEntry
{
    public int Id { get; }
    public string UnrealObjectPath { get; }
    public string RelativeUri { get; }
    public string PhysicalTargetPath { get; }
    public string Mime { get; }
    public IReadOnlyList<byte> Bytes { get; }
    public bool IsExternal { get; }

    public ResourceEntry(int id, string unrealObjectPath, string relativeUri, string mime, byte[] bytes, bool isExternal, string? physicalTargetPath = null)
    {
        Id = id;
        UnrealObjectPath = unrealObjectPath;
        RelativeUri = relativeUri;
        PhysicalTargetPath = physicalTargetPath ?? relativeUri;
        Mime = mime;
        Bytes = new ReadOnlyCollection<byte>(bytes.ToArray());
        IsExternal = isExternal;
    }
}
