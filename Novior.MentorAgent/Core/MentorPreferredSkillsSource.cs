using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// An <see cref="AgentSkillsSource"/> whose skills win a name: wrap a source in it, in
/// <see cref="Options.MentorOptions.SkillSources"/>, when the skills it gives must replace the application's own of the
/// same name, and those of the other sources (rule <c>skill.name-precedence</c>). Without it the application's skills win,
/// then the first source registered.
///
/// <para>
/// It is for a source the application trusts to decide, such as the console of an integration that manages the
/// assistant's skills: the decision is the application's, made where it registers the source, never the source's.
/// Every replacement is logged, naming the skill. Several preferred sources are asked first, in the order they were
/// registered; <see cref="Options.MentorOptions.SkillFilter"/> applies to their skills as to the others'.
/// </para>
///
/// <code>
/// options.SkillSources =
/// [
///     new MentorPreferredSkillsSource(consoleSkills),        // its skills replace yours of the same name
///     new AgentFileSkillsSource("/srv/shared-skills"),       // yours win over these
/// ];
/// </code>
/// </summary>
public sealed class MentorPreferredSkillsSource : AgentSkillsSource
{
    /// <summary>The skills this source gave, by identity: the framework's decorators hand the same instances on.</summary>
    private readonly ConditionalWeakTable<AgentSkill, object> _given = [];

    private readonly AgentSkillsSource _inner;

    /// <summary>Wraps <paramref name="inner"/>, whose skills then win a name.</summary>
    public MentorPreferredSkillsSource(AgentSkillsSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The source whose skills win a name.</summary>
    public AgentSkillsSource Inner => _inner;

    /// <inheritdoc />
    public override async Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
    {
        var skills = await _inner.GetSkillsAsync(context, cancellationToken).ConfigureAwait(false);
        foreach (var skill in skills)
            if (skill is not null) _given.AddOrUpdate(skill, _given);
        return skills;
    }

    /// <summary>Whether <paramref name="skill"/> came from this source.</summary>
    internal bool Gave(AgentSkill skill) => _given.TryGetValue(skill, out _);
}
