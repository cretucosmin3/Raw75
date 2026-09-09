# Raw75

Desktop app for editing RAW photos. Free software, AGPL-3.

UI is Blossom. Photo preview is Skia on the GPU.

## Spec

See `SPEC.md` for the pipeline, `PLAN.md` for the work, `docs/blossom.md` for the UI host.

Short version: LibRaw once, GPU after that. Proxy for sliders, tiles at 1:1, same passes for preview and export. ImageSharp only writes the file. Presets are JSON offsets.

## Features

### Tone

- [x] Exposure / brightness
- [x] Contrast
- [x] Highlights & shadows
- [x] Whites & blacks
- [x] Live histogram (RGB + luminance, clipped pixels)

### Color

- [x] Temperature & tint
- [x] Vibrance & saturation
- [x] HSL mixer (six colors)

### Detail & crop

- [x] Sharpen & denoise
- [x] Crop (handles on the photo)
- [x] Straighten, 90° rotate, flip

### Working

- [x] Zoom & pan, 1:1, click-zoom
- [x] Before / after (and split)
- [x] Undo / redo
- [x] Export (JPEG, PNG, WebP, TIFF)

### Underneath

- [x] Open RAW into a GPU texture (thumbnail, then half-size preview)
- [x] SkSL develop shader
- [x] JSON presets
- [x] `.cube` LUT support in the shader (set LutPath)
- [x] HDR merge (simple average of open photos; align/de-ghost later)

## Build

```bash
dotnet run --project src/Raw75/Raw75.csproj
```

Self-contained Linux + Windows (Release, ReadyToRun) into `publish/`:

```bash
./publish.sh
```

Needs .NET 10. Blossom lives next to this repo (`../Blossom`).

## License

Copyright (C) 2026 Cosmin Crețu

GNU Affero General Public License v3. See `LICENSE`.
