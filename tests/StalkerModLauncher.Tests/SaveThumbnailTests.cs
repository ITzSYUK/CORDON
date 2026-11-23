using System.Windows.Media;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class SaveThumbnailTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadSaveThumbnailHandlesIxRayDds(bool truncated)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dds");
        try
        {
            // One BGRA pixel in the legacy DDS layout written by IX-Ray.
            var header = new uint[32];
            header[0] = 0x20534444;
            header[1] = 124;
            header[2] = 0x2100f;
            header[3] = header[4] = header[7] = 1;
            header[5] = 4;
            header[19] = 32;
            header[20] = 0x41;
            header[22] = 32;
            header[23] = 0x00ff0000;
            header[24] = 0x0000ff00;
            header[25] = 0x000000ff;
            header[26] = 0xff000000;
            header[27] = 0x1000;
            byte[] expectedPixels = [20, 60, 100, 255];
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                foreach (var field in header)
                    writer.Write(field);
                if (!truncated)
                    writer.Write(expectedPixels);
            }

            var thumbnail = ScreenshotImageLoader.LoadSaveThumbnail(path);
            if (truncated)
            {
                Assert.Null(thumbnail);
                return;
            }

            Assert.NotNull(thumbnail);
            Assert.True(thumbnail.IsFrozen);
            Assert.Equal(1, thumbnail.PixelWidth);
            Assert.Equal(1, thumbnail.PixelHeight);
            Assert.Equal(PixelFormats.Bgra32, thumbnail.Format);
            var pixels = new byte[4];
            thumbnail.CopyPixels(pixels, 4, 0);
            Assert.Equal(expectedPixels, pixels);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
