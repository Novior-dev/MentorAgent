using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// The page context — the seam a Blazor page uses to tell the assistant what is on screen and what
/// it is allowed to do to it.
///
/// <para>
/// Two things make this worth pinning carefully. The first is the readiness handshake: navigation
/// waits for <c>SignalReady()</c> before letting the assistant touch the page, and that is a
/// rendezvous between the AI thread and the Blazor UI thread — the same shape as the approval store,
/// which is where BUG-012 lived. Signalling before anyone waits must not be lost.
/// </para>
///
/// <para>
/// The second is the generated parameter hint. It is the only description of an action's argument
/// the model ever sees, so a wrong one produces a call the handler cannot deserialise — and the
/// generator walks arbitrary host types by reflection, which is a place a self-referential model
/// (an EF navigation property, a tree node) could take the process down.
/// </para>
/// </summary>
public sealed class PageContextTests
{
    private static MentorPageContext New() => new();

    // ── Context data ────────────────────────────────────────────────────────────

    [Fact]
    public void The_page_name_round_trips()
    {
        var ctx = New();

        ctx.SetPageName("Orders");

        ctx.PageName.Should().Be("Orders");
    }

    [Fact]
    public void Registration_is_fluent_all_the_way_down()
    {
        // The documented usage is one chained statement in OnInitialized. Any overload returning
        // something other than the context breaks the whole example in the README.
        var ctx = New();

        var result = ctx.SetPageName("Orders")
                        .Set("filter", "Pending")
                        .RegisterUIAction("a", "d", _ => { })
                        .RegisterUIAction<int>("b", "d", _ => { })
                        .RegisterUIActionAsync("c", "d", _ => Task.CompletedTask)
                        .RegisterUIActionAsync<int>("d", "d", _ => Task.CompletedTask)
                        .UnregisterUIAction("d")
                        .Remove("filter")
                        .SignalReady();

        result.Should().BeSameAs(ctx);
    }

    [Fact]
    public void Context_entries_are_readable_back()
    {
        var ctx = New();

        ctx.Set("filter", "Pending").Set("count", 3);

        ctx.ContextData["filter"].Should().Be("Pending");
        ctx.ContextData["count"].Should().Be(3);
    }

    [Fact]
    public void Setting_the_same_key_twice_overwrites()
    {
        var ctx = New();

        ctx.Set("filter", "Pending").Set("filter", "Shipped");

        ctx.ContextData.Should().HaveCount(1);
        ctx.ContextData["filter"].Should().Be("Shipped");
    }

    [Fact]
    public void Removing_an_entry_leaves_the_others()
    {
        var ctx = New();
        ctx.Set("a", 1).Set("b", 2);

        ctx.Remove("a").Remove("never-set");

        string.Join(",", ctx.ContextData.Keys).Should().Be("b");
    }

    [Fact]
    public void The_context_snapshot_is_a_copy()
    {
        // AppContextProvider reads this on the AI thread while the page writes on the UI thread.
        // Handing out the live dictionary would make every read a race, and the failure mode is an
        // InvalidOperationException thrown mid-turn from inside a foreach.
        var ctx = New();
        ctx.Set("a", 1);

        var snapshot = ctx.ContextData;
        ctx.Set("b", 2);

        snapshot.Should().HaveCount(1, "a snapshot taken before the second write cannot contain it");
        ctx.ContextData.Should().HaveCount(2);
    }

    [Fact]
    public void The_action_snapshot_is_a_copy_too()
    {
        var ctx = New();
        ctx.RegisterUIAction("a", "d", _ => { });

        var snapshot = ctx.UIActions;
        ctx.RegisterUIAction("b", "d", _ => { });

        snapshot.Should().HaveCount(1);
        ctx.UIActions.Should().HaveCount(2);
    }

