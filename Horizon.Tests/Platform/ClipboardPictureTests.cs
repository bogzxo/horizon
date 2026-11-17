using Horizon.Graphics;
using Horizon.Platform.Desktop;

namespace Horizon.Tests.Platform;

/// <summary>
/// A picture on Windows' clipboard as a bitmap with its header in front, which is how a screenshot comes, turned into
/// a PNG the right way up and in the right colours.
/// </summary>
public sealed class ClipboardPictureTests
{
    // A bitmap of two by two, its header and then its rows bottom first, each pixel blue, green, red and alpha
    private static byte[] Bitmap(int bits, bool alpha)
    {
        int stride = (2 * bits / 8 + 3) & ~3;
        byte[] dib = new byte[40 + stride * 2];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(2).CopyTo(dib, 4);
        BitConverter.GetBytes(2).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)bits).CopyTo(dib, 14);

        // The bottom row red then green, the top row blue then white
        byte[][] rows = [[0, 0, 255, 0, 255, 0], [255, 0, 0, 255, 255, 255]];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int at = 40 + y * stride + x * bits / 8;
                dib[at] = rows[y][x * 3];
                dib[at + 1] = rows[y][x * 3 + 1];
                dib[at + 2] = rows[y][x * 3 + 2];
                if (bits == 32)
                    dib[at + 3] = alpha ? (byte)128 : (byte)0;
            }
        }

        return dib;
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void ABitmapBecomesAPngTheRightWayUp(int bits, bool alpha)
    {
        byte[]? png = WindowsClipboard.PngOfBitmap(Bitmap(bits, alpha));
        Assert.NotNull(png);
        ImagePixels read = ImageFile.Load(new MemoryStream(png!));
        Assert.Equal(2, read.Width);
        Assert.Equal(2, read.Height);

        // The top row first now, blue then white, and an alpha of nothing everywhere read as opaque
        byte expectedAlpha = alpha ? (byte)128 : (byte)255;
        Assert.Equal(new byte[] { 0, 0, 255, expectedAlpha, 255, 255, 255, expectedAlpha, 255, 0, 0, expectedAlpha, 0, 255, 0, expectedAlpha }, read.Pixels);
    }

    [Fact]
    public void WhatIsntABitmapItCanReadIsNothing()
    {
        Assert.Null(WindowsClipboard.PngOfBitmap([1, 2, 3]));
        byte[] eight = Bitmap(24, false);
        BitConverter.GetBytes((ushort)8).CopyTo(eight, 14);
        Assert.Null(WindowsClipboard.PngOfBitmap(eight));
    }
}
