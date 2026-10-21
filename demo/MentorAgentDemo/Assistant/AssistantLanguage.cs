using System.Globalization;
using Novior.MentorAgent.Abstractions.Models;

namespace MentorAgentDemo.Assistant;

/// <summary>
/// The assistant's language, <c>MentorAgent:Language</c>, read once at startup. The texts the assistant shows —
/// cards, the confirmation banner, the policy it cites — follow it, not the EN/IT button of the pages.
/// </summary>
public sealed class AssistantLanguage(MentorLanguage language)
{
    public MentorLanguage Language { get; } = language;

    public bool Italian => Language == MentorLanguage.Italian;

    public CultureInfo Culture => CultureInfo.GetCultureInfo(Italian ? "it-IT" : "en-IE");

    public string Pick(string english, string italian) => Italian ? italian : english;

    public static AssistantLanguage From(IConfiguration configuration)
        => new(configuration.GetValue("MentorAgent:Language", MentorLanguage.English));
}
