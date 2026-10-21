namespace MentorAgentDemo.Localization;

/// <summary>
/// The language of the demo's pages, one per visitor's circuit, switched with the EN/IT button. It does not
/// change the assistant: MentorAgent has one language for the whole application, read at startup from
/// <c>MentorAgent:Language</c> in appsettings.json.
/// </summary>
public sealed class DemoLanguage
{
    public bool Italian { get; private set; }

    /// <summary>The page language as an HTML <c>lang</c> value.</summary>
    public string Code => Italian ? "it" : "en";

    /// <summary>Raised after the language changes, so the layout and the pages render again.</summary>
    public event Action? Changed;

    public void Set(bool italian)
    {
        if (Italian == italian) return;
        Italian = italian;
        Changed?.Invoke();
    }

    /// <summary>The text for <paramref name="key"/> in the current language.</summary>
    public string this[string key] => DemoText.Get(key, Italian);
}
