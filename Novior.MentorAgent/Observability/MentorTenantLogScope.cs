using System.Collections;

namespace Novior.MentorAgent.Observability;

/// <summary>
/// Story 157 — the state of the log scope that names the tenant: one pair, <c>mentoragent.tenant</c>, the key of the span
/// and metric tag (<see cref="MentorTelemetry.TenantTag"/>), so an OpenTelemetry exporter writes it as the same attribute.
/// A provider that writes scopes as text prints <c>mentoragent.tenant=…</c>.
///
/// <para>
/// A scope follows the flow it is begun in, through every logger of the application's factory: a turn's scope is on
/// every line the turn writes, the application's own included, and on the work it starts. It is seen only by a provider
/// that includes scopes (the console's <c>IncludeScopes</c>, OpenTelemetry's <c>IncludeScopes</c>, and the like).
/// </para>
/// </summary>
internal sealed class MentorTenantLogScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    private MentorTenantLogScope(string tenant) => Tenant = tenant;

    internal string Tenant { get; }

    /// <summary>
    /// The scope of <paramref name="tenant"/> on <paramref name="logger"/>, until it is disposed; none for a single tenant,
    /// so an application without a resolver pays nothing.
    /// </summary>
    internal static IDisposable? Begin(ILogger logger, string? tenant)
        => tenant is null ? null : logger.BeginScope(new MentorTenantLogScope(tenant));

    public KeyValuePair<string, object?> this[int index]
        => index == 0 ? new(MentorTelemetry.TenantTag, Tenant) : throw new ArgumentOutOfRangeException(nameof(index));

    public int Count => 1;

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return this[0];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{MentorTelemetry.TenantTag}={Tenant}";
}
