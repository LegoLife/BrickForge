namespace BrickForge.Core.Tests;

public class ComponentLibraryTests
{
    private static readonly PartType Brick2x2 = PartCatalog.Get("brick-2x2");
    private static readonly PartType Plate1x1 = PartCatalog.Get("plate-1x1");

    private static PartGroup Tower() => PartGroup.From([
        new PlacedPart(1, Brick2x2, new GridPos(4, 0, 4), Rotation.R0, 5),
        new PlacedPart(2, Plate1x1, new GridPos(4, 3, 4), Rotation.R90, 3),
    ]);

    [Fact]
    public void Added_components_get_unique_ids_and_trimmed_names()
    {
        var library = new ComponentLibrary();

        var a = library.Add("  Tree  ", Tower());
        var b = library.Add("Tree", Tower());

        Assert.Equal("Tree", a.Name);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal([a, b], library.Components);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_names_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new ComponentLibrary().Add(name, Tower()));
    }

    [Fact]
    public void Long_names_are_cut_to_the_limit()
    {
        var component = new ComponentLibrary().Add(new string('x', 100), Tower());

        Assert.Equal(ComponentLibrary.MaxNameLength, component.Name.Length);
    }

    [Fact]
    public void Rename_and_delete_by_id()
    {
        var library = new ComponentLibrary();
        var tree = library.Add("Tree", Tower());
        var wall = library.Add("Wall", Tower());

        Assert.True(library.Rename(tree.Id, "Oak"));
        Assert.True(library.Delete(wall.Id));

        Assert.Equal(["Oak"], library.Components.Select(c => c.Name));
        Assert.False(library.Delete("missing"));
        Assert.False(library.Rename("missing", "x"));
    }

    [Fact]
    public void Every_change_raises_changed()
    {
        var library = new ComponentLibrary();
        var raised = 0;
        library.Changed += () => raised++;

        var tree = library.Add("Tree", Tower());
        library.Rename(tree.Id, "Oak");
        library.Delete(tree.Id);

        Assert.Equal(3, raised);
    }

    [Fact]
    public void Import_replaces_components_with_the_same_id_and_appends_the_rest()
    {
        var library = new ComponentLibrary();
        var tree = library.Add("Tree", Tower());
        var incoming = new[]
        {
            tree with { Name = "Tree v2" },
            new Component("fresh", "Wall", Tower()),
        };

        var count = library.Import(incoming);

        Assert.Equal(2, count);
        Assert.Equal(["Tree v2", "Wall"], library.Components.Select(c => c.Name));
    }

    // ---- file format --------------------------------------------------------------------------

    [Fact]
    public void Components_round_trip_through_a_file()
    {
        var library = new ComponentLibrary();
        var tree = library.Add("Tree", Tower());

        var read = ComponentFile.Read(ComponentFile.Write(library.Components)).Single();

        Assert.Equal((tree.Id, tree.Name), (read.Id, read.Name));
        Assert.Equal(tree.Group.Parts, read.Group.Parts);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format":"brickforge","version":1,"components":[]}""")]
    [InlineData("""{"format":"brickforge-components","version":9,"components":[]}""")]
    [InlineData("""{"format":"brickforge-components","version":1,"components":[{"id":"a","name":"","parts":[["brick-1x1",0,0,0,0,0]]}]}""")]
    [InlineData("""{"format":"brickforge-components","version":1,"components":[{"id":"a","name":"X","parts":[]}]}""")]
    [InlineData("""{"format":"brickforge-components","version":1,"components":[{"id":"a","name":"X","parts":[["nope",0,0,0,0,0]]}]}""")]
    [InlineData("""{"format":"brickforge-components","version":1,"components":[{"id":"a","name":"X","parts":[["brick-1x1",0,0,0,0,0],["brick-1x1",0,1,0,0,0]]}]}""")]
    [InlineData("""{"format":"brickforge-components","version":1,"components":[{"id":"a","name":"X","parts":[["brick-1x1",-1,0,0,0,0]]}]}""")]
    public void Invalid_component_files_are_rejected(string json)
    {
        Assert.Throws<BuildFileException>(() => ComponentFile.Read(json));
    }

    [Fact]
    public void Offsets_are_normalised_on_read()
    {
        const string json = """{"format":"brickforge-components","version":1,"components":[{"id":"a","name":"X","parts":[["brick-1x1",3,2,5,0,0]]}]}""";

        Assert.Equal(new GridPos(0, 0, 0), ComponentFile.Read(json).Single().Group.Parts.Single().Offset);
    }
}
