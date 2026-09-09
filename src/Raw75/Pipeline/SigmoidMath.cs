using System;

namespace Raw75.Pipeline;

/// <summary>
/// Generalized log-logistic sigmoid curve parameters and evaluation.
/// Math ported from darktable src/iop/sigmoid.c.
/// </summary>
// ref: darktable src/iop/sigmoid.c
public readonly record struct SigmoidParams(
    float Magnitude,
    float PaperExp,
    float FilmFog,
    float FilmPower,
    float PaperPower)
{
    public static readonly SigmoidParams Default = Compute(1.5f, 0.0f);

    public static SigmoidParams Compute(
        float contrast,
        float skew,
        float displayWhiteTarget = 100.0f,
        float displayBlackTarget = 0.0152f)
    {
        const float MiddleGrey = 0.1845f;
        const float Delta = 1e-6f;

        contrast = Math.Clamp(contrast, 0.1f, 10.0f);
        skew = Math.Clamp(skew, -1.0f, 1.0f);
        float whiteTarget = Math.Max(0.001f, 0.01f * displayWhiteTarget);
        float blackTarget = Math.Clamp(0.01f * displayBlackTarget, 1e-7f, whiteTarget * 0.5f);

        // 1. Reference slope for no skew and normalized display
        float refFilmPower = contrast;
        float refPaperPower = 1.0f;
        float refMagnitude = 1.0f;
        float refFilmFog = 0.0f;
        float refPaperExp = MathF.Pow(refFilmFog + MiddleGrey, refFilmPower) * ((refMagnitude / MiddleGrey) - 1.0f);
        float refSlope = (Eval(MiddleGrey + Delta, refMagnitude, refPaperExp, refFilmFog, refFilmPower, refPaperPower)
                        - Eval(MiddleGrey - Delta, refMagnitude, refPaperExp, refFilmFog, refFilmPower, refPaperPower))
                       / (2.0f * Delta);

        // 2. Add skew
        float paperPower = MathF.Pow(5.0f, -skew);

        // 3. Slope at temp film power = 1.0
        float tempFilmPower = 1.0f;
        float tempWhiteGreyRelation = MathF.Pow(whiteTarget / MiddleGrey, 1.0f / paperPower) - 1.0f;
        float tempPaperExp = MathF.Pow(MiddleGrey, tempFilmPower) * tempWhiteGreyRelation;
        float tempSlope = (Eval(MiddleGrey + Delta, whiteTarget, tempPaperExp, refFilmFog, tempFilmPower, paperPower)
                         - Eval(MiddleGrey - Delta, whiteTarget, tempPaperExp, refFilmFog, tempFilmPower, paperPower))
                        / (2.0f * Delta);

        // 4. Film power fulfilling target slope
        float filmPower = tempSlope > 1e-6f ? refSlope / tempSlope : 1.0f;

        // 5. White/grey and white/black relations
        float whiteGreyRelation = MathF.Pow(whiteTarget / MiddleGrey, 1.0f / paperPower) - 1.0f;
        float whiteBlackRelation = MathF.Pow(blackTarget / whiteTarget, -1.0f / paperPower) - 1.0f;

        float num = MiddleGrey * MathF.Pow(whiteGreyRelation, 1.0f / filmPower);
        float denom = MathF.Pow(whiteBlackRelation, 1.0f / filmPower) - MathF.Pow(whiteGreyRelation, 1.0f / filmPower);
        float filmFog = denom > 1e-6f ? num / denom : 0.0f;
        float paperExposure = MathF.Pow(filmFog + MiddleGrey, filmPower) * whiteGreyRelation;

        return new SigmoidParams(whiteTarget, paperExposure, filmFog, filmPower, paperPower);
    }

    public static float Eval(float value, float magnitude, float paperExp, float filmFog, float filmPower, float paperPower)
    {
        float clamped = MathF.Max(value, 0.0f);
        float filmResponse = MathF.Pow(filmFog + clamped, filmPower);
        float paperResponse = magnitude * MathF.Pow(filmResponse / (paperExp + filmResponse), paperPower);
        return float.IsNaN(paperResponse) ? magnitude : paperResponse;
    }

    public float Eval(float value) => Eval(value, Magnitude, PaperExp, FilmFog, FilmPower, PaperPower);
}
