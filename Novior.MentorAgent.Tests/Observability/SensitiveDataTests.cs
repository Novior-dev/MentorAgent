using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Observability;

/// <summary>
/// What telemetry carries out of the process.
///
/// <para>
/// <c>EnableObservability</c> produces GenAI-convention spans, and
/// <c>ObservabilityIncludeSensitiveData</c> decides whether the user's own words travel with them.
/// Both READMEs mark that flag <b>Development only</b>, and the ROADMAP's own verification step for
/// #8 says to "verify prompt/response text is absent unless it is on". Nothing verified it.
/// </para>
///
/// <para>
/// This is the one option in the package whose failure is invisible from inside the application and
/// permanent outside it: the widget behaves identically either way, and the difference only shows up
/// in an exporter — by which point whatever a user typed is in a third-party observability backend,
/// retained on that backend's schedule rather than the application's. A default that leaked would
/// have leaked from the day the feature shipped and nobody would have looked.
/// </para>
///
/// <para>
/// Asserted on the spans themselves rather than on the option value, because the option is only a
/// claim until something reads it — and it is read inside a lambda handed to the framework's own
/// <c>UseOpenTelemetry</c>, which is exactly the kind of wiring that survives being wrong.
/// </para>
/// </summary>
public sealed class SensitiveDataTests
{
    /// <summary>
    /// A phrase no prompt template, tool name or instruction would ever contain — and unique to the
    /// invocation that plants it.
    ///
    /// <para>
    /// The uniqueness is not decoration. <c>ActivitySource.AddActivityListener</c> is
    /// <b>process-global</b>: the listener below receives spans from every test class running in
    /// parallel, not only from the host this method built. With a shared constant, a sibling class
    /// that happened to be mid-turn would plant the same phrase in its own spans, and the
    /// flag-off test would report a leak that its own host never produced. That is exactly what
    /// happened when a slow hub suite was added alongside it — both directions of the assertion
    /// failed at once, which is impossible for a real defect and is the tell for contamination.
    /// </para>
    /// </summary>
    private static string NewSecret() => $"PINEAPPLE-{Guid.NewGuid():N}-ORDER-CANCELLED";

    /// <summary>
    /// Everything a span carries: its tags, its events, and the events' own tags, flattened. Which
    /// of those the GenAI conventions use has changed between framework versions; the question here
    /// is only whether the user's words are anywhere in the exported payload at all.
    /// </summary>
    private static string Flatten(IEnumerable<Activity> activities)
        => string.Join("\n", activities.Select(a => string.Join(" | ",
            new[] { a.DisplayName }
                .Concat(a.Tags.Select(t => $"{t.Key}={t.Value}"))
                .Concat(a.Events.SelectMany(e => new[] { e.Name }
                    .Concat(e.Tags.Select(t => $"{t.Key}={t.Value}")))))));

