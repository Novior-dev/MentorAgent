using Microsoft.AspNetCore.Http;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Endpoints;

namespace Novior.MentorAgent.Server.Extensions;

/// <summary>
/// Story 1287 — an endpoint of the application as an action of the coordinator, without writing it again:
/// <code>
/// app.MapPost("/orders/{id}/cancel", CancelOrder)
///    .WithName("CancelOrder")
///    .WithMentorAction(a => a.Describe("Cancels an order").RequireRoles("Manager"));
/// </code>
/// The action runs the endpoint itself, in the process, as the user of the turn: its authorization first, then its
/// filters, its binding and its validation. A controller's action is declared with <c>[MentorAction]</c> on its method.
/// Needs <c>AddMentorAgentServer()</c>.
/// </summary>
public static class MentorEndpointActionExtensions
{
    /// <summary>
    /// Declares the endpoint as an action of the coordinator, named after the endpoint's name (<c>WithName</c>) or its
    /// handler's method, in snake_case. A lambda without <c>WithName</c> needs <see cref="WithMentorAction{TBuilder}(TBuilder, string, Action{MentorActionBuilder})"/>.
    /// </summary>
    public static TBuilder WithMentorAction<TBuilder>(this TBuilder builder, Action<MentorActionBuilder> configure)
        where TBuilder : IEndpointConventionBuilder
        => Declare(builder, null, configure);

    /// <summary>
    /// Declares the endpoint as an action of the coordinator named <paramref name="name"/>, as a model's tool name:
    /// letters, digits, <c>_</c> and <c>-</c>, up to 64.
    /// </summary>
    public static TBuilder WithMentorAction<TBuilder>(this TBuilder builder, string name, Action<MentorActionBuilder> configure)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Declare(builder, name, configure);
    }

    private static TBuilder Declare<TBuilder>(TBuilder builder, string? name, Action<MentorActionBuilder> configure)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var action = new MentorActionBuilder();
        configure(action);
        builder.Add(endpoint => endpoint.Metadata.Add(new MentorEndpointActionMetadata(name, action)));
        return builder;
    }
}
