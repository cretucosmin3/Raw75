using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Raw75.Develop;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaskShape
{
    Radial = 0,
    Linear = 1,
    Rectangle = 2,
    Spline = 3
}

/// <summary>One control point of a <see cref="MaskShape.Spline"/> mask, in source-image UVs (0–1).</summary>
public sealed class MaskPoint
{
    public float X { get; set; }
    public float Y { get; set; }

    public MaskPoint() { }

    public MaskPoint(float x, float y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// A local adjustment: a shape on the photo plus the Light / Color offsets applied inside it.
/// Positions are source-image UVs (0–1), so a mask stays glued to the photo through crop, rotation and flips.
/// Sizes (radii, half extents) are fractions of the source height and angles are degrees, both measured in
/// that isotropic space (u scaled by the source aspect), so a circle stays round on any aspect.
/// Feather 0 is a hard edge; Amount scales the whole effect.
/// </summary>
public sealed class MaskLayer
{
    /// <summary>Enabled masks the pipelines blend (one atlas tile and uniform slot each).</summary>
    public const int MaxMasks = 8;

    public string Name { get; set; } = "Mask";
    public MaskShape Shape { get; set; } = MaskShape.Radial;
    public bool Enabled { get; set; } = true;
    public bool Invert { get; set; }
    public float Feather { get; set; } = 50f; // 0..100
    public float Amount { get; set; } = 100f; // 0..100

    // Radial (ellipse) and Rectangle: center, half extents, rotation.
    public float CenterX { get; set; } = 0.5f;
    public float CenterY { get; set; } = 0.5f;
    public float RadiusX { get; set; } = 0.25f;
    public float RadiusY { get; set; } = 0.18f;
    public float Angle { get; set; }

    // Linear: full effect at Start, fading to none at End.
    public float StartX { get; set; } = 0.5f;
    public float StartY { get; set; } = 0.35f;
    public float EndX { get; set; } = 0.5f;
    public float EndY { get; set; } = 0.65f;

    /// <summary>Spline: closed Catmull-Rom curve through these points.</summary>
    public List<MaskPoint> Points { get; set; } = new();

    // Light
    public bool EnableLight { get; set; } = true;
    public float Exposure { get; set; }   // -5..+5 EV
    public float Contrast { get; set; }   // -100..100
    public float Highlights { get; set; } // -100..100
    public float Shadows { get; set; }    // -100..100
    public float Whites { get; set; }     // -100..100
    public float Blacks { get; set; }     // -100..100
    public float Dehaze { get; set; }     // -100..100

    // Color
    public bool EnableColor { get; set; } = true;
    public float Temperature { get; set; } // -100..100
    public float Tint { get; set; }        // -100..100
    public float Saturation { get; set; }  // -100..100
    public float Vibrance { get; set; }    // -100..100

    public const float MinRadius = 0.01f;

    /// <summary>Linear gradients have no edge to soften; their length is the transition.</summary>
    [JsonIgnore]
    public bool HasFeather => Shape != MaskShape.Linear;

    public static string ShapeName(MaskShape shape) => shape switch
    {
        MaskShape.Radial => "Radial",
        MaskShape.Linear => "Linear",
        MaskShape.Rectangle => "Rectangle",
        _ => "Custom"
    };

    /// <summary>"Radial 3": the shape name and the next number not taken by a mask of that shape.</summary>
    public static string NextName(IReadOnlyList<MaskLayer>? masks, MaskShape shape)
    {
        string baseName = ShapeName(shape);
        int n = 1;
        if (masks != null)
        {
            foreach (var m in masks)
            {
                if (m?.Name == null || !m.Name.StartsWith(baseName + " ", StringComparison.Ordinal))
                    continue;
                if (int.TryParse(m.Name.AsSpan(baseName.Length + 1), out int k) && k >= n)
                    n = k + 1;
            }
        }
        return $"{baseName} {n}";
    }

    public void ResetAdjustments()
    {
        Exposure = Contrast = Highlights = Shadows = Whites = Blacks = Dehaze = 0f;
        Temperature = Tint = Saturation = Vibrance = 0f;
        EnableLight = EnableColor = true;
    }

    public MaskLayer Clone()
    {
        var c = (MaskLayer)MemberwiseClone();
        c.Points = new List<MaskPoint>(Points?.Count ?? 0);
        if (Points != null)
        {
            for (int i = 0; i < Points.Count; i++)
                c.Points.Add(new MaskPoint(Points[i].X, Points[i].Y));
        }
        return c;
    }

    public bool LooksLike(MaskLayer o)
    {
        if (o == null) return false;
        if (Name != o.Name || Shape != o.Shape || Enabled != o.Enabled || Invert != o.Invert
            || Feather != o.Feather || Amount != o.Amount
            || !Near(CenterX, o.CenterX) || !Near(CenterY, o.CenterY)
            || !Near(RadiusX, o.RadiusX) || !Near(RadiusY, o.RadiusY) || !Near(Angle, o.Angle)
            || !Near(StartX, o.StartX) || !Near(StartY, o.StartY) || !Near(EndX, o.EndX) || !Near(EndY, o.EndY)
            || EnableLight != o.EnableLight || EnableColor != o.EnableColor
            || Exposure != o.Exposure || Contrast != o.Contrast || Highlights != o.Highlights
            || Shadows != o.Shadows || Whites != o.Whites || Blacks != o.Blacks || Dehaze != o.Dehaze
            || Temperature != o.Temperature || Tint != o.Tint || Saturation != o.Saturation || Vibrance != o.Vibrance)
            return false;
        int na = Points?.Count ?? 0;
        int nb = o.Points?.Count ?? 0;
        if (na != nb) return false;
        for (int i = 0; i < na; i++)
        {
            if (!Near(Points![i].X, o.Points![i].X) || !Near(Points[i].Y, o.Points[i].Y))
                return false;
        }
        return true;
    }

    /// <summary>Hash of everything that changes the mask's coverage (not its adjustments, amount or invert).</summary>
    public int CoverageHash()
    {
        var h = new HashCode();
        h.Add(Shape);
        h.Add(Feather);
        switch (Shape)
        {
            case MaskShape.Linear:
                h.Add(StartX); h.Add(StartY); h.Add(EndX); h.Add(EndY);
                break;
            case MaskShape.Spline:
                if (Points != null)
                {
                    h.Add(Points.Count);
                    foreach (var p in Points)
                    {
                        h.Add(p.X);
                        h.Add(p.Y);
                    }
                }
                break;
            default:
                h.Add(CenterX); h.Add(CenterY); h.Add(RadiusX); h.Add(RadiusY); h.Add(Angle);
                break;
        }
        return h.ToHashCode();
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-5f;
}
