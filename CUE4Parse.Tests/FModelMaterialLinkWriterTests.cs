using System.Buffers.Binary;
using System.Text;
using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;

namespace CUE4Parse.Tests;

public class FModelMaterialLinkWriterTests
{
    [Fact]
    public void None_DoesNotAppendExtensions()
    {
        var archive = new FArchiveWriter();
        archive.Write([0x10, 0x20, 0x30]);

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.None, Links());

        Assert.Equal([0x10, 0x20, 0x30], archive.GetBuffer());
    }

    [Fact]
    public void Relative_WritesOnlyMaterialsV1Chunk()
    {
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, Links());

        Assert.Equal(ExpectedMaterialsPayload(MaterialLinkMode.Relative), ReadChunk(archive.GetBuffer(), out var name, out var count));
        Assert.Equal("FMODEL_MATERIALS", name);
        Assert.Equal(1, count);
    }

    [Fact]
    public void MaterialsV2_WritesSourceMaterialIndexWithoutChangingSectionSlotIndex()
    {
        var archive = new FArchiveWriter();
        var links = Links();
        links.WriteSourceMaterialIndices = true;
        links.Slots[0] = new FModelMaterialSlot
        {
            SlotIndex = 0,
            SourceMaterialIndex = 1,
            SlotName = "Section0",
            UnrealMaterialPath = "/Game/Materials/M_Section0.M_Section0",
            MaterialJsonUri = "materials/section0.json"
        };
        links.Slots.Add(new FModelMaterialSlot
        {
            SlotIndex = 1,
            SourceMaterialIndex = 0,
            SlotName = "Section1",
            UnrealMaterialPath = "/Game/Materials/M_Section1.M_Section1",
            MaterialJsonUri = "materials/section1.json"
        });

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);

        var payload = ReadChunk(archive.GetBuffer(), out _, out var count);
        Assert.Equal(2, count);
        Assert.Equal((byte) 2, payload[0]);
        Assert.Equal((0, 1), ReadMaterialSlotIndexes(payload, 0));
        Assert.Equal((1, 0), ReadMaterialSlotIndexes(payload, 1));
    }

    [Fact]
    public void Bundle_UsesBundleLocationMode()
    {
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Bundle, Links());

        Assert.Equal(ExpectedMaterialsPayload(MaterialLinkMode.Bundle), ReadChunk(archive.GetBuffer(), out _, out _));
    }

    [Theory]
    [InlineData(MaterialLinkMode.Relative, (byte) 1)]
    [InlineData(MaterialLinkMode.Bundle, (byte) 2)]
    [InlineData(MaterialLinkMode.Embedded, (byte) 3)]
    public void TextureLocations_UseExactV1WireValues(MaterialLinkMode mode, byte expectedLocationMode)
    {
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, mode, Links());

        var payload = ReadChunk(archive.GetBuffer(), out _, out _);
        Assert.Equal(expectedLocationMode, payload[LocationModeOffset(payload)]);
    }

    [Fact]
    public void NonNoneWithoutRecords_DoesNotAppendExtensions()
    {
        var archive = new FArchiveWriter();
        archive.Write([0x10, 0x20, 0x30]);

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Embedded, new FModelMaterialLinks());

        Assert.Equal([0x10, 0x20, 0x30], archive.GetBuffer());
    }

    [Fact]
    public void Embedded_WritesDeduplicatedTexturesAfterMaterials()
    {
        var texture = new FModelTextureReference
        {
            UnrealObjectPath = "/Game/Textures/T_Body.T_Body",
            Uri = "textures/body.png",
            Mime = "image/png",
            ImageBytes = [0x89, 0x50, 0x4E, 0x47],
            ColorSpace = 1
        };
        var links = new FModelMaterialLinks
        {
            Slots =
            [
                new FModelMaterialSlot
                {
                    SlotIndex = 4,
                    SlotName = "Body",
                    UnrealMaterialPath = "/Game/Materials/M_Body.M_Body",
                    MaterialJsonUri = "materials/body.json",
                    Parameters =
                    [
                        FModelMaterialParameter.Texture("BaseColor", "base_color", texture),
                        FModelMaterialParameter.Texture("Mask", "mask", texture)
                    ]
                }
            ]
        };
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Embedded, links);

        var bytes = archive.GetBuffer();
        _ = ReadChunk(bytes, out var materialsName, out _);
        var materialsChunkSize = ChunkSize(bytes, 0);
        var texturesPayload = ReadChunk(bytes[materialsChunkSize..], out var texturesName, out var textureCount);
        Assert.Equal("FMODEL_MATERIALS", materialsName);
        Assert.Equal("FMODEL_TEXTURES", texturesName);
        Assert.Equal(1, textureCount);
        Assert.Equal(ExpectedTexturesPayload(), texturesPayload);
    }

    [Fact]
    public void Embedded_EmptyTextureBytes_ThrowsWithoutWriting()
    {
        var archive = new FArchiveWriter();
        archive.Write([0x10, 0x20, 0x30]);
        var links = new FModelMaterialLinks
        {
            Slots =
            [
                new FModelMaterialSlot
                {
                    Parameters =
                    [
                        FModelMaterialParameter.Texture("BaseColor", "base_color", new FModelTextureReference())
                    ]
                }
            ]
        };

        var exception = Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Embedded, links));

        Assert.Contains("Embedded resource requires non-empty bytes", exception.Message);
        Assert.Equal([0x10, 0x20, 0x30], archive.GetBuffer());
    }

    [Fact]
    public void WritesPbrInterfacesV2ChunkAfterExistingChunks()
    {
        var archive = new FArchiveWriter();
        var links = Links();
        links.PbrInterfaces.Add(new FModelPbrInterface { SlotIndex = 4, ParameterIndex = 0, Role = FModelPbrRole.BaseColor });
        links.PbrInterfaces.Add(new FModelPbrInterface { SlotIndex = 4, ParameterIndex = 0, Role = FModelPbrRole.BaseColorFactor });
        links.PbrInterfaces.Add(new FModelPbrInterface { SlotIndex = 4, ParameterIndex = 1, Role = FModelPbrRole.Roughness });

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);

        var bytes = archive.GetBuffer();
        var materialsSize = ChunkSize(bytes, 0);
        var payload = ReadChunk(bytes[materialsSize..], out var name, out var count);
        Assert.Equal("FMODEL_PBR_INTERFACES", name);
        Assert.Equal(3, count);
        Assert.Equal(ExpectedPbrInterfacesPayload(), payload);
    }

    [Fact]
    public void MaterialLinkBuilder_PbrInterfacesSerializeSelectedIndexesAndDuplicateRoles()
    {
        var links = MaterialLinkBuilder.Build(
            MaterialLinkMode.Relative,
            "Game/Meshes/Hero.uemodel",
            "Game/Materials/M_Hero.json",
            "/Game/Materials/M_Hero.M_Hero",
            [
                MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Base.T_Base", "Game/T_Base.png", "image/png", [1]),
                MaterialLinkSourceParameter.Texture("M", "/Game/T_AO.T_AO", "Game/T_AO.png", "image/png", [2]),
                MaterialLinkSourceParameter.Vector("Color", .1f, .2f, .3f, 1f),
                MaterialLinkSourceParameter.Vector("Skin Boost Color And Exponent", .4f, .5f, .6f, 2f),
                MaterialLinkSourceParameter.Scalar("RoughnessMin", .1f),
                MaterialLinkSourceParameter.Scalar("RoughnessMax", .9f),
                MaterialLinkSourceParameter.Scalar("Roughness", .4f),
                MaterialLinkSourceParameter.Scalar("emissive mult", 2f)
            ],
            slotIndex: 7);
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);

        var bytes = archive.GetBuffer();
        var payload = ReadChunk(bytes[ChunkSize(bytes, 0)..], out var name, out _);
        Assert.Equal("FMODEL_PBR_INTERFACES", name);
        Assert.Equal(
        [
            (7, 0, (byte) 1),
            (7, 2, (byte) 2),
            (7, 7, (byte) 6),
            (7, 4, (byte) 7),
            (7, 5, (byte) 8),
            (7, 6, (byte) 9),
            (7, 1, (byte) 10),
            (7, 3, (byte) 11),
            (7, 3, (byte) 12)
        ],
        ReadPbrInterfaceRecords(payload));
    }

    [Fact]
    public void Build_WritePbrInterfaces_DuplicateCandidatesKeepFirstIndexAndColorDualRoles()
    {
        var links = MaterialLinkBuilder.Build(
            MaterialLinkMode.Relative,
            "Game/Meshes/Hero.uemodel",
            "Game/Materials/M_Hero.json",
            "/Game/Materials/M_Hero.M_Hero",
            [
                MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_First.T_First", "Game/T_First.png", "image/png", [1]),
                MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Later.T_Later", "Game/T_Later.png", "image/png", [2]),
                MaterialLinkSourceParameter.Vector("Color", .1f, .2f, .3f, 1f),
                MaterialLinkSourceParameter.Vector("Color", .4f, .5f, .6f, 1f)
            ],
            slotIndex: 7);
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);

        var bytes = archive.GetBuffer();
        var payload = ReadChunk(bytes[ChunkSize(bytes, 0)..], out var name, out _);
        Assert.Equal("FMODEL_PBR_INTERFACES", name);
        var records = ReadPbrInterfaceRecords(payload);
        Assert.Equal(
        [
            (7, 0, (byte) 1),
            (7, 2, (byte) 2)
        ],
        records);
        Assert.DoesNotContain(records, record => record.ParameterIndex == 1);
        Assert.Contains(records, record => record.ParameterIndex == 2 && record.Role == (byte) FModelPbrRole.BaseColorFactor);
        Assert.DoesNotContain(records, record => record.ParameterIndex == 2 && record.Role == (byte) FModelPbrRole.EmissiveColorFactor);
    }

    [Fact]
    public void Embedded_OmittedTexture_CompactsPbrRoleParameterIndexes()
    {
        var links = MaterialLinkBuilder.Build(
            MaterialLinkMode.Embedded,
            "Game/Meshes/Hero.uemodel",
            "Game/Materials/M_Hero.json",
            "/Game/Materials/M_Hero.M_Hero",
            [
                MaterialLinkSourceParameter.Texture("BaseColor", "/Game/T_Empty.T_Empty", "Game/T_Empty.png", "image/png", []),
                MaterialLinkSourceParameter.Vector("Color", .1f, .2f, .3f, 1f),
                MaterialLinkSourceParameter.Scalar("Roughness", .4f)
            ],
            slotIndex: 7);
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Embedded, links);

        var bytes = archive.GetBuffer();
        var materialsPayload = ReadChunk(bytes, out var materialsName, out _);
        var rolesPayload = ReadChunk(bytes[ChunkSize(bytes, 0)..], out var rolesName, out _);
        var roles = ReadPbrInterfaceRecords(rolesPayload);
        Assert.Equal("FMODEL_MATERIALS", materialsName);
        Assert.Equal("FMODEL_PBR_INTERFACES", rolesName);
        Assert.Equal(2, ReadFirstMaterialParameterCount(materialsPayload));
        Assert.Equal(
        [
            (7, 1, (byte) FModelPbrRole.Roughness)
        ],
        roles);
        Assert.All(roles, role => Assert.InRange(role.ParameterIndex, 0, 1));
        Assert.DoesNotContain(roles, role => role.Role == (byte) FModelPbrRole.BaseColor);
    }

    [Theory]
    [InlineData(FModelPbrRole.BaseColor, (byte) 1)]
    [InlineData(FModelPbrRole.BaseColorFactor, (byte) 2)]
    [InlineData(FModelPbrRole.Normal, (byte) 3)]
    [InlineData(FModelPbrRole.Emissive, (byte) 4)]
    [InlineData(FModelPbrRole.EmissiveColorFactor, (byte) 5)]
    [InlineData(FModelPbrRole.EmissiveStrength, (byte) 6)]
    [InlineData(FModelPbrRole.RoughnessMin, (byte) 7)]
    [InlineData(FModelPbrRole.RoughnessMax, (byte) 8)]
    [InlineData(FModelPbrRole.Roughness, (byte) 9)]
    [InlineData(FModelPbrRole.Ao, (byte) 10)]
    [InlineData(FModelPbrRole.AoColorBoost, (byte) 11)]
    [InlineData(FModelPbrRole.AoStrength, (byte) 12)]
    public void PbrInterfaces_UseExactV2RoleWireValues(FModelPbrRole role, byte expectedRoleByte)
    {
        var archive = new FArchiveWriter();
        var links = Links();
        links.PbrInterfaces.Add(new FModelPbrInterface { SlotIndex = 4, ParameterIndex = 0, Role = role });

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);

        var bytes = archive.GetBuffer();
        var payload = ReadChunk(bytes[ChunkSize(bytes, 0)..], out _, out _);
        Assert.Equal(expectedRoleByte, payload[13]);
    }

    [Fact]
    public void EmptyPbrInterfaces_DoesNotWriteOptionalChunk()
    {
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, Links());

        Assert.Equal(ChunkSize(archive.GetBuffer(), 0), archive.GetBuffer().Length);
    }

    [Theory]
    [InlineData("/textures/body.png")]
    [InlineData("C:/textures/body.png")]
    [InlineData("C:\\textures\\body.png")]
    [InlineData("\\\\server\\share\\body.png")]
    [InlineData("\\\\?\\C:\\textures\\body.png")]
    [InlineData("textures//body.png")]
    [InlineData("textures/./body.png")]
    [InlineData("textures/../body.png")]
    [InlineData("textures\0body.png")]
    public void LogicalUris_RejectUnsafePathsWithoutAppending(string uri)
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        links.Slots[0] = new FModelMaterialSlot
        {
            SlotIndex = 4,
            SlotName = "Body",
            UnrealMaterialPath = "/Game/Materials/M_Body.M_Body",
            MaterialJsonUri = uri,
            Parameters = links.Slots[0].Parameters
        };

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links));

        AssertPrefixUnchanged(archive);
    }

    [Fact]
    public void LogicalUris_RejectEmptyTextureUriWithoutAppending()
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        var texture = links.Slots[0].Parameters[0].TextureValue!;
        links.Slots[0] = new FModelMaterialSlot
        {
            SlotIndex = 4,
            SlotName = "Body",
            UnrealMaterialPath = "/Game/Materials/M_Body.M_Body",
            MaterialJsonUri = "materials/body.json",
            Parameters =
            [
                FModelMaterialParameter.Texture("BaseColor", "base_color", new FModelTextureReference
                {
                    UnrealObjectPath = texture.UnrealObjectPath,
                    Uri = string.Empty,
                    Mime = texture.Mime,
                    ImageBytes = texture.ImageBytes
                })
            ]
        };

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links));

        AssertPrefixUnchanged(archive);
    }

    [Fact]
    public void RelativeMaterialJsonUri_AllowsMultipleLeadingParentSegments()
    {
        var archive = new FArchiveWriter();
        var links = Links();
        links.Slots[0] = new FModelMaterialSlot
        {
            SlotIndex = 4,
            SlotName = "Body",
            UnrealMaterialPath = "/Game/Materials/M_Body.M_Body",
            MaterialJsonUri = "../../Materials/M_Body.json",
            Parameters = links.Slots[0].Parameters
        };

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);

        Assert.NotEmpty(archive.GetBuffer());
    }

    [Theory]
    [InlineData(MaterialLinkMode.Bundle, "../outside.png")]
    [InlineData(MaterialLinkMode.Bundle, "Textures/../../outside.png")]
    [InlineData(MaterialLinkMode.Embedded, "../outside.png")]
    [InlineData(MaterialLinkMode.Embedded, "Textures/../../outside.png")]
    public void TextureUris_RejectTraversalOutsideRelativeModeWithoutAppending(MaterialLinkMode mode, string uri)
    {
        var archive = ArchiveWithPrefix();

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, mode, LinksWithTextureUri(uri)));

        AssertPrefixUnchanged(archive);
    }

    [Fact]
    public void RelativeTextureUri_AllowsMultipleLeadingParentSegments()
    {
        var archive = new FArchiveWriter();

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, LinksWithTextureUri("../../Textures/T_Body.png"));

        Assert.NotEmpty(archive.GetBuffer());
    }

    [Fact]
    public void DuplicateSlotIndexes_ThrowWithoutAppending()
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        links.Slots.Add(links.Slots[0]);

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links));

        AssertPrefixUnchanged(archive);
    }

    [Fact]
    public void DuplicateSourceMaterialIndexes_AreAllowedForSharedSourceMaterials()
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        links.WriteSourceMaterialIndices = true;
        links.Slots[0].SourceMaterialIndex = 1;
        links.Slots.Add(new FModelMaterialSlot { SlotIndex = 5, SourceMaterialIndex = 1, MaterialJsonUri = "materials/other.json" });

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);
        Assert.NotEmpty(archive.GetBuffer());
    }

    [Fact]
    public void MaterialsV2_WritesAbsentSourceMappingAsNegativeSentinel()
    {
        var archive = new FArchiveWriter();
        var links = Links();
        links.WriteSourceMaterialIndices = true;
        links.Slots[0].SourceMaterialIndex = null;

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links);

        var payload = ReadChunk(archive.GetBuffer(), out _, out _);
        Assert.Equal((byte) 2, payload[0]);
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1 + sizeof(int) + sizeof(int))));
    }

    [Fact]
    public void NegativeSourceMaterialIndex_ThrowsWithoutAppending()
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        links.Slots[0].SourceMaterialIndex = -1;

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links));

        AssertPrefixUnchanged(archive);
    }

    [Fact]
    public void NegativeSlotIndex_ThrowsWithoutAppending()
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        links.Slots[0] = new FModelMaterialSlot { SlotIndex = -1, MaterialJsonUri = "materials/body.json" };

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links));

        AssertPrefixUnchanged(archive);
    }

    [Theory]
    [InlineData(5, 0, FModelPbrRole.BaseColor)]
    [InlineData(4, 3, FModelPbrRole.BaseColor)]
    [InlineData(4, 0, (FModelPbrRole) 99)]
    public void InvalidPbrInterface_ThrowsWithoutAppending(int slotIndex, int parameterIndex, FModelPbrRole role)
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        links.PbrInterfaces.Add(new FModelPbrInterface { SlotIndex = slotIndex, ParameterIndex = parameterIndex, Role = role });

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links));

        AssertPrefixUnchanged(archive);
    }

    [Fact]
    public void DuplicatePbrSlotRole_ThrowsWithoutAppending()
    {
        var archive = ArchiveWithPrefix();
        var links = Links();
        links.PbrInterfaces.Add(new FModelPbrInterface { SlotIndex = 4, ParameterIndex = 0, Role = FModelPbrRole.BaseColor });
        links.PbrInterfaces.Add(new FModelPbrInterface { SlotIndex = 4, ParameterIndex = 1, Role = FModelPbrRole.BaseColor });

        Assert.Throws<ArgumentException>(() => FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Relative, links));

        AssertPrefixUnchanged(archive);
    }

    [Fact]
    public void EmbeddedTextureRecords_UseUniqueSequentialIds()
    {
        var archive = new FArchiveWriter();
        var links = Links();
        links.Slots[0] = new FModelMaterialSlot
        {
            SlotIndex = 4,
            SlotName = "Body",
            UnrealMaterialPath = "/Game/Materials/M_Body.M_Body",
            MaterialJsonUri = "materials/body.json",
            Parameters =
            [
                links.Slots[0].Parameters[0],
                FModelMaterialParameter.Texture("Normal", "normal", new FModelTextureReference
                {
                    UnrealObjectPath = "/Game/Textures/T_Normal.T_Normal",
                    Uri = "textures/normal.png",
                    Mime = "image/png",
                    ImageBytes = [1]
                })
            ]
        };

        FModelMaterialLinkWriter.Write(archive, MaterialLinkMode.Embedded, links);

        var bytes = archive.GetBuffer();
        var textures = ReadChunk(bytes[ChunkSize(bytes, 0)..], out _, out var count);
        Assert.Equal(2, count);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(textures.AsSpan(5)));
        var secondIdOffset = 5 + sizeof(int) + sizeof(int) + Encoding.UTF8.GetByteCount("/Game/Textures/T_Body.T_Body") + sizeof(int) + Encoding.UTF8.GetByteCount("image/png") + sizeof(int) + 4;
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(textures.AsSpan(secondIdOffset)));
    }

    private static FModelMaterialLinks Links() => new()
    {
        Slots =
        [
            new FModelMaterialSlot
            {
                SlotIndex = 4,
                SlotName = "Body",
                UnrealMaterialPath = "/Game/Materials/M_Body.M_Body",
                MaterialJsonUri = "materials/body.json",
                Parameters =
                [
                    FModelMaterialParameter.Texture("BaseColor", "base_color", new FModelTextureReference
                    {
                        UnrealObjectPath = "/Game/Textures/T_Body.T_Body",
                        Uri = "textures/body.png",
                        Mime = "image/png",
                        ImageBytes = [0x89, 0x50, 0x4E, 0x47],
                        ColorSpace = 1
                    }),
                    FModelMaterialParameter.Scalar("Roughness", "custom", 0.25f),
                    FModelMaterialParameter.Vector("Tint", "custom", 0.1f, 0.2f, 0.3f, 1f)
                ]
            }
        ]
    };

    private static FModelMaterialLinks LinksWithTextureUri(string uri)
    {
        var links = Links();
        var texture = links.Slots[0].Parameters[0].TextureValue!;
        links.Slots[0] = new FModelMaterialSlot
        {
            SlotIndex = links.Slots[0].SlotIndex,
            SlotName = links.Slots[0].SlotName,
            UnrealMaterialPath = links.Slots[0].UnrealMaterialPath,
            MaterialJsonUri = links.Slots[0].MaterialJsonUri,
            Parameters =
            [
                FModelMaterialParameter.Texture("BaseColor", "base_color", new FModelTextureReference
                {
                    UnrealObjectPath = texture.UnrealObjectPath,
                    Uri = uri,
                    Mime = texture.Mime,
                    ImageBytes = texture.ImageBytes,
                    ColorSpace = texture.ColorSpace,
                    Packing = texture.Packing,
                    UvSet = texture.UvSet
                }),
                .. links.Slots[0].Parameters.Skip(1)
            ]
        };
        return links;
    }

    private static FArchiveWriter ArchiveWithPrefix()
    {
        var archive = new FArchiveWriter();
        archive.Write([0x10, 0x20, 0x30]);
        return archive;
    }

    private static void AssertPrefixUnchanged(FArchiveWriter archive) => Assert.Equal([0x10, 0x20, 0x30], archive.GetBuffer());

    private static byte[] ExpectedMaterialsPayload(MaterialLinkMode mode)
    {
        var bytes = new List<byte> { 1 };
        WriteInt(bytes, 1);
        WriteInt(bytes, 4);
        WriteString(bytes, "Body");
        WriteString(bytes, "/Game/Materials/M_Body.M_Body");
        WriteString(bytes, "materials/body.json");
        WriteInt(bytes, 3);
        WriteString(bytes, "BaseColor");
        bytes.Add(1);
        WriteString(bytes, "base_color");
        bytes.Add(mode switch
        {
            MaterialLinkMode.Relative => 1,
            MaterialLinkMode.Bundle => 2,
            MaterialLinkMode.Embedded => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        });
        WriteString(bytes, "textures/body.png");
        WriteInt(bytes, mode == MaterialLinkMode.Embedded ? 0 : -1);
        WriteString(bytes, "/Game/Textures/T_Body.T_Body");
        bytes.Add(1);
        bytes.Add(0);
        WriteInt(bytes, 0);
        WriteString(bytes, "Roughness");
        bytes.Add(2);
        WriteString(bytes, "custom");
        WriteFloat(bytes, 0.25f);
        WriteString(bytes, "Tint");
        bytes.Add(3);
        WriteString(bytes, "custom");
        WriteFloat(bytes, 0.1f);
        WriteFloat(bytes, 0.2f);
        WriteFloat(bytes, 0.3f);
        WriteFloat(bytes, 1f);
        return [.. bytes];
    }

    private static byte[] ExpectedTexturesPayload()
    {
        var bytes = new List<byte> { 1 };
        WriteInt(bytes, 1);
        WriteInt(bytes, 0);
        WriteString(bytes, "/Game/Textures/T_Body.T_Body");
        WriteString(bytes, "image/png");
        WriteInt(bytes, 4);
        bytes.AddRange([0x89, 0x50, 0x4E, 0x47]);
        return [.. bytes];
    }

    private static byte[] ExpectedPbrInterfacesPayload()
    {
        var bytes = new List<byte> { 2 };
        WriteInt(bytes, 3);
        WriteInt(bytes, 4);
        WriteInt(bytes, 0);
        bytes.Add(1);
        WriteInt(bytes, 4);
        WriteInt(bytes, 0);
        bytes.Add(2);
        WriteInt(bytes, 4);
        WriteInt(bytes, 1);
        bytes.Add(9);
        return [.. bytes];
    }

    private static byte[] ReadChunk(byte[] bytes, out string name, out int count)
    {
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        name = Encoding.UTF8.GetString(bytes, 4, nameLength);
        count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4 + nameLength));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8 + nameLength));
        return bytes[(12 + nameLength)..(12 + nameLength + payloadLength)];
    }

    private static int ChunkSize(byte[] bytes, int offset)
    {
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 8 + nameLength));
        return 12 + nameLength + payloadLength;
    }

    private static (int SlotIndex, int ParameterIndex, byte Role)[] ReadPbrInterfaceRecords(byte[] payload)
    {
        Assert.Equal((byte) 2, payload[0]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1));
        Assert.Equal(1 + sizeof(int) + count * (sizeof(int) * 2 + sizeof(byte)), payload.Length);

        var records = new (int SlotIndex, int ParameterIndex, byte Role)[count];
        var offset = 1 + sizeof(int);
        for (var index = 0; index < count; index++)
        {
            records[index] = (
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset)),
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + sizeof(int))),
                payload[offset + sizeof(int) * 2]);
            offset += sizeof(int) * 2 + sizeof(byte);
        }
        return records;
    }

    private static int ReadFirstMaterialParameterCount(byte[] payload)
    {
        var offset = 1 + sizeof(int) + sizeof(int);
        for (var index = 0; index < 3; index++)
            offset += sizeof(int) + BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
        return BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
    }

    private static (int SlotIndex, int SourceMaterialIndex) ReadMaterialSlotIndexes(byte[] payload, int slotOrdinal)
    {
        var offset = 1 + sizeof(int);
        for (var index = 0; index < slotOrdinal; index++)
        {
            offset += sizeof(int) * 2;
            foreach (var _ in new[] { "Section0", "/Game/Materials/M_Section0.M_Section0", "materials/section0.json" })
                offset += sizeof(int) + Encoding.UTF8.GetByteCount(_);
            offset += sizeof(int);
        }

        return (
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + sizeof(int))));
    }

    private static int LocationModeOffset(byte[] payload)
    {
        var offset = 1 + sizeof(int) + sizeof(int);
        foreach (var value in new[] { "Body", "/Game/Materials/M_Body.M_Body", "materials/body.json" })
            offset += sizeof(int) + Encoding.UTF8.GetByteCount(value);
        offset += sizeof(int);
        offset += sizeof(int) + Encoding.UTF8.GetByteCount("BaseColor");
        offset += sizeof(byte);
        offset += sizeof(int) + Encoding.UTF8.GetByteCount("base_color");
        return offset;
    }

    private static void WriteInt(List<byte> bytes, int value) => bytes.AddRange(BitConverter.GetBytes(value));
    private static void WriteFloat(List<byte> bytes, float value) => bytes.AddRange(BitConverter.GetBytes(value));
    private static void WriteString(List<byte> bytes, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        WriteInt(bytes, encoded.Length);
        bytes.AddRange(encoded);
    }
}
