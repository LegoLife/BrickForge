using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrickForge.Core;

public sealed class BuildFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A build read from a file: its baseplate and its parts, with fresh ids.</summary>
public sealed record LoadedBuild(Baseplate Baseplate, IReadOnlyList<PlacedPart> Parts);

/// <summary>
/// The saved-build format, used for both autosave and file export:
/// <c>{"format":"brickforge","version":1,"baseplate":{"width":48,"depth":48},"parts":[["brick-2x4",x,y,z,rotation,color], ...]}</c>.
/// Parts are compact arrays rather than objects: builds run to thousands of parts, and this halves
/// the size and the time to write the autosave.
/// </summary>
public static class BuildFile
{
    private const string Format = "brickforge";
    private const int Version = 1;

    public static string Write(Build build) => JsonSerializer.Serialize(
        new BuildDocument(
            Format,
            Version,
            new BaseplateEntry(build.Baseplate.WidthStuds, build.Baseplate.DepthStuds),
            build.Parts.OrderBy(p => p.Id)
                .Select(p => new PartEntry(p.Part.Id, p.Position.X, p.Position.Y, p.Position.Z, (int)p.Rotation, p.ColorId))
                .ToList()),
        BuildFileJson.Default.BuildDocument);

    /// <summary>
    /// Parses and validates a saved build. Parts must be known, on the file's baseplate and
    /// non-overlapping; they need not be connected. Parts get fresh ids.
    /// </summary>
    /// <exception cref="BuildFileException">The file is unreadable or describes an impossible build.</exception>
    public static LoadedBuild Read(string json)
    {
        BuildDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(json, BuildFileJson.Default.BuildDocument);
        }
        catch (JsonException ex)
        {
            throw new BuildFileException("This isn't a BrickForge build file.", ex);
        }

        if (doc is null || doc.Format != Format || doc.Parts is null)
            throw new BuildFileException("This isn't a BrickForge build file.");
        if (doc.Version != Version)
            throw new BuildFileException($"Unsupported build file version {doc.Version}.");

        if (doc.Baseplate is not { } size || !Baseplate.IsValidSize(size.Width) || !Baseplate.IsValidSize(size.Depth))
            throw new BuildFileException(
                $"The baseplate must be between {Baseplate.MinSize} and {Baseplate.MaxSize} studs on each side.");

        var build = new Build(new Baseplate(size.Width, size.Depth));
        var nextId = 1;
        foreach (var entry in doc.Parts)
        {
            var where = $"Part {nextId} ({entry.Part} at {entry.X},{entry.Y},{entry.Z})";
            if (!PartCatalog.TryGet(entry.Part, out var part))
                throw new BuildFileException($"{where}: unknown part '{entry.Part}'.");
            if (Palette.All.All(c => c.Id != entry.Color))
                throw new BuildFileException($"{where}: unknown colour {entry.Color}.");
            if (entry.Rotation is < 0 or > 3)
                throw new BuildFileException($"{where}: invalid rotation {entry.Rotation}.");

            var placed = new PlacedPart(nextId++, part, new GridPos(entry.X, entry.Y, entry.Z), (Rotation)entry.Rotation, entry.Color);
            try
            {
                build.Restore(placed);
            }
            catch (InvalidOperationException ex)
            {
                throw new BuildFileException($"{where}: {ex.Message}", ex);
            }
        }
        return new LoadedBuild(build.Baseplate, build.Parts.OrderBy(p => p.Id).ToList());
    }
}

internal sealed record BuildDocument(string Format, int Version, BaseplateEntry? Baseplate, IReadOnlyList<PartEntry>? Parts);

internal sealed record BaseplateEntry(int Width, int Depth);

[JsonConverter(typeof(PartEntryConverter))]
internal sealed record PartEntry(string Part, int X, int Y, int Z, int Rotation, int Color);

/// <summary>Reads and writes a <see cref="PartEntry"/> as <c>["part-id", x, y, z, rotation, color]</c>.</summary>
internal sealed class PartEntryConverter : JsonConverter<PartEntry>
{
    public override PartEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Expect(ref reader, JsonTokenType.StartArray);
        reader.Read();
        Expect(ref reader, JsonTokenType.String);
        var part = reader.GetString()!;
        var x = NextInt(ref reader);
        var y = NextInt(ref reader);
        var z = NextInt(ref reader);
        var rotation = NextInt(ref reader);
        var color = NextInt(ref reader);
        reader.Read();
        Expect(ref reader, JsonTokenType.EndArray);
        return new PartEntry(part, x, y, z, rotation, color);
    }

    public override void Write(Utf8JsonWriter writer, PartEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.Part);
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.Rotation);
        writer.WriteNumberValue(value.Color);
        writer.WriteEndArray();
    }

    private static int NextInt(ref Utf8JsonReader reader)
    {
        reader.Read();
        Expect(ref reader, JsonTokenType.Number);
        return reader.TryGetInt32(out var value)
            ? value
            : throw new JsonException("Expected a whole number in a part entry.");
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type)
            throw new JsonException($"Expected {type} in a part entry but found {reader.TokenType}.");
    }
}

// Source-generated so serialization survives trimming in the published WebAssembly app.
[JsonSerializable(typeof(BuildDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BuildFileJson : JsonSerializerContext;
