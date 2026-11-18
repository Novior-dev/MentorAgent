namespace Novior.MentorAgent.Attributes;

/// <summary>
/// Keeps a class or a method out of the AutoMentor's candidates (<see cref="Options.MentorAutoMentorOptions"/>): what
/// carries it never becomes an action by discovery, whatever its namespace, and it is not listed in the trace either
/// (<c>candidate.excluded</c>). On a class it covers every method of the class and of the classes derived from it.
/// It does not change an action declared with <c>[MentorAction]</c>, <c>[Description]</c> or in code.
/// <code>
/// [MentorExclude]
/// public sealed class PaymentGateway { … }
///
/// public sealed class OrderService
/// {
///     public Order Find(int id) { … }
///
///     [MentorExclude]
///     public void Purge() { … }
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class MentorExcludeAttribute : Attribute;
