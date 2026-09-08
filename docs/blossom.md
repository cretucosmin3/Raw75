# Blossom in Raw75

Raw75 is a Blossom app. The window, panels, sliders, and input are Blossom. The photograph is not: decode, develop, denoise, and export live in this repo. Blossom gives us a GPU context and a place to draw the result.

Host is next door: `../Blossom` (`BlossomRoot` in `Directory.Build.props`). Project reference only — no copy of the framework.

The photo pipeline itself is `SPEC.md`. This file is the glue.

## Boot

```csharp
Browser.Initialize(new Raw75Application());
```

That creates the GLFW window, the Skia GPU context, and runs until the window closes. `Gpu` is ready after load (`Gpu.IsReady`). Do not create a second GL context.

Work that must touch GPU or UI goes through `Browser.Post(...)`. LibRaw decode and ImageSharp encode can run on other threads; uploading textures and `InvalidatePaint` do not.

## Who draws what

| | Blossom | Raw75 |
|---|---|---|
| Window, resize, mouse, keys | yes | — |
| Panels, sliders, buttons, histogram chrome | `VisualElement` tree | we author the controls |
| Photo pixels | blit only | LibRaw → GPU surfaces → SkSL |
| Crop handles, split line | `DrawCallbackCommand` on top of the photo | we implement the widgets |
| File drop | `Browser.FilesDropped` | we open the RAW |
| JPEG/PNG/WebP/TIFF | — | ImageSharp after GPU readback |

The view (`WorkspaceView`) is a Blossom `View`. Chrome is elements with anchors. The photo is `PhotoPane`: a `VisualElement` that blits an `SKImage` from `Gpu`.

Drop a file on the window: JPEG/PNG open immediately; a RAW shows its embedded thumbnail first, then a half-size LibRaw demosaic replaces it. That’s a preview, not the develop pipeline.

## GPU

`Blossom.Core.Gpu` is the window’s `GRContext`.

- `CreateSurface(w, h, SKColorType.RgbaF16)` — proxy and ping-pong. F32 if the driver allows it. The *window* stays RGBA8.
- `Snapshot(surface)` → `SKImage` we own until we replace it.
- `ReadPixels(...)` for export. Then ImageSharp on a worker.
- `ResetContext()` if we ever issue raw GL. Prefer staying in Skia.
- `Flush()` before snapshot/readback.

Typical slider frame: develop pass on the proxy surface → snapshot → `InvalidatePaint` on the photo element. Denoise can land a frame later on another surface.

Tiles at 1:1 are more surfaces, same context. Do not upload the full 45 MP float image as one texture.

## Drawing the photo

In `OnAfterStyleDraw` (or a full `RecordDrawCommands` override):

```csharp
cmds.Add(new DrawSkImageCommand(_developed, dest));           // GPU image, we still own it
cmds.Add(new DrawPaintCommand(dest, paintWithRuntimeEffect)); // custom SkSL on a rect
cmds.Add(new DrawCallbackCommand(canvas => { /* crop overlay */ }));
```

`InvalidatePaint()` when the image or overlays change. The command’s `Execute` can read live uniforms so we are not rebuilding the ledger every tick.

Clip the photo with `Overflow = Clip`. Zoom/pan is our matrix inside that element, not `ScrollContainer`. Pointer capture is already on `VisualElement` (crop drag, pan). Wheel is `Events.OnScroll` / `OnMouseScroll`. Hit-test crop handles with `HitTestLocal`.

## Opening files

```csharp
Browser.FilesDropped += paths => { /* … */ };
Events.OnFilesDropped += paths => { /* same, on the view */ };
```

Absolute paths. No per-element drop target — if we need a “drop here” zone, hit-test the cursor ourselves.

A file-open dialog is our problem (not Blossom). Drop is how people will actually open RAWs.

## Input we rely on

- Pointer capture for sliders, crop, pan
- `Handled` so a slider drag does not also pan the photo
- `ReceivesKeyboard` on fields; no tab-focus system
- `Browser.Post` from decode/export threads

## What we do not use Blossom for

- LibRaw, color matrices, Rec.2020, match-gray
- The develop / denoise / sharpen / LUT shaders (`SKSLShaderManager` in Blossom is UI candy, not our look)
- Histogram math (chrome can be a Blossom element; the bins are ours)
- Preset JSON, undo stack of parameters
- ImageSharp encode + ICC
- HDR align/merge

If it would force Blossom to know what a RAW is, it stays here.

## Pointers in Blossom

| Need | Where |
|---|---|
| GPU | `src/Blossom/Core/Gpu.cs`, `docs/gpu.md` |
| Draw commands | `src/Blossom/Core/CommandLedger.cs` |
| Custom controls | `docs/custom_controls.md` |
| Input / drop | `docs/input_model.md` |
| Host | `src/Blossom/Browser.cs` |
