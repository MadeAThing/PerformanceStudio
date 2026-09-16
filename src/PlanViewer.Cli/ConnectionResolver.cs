using PlanViewer.Core.Services;

namespace PlanViewer.Cli;

/// <summary>
/// Resolves a --server value against the GUI's saved connections (~/.planview/connections.json)
/// so the CLI can be pointed at a connection/display name instead of a bare hostname, and picks
/// up that connection's credential-store key (a GUID for GUI-saved connections) instead of
/// guessing the hostname is also the credential key.
/// </summary>
internal static class ConnectionResolver
{
    public record Resolved(string ServerName, string CredentialId, string? Database);

    public static Resolved Resolve(string serverInput)
    {
        var match = new ConnectionStore().Load().FirstOrDefault(c =>
            c.Id.Equals(serverInput, StringComparison.OrdinalIgnoreCase) ||
            c.ServerName.Equals(serverInput, StringComparison.OrdinalIgnoreCase) ||
            c.DisplayName.Equals(serverInput, StringComparison.OrdinalIgnoreCase));

        return match != null
            ? new Resolved(match.ServerName, match.Id, match.Database)
            : new Resolved(serverInput, serverInput, null);
    }
}
