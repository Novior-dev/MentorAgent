namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Language for AI responses and the widget interface.</summary>
public enum MentorLanguage
{
    /// <summary>English (en)</summary>
    English = 0,
    /// <summary>Italian (it)</summary>
    Italian = 1,
    /// <summary>French (fr)</summary>
    French = 2,
    /// <summary>German (de)</summary>
    German = 3,
    /// <summary>Spanish (es)</summary>
    Spanish = 4,
    /// <summary>Portuguese (pt)</summary>
    Portuguese = 5,
    /// <summary>Dutch (nl)</summary>
    Dutch = 6,
    /// <summary>Polish (pl)</summary>
    Polish = 7,
    /// <summary>Japanese (ja)</summary>
    Japanese = 8,
    /// <summary>Simplified Chinese (zh)</summary>
    Chinese = 9,
}

/// <summary>Utility methods for MentorLanguage.</summary>
public static class MentorLanguageExtensions
{
    /// <summary>Converts the enum to an ISO 639-1 code (e.g. Italian → "it")</summary>
    public static string ToIsoCode(this MentorLanguage lang) => lang switch
    {
        MentorLanguage.English    => "en",
        MentorLanguage.Italian    => "it",
        MentorLanguage.French     => "fr",
        MentorLanguage.German     => "de",
        MentorLanguage.Spanish    => "es",
        MentorLanguage.Portuguese => "pt",
        MentorLanguage.Dutch      => "nl",
        MentorLanguage.Polish     => "pl",
        MentorLanguage.Japanese   => "ja",
        MentorLanguage.Chinese    => "zh",
        _                         => "en"
    };

    /// <summary>Converts the enum to the full English name (e.g. Italian → "Italian")</summary>
    public static string ToFullName(this MentorLanguage lang) => lang.ToString();
}
