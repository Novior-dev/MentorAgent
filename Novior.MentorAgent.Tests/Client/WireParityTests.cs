using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Tests.Docs;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Client;

/// <summary>
/// The SignalR wire contract, checked from both ends at once.
///
/// <para>
/// The hub and the WebAssembly client are in different packages, compiled independently, joined
/// only by string literals. Nothing in the type system connects <c>SendAsync("Cards", …)</c> on the
/// server to <c>_hub.On&lt;List&lt;MentorCard&gt;&gt;("Cards", …)</c> on the client — so a server
/// that starts sending a new event, or renames one, produces a client that compiles, connects,
/// runs, and silently ignores it. Exactly that drift is what BUG-002 turned out to be in the
/// documentation; this is the same check applied to the code.
/// </para>
///
/// <para>
/// Parsed from source rather than driven over a live connection on purpose: a live test can only
/// prove the events it happens to trigger, and the events that go missing are by definition the
/// rare ones nobody thought to exercise.
/// </para>
/// </summary>
public sealed class WireParityTests(ITestOutputHelper output)
{
    /// <summary>Event name → argument count, as the server sends it.</summary>
    private static Dictionary<string, int> ServerEvents()
    {
        var events = new Dictionary<string, int>(StringComparer.Ordinal);

        // The decorator forwards the state service's notifications for the duration of a turn.
        var decorator = File.ReadAllText(Path.Combine(
            RepoFiles.Root, RepoFiles.Server, "Hubs", "HubStateServiceDecorator.cs"));

        // SendAsync("Name", a, b) — the argument list may wrap across lines.
        foreach (Match m in Regex.Matches(decorator, @"SendAsync\(""(\w+)""([^;]*)", RegexOptions.Singleline))
            events[m.Groups[1].Value] = CountArgs(m.Groups[2].Value);

        // HostedToolsDeclared is sent by the hub itself on connect, not through the decorator —
        // it describes the server's capabilities rather than anything that happened in a turn.
        var hub = File.ReadAllText(Path.Combine(RepoFiles.Root, RepoFiles.Server, "Hubs", "MentorHub.cs"));
        foreach (Match m in Regex.Matches(hub, @"SendAsync\(""(\w+)""([^;]*)", RegexOptions.Singleline))
            events[m.Groups[1].Value] = CountArgs(m.Groups[2].Value);

        return events;
    }

