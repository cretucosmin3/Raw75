# How Raw75 works

You open a RAW, move sliders, export a picture. The preview has to stay instant, and the file you write has to match what you saw.

Blossom draws the chrome (panels, sliders, buttons). The photograph is not a pile of UI rectangles. It is its own Skia surface: a developed GPU image, with crop handles and split-view drawn on top. Dirty-rect UI and the photo pipeline do not share a shader.

## Opening a file

LibRaw reads the sensor. Demosaic, black/white point, camera color matrix, EXIF. Nikon NEF, Canon CR2/CR3, Sony ARW, Adobe DNG.

Output is linear camera RGB, 16-bit or 32-bit float. Not a JPEG. We decode once. Edits never go back to the file.

That buffer is too big to live as one 32-bit texture (a 45 MP `RgbaF32` image is on the order of 700 MB). So the decode is stored as the **source**, and the GPU gets:

- A **proxy** of the whole frame, long edge around 2–4K, half-float. This is what sliders run on.
- **1:1 tiles** pulled on demand when you zoom in.

Histogram, export, and true 1:1 use developed pixels at the resolution they need. They do not sample only what’s on screen.

## Color

Camera RGB is not a working space. Fixed conversion:

1. White balance as multipliers in camera RGB (from the file, then the user’s temp/tint on top).
2. Camera matrix into XYZ, then into **linear Rec.2020**.
3. Tone and color sliders run in a **log-ish encoding** of that (so HSL and contrast don’t explode on linear highlights). Exact curve is one function, shared by preview and export.
4. Out to the display via an sRGB (or later, display ICC) transfer. Out to a file with an ICC that matches the export space.

If we skip this and run HSL on raw linear, the mixer will look wrong. If we skip ICC on export, the JPEG will look wrong in other apps.

**Match gray** (map midtones toward 18% / ~0.18 linear) is an explicit option, not a hidden pre-pass. Presets can set “apply match-gray” or not. Leaving it always on would make two exposures of the same scene collapse into each other, which photographers will not thank us for.

## Order of operations

Frozen. Same order on the proxy, on 1:1 tiles, and on export. If you change this, every preset on disk is a lie.

1. Sample the source with crop / straighten / rotate / flip (plus a border of extra pixels for blur kernels).
2. White balance, then camera → Rec.2020.
3. Exposure (linear EV gain).
4. Contrast (S-curve in the working encoding).
5. Highlights / shadows.
6. Whites / blacks.
7. Vibrance, then saturation.
8. HSL mixer (six colors).
9. Denoise (luma and chroma, separate).
10. Sharpen.
11. 3D LUT, if any.
12. Display transform, or export encode.

Geometry is first so we don’t denoise and sharpen pixels we’re going to throw away. Detail is last so we don’t sharpen a curve.

## GPU passes

Not one shader for everything. Neighborhood filters cannot be faked as point ops.

| Pass | What | Why it is separate |
|---|---|---|
| Develop | Steps 2–8, fused | Point ops. Sliders are uniforms. This is the fast loop. |
| Denoise | Step 9 | Needs neighbors. At least a blur / bilateral-style pass, luma vs chroma. |
| Sharpen | Step 10 | Unsharp needs a blurred luma. Cheap, but not a single tap on the source. |
| LUT | Step 11 | `.cube` unwrapped to a 2D texture, sampled at the end. |
| Display | Step 12 | Transfer + gamut for the screen. |

Slider dragging only re-runs what changed. Moving exposure does not re-run denoise if denoise inputs didn’t change. We still keep this to a handful of passes, not a complex module graph.

Preview target: the develop pass on the proxy in about 5–16 ms. Denoise may be slower; if it is, we can show develop immediately and let denoise catch up a frame later. Never fall back to ImageSharp for the view.

## Preview vs export

The view shader running on visible proxy pixels is **only the view**. Histogram, 1:1, and export all develop the pixels they need with the **same** passes and the **same** uniforms.

Export:

1. Develop the cropped region at export resolution on the GPU (tile if it doesn’t fit).
2. Read back.
3. ImageSharp writes JPEG / PNG / WebP / TIFF on a background thread (quality, size, ICC).

ImageSharp does not reimplement the look. If it did, the file would drift from the screen. It is an encoder.

Before/after is the same pipeline with the look uniforms zeroed (or split-screen: left with look, right without).

## Histogram

RGB + luminance, with marks for clipped whites and blacks.

It is computed from a downsample of the **whole developed image** (the proxy is fine), not from the zoomed viewport. A 1:1 crop of a window is not a histogram of the photo.

When sliders move, rebuild it from the proxy after develop. Do not read the framebuffer.

## Crop and geometry

Free crop and 1:1 / 4:3 / 16:9. Straighten is a continuous angle; auto-crop keeps the frame rectangular. 90° and mirrors are flags on the same transform.

The transform is how we sample the source (step 1). Overlay widgets live in Blossom on top of the photo surface.

Zoom and pan are a view matrix on already-developed pixels. 1:1 means showing source pixels at screen pixels, using tiles, not stretching the proxy.

## Presets

JSON of offsets, not absolute scene values. Example: contrast 1.12, highlights −0.28, a bit of orange in the mixer. Temperature, exposure, and match-gray are optional fields so a look can travel across cameras without fighting the shot.

We are not storing complex scene-referred graphs.

A `.cube` LUT is extra, sampled in the LUT pass. It is not a replacement for the JSON.

## Undo

A stack of the parameter set (and the crop). Not pixel snapshots. When we add local adjustments later, those are more parameters (a mask + a small set of deltas), still not bitmaps.

## Local adjustments

Global uniforms stay the default. The develop pass should allow a **mask texture** times an extra copy of the tone/color deltas, so brushes don’t force a new architecture. Masks are not required to ship the global editor, but the shader layout should leave the slot.

## HDR brackets

Several exposures, already linear from LibRaw, so no camera-response curve.

1. Align (handheld won’t match pixel for pixel).
2. Scale each by shutter time.
3. Blend with more weight on midtones, less on clipped highlights and noisy shadows.
4. Where something moved, keep the middle exposure.
5. The merge **is** the source buffer (`RgbaF32`). It then goes through the same proxy / develop / export path as a single RAW.

Tone-map (Reinhard or ACES) is the display/export end of that path, not a special HDR-only view.

Align + de-ghost is its own pile of work. It still uses this pipeline; it does not replace it.

## What we are not doing

- No CPU preview. No ImageSharp in the slider loop.
- No sprawling pixelpipe (dozens of modules, full-buffer, hitchy).
- No ComputeSharp as the engine. Skia/SkSL is the GPU path everywhere, including Windows.
- No second, different look for export.
- No uploading the full 32-bit megapixel image as one texture and hoping VRAM is fine.
