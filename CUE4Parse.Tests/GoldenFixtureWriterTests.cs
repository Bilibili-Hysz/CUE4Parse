using System.Security.Cryptography;
using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion.UEFormat;
using CUE4Parse_Conversion.UEFormat.MaterialLinks;
using CUE4Parse_Conversion.UEFormat.Structs;

namespace CUE4Parse.Tests;

public class GoldenFixtureWriterTests
{
    [Fact]
    public void WritesDeterministicUeModelGoldenFixture()
    {
        var outputPath = Environment.GetEnvironmentVariable("FMODEL_GOLDEN_FIXTURE_PATH");
        var model = new FArchiveWriter();
        WriteStandardGeometry(model);
        FModelMaterialLinkWriter.Write(model, MaterialLinkMode.Embedded, MaterialLinks());
        using (var futureChunk = new FDataChunk("FUTURE_GOLDEN", 1))
        {
            futureChunk.Write([0x47, 0x4F, 0x4C, 0x44]);
            futureChunk.Serialize(model);
        }

        var fixture = new FArchiveWriter();
        var header = new FUEFormatHeader("UEMODEL", "FModelGoldenTriangle");
        header.UncompressedSize = checked((int) model.Length);
        header.CompressedSize = checked((int) model.Length);
        header.Serialize(fixture);
        fixture.Write(model.GetBuffer());

        var bytes = fixture.GetBuffer();
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, bytes);
        }

        var deepRelativePath = Environment.GetEnvironmentVariable("FMODEL_DEEP_RELATIVE_FIXTURE_PATH");
        if (!string.IsNullOrWhiteSpace(deepRelativePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(deepRelativePath)!);
            File.WriteAllBytes(deepRelativePath, BuildDeepRelativeFixture());
        }

        Assert.Equal("d5f0724e6d45824148f9d572122aedc2eb2ef05312fc8da58816fd38b6346722", Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static byte[] BuildDeepRelativeFixture()
    {
        var model = new FArchiveWriter();
        WriteStandardGeometry(model);
        FModelMaterialLinkWriter.Write(model, MaterialLinkMode.Relative, new FModelMaterialLinks
        {
            Slots =
            [
                new FModelMaterialSlot
                {
                    SlotIndex = 0,
                    SlotName = "Deep",
                    UnrealMaterialPath = "/Game/Materials/M_Deep.M_Deep",
                    MaterialJsonUri = "../../Materials/M_Deep.json",
                    Parameters =
                    [
                        FModelMaterialParameter.Texture("BaseColor", "base_color", new FModelTextureReference
                        {
                            UnrealObjectPath = "/Game/Textures/T_Deep.T_Deep",
                            Uri = "../../Textures/T_Deep.png",
                            Mime = "image/png"
                        })
                    ]
                }
            ]
        });

        var fixture = new FArchiveWriter();
        var header = new FUEFormatHeader("UEMODEL", "FModelDeepRelative");
        header.UncompressedSize = checked((int) model.Length);
        header.CompressedSize = checked((int) model.Length);
        header.Serialize(fixture);
        fixture.Write(model.GetBuffer());
        return fixture.GetBuffer();
    }

    private static void WriteStandardGeometry(FArchiveWriter archive)
    {
        using var lods = new FDataChunk("LODS", 1);
        using (var lod = new FStaticDataChunk("LOD0"))
        {
            using (var vertices = new FDataChunk("VERTICES", 3))
            {
                foreach (var value in new[] { 0f, 0f, 0f, 100f, 0f, 0f, 0f, 100f, 0f })
                    vertices.Write(value);
                vertices.Serialize(lod);
            }
            using (var normals = new FDataChunk("NORMALS", 3))
            {
                foreach (var value in new[] { 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f })
                    normals.Write(value);
                normals.Serialize(lod);
            }
            using (var texCoords = new FDataChunk("TEXCOORDS", 1))
            {
                texCoords.Write(3);
                foreach (var value in new[] { 0f, 0f, 1f, 0f, 0f, 1f })
                    texCoords.Write(value);
                texCoords.Serialize(lod);
            }
            using (var indices = new FDataChunk("INDICES", 3))
            {
                indices.Write((uint) 0);
                indices.Write((uint) 1);
                indices.Write((uint) 2);
                indices.Serialize(lod);
            }
            using (var materials = new FDataChunk("MATERIALS", 2))
            {
                materials.WriteFString("M_GoldenBody");
                materials.WriteFString("/Game/Materials/M_GoldenBody.M_GoldenBody");
                materials.Write(0);
                materials.Write(1);
                materials.WriteFString("M_GoldenOverlay");
                materials.WriteFString("/Game/Materials/M_GoldenOverlay.M_GoldenOverlay");
                materials.Write(0);
                materials.Write(1);
                materials.Serialize(lod);
            }
            lod.Serialize(lods);
        }
        lods.Serialize(archive);
    }

    private static FModelMaterialLinks MaterialLinks()
    {
        var normal = new FModelTextureReference
        {
            UnrealObjectPath = "/Game/Textures/T_Golden_N.T_Golden_N",
            Uri = "textures/golden_normal.png",
            Mime = "image/png",
            ImageBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A],
            ColorSpace = 0,
        };
        return new FModelMaterialLinks
        {
            Slots =
            [
                new FModelMaterialSlot
                {
                    SlotIndex = 0,
                    SlotName = "Body",
                    UnrealMaterialPath = "/Game/Materials/M_GoldenBody.M_GoldenBody",
                    MaterialJsonUri = "materials/golden_body.json",
                    Parameters =
                    [
                        FModelMaterialParameter.Texture("BaseColor", "basecolor", new FModelTextureReference
                        {
                            UnrealObjectPath = "/Game/Textures/T_Golden_BC.T_Golden_BC",
                            Uri = "textures/golden_basecolor.png",
                            Mime = "image/png",
                            ImageBytes = [0x89, 0x50, 0x4E, 0x47, 0x42, 0x43],
                            ColorSpace = 1,
                        }),
                        FModelMaterialParameter.Texture("Normal", "normal", normal),
                        FModelMaterialParameter.Texture("WetnessMask", "WetnessMask", new FModelTextureReference
                        {
                            UnrealObjectPath = "/Game/Textures/T_Golden_Wetness.T_Golden_Wetness",
                            Uri = "textures/golden_wetness.png",
                            Mime = "image/png",
                            ImageBytes = [0x89, 0x50, 0x4E, 0x47, 0x57, 0x45, 0x54],
                            ColorSpace = 0,
                        }),
                        FModelMaterialParameter.Scalar("Roughness", "roughness", 0.25f),
                        FModelMaterialParameter.Vector("Tint", "tint", 0.1f, 0.2f, 0.3f, 1f),
                    ],
                },
                new FModelMaterialSlot
                {
                    SlotIndex = 1,
                    SlotName = "Overlay",
                    UnrealMaterialPath = "/Game/Materials/M_GoldenOverlay.M_GoldenOverlay",
                    MaterialJsonUri = "materials/golden_overlay.json",
                    Parameters = [FModelMaterialParameter.Texture("Normal", "normal", normal)],
                },
            ],
        };
    }
}
