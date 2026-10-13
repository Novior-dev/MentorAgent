using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Default <see cref="IMentorStructured"/> implementation (roadmap #12). Uses the configured
/// <c>ChatClient</c>'s structured-output support (<c>GetResponseAsync&lt;T&gt;</c>) — the JSON schema
/// is derived from the requested type <c>T</c> and the response is deserialized back to it.
/// </summary>
public sealed class MentorStructuredService : IMentorStructured
{
    private readonly MentorOptions _options;
    private readonly ILogger<MentorStructuredService> _logger;

    public MentorStructuredService(IOptions<MentorOptions> options, ILogger<MentorStructuredService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<T?> GenerateAsync<T>(string prompt, string? instructions = null, CancellationToken ct = default)
    {
        if (_options.ChatClient is not { } chatClient)
        {
            _logger.LogWarning("[MentorAgent] Structured output requires a ChatClient (Path A) — returning default.");
            return default;
        }

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(instructions))
            messages.Add(new ChatMessage(ChatRole.System, instructions));
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        try
        {
            var response = await chatClient.GetResponseAsync<T>(messages, cancellationToken: ct);
            return response.TryGetResult(out var result) ? result : default;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MentorAgent] Structured output generation failed — returning default.");
            return default;
        }
    }
}
