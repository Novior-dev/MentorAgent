using System.ComponentModel;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;

namespace Novior.MentorAgent.Tests.App;

/// <summary>
/// A miniature application for the discovery, gating and orchestration suites to scan.
///
/// <para>
/// MentorAgent's behaviour is driven almost entirely by what it finds in the assemblies it is
/// pointed at, so testing it needs a real surface to discover — attributes, services, pages, agents,
/// teams and skills — rather than mocks of the discovery result. Everything here is deliberately
/// boring and deterministic: no I/O, no time, no randomness, so an assertion that fails is about
/// MentorAgent and never about the fixture.
/// </para>
/// </summary>
public sealed class TestOrderService
{
    /// <summary>Tools called during a test, in order — lets a test prove a tool really ran.</summary>
    public List<string> Invoked { get; } = [];

    /// <summary>Set to throw from <see cref="FailingActionAsync"/>, for exception-mapping tests.</summary>
    public Exception? NextFailure { get; set; }

    // Plain [Description] — the zero-dependency path. Tool name: get_active_orders
    [Description("Returns the list of active orders")]
    public Task<List<string>> GetActiveOrdersAsync()
    {
        Invoked.Add(nameof(GetActiveOrdersAsync));
        return Task.FromResult<List<string>>(["order-1", "order-2"]);
    }

    // Every [MentorAction] parameter at once. Tool name: cancel_order
    [MentorAction(
        Description = "Cancels an existing order",
        Category = "Orders",
        RequiresConfirmation = true,
        RequiredRoles = ["Manager"],
        ProactiveHint = "Suggest checking active orders first",
        NavigateTo = "/orders")]
    public Task<bool> CancelOrderAsync(int orderId, string reason)
    {
        Invoked.Add($"{nameof(CancelOrderAsync)}({orderId},{reason})");
        return Task.FromResult(true);
    }

    // Confirmation without roles — separates the two gates, which are easy to conflate.
    [MentorAction(Description = "Issues a refund", RequiresConfirmation = true)]
    public Task<bool> IssueRefundAsync(int orderId)
    {
        Invoked.Add(nameof(IssueRefundAsync));
        return Task.FromResult(true);
    }

    // Roles without confirmation — the other half of that pair.
    // RequiresConfirmation = false is declared, not left out: since rc.15 an undeclared [MentorAction] asks
    // for confirmation (RequireConfirmationForWrites), and this one exists to be the roles-only gate.
    [MentorAction(Description = "Exports the order ledger", RequiredRoles = ["Admin"], RequiresConfirmation = false)]
    public string ExportLedger()
    {
        Invoked.Add(nameof(ExportLedger));
        return "ledger";
    }

    // Ungated, for the "does the gate leak onto neighbours" case.
    [MentorAction(Description = "Counts open orders", ReadOnly = true)]
    public int CountOpenOrders()
    {
        Invoked.Add(nameof(CountOpenOrders));
        return 2;
    }

    // With NavigateTo: the auto-navigation that follows a tool call, on a turn no page will ever show
    // (BUG-098). Tool name: mark_order_reviewed. A write that declares nothing: since rc.15 it asks for
    // confirmation by default (RequireConfirmationForWrites) — the undeclared case the default is about.
    [MentorAction(Description = "Marks an order as reviewed", NavigateTo = "/orders")]
    public string MarkOrderReviewed(int orderId)
    {
        Invoked.Add($"{nameof(MarkOrderReviewed)}({orderId})");
        return $"Order {orderId} marked as reviewed.";
    }

    // Returns a card — generative UI (#14 L2) travels a different path than prose.
    [MentorAction(Description = "Shows an order as a card", RequiresConfirmation = false)]
    public MentorCard ShowOrderCard(int orderId)
    {
        Invoked.Add(nameof(ShowOrderCard));
        return new MentorCard("order")
        {
            Title = $"Order #{orderId}",
            Subtitle = "Mario Rossi",
            Accent = MentorCardAccent.Success,
            Fields = [new("Total", "€2,599.98"), new("Status", "Delivered")],
            Actions = [new("Open", MentorCardActionKind.Navigate, $"/orders/{orderId}")],
        };
    }

    [MentorAction(Description = "Always fails, for exception mapping", RequiresConfirmation = false)]
    public Task FailingActionAsync()
    {
        Invoked.Add(nameof(FailingActionAsync));
        throw NextFailure ?? new InvalidOperationException("test failure");
    }

    // No attribute at all — must never become a tool.
    public string NotATool() => "invisible";
}

/// <summary>A second service, so "tools from several services" is exercised.</summary>
public sealed class TestWeatherService
{
    [Description("Returns the weather for a city")]
    public string GetWeather(string city) => $"sunny in {city}";
}

// ── Pages ────────────────────────────────────────────────────────────────────────

/// <summary>Pages the navigation tool discovers. One has UI actions, one does not.</summary>
[MentorPage(
    Url = "/orders",
    Name = "Orders",
    Description = "List and manage customer orders",
    HasUIActions = true,
    ReadyTimeout = 500)]   // short, so a SignalReady-timeout test does not stall the suite
