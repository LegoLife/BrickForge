namespace BrickForge.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Part_ids_are_unique()
    {
        var ids = PartCatalog.All.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("brick-2x4", 3, true)]
    [InlineData("plate-2x4", 1, true)]
    [InlineData("tile-2x4", 1, false)]
    public void Kind_determines_height_and_studs(string id, int heightPlates, bool hasStuds)
    {
        var part = PartCatalog.Get(id);

        Assert.Equal(heightPlates, part.HeightPlates);
        Assert.Equal(hasStuds, part.HasStuds);
    }

    [Theory]
    [InlineData(Rotation.R0, 2, 4)]
    [InlineData(Rotation.R90, 4, 2)]
    [InlineData(Rotation.R180, 2, 4)]
    [InlineData(Rotation.R270, 4, 2)]
    public void Rotation_turns_the_footprint(Rotation rotation, int sizeX, int sizeZ)
    {
        Assert.Equal((sizeX, sizeZ), PartCatalog.Get("brick-2x4").Footprint(rotation));
    }

    [Fact]
    public void Palette_ids_are_unique()
    {
        var ids = Palette.All.Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
