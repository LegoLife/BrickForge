using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrickForge.Core;

/// <summary>
/// The component library format, used for both browser storage and file export:
/// <c>{"format":"brickforge-components","version":1,"components":[{"id","name","parts":[["brick-2x4",dx,dy,dz,rotation,color], ...]}]}</c>.
/// Part entries are the same compact arrays as in a build file, with offsets instead of positions.
/// </summary>
public static class ComponentFile
{
    private const string Format = "brickforge-components";
    private const int Version = 1;

    public static string Write(IEnumerable<Component> components) => JsonSerializer.Serialize(
        new ComponentDocument(
            Format,
            Version,
            components
                .Select(c => new ComponentEntry(
                    c.Id,
                    c.Name,
                    c.Group.Parts
                        .Select(p => new PartEntry(p.Part.Id, p.Offset.X, p.Offset.Y, p.Offset.Z, (int)p.Rotation, p.ColorId))
                        .ToList()))
                .ToList()),
        ComponentFileJson.Default.ComponentDocument);

    /// <summary>
    /// Parses and validates a component library. Each component needs a name and at least one known
    /// part; parts must not overlap. Offsets are normalised so each group's minimum corner is the origin.
    /// </summary>
    /// <exception cref="BuildFileException">The file is unreadable or describes an impossible component.</exception>
    public static IReadOnlyList<Component> Read(string json)
    {
        ComponentDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(json, ComponentFileJson.Default.ComponentDocument);
        }
        catch (JsonException ex)
        {
            throw new BuildFileException("This isn't a BrickForge component library.", ex);
        }

        if (doc is null || doc.Format != Format || doc.Components is null)
            throw new BuildFileException("This isn't a BrickForge component library.");
        if (doc.Version != Version)
            throw new BuildFileException($"Unsupported component library version {doc.Version}.");

        return doc.Components.Select(ReadComponent).ToList();
    }

    private static Component ReadComponent(ComponentEntry entry, int index)
    {
        var label = string.IsNullOrWhiteSpace(entry.Name) ? $"Component {index + 1}" : $"'{entry.Name}'";
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name))
            throw new BuildFileException($"{label} needs an id and a name.");
        if (entry.Parts is not { Count: > 0 })
            throw new BuildFileException($"{label} has no parts.");

        // A component is at most a baseplate's worth of build, so validate it on the largest one.
        var scratch = new Build(new Baseplate(Baseplate.MaxSize, Baseplate.MaxSize));
        var parts = entry.Parts
            .Select((p, i) => BuildFile.RestoreEntry(scratch, i + 1, p, $"{label}, part {i + 1} ({p.Part})"))
            .ToList();

        return new Component(
            entry.Id,
            ComponentLibrary.NormalizeName(entry.Name),
            PartGroup.Create(parts.Select(p => new GroupPart(p.Part, p.Position, p.Rotation, p.ColorId))));
    }
}

internal sealed record ComponentDocument(string Format, int Version, IReadOnlyList<ComponentEntry>? Components);

internal sealed record ComponentEntry(string Id, string Name, IReadOnlyList<PartEntry>? Parts);

// Source-generated so serialization survives trimming in the published WebAssembly app.
[JsonSerializable(typeof(ComponentDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ComponentFileJson : JsonSerializerContext;
