namespace BrickForge.Core;

/// <summary>A named, reusable group of parts the user saved from a selection.</summary>
public sealed record Component(string Id, string Name, PartGroup Group);

/// <summary>
/// The user's components. Kept apart from any one build, so they survive New and Import;
/// it has no undo — deleting asks for confirmation in the UI instead.
/// </summary>
public sealed class ComponentLibrary
{
    public const int MaxNameLength = 40;

    private readonly List<Component> _components = [];

    public event Action? Changed;

    public IReadOnlyList<Component> Components => _components;

    public Component? Find(string id) => _components.Find(c => c.Id == id);

    /// <exception cref="ArgumentException">The name is blank.</exception>
    public Component Add(string name, PartGroup group)
    {
        var component = new Component(Guid.NewGuid().ToString("N"), NormalizeName(name), group);
        _components.Add(component);
        Changed?.Invoke();
        return component;
    }

    /// <exception cref="ArgumentException">The name is blank.</exception>
    public bool Rename(string id, string name)
    {
        var index = _components.FindIndex(c => c.Id == id);
        if (index < 0) return false;
        _components[index] = _components[index] with { Name = NormalizeName(name) };
        Changed?.Invoke();
        return true;
    }

    public bool Delete(string id)
    {
        if (_components.RemoveAll(c => c.Id == id) == 0) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Merges components in: one with an id already in the library replaces it (re-importing an
    /// updated library doesn't duplicate), the rest are appended.
    /// </summary>
    /// <returns>How many were imported.</returns>
    public int Import(IEnumerable<Component> components)
    {
        var count = 0;
        foreach (var component in components)
        {
            var index = _components.FindIndex(c => c.Id == component.Id);
            if (index >= 0) _components[index] = component;
            else _components.Add(component);
            count++;
        }
        if (count > 0) Changed?.Invoke();
        return count;
    }

    /// <exception cref="ArgumentException">The name is blank.</exception>
    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A component needs a name.", nameof(name));
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].TrimEnd() : trimmed;
    }
}
