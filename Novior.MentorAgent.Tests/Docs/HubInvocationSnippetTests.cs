using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Every documented hub invocation has to have the arity the hub actually accepts (BUG-051).
///
/// <para>
/// This is the test that failed on the READMEs as shipped in <c>1.0.0-rc.2</c>. Six snippets — the
/// JavaScript quickstart, the React hook, the Angular service, the Vue composable, the .NET
/// MAUI / console client and the UI-actions walk-through — invoked <c>SendMessage</c> with a single
/// argument, which SignalR rejects during protocol parsing. Between them they were every non-Blazor
/// integration path the package advertises.
/// </para>
///
/// <para>
/// It is a documentation test rather than a behavioural one because the behaviour cannot be
/// changed: hub methods cannot be overloaded and a C# default value is invisible on the wire, so
/// the two-argument call is the protocol. <c>Server/HubMethodArityTests</c> pins that half.
/// </para>
/// </summary>
public sealed class HubInvocationSnippetTests
{
    /// <summary>
    /// `invoke('SendMessage', …)` / `InvokeAsync("SendMessage", …)` up to the closing paren.
    /// Anchored on the quote styles the snippets actually use so a mention in prose is not matched.
    /// </summary>
    private static readonly Regex Invocation = new(
        @"[Ii]nvoke(?:Async)?\(\s*['""]SendMessage['""]\s*(?<rest>[^)]*)\)",
        RegexOptions.Compiled);

    private static IEnumerable<(string Readme, int Line, string Text)> Invocations()
    {
        foreach (var (name, path) in RepoFiles.Readmes)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                // A line marked ❌ is a deliberate counter-example — the warning box shows the
                // rejected form on purpose, next to the two that work. Flagging it would force the
                // documentation to stop naming the mistake it is warning about.
                if (lines[i].Contains('❌')) continue;

                foreach (Match m in Invocation.Matches(lines[i]))
                    yield return (name, i + 1, m.Value);
            }
        }
    }

    [Fact]
    public void No_documented_snippet_invokes_SendMessage_with_a_single_argument()
    {
        // The argument list after the method name. One argument means nothing follows the name but
        // the message itself — no comma at the top level.
        var offenders = Invocations()
            .Where(x => !HasSecondArgument(x.Text))
            .Select(x => $"{x.Readme} README line {x.Line}: {x.Text}")
            .ToList();

        offenders.Should().BeEmpty(
            "SignalR binds hub arguments by count, so `invoke('SendMessage', text)` is rejected in " +
            "TryParseMessage with \"Invocation provides 1 argument(s) but target expects 2\" — a " +
            "reader who copies one of these cannot send a single message (BUG-051). Pass the " +
            "attachments argument explicitly: null, or the array. Offenders:\n" +
            string.Join('\n', offenders));
    }

    [Fact]
    public void The_transport_table_no_longer_calls_the_attachments_argument_optional()
    {
        var server = RepoFiles.Readme("server");

        // The row that told readers the second argument could be left out. Keeping the word
        // "optional" anywhere on the SignalR transport row is what made six snippets look correct.
        var row = server
            .Split('\n')
            .FirstOrDefault(l => l.Contains("**SignalR**") && l.Contains("SendMessage"));

        row.Should().NotBeNull("the transport table documents how to send attachments on each transport");
        row!.Should().NotContain("optional trailing argument",
            "the argument is optional in C# and mandatory on the wire; saying 'optional' is what " +
            "produced BUG-051");
    }

    private static bool HasSecondArgument(string invocation)
    {
        // Everything after `'SendMessage'`, with the trailing `)` already excluded by the regex.
        var idx = invocation.IndexOf("SendMessage", StringComparison.Ordinal);
        var rest = invocation[(idx + "SendMessage".Length)..];

        // Drop the closing quote of the method name, then look for a top-level comma *after* the
        // message argument. Two commas total (one before the message, one before attachments).
        var depth = 0;
        var commas = 0;
        foreach (var c in rest)
        {
            switch (c)
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0: commas++; break;
            }
        }

        return commas >= 2;
    }
}
