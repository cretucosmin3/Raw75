using System;

namespace Raw75.Pipeline;

/// <summary>
/// A 2D control point for tone and color curves in [0, 255] coordinate space.
/// </summary>
public readonly record struct CurvePoint(float X, float Y)
{
    public float X { get; init; } = Math.Clamp(X, 0f, 255f);
    public float Y { get; init; } = Math.Clamp(Y, 0f, 255f);

    public override string ToString() => $"({X:0.0}, {Y:0.0})";
}

/// <summary>
/// Mathematical utilities for curve evaluation:
/// Monotone cubic Hermite spline interpolation (Fritsch-Carlson algorithm) using cubic hermite spline.
/// </summary>
public static class CurveMath
{
    public static CurvePoint[] DefaultCurve() => new[]
    {
        new CurvePoint(0f, 0f),
        new CurvePoint(255f, 255f)
    };

    public static bool IsIdentity(CurvePoint[]? points)
    {
        if (points == null || points.Length == 0)
            return true;
        if (points.Length == 2)
            return MathF.Abs(points[0].X - 0f) < 0.5f && MathF.Abs(points[0].Y - 0f) < 0.5f
                && MathF.Abs(points[1].X - 255f) < 0.5f && MathF.Abs(points[1].Y - 255f) < 0.5f;

        for (int i = 0; i < points.Length; i++)
        {
            if (MathF.Abs(points[i].X - points[i].Y) > 0.5f)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Fritsch-Carlson monotone cubic hermite spline evaluation matching standard cubic hermite spline.
    /// Evaluates 256 output points normalized in [0, 1].
    /// </summary>
    public static float[] EvaluateSpline(CurvePoint[]? inputPoints, int resolution = 256)
    {
        float[] lut = new float[resolution];
        if (inputPoints == null || inputPoints.Length < 2)
        {
            for (int i = 0; i < resolution; i++)
                lut[i] = i / (float)(resolution - 1);
            return lut;
        }

        var points = (CurvePoint[])inputPoints.Clone();
        Array.Sort(points, (a, b) => a.X.CompareTo(b.X));

        int n = points.Length;
        float[] deltas = new float[n - 1];
        float[] ms = new float[n];

        for (int i = 0; i < n - 1; i++)
        {
            float dx = points[i + 1].X - points[i].X;
            float dy = points[i + 1].Y - points[i].Y;
            deltas[i] = dx > 1e-4f ? dy / dx : (dy > 0f ? 1e4f : (dy < 0f ? -1e4f : 0f));
        }

        ms[0] = deltas[0];
        for (int i = 1; i < n - 1; i++)
        {
            if (deltas[i - 1] * deltas[i] <= 0f)
                ms[i] = 0f;
            else
                ms[i] = (deltas[i - 1] + deltas[i]) * 0.5f;
        }
        ms[n - 1] = deltas[n - 2];

        for (int i = 0; i < n - 1; i++)
        {
            if (MathF.Abs(deltas[i]) < 1e-5f)
            {
                ms[i] = 0f;
                ms[i + 1] = 0f;
            }
            else
            {
                float alpha = ms[i] / deltas[i];
                float beta = ms[i + 1] / deltas[i];
                float tau = alpha * alpha + beta * beta;
                if (tau > 9f)
                {
                    float scale = 3.0f / MathF.Sqrt(tau);
                    ms[i] = scale * alpha * deltas[i];
                    ms[i + 1] = scale * beta * deltas[i];
                }
            }
        }

        float maxVal = resolution - 1f;
        int pIndex = 0;
        for (int i = 0; i < resolution; i++)
        {
            float x = (i / maxVal) * 255f;

            if (x <= points[0].X)
            {
                lut[i] = Math.Clamp(points[0].Y / 255f, 0f, 1f);
                continue;
            }
            if (x >= points[n - 1].X)
            {
                lut[i] = Math.Clamp(points[n - 1].Y / 255f, 0f, 1f);
                continue;
            }

            while (pIndex < n - 2 && points[pIndex + 1].X < x)
                pIndex++;

            CurvePoint p0 = points[pIndex];
            CurvePoint p1 = points[pIndex + 1];
            float dx = p1.X - p0.X;
            float t = dx > 1e-5f ? (x - p0.X) / dx : 0f;
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            float y = h00 * p0.Y + h10 * ms[pIndex] * dx + h01 * p1.Y + h11 * ms[pIndex + 1] * dx;
            lut[i] = Math.Clamp(y / 255f, 0f, 1f);
        }

        return lut;
    }

    /// <summary>
    /// Populates a 256x2 RGBA pixel buffer (2048 bytes):
    /// Row 0 (y=0, coords [0..255, 0]): R = Red, G = Green, B = Blue, A = 255
    /// Row 1 (y=1, coords [0..255, 1]): R = Master, G = Master, B = Master, A = 255
    /// All pixels have A = 255 to eliminate any possibility of premultiplication side-effects.
    /// </summary>
    public static void BuildCurveLut2D(
        CurvePoint[]? rgbPoints,
        CurvePoint[]? redPoints,
        CurvePoint[]? greenPoints,
        CurvePoint[]? bluePoints,
        byte[] buffer2048)
    {
        float[] rLut = EvaluateSpline(redPoints);
        float[] gLut = EvaluateSpline(greenPoints);
        float[] bLut = EvaluateSpline(bluePoints);
        float[] mLut = EvaluateSpline(rgbPoints);

        for (int i = 0; i < 256; i++)
        {
            // Row 0: Red, Green, Blue
            int off0 = i * 4;
            buffer2048[off0]     = (byte)Math.Clamp((int)MathF.Round(rLut[i] * 255f), 0, 255);
            buffer2048[off0 + 1] = (byte)Math.Clamp((int)MathF.Round(gLut[i] * 255f), 0, 255);
            buffer2048[off0 + 2] = (byte)Math.Clamp((int)MathF.Round(bLut[i] * 255f), 0, 255);
            buffer2048[off0 + 3] = 255;

            // Row 1: Master / Luma curve
            int off1 = 1024 + i * 4;
            byte m = (byte)Math.Clamp((int)MathF.Round(mLut[i] * 255f), 0, 255);
            buffer2048[off1]     = m;
            buffer2048[off1 + 1] = m;
            buffer2048[off1 + 2] = m;
            buffer2048[off1 + 3] = 255;
        }
    }

    public static ExposureCurvePoint[] DefaultExposureCurve() => new[]
    {
        new ExposureCurvePoint(0f, 128f, 1.0f),
        new ExposureCurvePoint(255f, 128f, 1.0f)
    };

    public static bool IsIdentity(ExposureCurvePoint[]? points)
    {
        if (points == null || points.Length == 0)
            return true;
        for (int i = 0; i < points.Length; i++)
        {
            if (MathF.Abs(points[i].Y - 128f) > 0.5f)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Evaluates the exposure curve into normalized [0, 1] raw values (where 128/255 is 0 EV).
    /// Used for drawing the curve graph and for packing into texture LUT.
    /// Supports per-point Curvature blending between linear chord and cubic Hermite spline.
    /// </summary>
    public static float[] EvaluateExposureRaw(ExposureCurvePoint[]? inputPoints, int resolution = 256)
    {
        float[] lut = new float[resolution];
        if (inputPoints == null || inputPoints.Length < 2)
        {
            for (int i = 0; i < resolution; i++)
                lut[i] = 128f / 255f;
            return lut;
        }

        var points = (ExposureCurvePoint[])inputPoints.Clone();
        Array.Sort(points, (a, b) => a.X.CompareTo(b.X));

        int n = points.Length;
        float[] deltas = new float[n - 1];
        float[] ms = new float[n];

        for (int i = 0; i < n - 1; i++)
        {
            float dx = points[i + 1].X - points[i].X;
            float dy = points[i + 1].Y - points[i].Y;
            deltas[i] = dx > 1e-4f ? dy / dx : (dy > 0f ? 1e4f : (dy < 0f ? -1e4f : 0f));
        }

        ms[0] = deltas[0];
        for (int i = 1; i < n - 1; i++)
        {
            if (deltas[i - 1] * deltas[i] <= 0f)
                ms[i] = 0f;
            else
                ms[i] = (deltas[i - 1] + deltas[i]) * 0.5f;
        }
        ms[n - 1] = deltas[n - 2];

        for (int i = 0; i < n - 1; i++)
        {
            if (MathF.Abs(deltas[i]) < 1e-5f)
            {
                ms[i] = 0f;
                ms[i + 1] = 0f;
            }
            else
            {
                float alpha = ms[i] / deltas[i];
                float beta = ms[i + 1] / deltas[i];
                float tau = alpha * alpha + beta * beta;
                if (tau > 9f)
                {
                    float scale = 3.0f / MathF.Sqrt(tau);
                    ms[i] = scale * alpha * deltas[i];
                    ms[i + 1] = scale * beta * deltas[i];
                }
            }
        }

        float maxVal = resolution - 1f;
        int pIndex = 0;
        for (int i = 0; i < resolution; i++)
        {
            float x = (i / maxVal) * 255f;

            if (x <= points[0].X)
            {
                lut[i] = Math.Clamp(points[0].Y / 255f, 0f, 1f);
                continue;
            }
            if (x >= points[n - 1].X)
            {
                lut[i] = Math.Clamp(points[n - 1].Y / 255f, 0f, 1f);
                continue;
            }

            while (pIndex < n - 2 && points[pIndex + 1].X < x)
                pIndex++;

            ExposureCurvePoint p0 = points[pIndex];
            ExposureCurvePoint p1 = points[pIndex + 1];
            float dx = p1.X - p0.X;
            float t = dx > 1e-5f ? (x - p0.X) / dx : 0f;
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            float yHerm = h00 * p0.Y + h10 * ms[pIndex] * dx + h01 * p1.Y + h11 * ms[pIndex + 1] * dx;
            float yLin = p0.Y + t * (p1.Y - p0.Y);

            // Interpolate curvature factor across segment (0 = straight, 1 = curved)
            float curw = (1f - t) * p0.Curvature + t * p1.Curvature;
            float y = (1f - curw) * yLin + curw * yHerm;

            lut[i] = Math.Clamp(y / 255f, 0f, 1f);
        }

        return lut;
    }

    /// <summary>
    /// Evaluates the exposure curve into delta EV stops [-5.0 EV .. +5.0 EV].
    /// </summary>
    public static float[] EvaluateExposureSpline(ExposureCurvePoint[]? inputPoints, int resolution = 256)
    {
        float[] raw = EvaluateExposureRaw(inputPoints, resolution);
        float[] lut = new float[resolution];
        for (int i = 0; i < resolution; i++)
        {
            float yVal = raw[i] * 255f;
            lut[i] = (yVal - 128f) * (5.0f / 127.0f);
        }
        return lut;
    }

    /// <summary>
    /// Populates a 256x3 RGBA pixel buffer (3072 bytes):
    /// Row 0 (y=0): R = Red, G = Green, B = Blue, A = 255
    /// Row 1 (y=1): R = Master, G = Master, B = Master, A = 255
    /// Row 2 (y=2): R = Exposure Curve, G = Exposure Curve, B = Exposure Curve, A = 255
    /// </summary>
    public static void BuildCurveLut3D(
        CurvePoint[]? rgbPoints,
        CurvePoint[]? redPoints,
        CurvePoint[]? greenPoints,
        CurvePoint[]? bluePoints,
        ExposureCurvePoint[]? expPoints,
        bool enableExposureCurve,
        byte[] buffer3072)
    {
        BuildCurveLut2D(rgbPoints, redPoints, greenPoints, bluePoints, buffer3072);

        float[]? expRaw = enableExposureCurve ? EvaluateExposureRaw(expPoints) : null;
        for (int i = 0; i < 256; i++)
        {
            int off2 = 2048 + i * 4;
            byte bVal = (enableExposureCurve && expRaw != null)
                ? (byte)Math.Clamp((int)MathF.Round(expRaw[i] * 255f), 0, 255)
                : (byte)128; // 128 = 0.0 EV
            buffer3072[off2]     = bVal;
            buffer3072[off2 + 1] = bVal;
            buffer3072[off2 + 2] = bVal;
            buffer3072[off2 + 3] = 255;
        }
    }
}

/// <summary>
/// A 2D control point for the Exposure Curve in [0, 255] coordinate space.
/// Y=128 represents 0.0 EV (identity). Range: [-5.0 EV .. +5.0 EV].
/// Curvature: 1.0 = smooth cubic curve, 0.0 = straight line (sharp corner).
/// </summary>
public readonly record struct ExposureCurvePoint(float X, float Y, float Curvature = 1.0f)
{
    public float X { get; init; } = Math.Clamp(X, 0f, 255f);
    public float Y { get; init; } = Math.Clamp(Y, 0f, 255f);
    public float Curvature { get; init; } = Math.Clamp(Curvature, 0f, 1f);

    public override string ToString() => $"({X:0.0}, {Y:0.0}, c={Curvature:0.00})";
}