    /// <summary>Event name → handler arity, as the WebAssembly client subscribes.</summary>
    private static Dictionary<string, int> ClientSubscriptions()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoFiles.Root, RepoFiles.Blazor, "Services", "WasmMentorStateService.cs"));

        var subs = new Dictionary<string, int>(StringComparer.Ordinal);

        // _hub.On<T1, T2>("Name", …) — generic arity is the handler's argument count; the
        // non-generic _hub.On("Name", …) overload takes none.
        //
        // Scanned rather than matched with one regex: `On<List<MentorRagResult>>` nests, and a
        // character class that stops at the first '>' silently reports those three events as
        // unsubscribed. That is a parser bug that looks exactly like the defect being hunted, so
        // it is worth the extra few lines to not have to tell them apart.
        foreach (Match m in Regex.Matches(source, @"_hub\.On\s*"))
        {
            var i = m.Index + m.Length;
            var arity = 0;

            if (i < source.Length && source[i] == '<')
            {
                var end = MatchingAngle(source, i);
                if (end < 0) continue;
                arity = SplitGenerics(source[(i + 1)..end]);
                i = end + 1;
            }

            var name = Regex.Match(source[i..], @"^\s*\(\s*""(\w+)""");
            if (name.Success) subs[name.Groups[1].Value] = arity;
        }

        return subs;
    }

    [Fact]
    public void BothEndsWereActuallyParsed()
    {
        // Guard: an empty set on either side would make every comparison below vacuously true.
        ServerEvents().Should().HaveCountGreaterThan(15);
        ClientSubscriptions().Should().HaveCountGreaterThan(15);
    }

    [Fact]
    public void Every_event_the_server_sends_is_subscribed_by_the_wasm_client()
    {
        var server = ServerEvents();
        var client = ClientSubscriptions();

        var missing = server.Keys.Where(e => !client.ContainsKey(e)).Order().ToList();
        foreach (var e in missing) output.WriteLine($"not subscribed: {e}");

        string.Join(", ", missing).Should().BeEmpty(
            "an event nobody listens for is indistinguishable from a feature that does not work");
    }

    [Fact]
    public void The_wasm_client_subscribes_to_nothing_the_server_does_not_send()
    {
        // The other direction. A subscription to an event that no longer exists is dead code that
        // reads as coverage — it makes the feature look wired when it is not.
        var server = ServerEvents();
        var client = ClientSubscriptions();

        var orphans = client.Keys.Where(e => !server.ContainsKey(e)).Order().ToList();
        foreach (var e in orphans) output.WriteLine($"no sender: {e}");

        string.Join(", ", orphans).Should().BeEmpty();
    }

    [Fact]
    public void Every_shared_event_has_the_same_argument_count_on_both_sides()
    {
        // Arity is where this drift actually bites. SignalR does not error on a mismatch — the
        // handler is simply never invoked, so the symptom is one feature quietly not working
        // while everything around it does.
        var server = ServerEvents();
        var client = ClientSubscriptions();

        var mismatched = server
            .Where(kv => client.TryGetValue(kv.Key, out var arity) && arity != kv.Value)
            .Select(kv => $"{kv.Key}: server sends {kv.Value}, client expects {client[kv.Key]}")
            .Order()
            .ToList();

        foreach (var m in mismatched) output.WriteLine(m);

        string.Join(Environment.NewLine, mismatched).Should().BeEmpty(
            "a mismatched arity silently drops the event rather than failing");
    }

    [Fact]
    public void Every_event_is_documented_in_the_server_readme_protocol_reference()
    {
        // The README is the contract for client authors writing React, Vue or Angular, who have no
        // compiler to tell them what to subscribe to.
        var readme = RepoFiles.Readme("server");

        var undocumented = ServerEvents().Keys
            .Where(e => !readme.Contains($"`{e}`"))
            .Order()
            .ToList();

        foreach (var e in undocumented) output.WriteLine($"undocumented: {e}");

        string.Join(", ", undocumented).Should().BeEmpty();
    }

    [Fact]
    public void The_two_hub_methods_documented_as_unusable_are_not_called_by_the_wasm_client()
    {
        // CancelRequest and RespondToApproval are documented as do-not-use: SignalR dispatches one
        // invocation at a time per connection, so both would queue behind the very SendMessage they
        // are meant to interrupt. The WASM client must reach for the HTTP endpoints instead.
        var orchestrator = File.ReadAllText(Path.Combine(
            RepoFiles.Root, RepoFiles.Blazor, "Services", "WasmMentorOrchestrator.cs"));

        // Matched on the hub call specifically. A bare name match would hit the orchestrator's own
        // RespondToApprovalAsync declaration — it implements IMentorOrchestrator and has to keep
        // that name; what matters is what it does inside.
        orchestrator.Should().NotContain("_hub.CancelRequestAsync(",
            "cancelling through the hub deadlocks behind the running turn; use POST /mentor/cancel");
        orchestrator.Should().NotContain("_hub.RespondToApprovalAsync(",
            "approving through the hub deadlocks the same way; use POST /mentor/approve");

        // And the positive half: it has to actually reach the HTTP endpoints, or "does not call the
        // hub" would be satisfied by a method that does nothing at all.
        orchestrator.Should().Contain("/mentor/approve").And.Contain("/mentor/cancel");
    }

    private static int CountArgs(string tail)
    {
        // Everything up to the matching close paren of SendAsync(...), minus the event name.
        var depth = 1;
        var args = new System.Text.StringBuilder();
        foreach (var ch in tail)
        {
            if (ch == '(') depth++;
            else if (ch == ')') { depth--; if (depth == 0) break; }
            args.Append(ch);
        }

        var text = args.ToString();
        // Split on top-level commas only, so SendAsync("X", Foo(a, b)) counts as one argument.
        var count = 0;
        depth = 0;
        var seenContent = false;
        foreach (var ch in text)
        {
            if (ch is '(' or '<' or '[') depth++;
            else if (ch is ')' or '>' or ']') depth--;
            else if (ch == ',' && depth == 0) { count++; seenContent = false; continue; }
            else if (!char.IsWhiteSpace(ch)) seenContent = true;
        }

        // Minus the event name itself, which is the first argument to SendAsync.
        return Math.Max(0, (seenContent ? count + 1 : count) - 1);
    }

    /// <summary>Index of the '&gt;' closing the '&lt;' at <paramref name="open"/>, or -1.</summary>
    private static int MatchingAngle(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>' && --depth == 0) return i;
            else if (text[i] is ';' or '{') return -1;
        }
        return -1;
    }

    private static int SplitGenerics(string generics)
    {
        var count = 1;
        var depth = 0;
        foreach (var ch in generics)
        {
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            else if (ch == ',' && depth == 0) count++;
        }
        return count;
    }
}
