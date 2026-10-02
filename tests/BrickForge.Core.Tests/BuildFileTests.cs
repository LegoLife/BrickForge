namespace BrickForge.Core.Tests;

public class BuildFileTests
{
    private static readonly Baseplate Plate = new(16, 16);

    private static Build SampleBuild()
    {
        var build = new Build(Plate);
        build.TryPlace(PartCatalog.Get("brick-2x4"), new GridPos(0, 0, 0), Rotation.R0, 3);
        build.TryPlace(PartCatalog.Get("plate-2x2"), new GridPos(1, 3, 1), Rotation.R90, 8);
        build.TryPlace(PartCatalog.Get("tile-1x4"), new GridPos(6, 0, 2), Rotation.R270, 21);
        return build;
    }

    private static string Json(string parts) =>
        $$"""{"format":"brickforge","version":1,"baseplate":{"width":16,"depth":16},"parts":[{{parts}}]}""";

    [Fact]
    public void Parts_are_written_as_compact_arrays()
    {
        var json = BuildFile.Write(SampleBuild());

        Assert.Contains("""["brick-2x4",0,0,0,0,3]""", json);
    }

    [Fact]
    public void Round_trip_preserves_every_part()
    {
        var original = SampleBuild();

        var loaded = BuildFile.Read(BuildFile.Write(original)).Parts;

        static string Describe(PlacedPart p) => $"{p.Part.Id} {p.Position} {p.Rotation} {p.ColorId}";
        Assert.Equal(original.Parts.Select(Describe).Order(), loaded.Select(Describe).Order());
    }

    [Fact]
    public void The_baseplate_size_comes_from_the_file()
    {
        var build = new Build(new Baseplate(96, 40));

        Assert.Equal(new Baseplate(96, 40), BuildFile.Read(BuildFile.Write(build)).Baseplate);
    }

    [Theory]
    [InlineData(4, 16)]
    [InlineData(16, 200)]
    public void Baseplate_sizes_out_of_range_are_rejected(int width, int depth)
    {
        var json = $$"""{"format":"brickforge","version":1,"baseplate":{"width":{{width}},"depth":{{depth}}},"parts":[]}""";

        var ex = Assert.Throws<BuildFileException>(() => BuildFile.Read(json));
        Assert.Contains("baseplate", ex.Message);
    }

    [Fact]
    public void Loaded_parts_get_fresh_sequential_ids()
    {
        var loaded = BuildFile.Read(BuildFile.Write(SampleBuild())).Parts;

        Assert.Equal([1, 2, 3], loaded.Select(p => p.Id));
    }

    [Fact]
    public void Floating_parts_are_allowed()
    {
        var loaded = BuildFile.Read(Json("""["brick-1x1",0,9,0,0,0]""")).Parts;

        Assert.Single(loaded);
    }

    [Theory]
    [InlineData("""["brick-9x9",0,0,0,0,0]""", "brick-9x9")]
    [InlineData("""["brick-1x1",0,0,0,0,999]""", "colour 999")]
    [InlineData("""["brick-1x1",0,0,0,7,0]""", "rotation 7")]
    [InlineData("""["brick-2x4",15,0,0,0,0]""", "outside the baseplate")]
    [InlineData("""["brick-1x1",0,0,0,0,0],["brick-1x1",0,2,0,0,0]""", "overlaps")]
    public void Invalid_parts_are_rejected_with_a_reason(string parts, string expectedMessage)
    {
        var ex = Assert.Throws<BuildFileException>(() => BuildFile.Read(Json(parts)));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format":"something-else","version":1,"baseplate":{"width":16,"depth":16},"parts":[]}""")]
    [InlineData("""{"format":"brickforge","version":2,"baseplate":{"width":16,"depth":16},"parts":[]}""")]
    [InlineData("""{"format":"brickforge","version":1,"baseplate":{"width":16,"depth":16}}""")]
    [InlineData("""{"format":"brickforge","version":1,"baseplate":{"width":16,"depth":16},"parts":[["brick-1x1",0,0]]}""")]
    [InlineData("""{"format":"brickforge","version":1,"baseplate":{"width":16,"depth":16},"parts":[[1,0,0,0,0,0]]}""")]
    [InlineData("""{"format":"brickforge","version":1,"baseplate":{"width":16,"depth":16},"parts":[["brick-1x1",0.5,0,0,0,0]]}""")]
    public void Unreadable_files_are_rejected(string json)
    {
        Assert.Throws<BuildFileException>(() => BuildFile.Read(json));
    }
}