public sealed class OrdersPageMarker;

[MentorPage(Url = "/reports", Name = "Reports", Description = "Sales and inventory reports")]
public sealed class ReportsPageMarker;

// ── Level 2 ──────────────────────────────────────────────────────────────────────

/// <summary>A Level-2 specialist, reached through the handoff workflow.</summary>
[MentorAgent(
    Name = "ShippingAgent",
    Description = "Answers questions about shipping, tracking and delivery times",
    Instructions = "You handle shipping questions only. Never invent a tracking number.")]
public sealed class TestShippingAgent
{
    /// <summary>Tools called during a test, in order — proves a specialist tool really ran.</summary>
    public List<string> Invoked { get; } = [];

    [Description("Returns the tracking status of an order")]
    public string GetTrackingStatus(int orderId)
    {
        Invoked.Add(nameof(GetTrackingStatus));
        return $"in transit: {orderId}";
    }

    // Gated, so the "does the gate travel into a Level-2 specialist" question has something to
    // ask. A specialist runs its own function-calling loop inside the handoff workflow, where the
    // coordinator's middleware never reaches — before GatedAIFunction this attribute was ignored.
    [MentorAction(
        Description = "Cancels a shipment that has not left the warehouse",
        RequiresConfirmation = true,
        RequiredRoles = ["Manager"])]
    public string CancelShipment(int orderId)
    {
        Invoked.Add(nameof(CancelShipment));
        return $"cancelled: {orderId}";
    }
}

// ── Level 3 ──────────────────────────────────────────────────────────────────────

/// <summary>A Level-3 team. Member order matters for the round-robin assertions.</summary>
[MentorTeam(Name = "Business Analysis Team", Description = "Analyses business performance")]
public sealed class TestAnalysisTeam
{
    // Tools come from the attribute, not from the property type — the README's own example uses
    // `object?` for the property. Declaring them here is what gives the gate something to travel
    // into: TestAnalysisTools carries approve_budget_line (roles + confirmation). A class of its own:
    // since rc.15 a class listed in Tools is the member's only, not also the coordinator's, so the
    // level-1 TestOrderService cannot double as it.
    [TeamMember(
        Role = "Data Analyst",
        Tools = [typeof(TestAnalysisTools)],
        Instructions = "Interpret the numbers.")]
    public TestOrderService? Analyst { get; set; }

    // A [MentorSkill] as Tools: the skill's methods stay level-1 tools as well — that is the skill's own role.
    [TeamMember(
        Role = "Strategist",
        Tools = [typeof(TestExpenseSkill)],
        Instructions = "Turn the numbers into a recommendation.")]
    public TestWeatherService? Strategist { get; set; }
}

/// <summary>
/// The analyst member's tools. Listed only in a <c>[TeamMember(Tools = ...)]</c>, so they are the member's,
/// not the coordinator's. Also what a member must NOT get: an inherited method, a static one and an empty
/// description — the same rules as level 1.
/// </summary>
public sealed class TestAnalysisTools : TestAnalysisToolsBase
{
    [MentorAction(Description = "Reads the quarter's revenue", ReadOnly = true)]
    public decimal ReadQuarterRevenue() => 1250m;

    [MentorAction(Description = "Approves a budget line", RequiresConfirmation = true, RequiredRoles = ["Manager"])]
    public string ApproveBudgetLine(int lineId) => $"approved: {lineId}";

    [Description("A static helper: not a tool")]
    public static string StaticHelper() => "static";

    [Description("")]
    public string EmptyDescription() => "empty";
}

/// <summary>Abstract, so the scan does not pick it up as level 1 on its own.</summary>
public abstract class TestAnalysisToolsBase
{
    [Description("An inherited helper: not a tool")]
    public string InheritedHelper() => "inherited";
}

/// <summary>Declared as a team but with no members — must be skipped with a warning, not crash.</summary>
[MentorTeam(Name = "Empty Team", Description = "Has no members")]
public sealed class TestEmptyTeam;

// ── Skills ───────────────────────────────────────────────────────────────────────

/// <summary>A dual-role skill: knowledge plus callable tools.</summary>
[MentorSkill(
    Name = "expense-report",
    Description = "How to file an expense report and what the limits are",
    Instructions = "Meals are capped at €50 and need a category and a description.")]
public sealed class TestExpenseSkill
{
    [Description("Submits a new expense report")]
    public Task<string> SubmitExpenseReportAsync(string category, decimal amount, string description)
        => Task.FromResult($"filed:{category}:{amount}");

    [Description("Returns the reimbursement policy for a category")]
    public string GetPolicyForCategory(string category) => $"policy for {category}";
}

/// <summary>A knowledge-only skill — no methods, and so no DI registration needed.</summary>
[MentorSkill(
    Name = "tone-of-voice",
    Description = "How the assistant should sound",
    Instructions = "Be brief. Prefer the active voice. Never apologise twice.")]
public sealed class TestToneSkill;
