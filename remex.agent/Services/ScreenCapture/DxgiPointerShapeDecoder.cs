namespace Remex.Agent.Services.ScreenCapture;

internal static class DxgiPointerShapeDecoder
{
    internal const uint PointerShapeTypeMonochrome = 0x1;
    internal const uint PointerShapeTypeColor = 0x2;
    internal const uint PointerShapeTypeMaskedColor = 0x4;

    internal readonly record struct PointerShapeInfo(
        uint Type,
        int Width,
        int Height,
        int Pitch,
        int HotspotX,
        int HotspotY);

    public static bool TryDecode(PointerShapeInfo info, ReadOnlySpan<byte> buffer, out CursorShapeSnapshot? snapshot)
    {
        snapshot = info.Type switch
        {
            PointerShapeTypeColor => CreateColorSnapshot(info, buffer),
            PointerShapeTypeMonochrome => CreateMonochromeSnapshot(info, buffer),
            PointerShapeTypeMaskedColor => CreateMaskedColorSnapshot(info, buffer),
            _ => null,
        };

        return snapshot is not null;
    }

    private static CursorShapeSnapshot? CreateColorSnapshot(PointerShapeInfo info, ReadOnlySpan<byte> buffer)
    {
        if (!ValidateColorShape(info, buffer.Length))
        {
            return null;
        }

        var shapeBytes = NormalizeColorBuffer(info, buffer);
        return new CursorShapeSnapshot(IntPtr.Zero, info.Width, info.Height, info.HotspotX, info.HotspotY, shapeBytes);
    }

    private static CursorShapeSnapshot? CreateMaskedColorSnapshot(PointerShapeInfo info, ReadOnlySpan<byte> buffer)
    {
        if (!ValidateColorShape(info, buffer.Length))
        {
            return null;
        }

        var shapeBytes = new byte[info.Width * info.Height * 4];
        var rowBytes = info.Width * 4;

        for (var y = 0; y < info.Height; y++)
        {
            var sourceRow = buffer.Slice(y * info.Pitch, info.Pitch);
            var destinationOffset = y * rowBytes;
            for (var x = 0; x < info.Width; x++)
            {
                var sourceOffset = x * 4;
                var destinationIndex = destinationOffset + sourceOffset;
                var alpha = sourceRow[sourceOffset + 3];
                if (alpha == 0xFF)
                {
                    return null;
                }

                shapeBytes[destinationIndex] = sourceRow[sourceOffset];
                shapeBytes[destinationIndex + 1] = sourceRow[sourceOffset + 1];
                shapeBytes[destinationIndex + 2] = sourceRow[sourceOffset + 2];
                shapeBytes[destinationIndex + 3] = alpha == 0 ? (byte)0xFF : alpha;
            }
        }

        return new CursorShapeSnapshot(IntPtr.Zero, info.Width, info.Height, info.HotspotX, info.HotspotY, shapeBytes);
    }

    private static CursorShapeSnapshot? CreateMonochromeSnapshot(PointerShapeInfo info, ReadOnlySpan<byte> buffer)
    {
        if (info.Width <= 0 || info.Height <= 0 || info.Pitch <= 0)
        {
            return null;
        }

        var planeSize = info.Pitch * info.Height;
        if (buffer.Length < planeSize * 2)
        {
            return null;
        }

        var andMask = buffer.Slice(0, planeSize);
        var xorMask = buffer.Slice(planeSize, planeSize);
        var shapeBytes = new byte[info.Width * info.Height * 4];

        for (var y = 0; y < info.Height; y++)
        {
            for (var x = 0; x < info.Width; x++)
            {
                var andBit = ReadMaskBit(andMask, info.Pitch, x, y);
                var xorBit = ReadMaskBit(xorMask, info.Pitch, x, y);
                var pixelIndex = ((y * info.Width) + x) * 4;

                if (andBit)
                {
                    if (xorBit)
                    {
                        return null;
                    }

                    shapeBytes[pixelIndex] = 0;
                    shapeBytes[pixelIndex + 1] = 0;
                    shapeBytes[pixelIndex + 2] = 0;
                    shapeBytes[pixelIndex + 3] = 0;
                    continue;
                }

                var color = xorBit ? (byte)0xFF : (byte)0x00;
                shapeBytes[pixelIndex] = color;
                shapeBytes[pixelIndex + 1] = color;
                shapeBytes[pixelIndex + 2] = color;
                shapeBytes[pixelIndex + 3] = 0xFF;
            }
        }

        return new CursorShapeSnapshot(IntPtr.Zero, info.Width, info.Height, info.HotspotX, info.HotspotY, shapeBytes);
    }

    private static bool ValidateColorShape(PointerShapeInfo info, int bufferLength) =>
        info.Width > 0 &&
        info.Height > 0 &&
        info.Pitch >= info.Width * 4 &&
        bufferLength >= info.Pitch * info.Height;

    private static byte[] NormalizeColorBuffer(PointerShapeInfo info, ReadOnlySpan<byte> buffer)
    {
        var rowBytes = info.Width * 4;
        var shapeBytes = new byte[rowBytes * info.Height];
        for (var y = 0; y < info.Height; y++)
        {
            buffer.Slice(y * info.Pitch, rowBytes).CopyTo(shapeBytes.AsSpan(y * rowBytes, rowBytes));
        }

        return shapeBytes;
    }

    private static bool ReadMaskBit(ReadOnlySpan<byte> buffer, int pitch, int x, int y)
    {
        var rowOffset = y * pitch;
        var byteOffset = rowOffset + (x / 8);
        var bitMask = (byte)(0x80 >> (x % 8));
        return (buffer[byteOffset] & bitMask) != 0;
    }
}
