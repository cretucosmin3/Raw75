# Raw75

Desktop app for editing RAW photos. Free software, AGPL-3.

UI is Blossom. Photo preview is Skia on the GPU.

## Spec

See `SPEC.md` for the real pipeline. How we sit on Blossom: `docs/blossom.md`.

Short version: LibRaw once, GPU after that. Proxy for sliders, tiles at 1:1, same passes for preview and export. ImageSharp only writes the file. Presets are JSON offsets.

## Features

### Tone

- [ ] Exposure / brightness
- [ ] Contrast
- [ ] Highlights & shadows
- [ ] Whites & blacks
- [ ] Live histogram (RGB + luminance, clipped pixels)

### Color

- [ ] Temperature & tint
- [ ] Vibrance & saturation
- [ ] HSL mixer (six colors)

### Detail & crop

- [ ] Sharpen & denoise
- [ ] Crop (free, 1:1, 4:3, 16:9)
- [ ] Straighten, 90° rotate, flip

### Working

- [ ] Zoom & pan, 1:1
- [ ] Before / after
- [ ] Undo / redo
- [ ] Export (JPEG, PNG, WebP, TIFF)

### Underneath

- [ ] Open RAW into a GPU texture
- [ ] One SkSL shader for the edit
- [ ] JSON presets
- [ ] `.cube` LUTs
- [ ] HDR merge

## Build

```bash
dotnet run --project src/Raw75/Raw75.csproj
```

Needs .NET 10. Blossom lives next to this repo (`../Blossom`).

## License

Copyright (C) 2026 Cosmin Crețu

GNU Affero General Public License v3. See `LICENSE`.
