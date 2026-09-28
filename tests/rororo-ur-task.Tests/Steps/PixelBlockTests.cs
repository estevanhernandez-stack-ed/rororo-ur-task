using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class PixelBlockTests
{
    private static uint Argb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;

    [Fact]
    public void Averages_a_box_in_client_coordinates()
    {
        // 4x2 block at client (10,20): left half red, right half blue.
        var px = new[] { Argb(200, 0, 0), Argb(200, 0, 0), Argb(0, 0, 100), Argb(0, 0, 100),
                         Argb(200, 0, 0), Argb(200, 0, 0), Argb(0, 0, 100), Argb(0, 0, 100) };
        var block = new PixelBlock(10, 20, 4, 2, px);
        Assert.Equal(new Rgb(200, 0, 0), block.AverageBox(10, 20, 2, 2));
        Assert.Equal(new Rgb(100, 0, 50), block.AverageBox(10, 20, 4, 2));
    }

    [Fact]
    public void A_box_outside_the_block_is_null()
    {
        var block = new PixelBlock(10, 20, 4, 2, new uint[8]);
        Assert.Null(block.AverageBox(9, 20, 2, 2));
        Assert.Null(block.AverageBox(13, 20, 2, 2));
    }

    [Fact]
    public void Rejects_a_pixel_array_of_the_wrong_length()
        => Assert.Throws<ArgumentException>(() => new PixelBlock(0, 0, 3, 3, new uint[8]));

    [Fact]
    public void Counts_pixels_whose_every_channel_reaches_the_threshold()
    {
        // 4x1 at client (10,20): exactly at the threshold, one channel short, pure white, lava pink.
        var px = new[] { Argb(225, 225, 225), Argb(224, 255, 255), Argb(255, 255, 255), Argb(255, 120, 200) };
        var block = new PixelBlock(10, 20, 4, 1, px);
        Assert.Equal(2, block.CountNearWhite(10, 20, 4, 1, 225));
        Assert.Equal(1, block.CountNearWhite(10, 20, 4, 1, 226));
        Assert.Equal(1, block.CountNearWhite(12, 20, 2, 1, 225));
    }

    [Fact]
    public void A_count_box_outside_the_block_is_null()
    {
        var block = new PixelBlock(10, 20, 4, 2, new uint[8]);
        Assert.Null(block.CountNearWhite(9, 20, 2, 2, 225));
        Assert.Null(block.CountNearWhite(13, 20, 2, 2, 225));
        Assert.Null(block.CountNearWhite(10, 20, 0, 2, 225));
    }
}
