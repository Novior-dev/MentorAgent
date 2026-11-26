using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Server.Endpoints;

/// <summary>
/// BUG-250 — the A2A agent card, built for the requests that read it. The skills are the application's actions the code
/// lets the card list (<c>BuildAgentSkills</c>), less those an <see cref="Abstractions.Interfaces.IMentorActionPolicy"/>
/// does not allow at <see cref="MentorActionStage.Card"/>: off for every caller. The card has no caller, so a policy
/// that decides per caller is not the card's to follow; the task decides each call again, with its caller.
///
/// <para>
/// Built at the first request and again when it is older than <see cref="FreshFor"/>: a rule the application changes
/// reaches the card within that while, and a slow policy delays one request in that while, never every one. A request
/// that comes while the card is being built gets the card as it was. The card a policy could not be asked about leaves
/// that action out, as a turn does: the card promises less, never more.
/// </para>
/// </summary>
internal sealed class MentorAgentCard
{
    /// <summary>Where the SDK serves the card, and so where this one is served.</summary>
    internal const string Path = "/.well-known/agent-card.json";

    /// <summary>How long a card is served before it is built again with the policies.</summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<A2A.AgentSkill> _code;
    private readonly Func<List<A2A.AgentSkill>, A2A.AgentCard> _card;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _building = new(1, 1);
    private volatile Built? _last;

    private sealed record Built(A2A.AgentCard Card, DateTimeOffset At);

    internal MentorAgentCard(IServiceProvider services, IReadOnlyList<A2A.AgentSkill> code,
        Func<List<A2A.AgentSkill>, A2A.AgentCard> card)
    {
        _services = services;
        _code = code;
        _card = card;
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("Novior.MentorAgent.Server");
    }

    /// <summary>Maps the card's endpoint, with the HTTP cache of the same while.</summary>
    internal RouteHandlerBuilder Map(IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(Path, async (HttpContext http) =>
        {
            var card = await CurrentAsync(http.RequestAborted).ConfigureAwait(false);
            http.Response.Headers.CacheControl = $"public, max-age={(int)FreshFor.TotalSeconds}";
            return Results.Json(card, A2A.A2AJsonUtilities.DefaultOptions);
        });

    /// <summary>The card to serve: the last one while it is fresh, or while another request builds the next.</summary>
    internal async Task<A2A.AgentCard> CurrentAsync(CancellationToken ct)
    {
        var last = _last;
        if (last is not null && _time.GetUtcNow() - last.At < FreshFor) return last.Card;

        // One request builds; one that comes meanwhile gets the card as it was, unless there is none yet.
        if (last is not null && !await _building.WaitAsync(0, ct).ConfigureAwait(false)) return last.Card;
        if (last is null) await _building.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            last = _last;
            if (last is not null && _time.GetUtcNow() - last.At < FreshFor) return last.Card;
            var skills = await SkillsAsync(ct).ConfigureAwait(false);
            var built = new Built(_card(skills), _time.GetUtcNow());
            _last = built;
            return built.Card;
        }
        finally
        {
            _building.Release();
        }
    }

    /// <summary>The code's skills that every policy allows at <see cref="MentorActionStage.Card"/>.</summary>
    private async Task<List<A2A.AgentSkill>> SkillsAsync(CancellationToken ct)
    {
        var policies = _services.GetService<MentorActionPolicies>();
        if (policies is not { Any: true }) return [.. _code];

        await using var scope = _services.CreateAsyncScope();
        var kept = new List<A2A.AgentSkill>(_code.Count);
        foreach (var skill in _code)
        {
            var request = new MentorActionRequest
            {
                Stage       = MentorActionStage.Card,
                ToolName    = skill.Id,
                Description = string.IsNullOrWhiteSpace(skill.Description) ? null : skill.Description,
                User        = null,   // nobody: the card is the same for every caller
                Surface     = MentorActionSurface.A2A,
                Provenance  = MentorActionProvenance.Application,
                Services    = scope.ServiceProvider,
            };
            if (await policies.IsOfferedAsync(request, ct).ConfigureAwait(false)) kept.Add(skill);
        }
        if (kept.Count < _code.Count)
            _logger?.LogDebug("[MentorAgent:A2A] The agent card leaves out {Count} action(s) a policy turns off for every caller: {Names}.",
                _code.Count - kept.Count, string.Join(", ", _code.Select(s => s.Id).Except(kept.Select(s => s.Id))));
        return kept;
    }
}
