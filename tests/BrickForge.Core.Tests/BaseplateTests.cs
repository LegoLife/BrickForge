namespace BrickForge.Core.Tests;

public class BaseplateTests
{
    [Theory]
    [InlineData(8, 8)]
    [InlineData(128, 128)]
    [InlineData(16, 96)]
    public void Sizes_within_range_are_allowed(int width, int depth)
    {
        Assert.Equal(width, new Baseplate(width, depth).WidthStuds);
    }

    [Theory]
    [InlineData(7, 16)]
    [InlineData(16, 129)]
    [InlineData(0, 0)]
    public void Sizes_outside_range_are_rejected(int width, int depth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Baseplate(width, depth));
    }
}
