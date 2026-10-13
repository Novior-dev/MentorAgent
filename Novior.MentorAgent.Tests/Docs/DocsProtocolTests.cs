using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The SignalR protocol is a contract with clients MentorAgent does not compile — React, Vue,
/// Angular, MAUI. Those clients are written from the Server README, so for them the README
/// <em>is</em> the API. A wrong event name there is not a typo: it is a broken integration that
/// fails silently, because subscribing to an event nobody raises produces no error at all.
///
/// <para>
/// These tests read the hub's real <c>SendAsync("…")</c> call sites and hold the documentation to
/// them in both directions — every documented event must exist, and every existing event must be
/// documented.
/// </para>
/// </summary>
public sealed class DocsProtocolTests
{
    /// <summary>Event names the hub actually pushes to clients, read from the source.</summary>
    private static readonly HashSet<string> SentEvents = ReadSentEvents();

    /// <summary>The "Server → Client events" table of the Server README.</summary>
    private static readonly string ProtocolTable = ReadProtocolTable();

    [Fact]
    public void HubSendsAtLeastTheEventsWeExpect()
    {
        // A guard on the guards: if this ever reads zero, the extraction below is broken and the
        // other two tests would pass vacuously.
        SentEvents.Should().HaveCountGreaterThan(10)
            .And.Contain("StreamingChunk");
    }

    [Fact]
    public void EveryDocumentedHubEventIsActuallySent()
    {
        // BUG-001: the Streaming section named the event `ReceiveChunk`, which the hub never sends.
        // A client written from that section connects, streams nothing, and reports no error.
        var documented = Regex
            .Matches(RepoFiles.Readme("server"), @"`([A-Z][A-Za-z0-9]{3,})`\s*(?:event|hub event)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        documented.Should().NotBeEmpty("the README documents hub events by name");

        var phantom = documented.Where(e => !SentEvents.Contains(e)).Order().ToList();

        // Assert on the joined string, not the collection: FluentAssertions' BeEmpty() reports only
        // the first offending item, which once cost a round trip by hiding a second missing event
        // behind the one already being fixed.
        string.Join(", ", phantom).Should().BeEmpty(
            "every event the README tells a client to subscribe to must be one the hub raises; " +
            "the hub sends [{0}]", string.Join(", ", SentEvents.Order()));
    }

    [Fact]
    public void ProtocolReferenceListsEveryHubEvent()
    {
        // BUG-002: `Cards` and `HostedToolsDeclared` were missing from a table headed "Complete".
        // Their absence costs a custom client generative-UI cards and the hosted-tools badge.
        var undocumented = SentEvents
            .Where(e => !ProtocolTable.Contains($"`{e}`", StringComparison.Ordinal))
            .Order()
            .ToList();

        // Joined rather than asserted as a collection so every missing event is named at once.
        string.Join(", ", undocumented).Should().BeEmpty(
            "the section is titled \"Complete SignalR Protocol Reference\", so a client author is " +
            "entitled to treat it as exhaustive");
    }

    [Fact]
    public void RagSourceShapeMatchesMentorRagResult()
    {
        // The README documents the RagSourcesReady payload as a TypeScript `RagSource` interface,
        // stating it is camelCase System.Text.Json output from MentorRagResult. JavaScript clients
        // never see the .NET name, so the alias is right — but the members have to line up, or the
        // documented shape sends client authors after fields that never arrive.
        var documented = Regex
            .Matches(ReadSection(RepoFiles.Readme("server"), "#### `RagSource` object"),
                     @"^\s*(\w+)\??\s*:", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet();

        documented.Should().NotBeEmpty("the RagSource object is documented as a TypeScript interface");

        var record = File.ReadAllText(Path.Combine(RepoFiles.Root, RepoFiles.Abstractions,
                                                   "Models", "MentorRagResult.cs"));
        // The positional values, and the init properties declared in the body (Metadata, since rc.15):
        // both are serialised.
        var actual = Regex
            .Matches(record, @"^\s*(?:string|float|int|double)\??\s+(\w+)", RegexOptions.Multiline)
            .Concat(Regex.Matches(record, @"^\s*public\s+[^\r\n]+?\s(\w+)\s*\{\s*get;\s*init;", RegexOptions.Multiline))
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet();

        actual.Should().NotBeEmpty("MentorRagResult is a positional record");

        documented.Should().BeEquivalentTo(actual,
            "the documented JSON shape is the camelCase serialisation of MentorRagResult, so the " +
            "two member sets must match exactly in both directions");
    }

    [Fact]
    public void PageContextSnapshotShapeMatchesTheDocumentedInterface()
    {
        // A non-.NET client builds this object by hand from the README, so the documented member
        // list has to be exactly the serialised one — an extra field is dead weight a client will
        // send forever, a missing one is context the assistant never receives.
        // The section documents two interfaces back to back — PageContextSnapshot and the
        // UIActionInfo it contains — so the members have to be read from the first block only.
        //
        // Comments are stripped first: one of them is `// e.g. { activeFilter: "Pending" }`, and a
        // lazy match for the closing brace stops inside it, silently losing the last member.
        var section = Regex.Replace(
            ReadSection(RepoFiles.Readme("server"), "#### `PageContextSnapshot` object"),
            @"//[^\n]*", string.Empty);

        var block = Regex.Match(section, @"interface\s+PageContextSnapshot\s*\{(.*?)\}",
                                RegexOptions.Singleline);
        block.Success.Should().BeTrue("the object is documented as a TypeScript interface");

        var documented = Regex
            .Matches(block.Groups[1].Value, @"^\s*(\w+)\??\s*:", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet();

        documented.Should().NotBeEmpty();

        var actual = typeof(Novior.MentorAgent.Abstractions.Models.PageContextSnapshot)
            .GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToHashSet();

        documented.Should().BeEquivalentTo(actual,
            "the TypeScript interface in the README is the wire shape of PageContextSnapshot");
    }

    private static HashSet<string> ReadSentEvents()
    {
        var events = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in RepoFiles.SourceFiles.Where(f => f.EndsWith(".cs")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"SendAsync\(\s*""([A-Za-z][A-Za-z0-9]*)"""))
                events.Add(m.Groups[1].Value);

        return events;
    }

    private static string ReadProtocolTable()
        => ReadSection(RepoFiles.Readme("server"), "#### Server → Client events");

    /// <summary>Returns the text from <paramref name="heading"/> up to the next heading of any level.</summary>
    private static string ReadSection(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Section '{heading}' is missing from the README.");

        var body = markdown[(start + heading.Length)..];
        var end = Regex.Match(body, @"^#{1,6}\s", RegexOptions.Multiline);
        return end.Success ? body[..end.Index] : body;
    }
}