    [Fact]
    public void Clear_empties_the_name_the_data_and_the_actions()
    {
        // Called from the page's Dispose. Leaving anything behind means the next page inherits the
        // previous one's actions, and the assistant offers the model a tool that will run against a
        // disposed component.
        var ctx = New();
        ctx.SetPageName("Orders").Set("filter", "Pending").RegisterUIAction("a", "d", _ => { });

        ctx.Clear();

        ctx.PageName.Should().BeNull();
        ctx.ContextData.Should().BeEmpty();
        ctx.UIActions.Should().BeEmpty();
    }

    // ── Action registration ─────────────────────────────────────────────────────

    [Fact]
    public void An_action_carries_its_name_and_description()
    {
        var ctx = New();

        ctx.RegisterUIAction("highlight_row", "Highlights a row in the grid", _ => { });

        var reg = ctx.UIActions["highlight_row"];
        reg.ActionName.Should().Be("highlight_row");
        reg.Description.Should().Be("Highlights a row in the grid");
    }

    [Fact]
    public void Registering_the_same_action_name_twice_keeps_the_last_one()
    {
        // A page that re-renders re-registers. Accumulating would offer the model several copies of
        // one tool, all but the newest closing over stale component state.
        var ctx = New();
        var which = "";

        ctx.RegisterUIAction("a", "first", _ => which = "first");
        ctx.RegisterUIAction("a", "second", _ => which = "second");

        ctx.UIActions.Should().HaveCount(1);
        ctx.UIActions["a"].Description.Should().Be("second");
        ctx.UIActions["a"].Handler(null);
        which.Should().Be("second");
    }

    [Fact]
    public void Unregistering_an_action_removes_only_that_one()
    {
        var ctx = New();
        ctx.RegisterUIAction("a", "d", _ => { }).RegisterUIAction("b", "d", _ => { });

        ctx.UnregisterUIAction("a").UnregisterUIAction("never-registered");

        string.Join(",", ctx.UIActions.Keys).Should().Be("b");
    }

    [Fact]
    public async Task A_typed_action_receives_its_parameter_deserialised()
    {
        var ctx = New();
        var received = 0;
        ctx.RegisterUIAction<int>("highlight_row", "d", id => received = id);

        await ctx.UIActions["highlight_row"].HandlerAsync!(JsonDoc("5"));

        received.Should().Be(5);
    }

    [Fact]
    public async Task A_typed_action_maps_camelCase_json_onto_pascal_case_properties()
    {
        // The model writes camelCase JSON; .NET DTOs are PascalCase. Without the case-insensitive
        // option every property would silently arrive as its default, which reads as "the action
        // ran and did nothing".
        var ctx = New();
        OrderFilter? received = null;
        ctx.RegisterUIAction<OrderFilter>("apply_filter", "d", f => received = f);

        await ctx.UIActions["apply_filter"].HandlerAsync!(
            JsonDoc("""{"customerId":3,"onlyOpen":true}"""));

        received.Should().NotBeNull();
        received!.CustomerId.Should().Be(3);
        received.OnlyOpen.Should().BeTrue();
    }

    [Fact]
    public async Task A_typed_action_accepts_a_value_that_is_already_the_right_type()
    {
        var ctx = New();
        var received = 0;
        ctx.RegisterUIAction<int>("highlight_row", "d", id => received = id);

        await ctx.UIActions["highlight_row"].HandlerAsync!(7);

        received.Should().Be(7);
    }

    [Fact]
    public async Task A_typed_action_converts_a_boxed_primitive_of_another_width()
    {
        // A transport that went through JSON and back hands over a long or a double where the
        // handler wants an int.
        var ctx = New();
        var received = 0;
        ctx.RegisterUIAction<int>("highlight_row", "d", id => received = id);

        await ctx.UIActions["highlight_row"].HandlerAsync!(9L);

        received.Should().Be(9);
    }

    [Fact]
    public async Task An_async_untyped_action_receives_the_raw_value()
    {
        var ctx = New();
        object? received = null;
        ctx.RegisterUIActionAsync("do_thing", "d", p => { received = p; return Task.CompletedTask; });

        await ctx.UIActions["do_thing"].HandlerAsync!("raw");

        received.Should().Be("raw");
    }

