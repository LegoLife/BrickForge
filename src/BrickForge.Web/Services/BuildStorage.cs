using Microsoft.JSInterop;

namespace BrickForge.Web.Services;

/// <summary>Browser-side persistence: the localStorage autosave and file downloads.</summary>
public sealed class BuildStorage(IJSRuntime js) : IAsyncDisposable
{
    private const string AutosaveKey = "brickforge.autosave";
    private const string BrokenAutosaveKey = "brickforge.autosave.unreadable";

    private IJSObjectReference? _module;

    public async Task<string?> LoadAutosaveAsync() =>
        await (await ModuleAsync()).InvokeAsync<string?>("load", AutosaveKey);

    /// <returns>False if the browser refused (storage full or disabled).</returns>
    public async Task<bool> SaveAutosaveAsync(string json) =>
        await (await ModuleAsync()).InvokeAsync<bool>("save", AutosaveKey, json);

    /// <summary>Keeps an autosave we couldn't read, so the next autosave doesn't destroy it.</summary>
    public async Task SetAsideUnreadableAutosaveAsync(string json) =>
        await (await ModuleAsync()).InvokeAsync<bool>("save", BrokenAutosaveKey, json);

    public async Task DownloadAsync(string filename, string json) =>
        await (await ModuleAsync()).InvokeVoidAsync("downloadText", filename, json, "application/json");

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/files.js");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Page is going away.
        }
    }
}
