using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse_Conversion.Animations.UEFormat;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Meshes.UEFormat;
using CUE4Parse_Conversion.PoseAsset.Conversion;
using CUE4Parse_Conversion.PoseAsset.UEFormat;
using CUE4Parse_Conversion.UEFormat.Structs;

namespace CUE4Parse.Tests;

public class UEFormatIdentityIntegrationTests
{
    [Fact]
    public void WritesDeterministicIdentityFixtures()
    {
        var modelPath = Environment.GetEnvironmentVariable("FMODEL_IDENTITY_MODEL_FIXTURE_PATH");
        var animationPath = Environment.GetEnvironmentVariable("FMODEL_IDENTITY_ANIMATION_FIXTURE_PATH");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(animationPath))
            return;

        var skeleton = IdentitySkeleton();
        using var modelArchive = new FArchiveWriter();
        new UEModel("SK_Hero_Skeleton", skeleton, [new CSkelMeshBone { Name = new FName("root"), ParentIndex = -1 }], [], [], new ExporterOptions()).Save(modelArchive);
        var model = modelArchive.GetBuffer();

        using var animationArchive = new FArchiveWriter();
        new UEAnim("A_Run", AnimationSet("/Game/Hero/A_Run.A_Run"), 0,
            "/Game/Hero/A_Run.A_Run", FModelSkeletonIdentity.FromSkeleton(skeleton), new ExporterOptions()).Save(animationArchive);
        var animation = animationArchive.GetBuffer();

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(animationPath)!);
        File.WriteAllBytes(modelPath, model);
        File.WriteAllBytes(animationPath, animation);
        Assert.Equal(64, Convert.ToHexString(SHA256.HashData(model)).Length);
        Assert.Equal(64, Convert.ToHexString(SHA256.HashData(animation)).Length);
    }

    [Theory]
    [InlineData("/Game/Animations/A_Run.A_Run")]
    [InlineData("/Game/Animations/A_Montage.A_Montage")]
    [InlineData("/Game/Animations/A_Composite.A_Composite")]
    public void UEAnim_WritesStandardChunksThenOneTargetSkeletonWithExplicitSourcePath(string sourcePath)
    {
        using var archive = new FArchiveWriter();
        new UEAnim("Animation", AnimationSet("/Game/Animations/A_Segment.A_Segment"), 0, sourcePath, FModelSkeletonIdentity.FromSkeleton(IdentitySkeleton()), new ExporterOptions()).Save(archive);

        var chunks = ReadTopLevelChunks(archive.GetBuffer());
        Assert.Collection(chunks,
            chunk => Assert.Equal("METADATA", chunk.Name),
            chunk => Assert.Equal("TRACKS", chunk.Name),
            chunk => Assert.Equal("FMODEL_TARGET_SKELETON", chunk.Name));
        Assert.Equal(1, chunks.Count(chunk => chunk.Name == "FMODEL_TARGET_SKELETON"));
        Assert.Equal(sourcePath, ReadTargetSourcePath(chunks[^1].Payload));
        Assert.Equal(IdentitySkeleton().GetPathName(), ReadTargetSkeletonPath(chunks[^1].Payload));
    }

    [Fact]
    public void UEPose_WritesStandardChunksThenOneTargetSkeletonWithExplicitSourcePath()
    {
        var poseAsset = new CPoseAsset
        {
            CurveNames = ["Smile"],
            Poses =
            [
                new CPoseData
                {
                    PoseName = "Pose",
                    CurveData = [0.5f],
                    Keys =
                    [
                        new CPoseKey(
                            "root",
                            new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0),
                            new CUE4Parse.UE4.Objects.Core.Math.FQuat(0, 0, 0, 1),
                            new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1))
                    ]
                }
            ]
        };
        using var archive = new FArchiveWriter();

        new UEPose(
            "P_Pose",
            poseAsset,
            "/Game/Hero/P_Pose.P_Pose",
            FModelSkeletonIdentity.FromSkeleton(IdentitySkeleton()),
            new ExporterOptions()).Save(archive);

        var chunks = ReadTopLevelChunks(archive.GetBuffer());
        Assert.Equal(["POSES", "CURVES", "FMODEL_TARGET_SKELETON"], chunks.Select(chunk => chunk.Name));
        Assert.Equal("/Game/Hero/P_Pose.P_Pose", ReadTargetSourcePath(chunks[^1].Payload));
        Assert.Equal(IdentitySkeleton().GetPathName(), ReadTargetSkeletonPath(chunks[^1].Payload));
    }

    [Fact]
    public void AnimExporter_UnresolvedTopLevelSkeletonThrowsBeforePublishingAnySequences()
    {
        var animation = new UAnimSequence { Name = "A_Unresolved", Skeleton = new FPackageIndex() };

        var error = Assert.Throws<ArgumentException>(() => new AnimExporter(animation, new ExporterOptions()));

        Assert.Contains("resolvable target skeleton", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("montage")]
    [InlineData("composite")]
    public void AnimExporter_UsesTopLevelAssetIdentityForDirectAndFlattenedOutputs(string kind)
    {
        var skeleton = IdentitySkeleton();
        var segment = Sequence("A_Segment");
        UAnimationAsset topLevel = kind switch
        {
            "sequence" => Sequence("A_Run"),
            "montage" => Montage("A_Montage"),
            "composite" => Composite("A_Composite"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var package = new SkeletonPackage(skeleton, segment, topLevel);
        segment.Skeleton = new FPackageIndex(package, 1);
        topLevel.Skeleton = new FPackageIndex(package, 1);
        if (topLevel is UAnimMontage montage)
            montage.SlotAnimTracks = [SlotTrack(new FPackageIndex(package, 2))];
        if (topLevel is UAnimComposite composite)
            composite.AnimationTrack = Track(new FPackageIndex(package, 2));

        var options = new ExporterOptions { AnimFormat = EAnimFormat.UEFormat };
        var exporter = topLevel switch
        {
            UAnimSequence sequence => new AnimExporter(sequence, options),
            UAnimMontage animationMontage => new AnimExporter(animationMontage, options),
            UAnimComposite animationComposite => new AnimExporter(animationComposite, options),
            _ => throw new ArgumentOutOfRangeException(nameof(topLevel))
        };

        var output = Assert.Single(exporter.AnimSequences);
        var chunks = ReadTopLevelChunks(output.FileData);
        Assert.Equal("FMODEL_TARGET_SKELETON", chunks[^1].Name);
        Assert.Equal(topLevel.GetPathName(), ReadTargetSourcePath(chunks[^1].Payload));
        Assert.Equal(skeleton.GetPathName(), ReadTargetSkeletonPath(chunks[^1].Payload));
    }

    [Fact]
    public void UEModel_SkeletalAndSkeletonOnlyConstructorsAppendOneIdentityAfterStandardChunks()
    {
        var skeleton = IdentitySkeleton();
        var package = new SkeletonPackage(skeleton);
        var skeletonIndex = new FPackageIndex(package, 1);
        using var skeletalArchive = new FArchiveWriter();
        using var skeletonOnlyArchive = new FArchiveWriter();

        new UEModel("SK_Hero", new CSkeletalMesh(), null, [], skeletonIndex, new FPackageIndex(), new ExporterOptions()).Save(skeletalArchive);
        new UEModel("SK_Hero_Skeleton", skeleton, [], [], [], new ExporterOptions()).Save(skeletonOnlyArchive);

        var skeletalChunks = ReadTopLevelChunks(skeletalArchive.GetBuffer());
        Assert.Equal(["LODS", "SKELETON", "FMODEL_SKELETON_IDENTITY"], skeletalChunks.Select(chunk => chunk.Name));
        Assert.Equal(1, skeletalChunks.Count(chunk => chunk.Name == "FMODEL_SKELETON_IDENTITY"));
        Assert.Equal(1, package.ResolveCount);
        var skeletonOnlyChunks = ReadTopLevelChunks(skeletonOnlyArchive.GetBuffer());
        Assert.Equal(["SKELETON", "FMODEL_SKELETON_IDENTITY"], skeletonOnlyChunks.Select(chunk => chunk.Name));
        Assert.Equal(1, skeletonOnlyChunks.Count(chunk => chunk.Name == "FMODEL_SKELETON_IDENTITY"));
    }

    [Fact]
    public void UEModel_ModelIdentityUsesTheSerializedSkeletonBoneLayout()
    {
        var skeleton = IdentitySkeleton();
        var serializedBones = new List<CSkelMeshBone>
        {
            new() { Name = new FName("root"), ParentIndex = -1 },
            new() { Name = new FName("mesh_extra"), ParentIndex = 0 }
        };
        using var archive = new FArchiveWriter();

        new UEModel("SK_Hero", skeleton, serializedBones, [], [], new ExporterOptions()).Save(archive);

        var identityChunk = Assert.Single(ReadTopLevelChunks(archive.GetBuffer())
            .Where(chunk => chunk.Name == "FMODEL_SKELETON_IDENTITY"));
        var actual = ReadModelSkeletonLayoutHash(identityChunk.Payload);
        var expected = FModelSkeletonIdentity.ComputeLayoutHash(
            serializedBones.Select(bone => (bone.Name.Text, bone.ParentIndex)));

        Assert.Equal(expected, actual);
        Assert.False(FModelSkeletonIdentity.FromSkeleton(skeleton).BoneLayoutHash.SequenceEqual(actual));
    }

    [Fact]
    public void UEModel_UnresolvedSkeletonFailsBeforePublishingAnyOutput()
    {
        var package = new SkeletonPackage();
        var skeletonIndex = new FPackageIndex(package, 1);
        var outputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".uemodel");

        var error = Assert.Throws<ArgumentException>(() => new UEModel("SK_Unresolved", new CSkeletalMesh(), null, [], skeletonIndex, new FPackageIndex(), new ExporterOptions()));

        Assert.Contains("resolvable skeleton", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, package.ResolveCount);
        Assert.False(File.Exists(outputPath));
    }

    private static CAnimSet AnimationSet(string sourcePath)
    {
        var sequence = new UAnimSequence { Name = sourcePath };
        var converted = (CAnimSequence) RuntimeHelpers.GetUninitializedObject(typeof(CAnimSequence));
        SetField(converted, nameof(CAnimSequence.OriginalSequence), sequence);
        SetField(converted, nameof(CAnimSequence.NumFrames), 1);
        SetField(converted, nameof(CAnimSequence.FramesPerSecond), 30f);
        converted.Tracks = [];
        var set = new CAnimSet(IdentitySkeleton());
        set.Sequences.Add(converted);
        return set;
    }

    private static UAnimSequence Sequence(string name) => new()
    {
        Name = name,
        NumFrames = 1,
        SequenceLength = 1,
        RateScale = 1,
        RawAnimationData = [RawTrack()],
        CompressedTrackToSkeletonMapTable = [new FTrackToSkeletonMap { BoneTreeIndex = 0 }]
    };

    private static FRawAnimSequenceTrack RawTrack()
    {
        var track = (FRawAnimSequenceTrack) RuntimeHelpers.GetUninitializedObject(typeof(FRawAnimSequenceTrack));
        SetField(track, nameof(FRawAnimSequenceTrack.PosKeys), Array.Empty<CUE4Parse.UE4.Objects.Core.Math.FVector>());
        SetField(track, nameof(FRawAnimSequenceTrack.RotKeys), Array.Empty<CUE4Parse.UE4.Objects.Core.Math.FQuat>());
        SetField(track, nameof(FRawAnimSequenceTrack.ScaleKeys), Array.Empty<CUE4Parse.UE4.Objects.Core.Math.FVector>());
        SetField(track, nameof(FRawAnimSequenceTrack.KeyTimes), Array.Empty<float>());
        return track;
    }

    private static UAnimMontage Montage(string name) => new() { Name = name };

    private static UAnimComposite Composite(string name) => new() { Name = name };

    private static FSlotAnimationTrack SlotTrack(FPackageIndex animation)
    {
        var track = (FSlotAnimationTrack) RuntimeHelpers.GetUninitializedObject(typeof(FSlotAnimationTrack));
        track.SlotName = new FName("DefaultSlot");
        track.AnimTrack = Track(animation);
        return track;
    }

    private static FAnimTrack Track(FPackageIndex animation)
    {
        var track = (FAnimTrack) RuntimeHelpers.GetUninitializedObject(typeof(FAnimTrack));
        track.AnimSegments = [Segment(animation)];
        return track;
    }

    private static FAnimSegment Segment(FPackageIndex animation)
    {
        var segment = (FAnimSegment) RuntimeHelpers.GetUninitializedObject(typeof(FAnimSegment));
        segment.AnimReference = animation;
        segment.AnimEndTime = 1;
        segment.AnimPlayRate = 1;
        segment.LoopingCount = 1;
        return segment;
    }

    private static USkeleton IdentitySkeleton()
    {
        var referenceSkeleton = (FReferenceSkeleton) RuntimeHelpers.GetUninitializedObject(typeof(FReferenceSkeleton));
        SetField(referenceSkeleton, nameof(FReferenceSkeleton.FinalRefBoneInfo), new[] { new FMeshBoneInfo(new FName("root"), -1) });
        SetField(referenceSkeleton, nameof(FReferenceSkeleton.FinalRefBonePose), new CUE4Parse.UE4.Objects.Core.Math.FTransform[] { default });
        return new USkeleton
        {
            Name = "/Game/Characters/SK_Hero_Skeleton.SK_Hero_Skeleton",
            Guid = new FGuid(1, 2, 3, 4),
            ReferenceSkeleton = referenceSkeleton
        };
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(target, value);

    private static List<(string Name, byte[] Payload)> ReadTopLevelChunks(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file), Encoding.UTF8);
        reader.ReadBytes(8);
        ReadFString(reader);
        reader.ReadByte();
        ReadFString(reader);
        Assert.False(reader.ReadBoolean());
        var chunks = new List<(string Name, byte[] Payload)>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var name = ReadFString(reader);
            reader.ReadInt32();
            chunks.Add((name, reader.ReadBytes(reader.ReadInt32())));
        }
        return chunks;
    }

    private static string ReadTargetSourcePath(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        reader.ReadByte();
        return ReadFString(reader);
    }

    private static string ReadTargetSkeletonPath(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        reader.ReadByte();
        ReadFString(reader);
        return ReadFString(reader);
    }

    private static byte[] ReadModelSkeletonLayoutHash(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        reader.ReadByte();
        ReadFString(reader);
        if (reader.ReadByte() == 1)
            reader.ReadBytes(FGuid.Size);
        var layoutHash = reader.ReadBytes(32);
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        return layoutHash;
    }

    private static string ReadFString(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));

    private sealed class SkeletonPackage(params UObject[] exports) : IPackage
    {
        public int ResolveCount { get; private set; }
        public string Name { get; set; } = "TestPackage";
        public CUE4Parse.FileProvider.IFileProvider? Provider => null;
        public CUE4Parse.MappingsProvider.TypeMappings? Mappings => null;
        public FPackageFileSummary Summary => null!;
        public FNameEntrySerialized[] NameMap => [];
        public int ImportMapLength => 0;
        public int ExportMapLength => exports.Length;
        public Lazy<UObject>[] ExportsLazy => [];
        public bool IsFullyLoaded => true;
        public bool CanDeserialize => true;
        public bool HasFlags(EPackageFlags flags) => false;
        public int GetExportIndex(string name, StringComparison comparisonType = StringComparison.Ordinal) => -1;
        public ResolvedObject? ResolvePackageIndex(FPackageIndex? index)
        {
            ResolveCount++;
            return index is { Index: > 0 } && index.Index <= exports.Length
                ? new ResolvedLoadedObject(exports[index.Index - 1])
                : null;
        }
    }
}
