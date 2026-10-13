using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Extensions;

/// <summary>
/// The options as <c>AddMentorAgent</c> read them at registration time, left in the service collection
/// for registrations that run after it in another package.
///
/// <para>
/// Story 105: the MCP and A2A servers are registered by <c>Novior.MentorAgent.Server</c>, because their
/// transports need ASP.NET Core and this package must not. Whether to register them, and the name and
/// card they carry, are decided at registration time — before any <c>IOptions</c> exists — exactly as
/// <c>AddMentorAgent</c> decided them when it registered both servers itself.
/// </para>
/// </summary>
internal sealed class MentorRegistrationSnapshot(MentorOptions options, Exception? configureError = null)
{
    /// <summary>
    /// The options read at registration time, by name — what a configure callback that throws, or a
    /// <c>Configure&lt;MentorOptions&gt;</c> registered after <c>AddMentorAgent</c>, does not reach.
    /// </summary>
    internal const string RegistrationTimeOptions =
        "EnableObservability, ObservabilitySourceName, McpServerEnabled, A2AServerEnabled, " +
        "and AppName as the MCP server's name";

    /// <summary>The options after the host's configure callback, unvalidated.</summary>
    public MentorOptions Options { get; } = options;

    /// <summary>
    /// What the configure callback threw on the snapshot, if it did. The options it had not set yet
    /// kept their defaults here; reported as a warning when the host starts (story 116).
    /// </summary>
    public Exception? ConfigureError { get; } = configureError;
}
