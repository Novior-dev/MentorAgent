namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Handles a command: a request that changes data, written as a type of its own, usually a record. Registered with
/// <c>MentorOptions.Actions.AddCommand&lt;TCommand, THandler&gt;()</c>, the command is one of the coordinator's actions,
/// named after <typeparamref name="TCommand"/> in snake_case: the model gives the command's properties as the action's
/// arguments, and MentorAgent passes the command it built from them to <see cref="HandleAsync"/>. A command is a write,
/// so the default confirmation for writes applies to it.
/// <para>
/// The handler is a service the application registers itself. An application that already sends its commands through
/// a dispatcher, MediatR for example, writes a handler that sends the command on.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command: a type whose public properties are what the model gives.</typeparam>
public interface IMentorCommandHandler<in TCommand> where TCommand : notnull
{
    /// <summary>Runs <paramref name="command"/>. An exception fails the action, and the model reads that it failed.</summary>
    /// <param name="command">The command, built from the model's arguments.</param>
    /// <param name="cancellationToken">Cancelled when the turn is.</param>
    Task HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Handles a command that returns a result: a request that changes data, written as a type of its own, whose result
/// the model reads as an action's. Registered with <c>MentorOptions.Actions.AddCommand&lt;TCommand, THandler&gt;()</c>,
/// as <see cref="IMentorCommandHandler{TCommand}"/>.
/// </summary>
/// <typeparam name="TCommand">The command: a type whose public properties are what the model gives.</typeparam>
/// <typeparam name="TResult">What the command returns, given to the model as JSON.</typeparam>
public interface IMentorCommandHandler<in TCommand, TResult> where TCommand : notnull
{
    /// <summary>Runs <paramref name="command"/>. An exception fails the action, and the model reads that it failed.</summary>
    /// <param name="command">The command, built from the model's arguments.</param>
    /// <param name="cancellationToken">Cancelled when the turn is.</param>
    /// <returns>The result the model reads.</returns>
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
