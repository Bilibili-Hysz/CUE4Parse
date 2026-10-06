using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine.Animation;

namespace CUE4Parse.Tests;

public class AnimationCurveCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void UniformlySampledPreservesEveryConstantCurvePlacement(int constantMask)
    {
        const int curveCount = 3;
        const int samples = 3;
        const float rate = 30;
        var constantCount = Enumerable.Range(0, curveCount).Count(i => (constantMask & (1 << i)) != 0);
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(constantCount);
            writer.Write(samples);
            writer.Write(rate);
            writer.Write((uint)constantMask);
            for (var curve = 0; curve < curveCount; curve++)
                if ((constantMask & (1 << curve)) != 0) writer.Write(curve + 0.5f);
            for (var curve = 0; curve < curveCount; curve++)
                if ((constantMask & (1 << curve)) == 0)
                    for (var sample = 0; sample < samples; sample++) writer.Write(curve * 10f + sample);
        }
        var curves = new UAnimCurveCompressionCodec_UniformlySampled().ConvertCurves(new FSmartName[curveCount], data.ToArray());
        Assert.Equal(curveCount, curves.Length);
        for (var curve = 0; curve < curveCount; curve++)
        {
            Assert.Equal(samples, curves[curve].FloatCurve.Keys.Length);
            for (var sample = 0; sample < samples; sample++)
            {
                var key = curves[curve].FloatCurve.Keys[sample];
                Assert.Equal(sample / rate, key.Time);
                Assert.Equal((constantMask & (1 << curve)) != 0 ? curve + 0.5f : curve * 10f + sample, key.Value);
            }
        }
    }

    [Fact]
    public void UniformIndexablePreservesCurveMajorSamplesAndTimes()
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(3);
            writer.Write(20f);
            foreach (var value in new[] { 1f, 2f, 3f, 10f, 20f, 30f }) writer.Write(value);
        }
        var curves = new UAnimCurveCompressionCodec_UniformIndexable().ConvertCurves(new FSmartName[2], data.ToArray());
        Assert.Equal(new[] { 1f, 2f, 3f }, curves[0].FloatCurve.Keys.Select(key => key.Value));
        Assert.Equal(new[] { 10f, 20f, 30f }, curves[1].FloatCurve.Keys.Select(key => key.Value));
        Assert.Equal(new[] { 0f, 0.05f, 0.1f }, curves[1].FloatCurve.Keys.Select(key => key.Time));
    }

    [Fact]
    public void EmptyCurvesDoNotReadCompressedData()
    {
        Assert.Empty(new UAnimCurveCompressionCodec_UniformIndexable().ConvertCurves([], []));
        Assert.Empty(new UAnimCurveCompressionCodec_UniformlySampled().ConvertCurves([], []));
    }
}
