using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace QQDatabaseExplorer.Controls;

internal sealed class ApngAnimation
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = CreateCrcTable();

    private ApngAnimation(IReadOnlyList<ApngFrame> frames)
    {
        Frames = frames;
    }

    public IReadOnlyList<ApngFrame> Frames { get; }

    public static ApngAnimation? Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            return null;

        return TryParse(bytes, out var document) && document.Frames.Count > 1
            ? CreateAnimation(document)
            : null;
    }

    private static ApngAnimation? CreateAnimation(ApngDocument document)
    {
        using var canvasBitmap = new SKBitmap(
            document.Width,
            document.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        canvasBitmap.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(canvasBitmap);

        var frames = new List<ApngFrame>(document.Frames.Count);
        foreach (var frame in document.Frames)
        {
            using var previousBitmap = frame.DisposeOp == 2
                ? canvasBitmap.Copy()
                : null;

            using var frameBitmap = DecodeFrame(document, frame);
            if (frameBitmap is null)
                return null;

            var target = SKRect.Create(frame.XOffset, frame.YOffset, frame.Width, frame.Height);
            if (frame.BlendOp == 0)
            {
                using var clearPaint = new SKPaint { BlendMode = SKBlendMode.Clear };
                canvas.DrawRect(target, clearPaint);
            }

            canvas.DrawBitmap(frameBitmap, frame.XOffset, frame.YOffset);
            canvas.Flush();

            if (CreateAvaloniaBitmap(canvasBitmap) is not { } bitmap)
                return null;

            frames.Add(new ApngFrame(bitmap, frame.Delay));

            if (frame.DisposeOp == 1)
            {
                using var clearPaint = new SKPaint { BlendMode = SKBlendMode.Clear };
                canvas.DrawRect(target, clearPaint);
            }
            else if (frame.DisposeOp == 2 && previousBitmap is not null)
            {
                using var restorePaint = new SKPaint { BlendMode = SKBlendMode.Src };
                canvas.DrawBitmap(previousBitmap, 0, 0, restorePaint);
            }
        }

        return frames.Count > 1 ? new ApngAnimation(frames) : null;
    }

    private static SKBitmap? DecodeFrame(ApngDocument document, ApngParsedFrame frame)
    {
        var bytes = CreateFramePngBytes(document, frame);
        return SKBitmap.Decode(bytes);
    }

    private static Bitmap? CreateAvaloniaBitmap(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null)
            return null;

        using var stream = new MemoryStream(data.ToArray(), writable: false);
        return new Bitmap(stream);
    }

    private static bool TryParse(ReadOnlySpan<byte> bytes, out ApngDocument document)
    {
        document = default!;

        var index = PngSignature.Length;
        byte[]? ihdr = null;
        var globalChunks = new List<PngChunk>();
        var frames = new List<ApngParsedFrame>();
        FrameBuilder? currentFrame = null;
        var animationFrameCount = 0;

        while (index + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes[index..(index + 4)]);
            if (length < 0 || index + 12 + length > bytes.Length)
                return false;

            var type = bytes[(index + 4)..(index + 8)].ToArray();
            var typeText = TypeToString(type);
            var data = bytes[(index + 8)..(index + 8 + length)].ToArray();

            switch (typeText)
            {
                case "IHDR":
                    ihdr = data;
                    break;

                case "acTL":
                    if (data.Length >= 8)
                        animationFrameCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                    break;

                case "fcTL":
                    FinishFrame(currentFrame, frames);
                    currentFrame = FrameBuilder.Create(data);
                    if (currentFrame is null)
                        return false;
                    break;

                case "IDAT":
                    currentFrame?.AddImageData(data);
                    break;

                case "fdAT":
                    if (data.Length > 4)
                        currentFrame?.AddImageData(data[4..]);
                    break;

                case "IEND":
                    FinishFrame(currentFrame, frames);
                    currentFrame = null;
                    index = bytes.Length;
                    break;

                default:
                    if (currentFrame is null && IsGlobalChunk(typeText))
                        globalChunks.Add(new PngChunk(type, data));
                    break;
            }

            index += 12 + length;
        }

        if (ihdr is null || ihdr.Length < 13 || animationFrameCount <= 1 || frames.Count <= 1)
            return false;

        var width = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(0, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4, 4));
        if (width <= 0 || height <= 0)
            return false;

        document = new ApngDocument(width, height, ihdr, globalChunks, frames);
        return true;
    }

    private static void FinishFrame(FrameBuilder? frameBuilder, List<ApngParsedFrame> frames)
    {
        if (frameBuilder is { HasImageData: true })
            frames.Add(frameBuilder.ToFrame());
    }

    private static byte[] CreateFramePngBytes(ApngDocument document, ApngParsedFrame frame)
    {
        using var stream = new MemoryStream();
        stream.Write(PngSignature);

        var ihdr = document.Ihdr.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), frame.Height);
        WriteChunk(stream, "IHDR"u8, ihdr);

        foreach (var chunk in document.GlobalChunks)
        {
            WriteChunk(stream, chunk.Type, chunk.Data);
        }

        foreach (var imageData in frame.ImageData)
        {
            WriteChunk(stream, "IDAT"u8, imageData);
        }

        WriteChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        stream.Write(lengthBytes);
        stream.Write(type);
        stream.Write(data);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ComputeCrc(type, data));
        stream.Write(crcBytes);
    }

    private static bool IsGlobalChunk(string type)
    {
        return type is "PLTE" or "tRNS" or "gAMA" or "cHRM" or "sRGB" or "iCCP" or "pHYs";
    }

    private static string TypeToString(ReadOnlySpan<byte> type)
    {
        return string.Create(4, type.ToArray(), static (chars, value) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)value[i];
        });
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1
                    ? 0xEDB88320u ^ (value >> 1)
                    : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public sealed record ApngFrame(Bitmap Bitmap, TimeSpan Delay);

    private sealed record ApngDocument(
        int Width,
        int Height,
        byte[] Ihdr,
        IReadOnlyList<PngChunk> GlobalChunks,
        IReadOnlyList<ApngParsedFrame> Frames);

    private sealed record PngChunk(byte[] Type, byte[] Data);

    private sealed record ApngParsedFrame(
        int Width,
        int Height,
        int XOffset,
        int YOffset,
        TimeSpan Delay,
        byte DisposeOp,
        byte BlendOp,
        IReadOnlyList<byte[]> ImageData);

    private sealed class FrameBuilder
    {
        private readonly List<byte[]> _imageData = [];

        private FrameBuilder(
            int width,
            int height,
            int xOffset,
            int yOffset,
            TimeSpan delay,
            byte disposeOp,
            byte blendOp)
        {
            Width = width;
            Height = height;
            XOffset = xOffset;
            YOffset = yOffset;
            Delay = delay;
            DisposeOp = disposeOp;
            BlendOp = blendOp;
        }

        private int Width { get; }

        private int Height { get; }

        private int XOffset { get; }

        private int YOffset { get; }

        private TimeSpan Delay { get; }

        private byte DisposeOp { get; }

        private byte BlendOp { get; }

        public bool HasImageData => _imageData.Count > 0;

        public static FrameBuilder? Create(ReadOnlySpan<byte> data)
        {
            if (data.Length < 26)
                return null;

            var width = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
            var height = BinaryPrimitives.ReadInt32BigEndian(data[8..12]);
            var xOffset = BinaryPrimitives.ReadInt32BigEndian(data[12..16]);
            var yOffset = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
            var delayNumerator = BinaryPrimitives.ReadUInt16BigEndian(data[20..22]);
            var delayDenominator = BinaryPrimitives.ReadUInt16BigEndian(data[22..24]);
            var disposeOp = data[24];
            var blendOp = data[25];

            if (width <= 0 || height <= 0 || xOffset < 0 || yOffset < 0)
                return null;

            if (delayDenominator == 0)
                delayDenominator = 100;

            var delay = delayNumerator == 0
                ? TimeSpan.FromMilliseconds(100)
                : TimeSpan.FromMilliseconds(Math.Max(20, delayNumerator * 1000.0 / delayDenominator));

            return new FrameBuilder(width, height, xOffset, yOffset, delay, disposeOp, blendOp);
        }

        public void AddImageData(byte[] data)
        {
            _imageData.Add(data);
        }

        public ApngParsedFrame ToFrame()
        {
            return new ApngParsedFrame(
                Width,
                Height,
                XOffset,
                YOffset,
                Delay,
                DisposeOp,
                BlendOp,
                _imageData.ToArray());
        }
    }
}
