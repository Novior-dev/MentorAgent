using Novior.MentorAgent.Options;
using Microsoft.Agents.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Composes the Agent Framework's native <see cref="AgentSkillsSource"/> family and maps what it
/// yields onto MentorAgent's own <see cref="SkillRegistration"/>, so a host can contribute skills
/// from anywhere — a database, a remote catalogue, Microsoft's own
/// <c>AgentFileSkillsSource</c> — and have them reach the assistant through the same
/// <c>load_skill</c> / <c>read_skill_resource</c> tools, the same catalogue prompt and the same
/// gate as skills MentorAgent discovered itself.
///
/// <para>
/// <b>Why MentorAgent's own discovery is not replaced by <c>AgentFileSkillsSource</c>.</b> The two
/// read a different <c>SKILL.md</c> dialect. The framework's source states that "each file is
/// validated for YAML frontmatter"; MentorAgent's documented convention is a heading followed by a
/// prose paragraph, and frontmatter appearing before the heading is deliberately ignored. Swapping
/// the implementation would therefore reject every skill file written against our documentation.
/// The abstraction is adopted; the file dialect is not, and a host who wants the specification's
/// dialect gets it by registering <c>AgentFileSkillsSource</c> in
/// <see cref="MentorOptions.SkillSources"/> — both then work side by side.
/// </para>
///
/// <para>
/// <b>Ordering is the contract.</b> <see cref="DeduplicatingAgentSkillsSource"/> keeps the *first*
/// occurrence of a name, and MentorAgent's own source is aggregated first. So a host source cannot
/// silently shadow an application skill by reusing its name — the application wins, the same way a
/// class-based skill already wins over a file-based one of the same name. The one exception is the
/// application's own decision: a source it wraps in <see cref="MentorPreferredSkillsSource"/> is
/// asked first and its skills replace the application's of the same name, with a log line
/// (<c>skill.name-precedence</c>).
/// </para>
///
/// <para>
/// <b>The cache is per source.</b> With <see cref="MentorOptions.SkillsRefreshInterval"/> each source is wrapped in its
/// own <c>CachingAgentSkillsSource</c> before the aggregation, never the composition as a whole, so a source wrapped in
/// <see cref="MentorLiveSkillsSource"/> stays outside every cache and is asked at each coordinator build while the
/// others keep theirs (<c>skill.live-source</c>). The filter and the de-duplication run over the cached and the live
/// lists at each build, and they are cheap: the cost the interval saves is the sources' own.
/// </para>
/// </summary>
internal static class MentorNativeSkillSources
{
    /// <summary>
    /// Builds the composed source, or <c>null</c> when the host registered none — in which case
    /// nothing here runs and skills behave exactly as they did before this seam existed.
    /// </summary>
    internal static AgentSkillsSource? Compose(MentorOptions options, ILoggerFactory loggerFactory)
    {
        if (options.SkillSources is not { Count: > 0 } registered) return null;

        // Story 1570 slice C (skill.name-precedence): a MentorPreferredSkillsSource is asked first, in the order it was
        // registered, so the de-duplication below keeps its skill over another source's of the same name. A stable sort:
        // the others keep their order.
        var sources = registered.OrderBy(s => PreferredOf(s) is null ? 1 : 0).ToList();

        // Story 1573 (skill.live-source): with a refresh interval each source gets its own cache, and a live one none, so
        // a skill a live source withdraws is gone for the next session while the others are still read once an interval.
        if (options.SkillsRefreshInterval is { } interval && interval > TimeSpan.Zero)
        {
            var cache = new CachingAgentSkillsSourceOptions { RefreshInterval = interval };
            sources = sources
                .Select(s => IsLive(s) ? s : new CachingAgentSkillsSource(s, cache))
                .ToList();
        }

        AgentSkillsSource composed = sources.Count == 1
            ? sources[0]
            : new AggregatingAgentSkillsSource(sources);

        // A caller-supplied predicate runs before de-duplication, so filtering a name out lets the
        // next source with that name through rather than leaving a hole.
        if (options.SkillFilter is { } predicate)
            composed = new FilteringAgentSkillsSource(
                composed, (skill, _) => predicate(skill), loggerFactory);

        composed = new DeduplicatingAgentSkillsSource(composed, loggerFactory);

        // The caches above are only worth anything if they outlive one session, which is why
        // MentorSharedSkillSource holds this composition once per process. Without an interval, every
        // new coordinator re-reads the sources.
        return composed;
    }

