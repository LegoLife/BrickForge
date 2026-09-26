namespace BrickForge.Core.Tests;

/// <summary>The files in /samples are linked from the README; keep them loadable as the formats evolve.</summary>
public class SampleFileTests
{
    private static string Sample(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "samples", name);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"samples/{name} not found above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void Demo_village_loads()
    {
        var loaded = BuildFile.Read(Sample("demo-village.json"));

        Assert.Equal(new Baseplate(48, 48), loaded.Baseplate);
        Assert.Equal(94, loaded.Parts.Count);
    }

    [Fact]
    public void Demo_components_load()
    {
        var components = ComponentFile.Read(Sample("demo-components.json"));

        Assert.Equal(["Tree", "Cottage", "Car"], components.Select(c => c.Name));
    }
}
