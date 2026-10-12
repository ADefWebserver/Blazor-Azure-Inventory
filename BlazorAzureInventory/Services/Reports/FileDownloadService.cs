using Microsoft.JSInterop;

namespace BlazorAzureInventory.Services.Reports;

/// <summary>Streams a file to the browser over the circuit, so downloads need no extra HTTP endpoint.</summary>
public sealed class FileDownloadService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task DownloadAsync(string fileName, string contentType, Stream content)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/download.js");
        content.Position = 0;
        using var reference = new DotNetStreamReference(content, leaveOpen: true);
        await _module.InvokeVoidAsync("downloadFileFromStream", fileName, contentType, reference);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
