namespace Novior.MentorAgent.Options;

/// <summary>
/// How a model samples its reply (<see cref="MentorOptions.CoordinatorSampling"/>). Each value left
/// <see langword="null"/> is the provider's own; each one set goes into the request's <c>ChatOptions</c> as it is.
/// </summary>
/// <remarks>
/// Stop sequences are left out on purpose: a sequence that also occurs inside a tool call's arguments or a citation
/// would end the reply there, and the call would reach the application cut in half.
/// </remarks>
public sealed class MentorSamplingOptions
{
    /// <summary>Randomness of the reply: 0 is the most predictable. Not negative.</summary>
    public float? Temperature { get; set; }

    /// <summary>Nucleus sampling: only the most likely tokens whose probabilities add up to this are drawn from. In (0, 1].</summary>
    public float? TopP { get; set; }

    /// <summary>Only this many most likely tokens are drawn from. At least 1; not every provider takes it.</summary>
    public int? TopK { get; set; }

    /// <summary>The longest reply, in tokens, of one request. At least 1: a reply cut short stops mid-sentence.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Above 0 discourages repeating the same tokens, the more they recur; below 0 encourages it. The range is the provider's.</summary>
    public float? FrequencyPenalty { get; set; }

    /// <summary>Above 0 discourages any token already used, once is enough; below 0 encourages it. The range is the provider's.</summary>
    public float? PresencePenalty { get; set; }

    /// <summary>Asks the provider for repeatable sampling, where it supports it.</summary>
    public long? Seed { get; set; }

    /// <summary>Copies every value that is set onto <paramref name="options"/>; the others are left as they are.</summary>
    internal void ApplyTo(ChatOptions options)
    {
        if (Temperature is { } temperature) options.Temperature = temperature;
        if (TopP is { } topP) options.TopP = topP;
        if (TopK is { } topK) options.TopK = topK;
        if (MaxOutputTokens is { } max) options.MaxOutputTokens = max;
        if (FrequencyPenalty is { } frequency) options.FrequencyPenalty = frequency;
        if (PresencePenalty is { } presence) options.PresencePenalty = presence;
        if (Seed is { } seed) options.Seed = seed;
    }

    /// <summary>A value no provider accepts stops the application at startup, with the option's name.</summary>
    internal void Validate(string name)
    {
        if (Temperature < 0f) throw Invalid(name, nameof(Temperature), Temperature, "cannot be negative");
        if (TopP is { } topP && (topP <= 0f || topP > 1f)) throw Invalid(name, nameof(TopP), topP, "must be above 0 and at most 1");
        if (TopK < 1) throw Invalid(name, nameof(TopK), TopK, "must be at least 1");
        if (MaxOutputTokens < 1) throw Invalid(name, nameof(MaxOutputTokens), MaxOutputTokens, "must be at least 1");
    }

    private static InvalidOperationException Invalid(string name, string value, object? given, string rule)
        => new($"[MentorAgent] {name}.{value} {rule} (it is {given}).");
}
