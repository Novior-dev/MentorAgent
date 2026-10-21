using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MentorAgentDemo;

/// <summary>
/// The model the assistant talks to, from the <c>Demo</c> section of the configuration: <c>Provider</c>
/// (<c>OpenAI</c> or <c>AzureOpenAI</c>), <c>Model</c>, <c>Endpoint</c> for Azure, and <c>ApiKey</c>, which belongs
/// in the user secrets of the project, never in appsettings.json. Without what it needs the app still starts, the
/// assistant stays off, and the pages say what is missing.
/// </summary>
public sealed class DemoModel
{
    public const string DefaultModel = "gpt-4.1-mini";

    private DemoModel(string provider, string model, string? endpoint, string? apiKey, IReadOnlyList<string> missing)
    {
        Provider = provider;
        Model = model;
        Endpoint = endpoint;
        _apiKey = apiKey;
        Missing = missing;
    }

    private readonly string? _apiKey;

    public string Provider { get; }
    public string Model { get; }
    public string? Endpoint { get; }

    /// <summary>The settings still to set, by their configuration name; empty when the assistant can run.</summary>
    public IReadOnlyList<string> Missing { get; }

    public bool IsConfigured => Missing.Count == 0;

    public bool IsAzure => string.Equals(Provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase);

    public static DemoModel From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Demo");
        var provider = section["Provider"] is { Length: > 0 } p ? p : "OpenAI";
        var model = section["Model"] is { Length: > 0 } m ? m : DefaultModel;
        var endpoint = section["Endpoint"] is { Length: > 0 } e ? e : null;
        var apiKey = section["ApiKey"] is { Length: > 0 } k ? k : null;

        var missing = new List<string>();
        if (apiKey is null) missing.Add("Demo:ApiKey");

        var azure = string.Equals(provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase);
        if (azure && (endpoint is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out _))) missing.Add("Demo:Endpoint");
        if (!azure && !string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            missing.Add("Demo:Provider (OpenAI or AzureOpenAI)");

        return new DemoModel(provider, model, endpoint, apiKey, missing);
    }

    /// <summary>The chat client for the configured provider. Call it only when <see cref="IsConfigured"/>.</summary>
    public IChatClient CreateChatClient()
    {
        if (!IsConfigured)
            throw new InvalidOperationException($"The model is not configured: {string.Join(", ", Missing)}.");

        var key = new ApiKeyCredential(_apiKey!);
        return IsAzure
            ? new AzureOpenAIClient(new Uri(Endpoint!), key).GetChatClient(Model).AsIChatClient()
            : new OpenAIClient(key).GetChatClient(Model).AsIChatClient();
    }
}