    [Fact]
    public async Task An_async_typed_action_deserialises_like_the_sync_one()
    {
        var ctx = New();
        var received = 0;
        ctx.RegisterUIActionAsync<int>("highlight_row", "d", id => { received = id; return Task.CompletedTask; });

        await ctx.UIActions["highlight_row"].HandlerAsync!(JsonDoc("5"));

        received.Should().Be(5);
    }

    // ── The generated parameter hint ────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(long), "integer")]
    [InlineData(typeof(short), "integer")]
    [InlineData(typeof(byte), "integer")]
    [InlineData(typeof(float), "number")]
    [InlineData(typeof(double), "number")]
    [InlineData(typeof(decimal), "number")]
    [InlineData(typeof(Guid), "string (GUID)")]
    [InlineData(typeof(DateTime), "string (ISO 8601 date)")]
    [InlineData(typeof(DateTimeOffset), "string (ISO 8601 date)")]
    public void A_primitive_parameter_is_described_by_its_json_shape(Type type, string expected)
    {
        // This string is the only thing the model is told about the argument. "Int32" would be a
        // .NET name in a JSON world; "integer" is what a schema-shaped model expects.
        HintFor(type).Should().Be(expected);
    }

    [Fact]
    public void An_enum_parameter_lists_its_allowed_values()
    {
        HintFor(typeof(OrderStatus)).Should().Be("string (Active|Inactive|Pending)");
    }

    [Fact]
    public void A_nullable_parameter_is_marked_as_optional()
    {
        HintFor(typeof(int?)).Should().Be("integer?");
    }

    [Fact]
    public void A_collection_parameter_describes_its_element()
    {
        HintFor(typeof(int[])).Should().Be("array<integer>");
        HintFor(typeof(List<string>)).Should().Be("array<string>");
        HintFor(typeof(IEnumerable<bool>)).Should().Be("array<boolean>");
    }

    [Fact]
    public void A_dictionary_parameter_describes_both_sides()
    {
        HintFor(typeof(Dictionary<string, int>)).Should().Be("object<string,integer>");
    }

    [Fact]
    public void A_complex_parameter_is_described_property_by_property_in_camelCase()
    {
        // camelCase because that is what the model should emit — the deserialiser is
        // case-insensitive, but telling the model the wrong casing invites it to invent its own.
        HintFor(typeof(OrderFilter)).Should().Be("{ customerId: integer, onlyOpen: boolean }");
    }

    [Fact]
    public void A_self_referential_parameter_does_not_recurse_forever()
    {
        // An EF navigation property or a tree node. Without the depth cap this is a
        // StackOverflowException, which cannot be caught and takes the whole process with it —
        // triggered by a host simply registering an action with their own domain model.
        var act = () => HintFor(typeof(TreeNode));

        act.Should().NotThrow();
        HintFor(typeof(TreeNode)).Should().Contain("object", "the cap collapses the deep levels");
    }

    [Fact]
    public void An_explicit_hint_wins_over_the_generated_one()
    {
        var ctx = New();

        ctx.RegisterUIAction<int>("a", "d", _ => { }, parameterHint: "the row number, 1-based");

        ctx.UIActions["a"].ParameterHint.Should().Be("the row number, 1-based");
    }

    [Fact]
    public void The_untyped_overload_offers_no_hint()
    {
        // There is no type to derive one from, and inventing "object" would tell the model less
        // than saying nothing.
        var ctx = New();

        ctx.RegisterUIAction("a", "d", _ => { });

        ctx.UIActions["a"].ParameterHint.Should().BeNull();
    }

    // ── The readiness handshake ─────────────────────────────────────────────────

    [Fact]
    public async Task A_page_that_signals_ready_releases_the_waiter()
    {
        var ctx = New();

        var waiting = ctx.WaitForPageReadyAsync(5000);
        ctx.SignalReady();

        (await waiting).Should().BeTrue();
    }

    [Fact]
    public async Task Signalling_before_anyone_waits_is_not_lost()
    {
        // The documented usage puts SignalReady() at the end of OnInitialized — which runs before
        // the navigation tool starts waiting. A handshake that only worked in the other order would
        // make every fast page time out, and the same race parked a turn forever in BUG-012.
        var ctx = New();

        ctx.SignalReady();

        (await ctx.WaitForPageReadyAsync(5000)).Should().BeTrue();
    }

