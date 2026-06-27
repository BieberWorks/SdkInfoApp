using Microsoft.JSInterop;

namespace SdkInfoApp.Web.Services;

public sealed class ThemeService(IJSRuntime js)
{
    private const string StorageKey = "sdkinfo.darkmode";

    public bool IsDarkMode { get; private set; } = true;
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        IsDarkMode = stored is null ? true : stored == "true";
        Changed?.Invoke();
    }

    public async Task ToggleAsync()
    {
        IsDarkMode = !IsDarkMode;
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, IsDarkMode ? "true" : "false");
        Changed?.Invoke();
    }
}
