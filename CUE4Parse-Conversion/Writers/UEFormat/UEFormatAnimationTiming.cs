namespace CUE4Parse_Conversion.Writers.UEFormat;

internal static class UEFormatAnimationTiming
{
    internal const float DefaultFramesPerSecond = 30.0f;

    internal static float ResolveFramesPerSecond(
        int frameCount,
        float sequenceLength,
        float rateScale = 1.0f,
        float? fallbackRate = null)
    {
        if (frameCount > 0
            && float.IsFinite(sequenceLength)
            && sequenceLength > 0.0f
            && float.IsFinite(rateScale))
        {
            var framesPerSecond = frameCount / sequenceLength * MathF.Max(1.0f, rateScale);
            if (float.IsFinite(framesPerSecond) && framesPerSecond > 0.0f)
                return framesPerSecond;
        }

        if (fallbackRate is { } rate && float.IsFinite(rate) && rate > 0.0f)
            return rate;

        return DefaultFramesPerSecond;
    }
}
