namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Handles a query: a request that only reads, written as a type of its own, usually a record. Registered with
/// <c>MentorOptions.Actions.AddQuery&lt;TQuery, THandler&gt;()</c>, the query is one of the coordinator's actions, named
/// after <typeparamref name="TQuery"/> in snake_case and always read-only: the model gives the query's properties as the
/// action's arguments, MentorAgent passes the query it built from them to <see cref="HandleAsync"/>, and the model reads
/// the result.
/// <para>
/// The handler is a service the application registers itself. An application that already sends its queries through a
/// dispatcher, MediatR for example, writes a handler that sends the query on.
/// </para>
/// </summary>
/// <typeparam name="TQuery">The query: a type whose public properties are what the model gives.</typeparam>
/// <typeparam name="TResult">What the query returns, given to the model as JSON.</typeparam>
public interface IMentorQueryHandler<in TQuery, TResult> where TQuery : notnull
{
    /// <summary>Answers <paramref name="query"/>. An exception fails the action, and the model reads that it failed.</summary>
    /// <param name="query">The query, built from the model's arguments.</param>
    /// <param name="cancellationToken">Cancelled when the turn is.</param>
    /// <returns>The result the model reads.</returns>
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
