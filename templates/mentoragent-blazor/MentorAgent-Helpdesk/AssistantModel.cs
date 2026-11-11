using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MentorAgent_Helpdesk;

/// <summary>
/// The model the assistant talks to, from the <c>Assistant</c> section of the configuration: <c>Model</c>, <c>ApiKey</c>
/// and, for a service other than OpenAI that speaks its API, <c>Endpoint</c> (for Azure OpenAI,
/// <c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c> with the deployment name as <c>Model</c>). The key belongs
/// in the user secrets of the project, never in appsettings.json. Without it the app still starts, without the
/// assistant, and the page says what is missing.
/// </summary>
public sealed class AssistantModel
{
    public const string DefaultModel = "gpt-4.1-mini";

    private readonly string? _apiKey;

    private AssistantModel(string model, Uri? endpoint, string? apiKey, IReadOnlyList<string> missing)
    {
        Model = model;
        Endpoint = endpoint;
        _apiKey = apiKey;
        Missing = missing;
    }

    public string Model { get; }

    /// <summary>The service's address; <see langword="null"/> for OpenAI.</summary>
    public Uri? Endpoint { get; }

    /// <summary>The settings still to set, by their configuration name; empty when the assistant can run.</summary>
    public IReadOnlyList<string> Missing { get; }

    public bool IsConfigured => Missing.Count == 0;

    public static AssistantModel From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Assistant");
        var model = section["Model"] is { Length: > 0 } m ? m : DefaultModel;
        var apiKey = section["ApiKey"] is { Length: > 0 } k ? k : null;

        var missing = new List<string>();
        if (apiKey is null) missing.Add("Assistant:ApiKey");

        Uri? endpoint = null;
        if (section["Endpoint"] is { Length: > 0 } e && !Uri.TryCreate(e, UriKind.Absolute, out endpoint))
            missing.Add("Assistant:Endpoint (an absolute address, or empty for OpenAI)");

        return new AssistantModel(model, endpoint, apiKey, missing);
    }

    /// <summary>The chat client of the configured model. Call it only when <see cref="IsConfigured"/>.</summary>
    public IChatClient CreateChatClient()
    {
        if (!IsConfigured)
            throw new InvalidOperationException($"The model is not configured: {string.Join(", ", Missing)}.");

        var options = new OpenAIClientOptions();
        if (Endpoint is not null) options.Endpoint = Endpoint;

        return new OpenAIClient(new ApiKeyCredential(_apiKey!), options).GetChatClient(Model).AsIChatClient();
    }
}
