using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-195 — the list an agent is built with carries one function per name: from Agent Framework 1.23 two
/// different functions with the same name fail every request. The first stays, the one a call by that name
/// already reached; the rest of the list is left as it is.
/// </summary>
public sealed class OneToolPerNameTests
{
    private static readonly AIFunction First = AIFunctionFactory.Create(() => "first", "look_up", "First copy");
    private static readonly AIFunction Second = AIFunctionFactory.Create(() => "second", "look_up", "Second copy");

    private static (List<AITool> Kept, TestLogCollector Logs) Keep(params AITool[] tools)
    {
        var logs = new TestLogCollector();
        var logger = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs))).CreateLogger("test");
        return (OneToolPerName.Keep([.. tools], "The coordinator", logger), logs);
    }

    [Fact]
    public void Of_two_functions_with_one_name_the_first_stays_and_the_warning_names_both()
    {
        var other = AIFunctionFactory.Create(() => "x", "other", "Another tool");

        var (kept, logs) = Keep(First, other, Second);

        kept.Should().Equal(First, other);
        logs.HasWarning("The coordinator is offered 1 tool name(s) by more than one tool").Should().BeTrue(logs.WarningText);
        logs.HasWarning("look_up (kept ").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void The_same_function_twice_is_one_tool_and_no_warning()
    {
        var (kept, logs) = Keep(First, First);

        kept.Should().Equal(First);
        logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Tools_that_are_not_functions_and_names_that_differ_in_case_are_left_alone()
    {
        // As the framework counts them: a provider-hosted tool is not a function, and names are compared ordinally.
        var hosted = new HostedWebSearchTool();
        var upper = AIFunctionFactory.Create(() => "x", "Look_up", "Other case");

        var (kept, logs) = Keep(hosted, First, upper, hosted);

        kept.Should().Equal(hosted, First, upper, hosted);
        logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning);
    }
}