    [Fact]
    public async Task Signalling_twice_is_harmless()
    {
        var ctx = New();

        ctx.SignalReady();
        ctx.SignalReady();

        (await ctx.WaitForPageReadyAsync(5000)).Should().BeTrue();
    }

    [Fact]
    public async Task Several_waiters_are_all_released_by_one_signal()
    {
        var ctx = New();

        var first = ctx.WaitForPageReadyAsync(5000);
        var second = ctx.WaitForPageReadyAsync(5000);
        ctx.SignalReady();

        (await Task.WhenAll(first, second)).Should().AllBeEquivalentTo(true);
    }

    [Fact]
    public async Task A_page_that_never_signals_times_out_rather_than_hanging()
    {
        // The navigation tool awaits this inside a turn. Throwing, or waiting forever, would take
        // the turn with it — the documented behaviour is to warn and carry on.
        var ctx = New();

        (await ctx.WaitForPageReadyAsync(50)).Should().BeFalse();
    }

    [Fact]
    public async Task A_timeout_is_reported_rather_than_thrown()
    {
        var ctx = New();

        var act = async () => await ctx.WaitForPageReadyAsync(50);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Cancelling_the_turn_ends_the_wait_without_throwing()
    {
        // The Stop button while navigation is waiting for a slow page.
        var ctx = New();
        using var cts = new CancellationTokenSource();

        var waiting = ctx.WaitForPageReadyAsync(30_000, cts.Token);
        await cts.CancelAsync();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_takes_the_ready_signal_back()
    {
        // Clear() runs in the page's Dispose. If the signal survived it, the next page would be
        // treated as ready before it had rendered, and its UI actions would be invoked against a
        // half-built component.
        var ctx = New();
        ctx.SignalReady();

        ctx.Clear();

        (await ctx.WaitForPageReadyAsync(50)).Should().BeFalse();
    }

    [Fact]
    public async Task A_new_page_can_signal_ready_after_a_clear()
    {
        var ctx = New();
        ctx.SignalReady();
        ctx.Clear();

        ctx.SignalReady();

        (await ctx.WaitForPageReadyAsync(5000)).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_signals_and_waits_settle_exactly_once()
    {
        for (var i = 0; i < 200; i++)
        {
            var ctx = New();

            var signal = Task.Run(() => ctx.SignalReady());
            var ready = await ctx.WaitForPageReadyAsync(5000);
            await signal;

            ready.Should().BeTrue("iteration {0}", i);
        }
    }

    // ── Helpers and fixtures ────────────────────────────────────────────────────

    private static JsonElement JsonDoc(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The generated hint for <paramref name="type"/>, read back through a registration — the
    /// generator is private, and the registration is the only place its output is ever used.
    /// </summary>
    private static string? HintFor(Type type)
    {
        var ctx = New();
        var register = typeof(MentorPageContext)
            .GetMethods()
            .First(m => m.Name == nameof(MentorPageContext.RegisterUIAction)
                        && m.IsGenericMethodDefinition)
            .MakeGenericMethod(type);

        var handlerType = typeof(Action<>).MakeGenericType(type);
        var noop = Delegate.CreateDelegate(
            handlerType,
            typeof(PageContextTests).GetMethod(nameof(Noop),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type));

        register.Invoke(ctx, ["a", "d", noop, null]);
        return ctx.UIActions["a"].ParameterHint;
    }

    private static void Noop<T>(T _) { }

    private sealed class OrderFilter
    {
        public int CustomerId { get; set; }
        public bool OnlyOpen { get; set; }
    }

    private enum OrderStatus { Active, Inactive, Pending }

    /// <summary>Self-referential, like an EF navigation property or a tree.</summary>
    private sealed class TreeNode
    {
        public string Name { get; set; } = "";
        public TreeNode? Parent { get; set; }
        public List<TreeNode> Children { get; set; } = [];
    }
}
