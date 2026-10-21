using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// A hand-written tour can be a scoped service, for example one that follows a language picked per visitor, as the
/// demo's does. The widget resolves <see cref="IMentorTour"/> in its own scope; if a singleton of MentorAgent took it
/// in its constructor, a Development host (which validates scopes) would stop at startup, and any other host would
/// give every visitor the tour of whoever came first.
/// </summary>
public sealed class OnboardingTourScopeTests
{
    private sealed class VisitorTour : IMentorTour
    {
        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MentorTourStep>>([new("Welcome", "A step written by hand.")]);
    }

    [Fact]
    public async Task A_scoped_tour_registered_before_AddMentorAgent_is_kept_and_resolved_per_scope()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        var services = builder.Services;
        services.AddScoped<IMentorTour, VisitorTour>();
        services.AddMentorAgent(o =>
        {
            o.ChatClient = new FakeChatClient();
            o.ScanAssemblies = [typeof(TestOrderService).Assembly];
            o.EnableOnboardingTour = true;
            o.MemoryAutoCapture = false;
        });

        services.Where(d => d.ServiceType == typeof(IMentorTour)).Should().ContainSingle()
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped, "AddMentorAgent adds its own tour only when none is registered");

        // A Development host validates scopes and every registration when it is built: a singleton that depends on a
        // scoped service stops it here.
        await using var app = builder.Build();

        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();
        var tour = first.ServiceProvider.GetRequiredService<IMentorTour>();
        tour.Should().BeOfType<VisitorTour>();
        second.ServiceProvider.GetRequiredService<IMentorTour>().Should().NotBeSameAs(tour, "each visitor gets their own");
    }
}
