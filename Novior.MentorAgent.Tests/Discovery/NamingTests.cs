using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Utility;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// Tool naming and tool-result JSON — the two smallest pieces of the library, and the two with the
/// widest blast radius.
///
/// <para>
/// A tool's name is its contract with everything else: <c>RequiresApproval</c> predicates, eval
/// checks, metrics keys, the documented built-in tool list, and every example a reader copies. The
/// core README leads its configuration section with "Tool names — the rule you need before
/// configuring anything else" for that reason, and BUG-001 was exactly a name drifting from its
/// documentation.
/// </para>
///
/// <para>
/// The JSON helpers are the other end: whatever a tool returns is parsed by the model. Malformed
/// JSON there does not throw anywhere in this process — it degrades the model's next reply, which
/// is the hardest possible symptom to trace back to a cause.
/// </para>
/// </summary>
public sealed class NamingTests
{
    // ── ToSnakeCase ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CreateOrder", "create_order")]
    [InlineData("CreateOrderAsync", "create_order")]
    [InlineData("Business Analysis Team", "business_analysis_team")]
    [InlineData("expense-report", "expense_report")]
    [InlineData("GetWeather", "get_weather")]
    [InlineData("ExportLedger", "export_ledger")]
    public void The_documented_examples_all_hold(string input, string expected)
    {
        // Straight from the XML docs and the README. If any of these change, a reader following the
        // documentation writes a predicate that never matches.
        MentorUtils.ToSnakeCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Save", "save")]
    [InlineData("S", "s")]
    [InlineData("", "")]
    public void Short_and_empty_names_do_not_break_the_scan(string input, string expected)
    {
        MentorUtils.ToSnakeCase(input).Should().Be(expected);
    }

    [Fact]
    public void Only_a_trailing_Async_is_stripped()
    {
        // "AsyncReport" starts with it and must keep it; "ProcessAsyncData" contains it mid-name.
        MentorUtils.ToSnakeCase("AsyncReport").Should().Be("async_report");
        MentorUtils.ToSnakeCase("ProcessAsyncData").Should().Be("process_async_data");
        MentorUtils.ToSnakeCase("ProcessAsync").Should().Be("process");
    }

    [Fact]
    public void The_Async_check_is_case_sensitive_as_documented()
    {
        // A method literally named "Processasync" is not the async convention.
        MentorUtils.ToSnakeCase("Processasync").Should().Be("processasync");
    }

    [Fact]
    public void A_name_that_is_only_Async_collapses_to_empty_rather_than_throwing()
    {
        // Degenerate but reachable — someone writes `public Task Async()`. An exception during
        // discovery would take down registration for the whole assembly.
        var act = () => MentorUtils.ToSnakeCase("Async");

        act.Should().NotThrow();
        MentorUtils.ToSnakeCase("Async").Should().BeEmpty();
    }

    [Fact]
    public void Consecutive_capitals_each_get_their_own_underscore()
    {
        // Acronyms are where snake_case converters usually disagree. Pinning the actual behaviour
        // matters more than which convention it picked: the README's rule has to match the code,
        // and a host reading a metrics key needs it to be stable.
        MentorUtils.ToSnakeCase("ExportPDF").Should().Be("export_p_d_f");
        MentorUtils.ToSnakeCase("GetHTTPStatus").Should().Be("get_h_t_t_p_status");
    }

    [Fact]
    public void An_underscore_already_present_does_not_gain_another()
    {
        // The `name[i - 1] != '_'` guard. Without it "Order_Details" becomes "order__details",
        // which is a different tool name from the one the developer wrote.
        MentorUtils.ToSnakeCase("Order_Details").Should().Be("order_details");
        MentorUtils.ToSnakeCase("already_snake").Should().Be("already_snake");
    }

    [Fact]
    public void A_leading_capital_never_produces_a_leading_underscore()
    {
        // The `i > 0` guard. A leading underscore would make every tool name invalid against the
        // documented naming rule.
        MentorUtils.ToSnakeCase("Order").Should().NotStartWith("_");
        MentorUtils.ToSnakeCase("A").Should().Be("a");
    }

    [Fact]
    public void Spaces_and_dashes_normalise_the_same_way()
    {
        MentorUtils.ToSnakeCase("Refund Policy").Should().Be(MentorUtils.ToSnakeCase("Refund-Policy"));
    }

    [Fact]
    public void Digits_are_preserved_and_do_not_introduce_breaks()
    {
        MentorUtils.ToSnakeCase("ExportQ4Report").Should().Be("export_q4_report");
        MentorUtils.ToSnakeCase("Get2FACode").Should().Be("get2_f_a_code");
    }

    [Fact]
    public void The_result_is_stable_when_applied_twice()
    {
        // Idempotence matters because a name can pass through the converter more than once —
        // discovery, then a builder re-deriving a member tool name.
        string[] names = ["CreateOrderAsync", "Business Analysis Team", "expense-report", "ExportPDF"];

        foreach (var name in names)
        {
            var once = MentorUtils.ToSnakeCase(name);
            MentorUtils.ToSnakeCase(once).Should().Be(once, "converting '{0}' twice must not drift", name);
        }
    }

    [Fact]
    public void Every_documented_tool_name_survives_a_round_trip_through_the_rule()
    {
        // The README states the rule as "PascalCase method name, Async stripped, snake_cased".
        // These are the built-in tools it names; each must be what the rule produces.
        var cases = new Dictionary<string, string>
        {
            ["NavigateTo"] = "navigate_to",
            ["RouteToSpecialist"] = "route_to_specialist",
            ["InvokeUIAction"] = "invoke_u_i_action",
            ["Remember"] = "remember",
            ["ForgetAll"] = "forget_all",
            ["LoadSkill"] = "load_skill",
            ["ReadSkillResource"] = "read_skill_resource",
        };

        foreach (var (method, expected) in cases)
            MentorUtils.ToSnakeCase(method).Should().Be(expected);
    }

    // ── ToFriendlyName ──────────────────────────────────────────────────────────

    [Fact]
    public void A_tool_name_becomes_a_title_cased_label()
    {
        MentorUtils.ToFriendlyName("cancel_order").Should().Be("Cancel Order");
        MentorUtils.ToFriendlyName("export_ledger").Should().Be("Export Ledger");
    }

    [Fact]
    public void A_single_word_tool_name_is_still_capitalised()
    {
        MentorUtils.ToFriendlyName("remember").Should().Be("Remember");
    }

    [Fact]
    public void An_empty_name_produces_an_empty_label_rather_than_throwing()
    {
        // Reachable from a malformed declarative definition; a confirmation dialog with a blank
        // title is bad, an exception while building one is worse.
        var act = () => MentorUtils.ToFriendlyName("");

        act.Should().NotThrow();
        MentorUtils.ToFriendlyName("").Should().BeEmpty();
    }

    [Fact]
    public void The_friendly_name_is_what_the_confirmation_dialog_shows()
    {
        // Round-trip: the label a user reads before approving comes from the method name they wrote.
        var toolName = MentorUtils.ToSnakeCase("IssueRefundAsync");

        MentorUtils.ToFriendlyName(toolName).Should().Be("Issue Refund");
    }

    // ── Tool result JSON ────────────────────────────────────────────────────────

    [Fact]
    public void Every_helper_produces_parseable_json()
    {
        string[] payloads =
        [
            MentorUtils.ToolSuccess(),
            MentorUtils.ToolSuccess("done"),
            MentorUtils.ToolSuccessAction("highlight_row"),
            MentorUtils.ToolSuccessNavigation("/orders", "Orders"),
            MentorUtils.ToolError("something went wrong"),
            MentorUtils.ToolNotFound("Action", "frobnicate", "cancel_order, export_ledger"),
        ];

        foreach (var payload in payloads)
        {
            var act = () => JsonDocument.Parse(payload);
            act.Should().NotThrow("'{0}' is handed straight to the model as tool output", payload);
        }
    }

    [Fact]
    public void The_success_flag_is_a_boolean_not_a_string()
    {
        // A quoted "true" reads as a truthy string to some models and as a literal to others.
        JsonDocument.Parse(MentorUtils.ToolSuccess()).RootElement
            .GetProperty("success").ValueKind.Should().Be(JsonValueKind.True);

        JsonDocument.Parse(MentorUtils.ToolError("x")).RootElement
            .GetProperty("success").ValueKind.Should().Be(JsonValueKind.False);
    }

    [Theory]
    [InlineData("he said \"no\"")]
    [InlineData(@"path C:\temp\file")]
    [InlineData("line one\nline two")]
    [InlineData("carriage\rreturn")]
    [InlineData("all four: \" \\ \r \n")]
    public void Quotes_backslashes_and_line_breaks_survive_a_round_trip(string message)
    {
        // The documented guarantee: "quotes or backslashes in error messages or tool names never
        // break the JSON structure". Round-tripped rather than string-compared, because an escape
        // that produces valid JSON but the wrong text is just as wrong.
        var json = MentorUtils.ToolError(message);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Be(message);
    }

    [Fact]
    public void An_exception_message_containing_json_does_not_break_the_envelope()
    {
        // The realistic case: a tool throws while parsing a payload and the exception message
        // quotes the payload back.
        var message = """Unexpected token '{"id": 5}' at position 12""";

        var json = MentorUtils.ToolError(message);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Be(message);
    }

    [Fact]
    public void A_tab_in_a_message_still_produces_valid_json()
    {
        // Tab is a control character, and RFC 8259 forbids raw control characters inside a JSON
        // string — so an unescaped tab makes the whole tool result unparseable, exactly like an
        // unescaped quote would. Reachable from any exception message that formats a table, and
        // from a Windows path echoed with a tab separator.
        var json = MentorUtils.ToolError("column\tvalue");

        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow("a raw tab inside a JSON string is invalid JSON");
    }

    [Fact]
    public void Every_control_character_is_escaped_not_just_the_familiar_ones()
    {
        // The general form of the tab case. RFC 8259 forbids all of U+0000–U+001F raw inside a
        // string, and a hand-written escape list will always cover the ones its author thought of.
        for (var c = (char)0; c <= 0x1F; c++)
        {
            var json = MentorUtils.ToolError($"before{c}after");

            var act = () => JsonDocument.Parse(json);
            act.Should().NotThrow("U+{0:X4} is a control character and must be escaped", (int)c);
        }
    }

    [Fact]
    public void Escaping_preserves_the_message_exactly()
    {
        // Escaping that mangles the text is no better than escaping that fails to parse — the
        // model reads this string and reports it to the user.
        var message = "tab\there, null\0there, unitseparator, quote\", slash\\";

        var round = JsonDocument.Parse(MentorUtils.ToolError(message))
            .RootElement.GetProperty("error").GetString();

        round.Should().Be(message);
    }

    [Fact]
    public void Non_ascii_text_survives_a_round_trip()
    {
        // Error messages are localised; the widget ships ten languages.
        var message = "Impossibile annullare l'ordine — è già spedito 已发货 🚚";

        var round = JsonDocument.Parse(MentorUtils.ToolError(message))
            .RootElement.GetProperty("error").GetString();

        round.Should().Be(message);
    }

    [Fact]
    public void The_navigation_result_reports_both_the_url_and_the_page()
    {
        var root = JsonDocument.Parse(MentorUtils.ToolSuccessNavigation("/orders/42", "Order detail")).RootElement;

        root.GetProperty("navigated_to").GetString().Should().Be("/orders/42");
        root.GetProperty("page").GetString().Should().Be("Order detail");
    }

    [Fact]
    public void The_not_found_result_lists_what_was_available()
    {
        // The whole point of the helper: the model has just hallucinated a name, and the fastest
        // correction is showing it the real ones.
        var error = JsonDocument.Parse(
                MentorUtils.ToolNotFound("Action", "frobnicate", "cancel_order, export_ledger"))
            .RootElement.GetProperty("error").GetString();

        error.Should().Contain("frobnicate")
            .And.Contain("cancel_order")
            .And.Contain("export_ledger");
    }

    [Fact]
    public void A_quote_in_any_not_found_field_is_escaped()
    {
        // Three separate interpolations in one helper — the easiest place to escape two of three.
        var json = MentorUtils.ToolNotFound("Ac\"tion", "fro\"b", "a\"b, c\"d");

        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }
}
