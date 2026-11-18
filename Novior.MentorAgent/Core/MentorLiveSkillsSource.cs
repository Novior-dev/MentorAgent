using Microsoft.Agents.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// An <see cref="AgentSkillsSource"/> that <see cref="Options.MentorOptions.SkillsRefreshInterval"/> never caches: wrap a
/// source in it, in <see cref="Options.MentorOptions.SkillSources"/>, when what it gives must reach every session built
/// after a change, not one built after the interval (rule <c>skill.live-source</c>).
///
/// <para>
/// With a refresh interval each source is cached on its own, so the others keep their cache while this one is asked
/// every time a session's coordinator is built: a skill it withdraws is gone for the next session, and one it adds is
/// there. A session keeps the list it started with, as for every source. Without an interval nothing changes, because
/// every source is then read at every session anyway.
/// </para>
///
/// <para>
/// It is for a source that is cheap to ask and whose changes must count at once, such as the console of an integration
/// that withdraws a skill. It composes with <see cref="MentorPreferredSkillsSource"/>, in either order, and
/// <see cref="Options.MentorOptions.SkillFilter"/> and the de-duplication apply to its skills as to the others'.
/// </para>
///
/// <code>
/// options.SkillsRefreshInterval = TimeSpan.FromMinutes(15);        // the remote catalogue is read every 15 minutes
/// options.SkillSources =
/// [
///     new MentorLiveSkillsSource(new MentorPreferredSkillsSource(consoleSkills)),   // read at every session
///     new AgentFileSkillsSource("/srv/shared-skills"),                              // cached
/// ];
/// </code>
/// </summary>
public sealed class MentorLiveSkillsSource : AgentSkillsSource
{
    private readonly AgentSkillsSource _inner;

    /// <summary>Wraps <paramref name="inner"/>, which is then asked at every session, never cached.</summary>
    public MentorLiveSkillsSource(AgentSkillsSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The source asked at every session.</summary>
    public AgentSkillsSource Inner => _inner;

    /// <inheritdoc />
    public override Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
        => _inner.GetSkillsAsync(context, cancellationToken);
}
