using System.Text;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion.Animations.UEFormat;
using CUE4Parse_Conversion.UEFormat.Structs;

namespace CUE4Parse.Tests;

public class FModelSkeletonExtensionWriterTests
{
    [Fact]
    public void WritesModelIdentityWithExactV1Payload()
    {
        var identity = Identity();
        using var archive = new FArchiveWriter();

        FModelSkeletonExtensionWriter.WriteModelIdentity(archive, identity);

        var chunk = ReadChunks(archive.GetBuffer()).Single();
        Assert.Equal("FMODEL_SKELETON_IDENTITY", chunk.Name);
        Assert.Equal(1, chunk.Count);
        using var reader = new BinaryReader(new MemoryStream(chunk.Payload), Encoding.UTF8);
        Assert.Equal((byte) 1, reader.ReadByte());
        Assert.Equal(identity.SkeletonPath, ReadFString(reader));
        Assert.Equal((byte) 1, reader.ReadByte());
        Assert.Equal(identity.SkeletonGuid!.Value.AsByteSpan().ToArray(), reader.ReadBytes(FGuid.Size));
        Assert.Equal(identity.BoneLayoutHash, reader.ReadBytes(32));
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    [Fact]
    public void WritesTargetSkeletonAfterUnknownChunkWithoutChangingItsLayout()
    {
        var identity = Identity();
        using var archive = new FArchiveWriter();
        using (var unknown = new FDataChunk("FUTURE_CHUNK", 7))
        {
            unknown.Write([0x10, 0x20, 0x30]);
            unknown.Serialize(archive);
        }

        FModelSkeletonExtensionWriter.WriteTargetSkeleton(archive, "/Game/Animations/A_Run.A_Run", identity);

        var chunks = ReadChunks(archive.GetBuffer());
        Assert.Collection(chunks,
            chunk =>
            {
                Assert.Equal("FUTURE_CHUNK", chunk.Name);
                Assert.Equal(7, chunk.Count);
                Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, chunk.Payload);
            },
            chunk =>
            {
                Assert.Equal("FMODEL_TARGET_SKELETON", chunk.Name);
                Assert.Equal(1, chunk.Count);
                using var reader = new BinaryReader(new MemoryStream(chunk.Payload), Encoding.UTF8);
                Assert.Equal((byte) 1, reader.ReadByte());
                Assert.Equal("/Game/Animations/A_Run.A_Run", ReadFString(reader));
                Assert.Equal(identity.SkeletonPath, ReadFString(reader));
                Assert.Equal((byte) 1, reader.ReadByte());
                Assert.Equal(identity.SkeletonGuid!.Value.AsByteSpan().ToArray(), reader.ReadBytes(FGuid.Size));
                Assert.Equal(identity.BoneLayoutHash, reader.ReadBytes(32));
                Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
            });
    }

    [Fact]
    public void WritesMissingGuidMarkerWithoutGuidBytes()
    {
        var identity = new FModelSkeletonIdentity("/Game/SK_Hero.SK_Hero", null, Enumerable.Repeat((byte) 0x5a, 32).ToArray());
        using var archive = new FArchiveWriter();

        FModelSkeletonExtensionWriter.WriteModelIdentity(archive, identity);

        var chunk = ReadChunks(archive.GetBuffer()).Single();
        using var reader = new BinaryReader(new MemoryStream(chunk.Payload), Encoding.UTF8);
        Assert.Equal((byte) 1, reader.ReadByte());
        Assert.Equal(identity.SkeletonPath, ReadFString(reader));
        Assert.Equal((byte) 0, reader.ReadByte());
        Assert.Equal(identity.BoneLayoutHash, reader.ReadBytes(32));
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    private static FModelSkeletonIdentity Identity() => new(
        "/Game/Skeletons/SK_Hero.SK_Hero",
        new FGuid(0x01234567, 0x89abcdef, 0x10203040, 0x50607080),
        Enumerable.Range(0, 32).Select(i => (byte) i).ToArray());

    private static List<(string Name, int Count, byte[] Payload)> ReadChunks(byte[] bytes)
    {
        var chunks = new List<(string Name, int Count, byte[] Payload)>();
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var name = ReadFString(reader);
            var count = reader.ReadInt32();
            var size = reader.ReadInt32();
            chunks.Add((name, count, reader.ReadBytes(size)));
        }
        return chunks;
    }

    private static string ReadFString(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
}