    private static async Task<(string Exported, string Secret)> CaptureAsync(bool includeSensitiveData)
    {
        var secret = NewSecret();

        // ConcurrentQueue, not List — and this is the whole of the flake that went unexplained for
        // four occurrences across this campaign.
        //
        // ActivitySource.AddActivityListener is process-global, so ActivityStopped fires on
        // whatever thread produced the span, from EVERY test class running in parallel. `List.Add`
        // from those threads while Flatten enumerates the same list throws
        // "Collection was modified; enumeration operation may not execute" — which surfaced as a
        // failing *privacy* test and sent three investigations looking for a leaked span that was
        // never there. The GUID secret above fixed the contamination of the content; the collection
        // itself was still unsynchronised.
        //
        // The listener is also disposed before the snapshot is taken, so nothing can arrive between
        // the last span of this turn and the read.
        var captured = new System.Collections.Concurrent.ConcurrentQueue<Activity>();

        // Listen before the host is built: the source is created when the pipeline is assembled.
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.Contains("MentorAgent", StringComparison.Ordinal)
                                    || source.Name.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal)
                                    || source.Name.Contains("Experimental", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.EnableObservability = true;
            o.ObservabilityIncludeSensitiveData = includeSensitiveData;
        });

        host.Chat!.Say($"I have cancelled it. {secret}");
        await host.RunTurnAsync($"please cancel my order, reference {secret}");

        // Stop listening, then snapshot. ToArray() on a ConcurrentQueue is atomic with respect to
        // concurrent writers, so Flatten enumerates a private copy that nothing else can touch.
        listener.Dispose();
        return (Flatten(captured.ToArray()), secret);
    }

    // ── The default ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sensitive_data_is_off_unless_someone_turns_it_on()
    {
        // The default is the whole protection. A host that enables observability — which the README
        // presents as a one-liner — must not start exporting conversations as a side effect.
        new MentorOptions { AppName = "x", ScanAssemblies = [] }
            .ObservabilityIncludeSensitiveData.Should().BeFalse();
    }

    [Fact]
    public void Observability_itself_is_off_by_default_too()
    {
        // Otherwise the flag above would be the only thing standing between a default installation
        // and an exporter nobody configured.
        new MentorOptions { AppName = "x", ScanAssemblies = [] }
            .EnableObservability.Should().BeFalse();
    }

    // ── What actually reaches a span ────────────────────────────────────────────

    [Fact]
    public async Task With_the_flag_off_the_users_own_words_never_reach_a_span()
    {
        // The claim both READMEs make, measured at the only place it can be measured. A prompt in a
        // span leaves the process: it is in the exporter, then in a backend, retained on that
        // backend's schedule and searchable by everyone with access to it.
        var (exported, secret) = await CaptureAsync(includeSensitiveData: false);

        // The failure message names the offending span rather than saying "a leak happened".
        //
        // This test flaked exactly once under full-suite load and passed alone and on the next full
        // run, and the cause was never established — the per-invocation GUID should already make it
        // impossible for a sibling class to plant *this* secret, so either that reasoning is wrong
        // or something else carries the user's words into a span. Guessing was declined; instead the
        // next occurrence has to explain itself. Do not soften this into a retry.
        var offending = string.Join("\n  ", exported.Split('\n').Where(line => line.Contains(secret)));
        exported.Should().NotContain(secret,
            "the message and the reply must stay inside the application unless the host opts in. "
            + $"Span(s) carrying it:\n  {offending}");
    }

    [Fact]
    public async Task Spans_are_still_produced_when_the_content_is_withheld()
    {
        // The control, and it is what makes the test above mean something: if no span were emitted
        // at all, "the prompt is not in the spans" would be true and worthless.
        var (exported, _) = await CaptureAsync(includeSensitiveData: false);

        exported.Should().NotBeEmpty("observability is on — the spans themselves are the point");
    }

    [Fact]
    public async Task Turning_it_on_is_what_puts_the_content_in()
    {
        // The other direction. Without this the two tests above would also pass against a build
        // where the flag did nothing at all — which is precisely the failure mode of an option read
        // inside a configuration lambda.
        var (exported, secret) = await CaptureAsync(includeSensitiveData: true);

        exported.Should().Contain(secret,
            "the flag exists so a developer can see the conversation while debugging");
    }

    // ── The documentation says Development only ─────────────────────────────────

    [Fact]
    public void Both_readmes_mark_the_flag_development_only()
    {
        // The option cannot enforce this itself — the library has no business reading the host's
        // environment — so the documentation is the control, and it has to keep saying so.
        foreach (var name in new[] { "core", "server" })
        {
            var readme = Docs.RepoFiles.Readme(name);

            readme.Should().Contain("ObservabilityIncludeSensitiveData");
            readme.ToLowerInvariant().Should().Contain("development only",
                $"the {name} README must keep the warning next to the option");
        }
    }

    [Fact]
    public void The_documented_recipe_ties_the_flag_to_the_environment()
    {
        // Both READMEs show `= builder.Environment.IsDevelopment()` rather than `= true`. That is
        // the difference between a reader copying a line that is safe to ship and one that is not.
        foreach (var name in new[] { "core", "server" })
        {
            Docs.RepoFiles.Readme(name)
                .Should().Contain("ObservabilityIncludeSensitiveData = builder.Environment.IsDevelopment()",
                    $"the {name} README's example must not be one anybody can paste into production");
        }
    }
}
