namespace Microsoft.AspNetCore.Components.WebView.Fake
{
    /// <summary>Stands for the JavaScript runtime of a MAUI Hybrid app's web view, which lives in this namespace.</summary>
    internal sealed class WebViewRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
