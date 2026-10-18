using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Raw75.Develop;
using Raw75.Imaging;

namespace Raw75.Io;

/// <summary>
/// 32-bit IEEE float scene-linear RGB TIFF reader and writer.
/// Tagged with Raw75-HDR metadata, source paths, and exposure parameters.
/// </summary>
internal static class HdrTiff
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometric = 262;
    private const ushort TagImageDescription = 270;
    private const ushort TagMake = 271;
    private const ushort TagModel = 272;
    private const ushort TagStripOffsets = 273;
    private const ushort TagOrientation = 274;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagXResolution = 282;
    private const ushort TagYResolution = 283;
    private const ushort TagPlanarConfig = 284;
    private const ushort TagResolutionUnit = 296;
    private const ushort TagSoftware = 305;
    private const ushort TagDateTime = 306;
    private const ushort TagSampleFormat = 339;

    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;

    public static bool IsHdrTiff(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        string ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".tif", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".tiff", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 16)
                return false;

            using var reader = new BinaryReader(stream);
            ushort endian = reader.ReadUInt16();
            if (endian != 0x4949 && endian != 0x4D4D) // II (little) or MM (big)
                return false;

            bool isLittle = endian == 0x4949;
            ushort magic = ReadUInt16(reader, isLittle);
            if (magic != 42)
                return false;

            uint ifdOffset = ReadUInt32(reader, isLittle);
            if (ifdOffset == 0 || ifdOffset >= stream.Length)
                return false;

            stream.Seek(ifdOffset, SeekOrigin.Begin);
            ushort numEntries = ReadUInt16(reader, isLittle);
            if (numEntries == 0 || numEntries > 500)
                return false;

            bool has32Bit = false;
            bool hasFloatSample = false;

            for (int i = 0; i < numEntries; i++)
            {
                ushort tag = ReadUInt16(reader, isLittle);
                ushort type = ReadUInt16(reader, isLittle);
                uint count = ReadUInt32(reader, isLittle);
                uint valOrOffset = ReadUInt32(reader, isLittle);

                if (tag == TagBitsPerSample)
                {
                    if (count == 1 && valOrOffset == 32)
                    {
                        has32Bit = true;
                    }
                    else if (count >= 3)
                    {
                        long saved = stream.Position;
                        stream.Seek(valOrOffset, SeekOrigin.Begin);
                        ushort b0 = ReadUInt16(reader, isLittle);
                        stream.Seek(saved, SeekOrigin.Begin);
                        if (b0 == 32)
                            has32Bit = true;
                    }
                }
                else if (tag == TagSampleFormat)
                {
                    if (count == 1 && valOrOffset == 3) // 3 = IEEEFP
                    {
                        hasFloatSample = true;
                    }
                    else if (count >= 3)
                    {
                        long saved = stream.Position;
                        stream.Seek(valOrOffset, SeekOrigin.Begin);
                        ushort f0 = ReadUInt16(reader, isLittle);
                        stream.Seek(saved, SeekOrigin.Begin);
                        if (f0 == 3)
                            hasFloatSample = true;
                    }
                }
            }

            return has32Bit || hasFloatSample;
        }
        catch
        {
            return false;
        }
    }

    public static void Write(
        string path,
        RasterBuffer raster,
        PhotoMetadata? metadata = null,
        IReadOnlyList<string>? sourcePaths = null,
        IReadOnlyList<float>? evs = null)
    {
        if (!raster.HasPixels || raster.Width <= 0 || raster.Height <= 0)
            throw new ArgumentException("Raster has no pixels to write.", nameof(raster));

        int width = raster.Width;
        int height = raster.Height;
        float[]? linear = raster.Linear;
        byte[]? rgba = raster.Rgba;

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Build metadata description
        var sbDesc = new StringBuilder();
        sbDesc.AppendLine("Raw75-HDR");
        if (sourcePaths != null && sourcePaths.Count > 0)
        {
            sbDesc.Append("Sources: ");
            sbDesc.AppendLine(string.Join(";", sourcePaths));
        }
        if (evs != null && evs.Count > 0)
        {
            sbDesc.Append("EVs: ");
            var evStrs = new List<string>(evs.Count);
            for (int i = 0; i < evs.Count; i++)
                evStrs.Add(evs[i].ToString("0.00", CultureInfo.InvariantCulture));
            sbDesc.AppendLine(string.Join(";", evStrs));
        }

        string desc = sbDesc.ToString();
        string make = metadata?.CameraMake ?? "";
        string model = metadata?.CameraModel ?? "";
        string software = "Raw75";
        string dateTime = metadata?.CaptureTime.HasValue == true
            ? metadata.CaptureTime.Value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
            : DateTime.Now.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs);

        // 1. TIFF Header
        writer.Write((ushort)0x4949); // 'II' Little Endian
        writer.Write((ushort)42);     // TIFF magic
        writer.Write((uint)8);        // IFD0 offset

        // 2. Prepare IFD entries
        var entries = new List<TiffDirEntry>(20);
        var extraData = new MemoryStream();
        var extraWriter = new BinaryWriter(extraData);

        // Entries will start at offset 8.
        // IFD size: 2 bytes count + N * 12 bytes + 4 bytes next IFD offset = 6 + N * 12.
        // Let's compute exact IFD offset and size.
        // We will write IFD entries first with placeholder offsets, then resolve offsets.

        void AddShortOrLong(ushort tag, uint val)
        {
            if (val <= 0xFFFF)
                entries.Add(new TiffDirEntry(tag, TypeShort, 1, val));
            else
                entries.Add(new TiffDirEntry(tag, TypeLong, 1, val));
        }

        AddShortOrLong(TagImageWidth, (uint)width);
        AddShortOrLong(TagImageLength, (uint)height);

        // BitsPerSample: 32, 32, 32
        entries.Add(new TiffDirEntry(TagBitsPerSample, TypeShort, 3, 0)); // resolved below
        entries.Add(new TiffDirEntry(TagCompression, TypeShort, 1, 1)); // None
        entries.Add(new TiffDirEntry(TagPhotometric, TypeShort, 1, 2)); // RGB

        if (!string.IsNullOrEmpty(desc))
            entries.Add(new TiffDirEntry(TagImageDescription, TypeAscii, (uint)Encoding.ASCII.GetByteCount(desc) + 1, 0));
        if (!string.IsNullOrEmpty(make))
            entries.Add(new TiffDirEntry(TagMake, TypeAscii, (uint)Encoding.ASCII.GetByteCount(make) + 1, 0));
        if (!string.IsNullOrEmpty(model))
            entries.Add(new TiffDirEntry(TagModel, TypeAscii, (uint)Encoding.ASCII.GetByteCount(model) + 1, 0));

        entries.Add(new TiffDirEntry(TagStripOffsets, TypeLong, 1, 0)); // resolved below
        entries.Add(new TiffDirEntry(TagOrientation, TypeShort, 1, 1));
        entries.Add(new TiffDirEntry(TagSamplesPerPixel, TypeShort, 1, 3));
        AddShortOrLong(TagRowsPerStrip, (uint)height);

        uint pixelBytes = (uint)width * (uint)height * 3u * 4u; // 3 channels * 4 bytes float
        entries.Add(new TiffDirEntry(TagStripByteCounts, TypeLong, 1, pixelBytes));

        entries.Add(new TiffDirEntry(TagXResolution, TypeRational, 1, 0)); // resolved below
        entries.Add(new TiffDirEntry(TagYResolution, TypeRational, 1, 0)); // resolved below
        entries.Add(new TiffDirEntry(TagPlanarConfig, TypeShort, 1, 1)); // Chunky
        entries.Add(new TiffDirEntry(TagResolutionUnit, TypeShort, 1, 2)); // Inch
        entries.Add(new TiffDirEntry(TagSoftware, TypeAscii, (uint)Encoding.ASCII.GetByteCount(software) + 1, 0));
        entries.Add(new TiffDirEntry(TagDateTime, TypeAscii, 20, 0));
        entries.Add(new TiffDirEntry(TagSampleFormat, TypeShort, 3, 0)); // 3, 3, 3 (IEEEFP)

        // Sort entries by Tag ascending (strict TIFF requirement)
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        uint ifdSize = 2u + (uint)entries.Count * 12u + 4u;
        uint extraDataStart = 8u + ifdSize;

        // Now lay out extra data
        uint AllocExtra(byte[] data)
        {
            uint off = extraDataStart + (uint)extraData.Position;
            extraWriter.Write(data);
            if ((data.Length % 2) != 0)
                extraWriter.Write((byte)0); // word alignment
            return off;
        }

        uint AllocAscii(string str)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(str + "\0");
            return AllocExtra(bytes);
        }

        // Fill out off-IFD values
        byte[] bitsPerSampleBytes = new byte[6];
        BitConverter.TryWriteBytes(bitsPerSampleBytes.AsSpan(0, 2), (ushort)32);
        BitConverter.TryWriteBytes(bitsPerSampleBytes.AsSpan(2, 2), (ushort)32);
        BitConverter.TryWriteBytes(bitsPerSampleBytes.AsSpan(4, 2), (ushort)32);
        uint bitsOffset = AllocExtra(bitsPerSampleBytes);

        byte[] sampleFormatBytes = new byte[6];
        BitConverter.TryWriteBytes(sampleFormatBytes.AsSpan(0, 2), (ushort)3); // IEEEFP
        BitConverter.TryWriteBytes(sampleFormatBytes.AsSpan(2, 2), (ushort)3);
        BitConverter.TryWriteBytes(sampleFormatBytes.AsSpan(4, 2), (ushort)3);
        uint sampleFormatOffset = AllocExtra(sampleFormatBytes);

        byte[] rational72 = new byte[8];
        BitConverter.TryWriteBytes(rational72.AsSpan(0, 4), (uint)72);
        BitConverter.TryWriteBytes(rational72.AsSpan(4, 4), (uint)1);
        uint xResOffset = AllocExtra(rational72);
        uint yResOffset = AllocExtra(rational72);

        uint descOffset = !string.IsNullOrEmpty(desc) ? AllocAscii(desc) : 0;
        uint makeOffset = !string.IsNullOrEmpty(make) ? AllocAscii(make) : 0;
        uint modelOffset = !string.IsNullOrEmpty(model) ? AllocAscii(model) : 0;
        uint softOffset = AllocAscii(software);
        uint dtOffset = AllocAscii(dateTime);

        uint pixelDataOffset = extraDataStart + (uint)extraData.Position;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            switch (e.Tag)
            {
                case TagBitsPerSample: e.ValOrOffset = bitsOffset; break;
                case TagSampleFormat: e.ValOrOffset = sampleFormatOffset; break;
                case TagXResolution: e.ValOrOffset = xResOffset; break;
                case TagYResolution: e.ValOrOffset = yResOffset; break;
                case TagImageDescription: e.ValOrOffset = descOffset; break;
                case TagMake: e.ValOrOffset = makeOffset; break;
                case TagModel: e.ValOrOffset = modelOffset; break;
                case TagSoftware: e.ValOrOffset = softOffset; break;
                case TagDateTime: e.ValOrOffset = dtOffset; break;
                case TagStripOffsets: e.ValOrOffset = pixelDataOffset; break;
            }
            entries[i] = e;
        }

        // Write IFD
        writer.Write((ushort)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            writer.Write(e.Tag);
            writer.Write(e.Type);
            writer.Write(e.Count);
            writer.Write(e.ValOrOffset);
        }
        writer.Write((uint)0); // No more IFDs

        // Write extra data
        writer.Write(extraData.ToArray());

        // Write pixel floats (RGB)
        byte[] rowBytes = new byte[width * 3 * sizeof(float)];
        Span<float> rowFloats = MemoryMarshal.Cast<byte, float>(rowBytes.AsSpan());

        for (int y = 0; y < height; y++)
        {
            int si = y * width * 4;
            int di = 0;
            for (int x = 0; x < width; x++)
            {
                if (linear != null)
                {
                    rowFloats[di++] = linear[si];
                    rowFloats[di++] = linear[si + 1];
                    rowFloats[di++] = linear[si + 2];
                }
                else if (rgba != null)
                {
                    rowFloats[di++] = rgba[si] / 255f;
                    rowFloats[di++] = rgba[si + 1] / 255f;
                    rowFloats[di++] = rgba[si + 2] / 255f;
                }
                si += 4;
            }
            writer.Write(rowBytes);
        }

        writer.Flush();
        fs.Flush();

        if (metadata?.CaptureTime.HasValue == true)
        {
            try
            {
                File.SetLastWriteTime(path, metadata.CaptureTime.Value);
            }
            catch { }
        }
    }

    public static bool TryRead(string path, out RasterBuffer raster, out PhotoMetadata? metadata)
    {
        raster = default;
        metadata = null;

        if (!File.Exists(path))
            return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            ushort endian = reader.ReadUInt16();
            if (endian != 0x4949 && endian != 0x4D4D)
                return false;

            bool isLittle = endian == 0x4949;
            ushort magic = ReadUInt16(reader, isLittle);
            if (magic != 42)
                return false;

            uint ifdOffset = ReadUInt32(reader, isLittle);
            if (ifdOffset == 0 || ifdOffset >= fs.Length)
                return false;

            fs.Seek(ifdOffset, SeekOrigin.Begin);
            ushort numEntries = ReadUInt16(reader, isLittle);

            int width = 0;
            int height = 0;
            int samplesPerPixel = 1;
            int bitsPerSample = 8;
            int sampleFormat = 1; // 1 = uint, 3 = IEEEFP
            int planarConfig = 1;
            var stripOffsets = new List<uint>();
            var stripByteCounts = new List<uint>();
            int rowsPerStrip = 0;
            string description = "";
            string make = "";
            string model = "";
            string dtStr = "";

            for (int i = 0; i < numEntries; i++)
            {
                ushort tag = ReadUInt16(reader, isLittle);
                ushort type = ReadUInt16(reader, isLittle);
                uint count = ReadUInt32(reader, isLittle);
                uint valOrOffset = ReadUInt32(reader, isLittle);

                switch (tag)
                {
                    case TagImageWidth:
                        width = (int)valOrOffset;
                        break;
                    case TagImageLength:
                        height = (int)valOrOffset;
                        break;
                    case TagSamplesPerPixel:
                        samplesPerPixel = (int)valOrOffset;
                        break;
                    case TagBitsPerSample:
                        if (count == 1)
                            bitsPerSample = (int)valOrOffset;
                        else
                        {
                            long cur = fs.Position;
                            fs.Seek(valOrOffset, SeekOrigin.Begin);
                            bitsPerSample = ReadUInt16(reader, isLittle);
                            fs.Seek(cur, SeekOrigin.Begin);
                        }
                        break;
                    case TagSampleFormat:
                        if (count == 1)
                            sampleFormat = (int)valOrOffset;
                        else
                        {
                            long cur = fs.Position;
                            fs.Seek(valOrOffset, SeekOrigin.Begin);
                            sampleFormat = ReadUInt16(reader, isLittle);
                            fs.Seek(cur, SeekOrigin.Begin);
                        }
                        break;
                    case TagPlanarConfig:
                        planarConfig = (int)valOrOffset;
                        break;
                    case TagRowsPerStrip:
                        rowsPerStrip = (int)valOrOffset;
                        break;
                    case TagStripOffsets:
                        if (count == 1)
                            stripOffsets.Add(valOrOffset);
                        else
                        {
                            long cur = fs.Position;
                            fs.Seek(valOrOffset, SeekOrigin.Begin);
                            for (int s = 0; s < count; s++)
                                stripOffsets.Add(type == TypeShort ? ReadUInt16(reader, isLittle) : ReadUInt32(reader, isLittle));
                            fs.Seek(cur, SeekOrigin.Begin);
                        }
                        break;
                    case TagStripByteCounts:
                        if (count == 1)
                            stripByteCounts.Add(valOrOffset);
                        else
                        {
                            long cur = fs.Position;
                            fs.Seek(valOrOffset, SeekOrigin.Begin);
                            for (int s = 0; s < count; s++)
                                stripByteCounts.Add(type == TypeShort ? ReadUInt16(reader, isLittle) : ReadUInt32(reader, isLittle));
                            fs.Seek(cur, SeekOrigin.Begin);
                        }
                        break;
                    case TagImageDescription:
                        description = ReadAscii(fs, reader, valOrOffset, count);
                        break;
                    case TagMake:
                        make = ReadAscii(fs, reader, valOrOffset, count);
                        break;
                    case TagModel:
                        model = ReadAscii(fs, reader, valOrOffset, count);
                        break;
                    case TagDateTime:
                        dtStr = ReadAscii(fs, reader, valOrOffset, count);
                        break;
                }
            }

            if (width <= 0 || height <= 0 || bitsPerSample != 32 || stripOffsets.Count == 0)
                return false;

            float[] linear = new float[width * height * 4];
            int ch = Math.Max(1, samplesPerPixel);

            if (stripOffsets.Count == 1 && stripOffsets[0] + (long)width * height * ch * 4 <= fs.Length)
            {
                fs.Seek(stripOffsets[0], SeekOrigin.Begin);
                byte[] raw = new byte[width * ch * 4];
                Span<float> rawFloats = MemoryMarshal.Cast<byte, float>(raw.AsSpan());

                int dstIdx = 0;
                for (int y = 0; y < height; y++)
                {
                    int read = fs.Read(raw, 0, raw.Length);
                    if (read < raw.Length)
                        break;

                    int srcIdx = 0;
                    for (int x = 0; x < width; x++)
                    {
                        if (ch >= 3)
                        {
                            linear[dstIdx] = rawFloats[srcIdx];
                            linear[dstIdx + 1] = rawFloats[srcIdx + 1];
                            linear[dstIdx + 2] = rawFloats[srcIdx + 2];
                        }
                        else
                        {
                            float g = rawFloats[srcIdx];
                            linear[dstIdx] = linear[dstIdx + 1] = linear[dstIdx + 2] = g;
                        }
                        linear[dstIdx + 3] = ch >= 4 ? rawFloats[srcIdx + 3] : 1f;
                        dstIdx += 4;
                        srcIdx += ch;
                    }
                }
            }
            else
            {
                // Multi-strip read
                int rowsPer = rowsPerStrip > 0 ? rowsPerStrip : height;
                int curY = 0;
                for (int s = 0; s < stripOffsets.Count && curY < height; s++)
                {
                    fs.Seek(stripOffsets[s], SeekOrigin.Begin);
                    int stripH = Math.Min(rowsPer, height - curY);
                    byte[] raw = new byte[width * stripH * ch * 4];
                    int read = fs.Read(raw, 0, raw.Length);
                    Span<float> rawFloats = MemoryMarshal.Cast<byte, float>(raw.AsSpan(0, read));

                    int srcIdx = 0;
                    for (int r = 0; r < stripH; r++)
                    {
                        int dstIdx = (curY + r) * width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            if (srcIdx + ch <= rawFloats.Length)
                            {
                                if (ch >= 3)
                                {
                                    linear[dstIdx] = rawFloats[srcIdx];
                                    linear[dstIdx + 1] = rawFloats[srcIdx + 1];
                                    linear[dstIdx + 2] = rawFloats[srcIdx + 2];
                                }
                                else
                                {
                                    float g = rawFloats[srcIdx];
                                    linear[dstIdx] = linear[dstIdx + 1] = linear[dstIdx + 2] = g;
                                }
                                linear[dstIdx + 3] = ch >= 4 ? rawFloats[srcIdx + 3] : 1f;
                            }
                            dstIdx += 4;
                            srcIdx += ch;
                        }
                    }
                    curY += stripH;
                }
            }

            raster = new RasterBuffer(linear, width, height);

            metadata = new PhotoMetadata
            {
                Width = width,
                Height = height,
                CameraMake = make.Trim(),
                CameraModel = model.Trim(),
                Description = description.Trim(),
                FileSizeBytes = fs.Length
            };

            if (!string.IsNullOrEmpty(dtStr) && RawDecoder.TryParseExifDate(dtStr, out var dt))
                metadata.CaptureTime = dt;
            else
                metadata.CaptureTime = File.GetLastWriteTime(path);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadAscii(FileStream fs, BinaryReader reader, uint valOrOffset, uint count)
    {
        if (count == 0)
            return "";

        long cur = fs.Position;
        try
        {
            if (count <= 4)
            {
                byte[] b = BitConverter.GetBytes(valOrOffset);
                return Encoding.ASCII.GetString(b).TrimEnd('\0');
            }

            fs.Seek(valOrOffset, SeekOrigin.Begin);
            byte[] bytes = reader.ReadBytes((int)Math.Min(count, 4096));
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            fs.Seek(cur, SeekOrigin.Begin);
        }
    }

    private static ushort ReadUInt16(BinaryReader reader, bool isLittle)
    {
        ushort val = reader.ReadUInt16();
        if (!isLittle)
            return (ushort)((val >> 8) | (val << 8));
        return val;
    }

    private static uint ReadUInt32(BinaryReader reader, bool isLittle)
    {
        uint val = reader.ReadUInt32();
        if (!isLittle)
        {
            return ((val & 0x000000FF) << 24) |
                   ((val & 0x0000FF00) << 8) |
                   ((val & 0x00FF0000) >> 8) |
                   ((val & 0xFF000000) >> 24);
        }
        return val;
    }

    private struct TiffDirEntry
    {
        public ushort Tag;
        public ushort Type;
        public uint Count;
        public uint ValOrOffset;

        public TiffDirEntry(ushort tag, ushort type, uint count, uint valOrOffset)
        {
            Tag = tag;
            Type = type;
            Count = count;
            ValOrOffset = valOrOffset;
        }
    }
}
