using System.Text;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Default <see cref="IMentorTour"/> (roadmap #18). Builds the onboarding tour from the
/// application's own surface — the pages registered with <c>[MentorPage]</c> and the L1 tools the
/// assistant actually has, those that need no role — so it cannot describe a screen that no longer
/// exists or promise a capability that was removed, or one its reader may not use.
/// <para>
/// The result is cached for the lifetime of the process, one tour per language. A tour is identical
/// for every visitor in a language, so generating it per user would pay for the same answer over and
/// over; the first request in a language warms it and everyone else is served from memory.
/// </para>
/// <para>
/// Without a <c>ChatClient</c> — or if generation fails, or returns nothing usable — it falls back
/// to a deterministic tour assembled from the same discovery data, costing no tokens. First-run
/// onboarding that silently shows nothing is worse than a plain one.
/// </para>
/// </summary>
/// <remarks>
/// Internal: it depends on <c>MentorDiscoveryService</c>, which is internal, and callers only ever
/// need <see cref="IMentorTour"/>. Replace it by registering that interface, not this type.
/// </remarks>
internal sealed class MentorTourService : IMentorTour
{
    private readonly MentorOptions _options;
    private readonly MentorDiscoveryService _discovery;
    private readonly IMentorStructured _structured;
    private readonly ILogger<MentorTourService> _logger;

    private readonly Lock _gate = new();
    // One per language (story 536): a page can ask for the tour in its own language.
    private readonly Dictionary<MentorLanguage, Task<IReadOnlyList<MentorTourStep>>> _building = [];
    private readonly IReadOnlyList<MentorTourStep>?[] _cached = new IReadOnlyList<MentorTourStep>?[Enum.GetValues<MentorLanguage>().Length];

    public MentorTourService(
        IOptions<MentorOptions> options,
        MentorDiscoveryService discovery,
        IMentorStructured structured,
        ILogger<MentorTourService> logger)
    {
        _options = options.Value;
        _discovery = discovery;
        _structured = structured;
        _logger = logger;
    }

    public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
        => GetStepsAsync(null, ct);

    /// <summary>The tour in <paramref name="language"/>; <see langword="null"/> is MentorOptions.Language, read at the call.</summary>
    public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(MentorLanguage? language, CancellationToken ct = default)
    {
        var lang = language ?? _options.Language;
        if (Volatile.Read(ref _cached[(int)lang]) is { } ready) return Task.FromResult(ready);

        // One generation for every visitor: several first-time visitors can arrive together, and
        // generation is a model call — one of them should pay for it, not all of them. It does not
        // run on any visitor's token, which only stops that visitor waiting: run on the first one's,
        // a visitor who closed the page while the model wrote turned the generation into the
        // built-in tour, kept for the life of the process (BUG-207). A build that threw is tried
        // again by the next visitor, as before. One generation per language.
        Task<IReadOnlyList<MentorTourStep>> building;
        lock (_gate)
        {
            if (!_building.TryGetValue(lang, out building!) || building.IsFaulted)
                _building[lang] = building = BuildAsync(lang);
        }
        return building.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<MentorTourStep>> BuildAsync(MentorLanguage language)
    {
        // Off the caller's thread, so the lock above is not held while discovery runs.
        await Task.Yield();

        var discovered = _discovery.Discover();
        var pages = discovered.Pages;
        if (pages.Count == 0)
        {
            _logger.LogInformation(
                "[MentorAgent:Tour] No [MentorPage] found — the onboarding tour has nothing to show.");
            return _cached[(int)language] = [];
        }

        var steps = await GenerateAsync(discovered, language) ?? BuildFallback(discovered);
        Volatile.Write(ref _cached[(int)language], steps);

        _logger.LogInformation("[MentorAgent:Tour] {N} step(s) ready.", steps.Count);
        return steps;
    }

    private async Task<IReadOnlyList<MentorTourStep>?> GenerateAsync(DiscoveryResult discovered, MentorLanguage language)
    {
        if (_options.ChatClient is null) return null;   // Path B — fall back, do not warn twice

        var prompt = BuildPrompt(discovered);
        var instructions =
            $"You write short in-app onboarding tours. Answer in {language}. " +
            "One step per screen that is worth showing, at most 6 steps, plus a one-line welcome " +
            "step first. Each step: a Title of a few words, a Body of one or two plain sentences, " +
            "the Url of the screen it describes (empty for the welcome step), and TryAsking — a " +
            "single question the user could send to the assistant, phrased exactly as they would " +
            "type it, and answerable with the listed capabilities. Never invent a screen, a URL " +
            "or a capability that is not in the list.";

        try
        {
            var plan = await _structured.GenerateAsync<TourPlan>(prompt, instructions, CancellationToken.None);
            var steps = Sanitize(plan, discovered);
            if (steps.Count > 0) return steps;

            _logger.LogWarning("[MentorAgent:Tour] The model returned no usable step — using the built-in tour.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MentorAgent:Tour] Generation failed — using the built-in tour.");
            return null;
        }
    }

    private string BuildPrompt(DiscoveryResult discovered)
    {
        var sb = new StringBuilder();
        sb.Append("Application: ").AppendLine(_options.AppName);
        if (!string.IsNullOrWhiteSpace(_options.AppDescription))
            sb.Append("Purpose: ").AppendLine(_options.AppDescription);

        sb.AppendLine().AppendLine("Screens:");
        foreach (var p in discovered.Pages)
            sb.Append("- ").Append(p.Name).Append(" (").Append(p.Url).Append(')')
              .AppendLine(string.IsNullOrWhiteSpace(p.Description) ? "" : " — " + p.Description);

        // The tool list is what makes TryAsking concrete. Descriptions only: names are internal
        // and a user would never type "get_all_orders". Only the actions that need no role (BUG-206):
        // the tour is one for every visitor, and /mentor/tour answers anyone — an action some of them
        // may not run is not one to suggest, and the MCP server describes it to nobody without the role. Nor one the
        // code ships off (story 1549): the tour is built once, and cannot follow what a governor turns on per session.
        var tools = discovered.Level1Methods
            .Where(m => m.RequiredRoles.Length == 0 && m.EnabledByDefault)
            .Select(m => m.Description)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        if (tools.Count > 0)
        {
            sb.AppendLine().AppendLine("The assistant can:");
            foreach (var d in tools) sb.Append("- ").AppendLine(d);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Keeps only steps the application can actually honour. A generated URL that matches no
    /// registered page is dropped rather than shown: a tour whose "next" button leads to a 404 is
    /// worse than no tour.
    /// </summary>
    private List<MentorTourStep> Sanitize(TourPlan? plan, DiscoveryResult discovered)
    {
        if (plan?.Steps is not { Count: > 0 } raw) return [];

        var known = discovered.Pages
            .Select(p => p.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var steps = new List<MentorTourStep>();
        foreach (var s in raw)
        {
            if (string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Body)) continue;

            var url = string.IsNullOrWhiteSpace(s.Url) ? null : s.Url.Trim();
            if (url is not null && !known.Contains(url))
            {
                _logger.LogDebug("[MentorAgent:Tour] Dropped the URL '{Url}' — no page is registered for it.", url);
                url = null;
            }

            steps.Add(new MentorTourStep(
                s.Title.Trim(),
                s.Body.Trim(),
                url,
                string.IsNullOrWhiteSpace(s.TryAsking) ? null : s.TryAsking.Trim()));

            if (steps.Count == 8) break;   // a tour nobody finishes is not a tour
        }
        return steps;
    }

    /// <summary>Deterministic tour from the discovery data — no model, no tokens.</summary>
    private List<MentorTourStep> BuildFallback(DiscoveryResult discovered)
    {
        var steps = new List<MentorTourStep>
        {
            new(_options.AppName,
                string.IsNullOrWhiteSpace(_options.AppDescription)
                    ? $"Welcome to {_options.AppName}."
                    : _options.AppDescription)
        };

        foreach (var p in discovered.Pages.Take(6))
            steps.Add(new MentorTourStep(
                p.Name,
                string.IsNullOrWhiteSpace(p.Description) ? p.Name : p.Description!,
                p.Url));

        return steps;
    }

    /// <summary>Response shape for <see cref="IMentorStructured"/>; the JSON schema comes from it.</summary>
    internal sealed class TourPlan
    {
        public List<TourPlanStep> Steps { get; set; } = [];
    }

    internal sealed class TourPlanStep
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? TryAsking { get; set; }
    }
}
