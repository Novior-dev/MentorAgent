namespace Novior.MentorAgent.Options;

/// <summary>
/// How MentorAgent identifies a user who is <b>not signed in</b> — which decides whose memory they
/// read and whose rate-limit allowance they spend.
/// </summary>
public enum MentorAnonymousIdentity
{
    /// <summary>
    /// Each session gets its own key: a Blazor Server circuit, a SignalR connection, an application
    /// run. Two visitors of the same site never see each other's memories, and one visitor cannot
    /// spend everybody's rate-limit allowance. <b>Default.</b>
    ///
    /// <para>
    /// The trade is that an anonymous visitor is not recognised on their next visit — which is what
    /// "anonymous" means: without a sign-in there is nothing to recognise them by.
    /// </para>
    /// </summary>
    PerSession = 0,

    /// <summary>
    /// Every unauthenticated session shares the single key <c>"anonymous"</c> — the behaviour before
    /// this option existed.
    ///
    /// <para>
    /// Right for a <b>single-user</b> application (MAUI, a desktop tool, a kiosk), where it is what
    /// makes memory survive a restart. On a website it means a fact one visitor states is recalled
    /// for the next one, and <c>RateLimitPerUser</c> becomes one allowance for all of them;
    /// MentorAgent says so in the log the first time a second session reaches the shared bucket.
    /// </para>
    /// </summary>
    Shared = 1,
}
