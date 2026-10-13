using FluentAssertions;
using Novior.MentorAgent.Server.Hubs;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// SignalR's message-size ceiling, which is the single most brutal failure mode in this transport.
///
/// <para>
/// The default <c>MaximumReceiveMessageSize</c> is 32 KB — smaller than any photograph. Exceeding it
/// does not produce an error: the server <b>aborts the connection</b>. What the user sees is their
/// own bubble appearing and then nothing at all — no reply, no error, no busy spinner — because the
/// invocation never reached the server. Nothing about that symptom points at a size limit.
/// </para>
///
/// <para>
/// The other half of the trap is that <c>HubOptions&lt;MentorHub&gt;</c> is <b>ignored entirely</b>
/// unless <c>AddHubOptions&lt;MentorHub&gt;()</c> was called — SignalR keeps an internal
/// "the user configured this hub" flag and falls back to the global options when it is unset. A
/// limit computed correctly and never applied looks exactly like no limit having been raised.
/// </para>
/// </summary>
public sealed class HubMessageSizeTests
{
    private static long? LimitOf(ServerAppFactory app)
        => app.Services.GetRequiredService<IOptions<HubOptions<MentorHub>>>().Value.MaximumReceiveMessageSize;

    [Fact]
    public async Task The_limit_is_raised_above_a_full_sized_image_payload()
    {
        // Asserted ABOVE the configured payload, not near it: base64 inflates by 4/3 and the text
        // plus page-context snapshot ride in the same invocation. A limit that merely equals the
        // image bytes still aborts the connection on a real message.
        await using var app = await ServerAppFactory.CreateAsync(o =>
        {
            o.EnableImageInput = true;
            o.MaxImageBytes = 4 * 1024 * 1024;
            o.MaxImagesPerMessage = 3;
        });

        var limit = LimitOf(app);

        limit.Should().NotBeNull();
        limit.Should().BeGreaterThan(4L * 1024 * 1024 * 3 * 4 / 3,
            "the ceiling has to clear the base64-inflated images with room for the rest of the message");
    }

    [Fact]
    public async Task The_limit_scales_with_the_number_of_images_allowed()
    {
        await using var one = await ServerAppFactory.CreateAsync(o =>
        {
            o.EnableImageInput = true;
            o.MaxImageBytes = 2 * 1024 * 1024;
            o.MaxImagesPerMessage = 1;
        });
        await using var four = await ServerAppFactory.CreateAsync(o =>
        {
            o.EnableImageInput = true;
            o.MaxImageBytes = 2 * 1024 * 1024;
            o.MaxImagesPerMessage = 4;
        });

        LimitOf(four).Should().BeGreaterThan(LimitOf(one)!.Value,
            "allowing four attachments and sizing for one is the same bug with extra steps");
    }

    [Fact]
    public async Task A_zero_images_per_message_setting_still_leaves_room_for_one()
    {
        // Math.Max(1, …) guards this: 0 would compute a limit of just the headroom, and the
        // feature is on, so an image can still legitimately arrive.
        await using var app = await ServerAppFactory.CreateAsync(o =>
        {
            o.EnableImageInput = true;
            o.MaxImageBytes = 2 * 1024 * 1024;
            o.MaxImagesPerMessage = 0;
        });

        LimitOf(app).Should().BeGreaterThan(2L * 1024 * 1024 * 4 / 3);
    }

    [Fact]
    public async Task With_image_input_off_the_limit_is_left_at_the_framework_default()
    {
        // A text-only deployment gets no free pass to accept multi-megabyte invocations. The 32 KB
        // default is a reasonable ceiling when nothing large is expected.
        await using var app = await ServerAppFactory.CreateAsync(o => o.EnableImageInput = false);

        LimitOf(app).Should().Be(32 * 1024);
    }

    [Fact]
    public async Task A_host_that_set_a_larger_limit_itself_is_not_lowered()
    {
        // The raise is one-directional on purpose: a host with its own reason for a bigger ceiling
        // must not have MentorAgent quietly shrink it.
        const long hostLimit = 64L * 1024 * 1024;

        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.EnableImageInput = true;
                o.MaxImageBytes = 1024 * 1024;
                o.MaxImagesPerMessage = 1;
            },
            configureServices: services => services.Configure<HubOptions<MentorHub>>(
                h => h.MaximumReceiveMessageSize = hostLimit));

        LimitOf(app).Should().Be(hostLimit);
    }

    [Fact]
    public async Task A_host_that_removed_the_limit_entirely_keeps_it_removed()
    {
        // null means unlimited. The `is { } current` pattern is what preserves it — a plain
        // comparison would treat null as zero and clamp an explicitly unlimited hub.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.EnableImageInput = true;
                o.MaxImageBytes = 1024 * 1024;
                o.MaxImagesPerMessage = 1;
            },
            configureServices: services => services.Configure<HubOptions<MentorHub>>(
                h => h.MaximumReceiveMessageSize = null));

        LimitOf(app).Should().BeNull();
    }

    [Fact]
    public async Task The_per_hub_options_are_actually_registered_and_not_silently_ignored()
    {
        // The AddHubOptions<MentorHub>() flag. Without it SignalR reads the global HubOptions and
        // every value computed above is dead configuration — which presents identically to the
        // limit never having been raised at all.
        await using var app = await ServerAppFactory.CreateAsync(o =>
        {
            o.EnableImageInput = true;
            o.MaxImageBytes = 8 * 1024 * 1024;
            o.MaxImagesPerMessage = 1;
        });

        var perHub = app.Services.GetRequiredService<IOptions<HubOptions<MentorHub>>>().Value;
        var global = app.Services.GetRequiredService<IOptions<HubOptions>>().Value;

        perHub.MaximumReceiveMessageSize.Should().NotBe(global.MaximumReceiveMessageSize,
            "if the two agree at the default, the per-hub options were never applied");
        perHub.MaximumReceiveMessageSize.Should().BeGreaterThan(8L * 1024 * 1024);
    }
}