    /// <summary>
    /// Whether <paramref name="source"/> is never cached: a <see cref="MentorLiveSkillsSource"/>, or a
    /// <see cref="MentorPreferredSkillsSource"/> around one.
    /// </summary>
    internal static bool IsLive(AgentSkillsSource source)
        => source is MentorLiveSkillsSource or MentorPreferredSkillsSource { Inner: MentorLiveSkillsSource };

    /// <summary>
    /// The <see cref="MentorPreferredSkillsSource"/> that <paramref name="source"/> is, or that it wraps in a
    /// <see cref="MentorLiveSkillsSource"/>; <c>null</c> for any other source.
    /// </summary>
    internal static MentorPreferredSkillsSource? PreferredOf(AgentSkillsSource source)
        => source as MentorPreferredSkillsSource ?? (source as MentorLiveSkillsSource)?.Inner as MentorPreferredSkillsSource;

    /// <summary>
    /// Invokes the composed source and maps each <see cref="AgentSkill"/> onto a
    /// <see cref="SkillRegistration"/>.
    ///
    /// <para>
    /// <b>Resources are looked up, never enumerated.</b> <see cref="AgentSkill"/> exposes
    /// <c>GetResourceAsync(name)</c> and no way to list what it holds, so a native skill's
    /// registration carries the skill itself rather than a materialised dictionary and
    /// <c>read_skill_resource</c> resolves through it on demand. Eagerly probing names would mean
    /// guessing them.
    /// </para>
    /// </summary>
    internal static async Task<List<SkillRegistration>> LoadAsync(
        AgentSkillsSource source,
        AgentSkillsSourceContext context,
        ILogger logger,
        CancellationToken ct)
    {
        var registrations = new List<SkillRegistration>();

        IList<AgentSkill> skills;
        try
        {
            skills = await source.GetSkillsAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A skill catalogue that cannot be reached must not stop the assistant starting — the
            // same stance MentorAgent already takes for an unreachable MCP server or A2A peer.
            logger.LogWarning(ex,
                "[MentorAgent:Skills] A registered AgentSkillsSource failed; continuing without its skills.");
            return registrations;
        }

        foreach (var skill in skills)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await skill.GetContentAsync(ct).ConfigureAwait(false);
                registrations.Add(new SkillRegistration(
                    Name: skill.Frontmatter.Name,
                    Description: skill.Frontmatter.Description,
                    InstructionsMarkdown: content,
                    Resources: null,
                    Native: skill));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "[MentorAgent:Skills] Skill '{Name}' from a registered source could not be read; skipped.",
                    skill?.Frontmatter is { } fm ? fm.Name : "(unnamed)");
            }
        }

        if (registrations.Count > 0)
            logger.LogInformation(
                "[MentorAgent:Skills] {N} skill(s) from registered AgentSkillsSource(s): {Names}",
                registrations.Count, string.Join(", ", registrations.Select(r => r.Name)));

        return registrations;
    }
}

/// <summary>
/// The composed, cached skill source shared by every coordinator in the process when
/// <see cref="MentorOptions.SkillsRefreshInterval"/> is set.
///
/// <para>
/// Found by the rc.11 README audit. The skills builder is scoped and composes the sources on each
/// coordinator build, so each session got its own <c>CachingAgentSkillsSource</c> and queried it
/// once: the interval never elapsed inside anyone's lifetime, every new session re-read the
/// catalogue anyway, and nothing was refreshed during a session. The option did nothing. Holding
/// one composition per process makes it do what it says: sessions share the list, and it is
/// re-fetched when the interval has passed.
/// </para>
/// </summary>
internal sealed class MentorSharedSkillSource
{
    private readonly Lock _gate = new();
    private AgentSkillsSource? _source;
    private bool _composed;

    /// <summary>The shared composition with an interval set; a fresh one per call without it.</summary>
    internal AgentSkillsSource? Get(MentorOptions options, ILoggerFactory loggerFactory)
    {
        if (options.SkillsRefreshInterval is not { } interval || interval <= TimeSpan.Zero)
            return MentorNativeSkillSources.Compose(options, loggerFactory);

        lock (_gate)
        {
            if (!_composed)
            {
                _source = MentorNativeSkillSources.Compose(options, loggerFactory);
                _composed = true;
            }
            return _source;
        }
    }
}
