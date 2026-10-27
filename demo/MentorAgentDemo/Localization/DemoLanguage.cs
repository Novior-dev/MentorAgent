namespace MentorAgentDemo.Localization;

/// <summary>
/// The language of the demo's pages, one per visitor's circuit, switched with the EN/IT button. The layout gives
/// it to the widget as its <c>Language</c>, so the button changes the assistant too: its texts, and the language
/// of the turns. The policy handbook and the labels of the expense actions stay in <c>MentorAgent:Language</c>
/// of appsettings.json, read at startup: they are the application's data, not the widget's.
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
