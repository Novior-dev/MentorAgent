namespace Novior.MentorAgent.Options;

/// <summary>
/// Where the context of each request goes — the page open, the data it shows, its address, the date and time
/// (<see cref="MentorOptions.SessionContextPlacement"/>).
/// </summary>
public enum MentorSessionContextPlacement
{
    /// <summary>
    /// At the end of the instructions, as before this option existed. The instructions change when the minute or
    /// the page does, and a model that caches the prompt by its prefix reads the whole conversation again.
    /// </summary>
    Instructions = 0,

    /// <summary>
    /// At the start of the user's last message, in a block the instructions name, and only in the request sent to
    /// the model: the conversation keeps what the user wrote. The instructions stay the same through the session.
    /// Path A with local history only.
    /// </summary>
    LastUserMessage,
}
