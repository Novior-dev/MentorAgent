using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Novior.MentorAgent.Server.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Novior.MentorAgent.Server.Extensions;

/// <summary>
/// What <c>/mentor/session</c> hands a client, and what it accepts back (BUG-110).
///
/// <para>
/// The core snapshot is the conversation in clear, and until rc.13 the endpoint returned it as it was:
/// a client could read it, edit it — a tampered transcript restored the tampered codes, and a forged tool
/// result or approval would be history the model trusts — and restore another user's snapshot into its
/// own connection, which the ownership check on the CONNECTION did not see. Driven live on the published
/// rc.13, all three.
/// </para>
///
/// <para>
/// So the snapshot travels protected with ASP.NET Core Data Protection, under a purpose that names the
/// connection's owner: it is opaque to the client, any edit breaks it, and it restores only for the user
/// it was saved for, on this application's key ring. Restoring across a restart or a second instance
/// needs the key ring persisted and shared — the same condition as the host's authentication cookies.
/// In-process callers of <c>IMentorOrchestrator</c> get the core snapshot as before: they are the host.
/// </para>
/// </summary>
internal static class SessionSnapshotProtection
{
    // Not a namespace: the marker, and the purpose in For below, are in every snapshot a client has saved
    // since rc.14. Changed, they make all of them unreadable; keep them on a rename (ContractNameTests).
    internal const string MarkerProperty = "mentorAgentServerSession";
    internal const string ProtectedProperty = "protected";
    private const int FormatVersion = 1;

    /// <summary>The one answer for a snapshot that cannot be read, whatever the reason: no oracle.</summary>
    internal const string Unreadable =
        "The snapshot could not be read: it was changed, saved for another user or by another application, " +
        "or the keys that protected it are gone. The current conversation has been left untouched.";

    /// <summary>The protector for snapshots of a connection owned by <paramref name="owner"/>.</summary>
    internal static IDataProtector For(IServiceProvider services, ClaimsPrincipal? owner)
        => services.GetRequiredService<IDataProtectionProvider>()
                   .CreateProtector("MentorAgent.Server.SessionSnapshot", "v1", OwnerKey(owner));   // a purpose, not a namespace

    /// <summary>The owner's key — the one the ownership check compares (BUG-130) — or "anonymous".</summary>
    internal static string OwnerKey(ClaimsPrincipal? owner)
        => PrincipalKey.Of(owner) ?? "anonymous";

    /// <summary>Wraps a core snapshot for the client.</summary>
    internal static JsonElement Protect(JsonElement snapshot, IDataProtector protector)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [MarkerProperty] = FormatVersion,
            [ProtectedProperty] = protector.Protect(snapshot.GetRawText()),
        });

    /// <summary>
    /// Unwraps what a client sent back into the core snapshot, or explains, in words fit for a 400, why
    /// it cannot.
    /// </summary>
    internal static bool TryUnprotect(JsonElement body, IDataProtector protector, out JsonElement snapshot, out string error)
    {
        snapshot = default;

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty(MarkerProperty, out var marker)
            || !body.TryGetProperty(ProtectedProperty, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            error = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("mentorAgentSession", out _)
                ? "This snapshot was saved unprotected, before 1.0.0-rc.14, and is no longer accepted. Save the " +
                  "conversation again with GET /mentor/session. The current conversation has been left untouched."
                : "This is not a snapshot this server issued. Pass back exactly what GET /mentor/session " +
                  "returned. The current conversation has been left untouched.";
            return false;
        }

        if (marker.ValueKind != JsonValueKind.Number || !marker.TryGetInt32(out var version) || version != FormatVersion)
        {
            error = $"Snapshot format {marker} cannot be read by this version (expected {FormatVersion}). " +
                    "The current conversation has been left untouched.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(protector.Unprotect(value.GetString()!));
            snapshot = document.RootElement.Clone();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            error = Unreadable;
            return false;
        }
    }
}
