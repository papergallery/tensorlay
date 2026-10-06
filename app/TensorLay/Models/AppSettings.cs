using System.IO;

namespace TensorLay.Models;

public class AppSettings
{
    public string VpsHost { get; set; } = "";
    public string VpsUser { get; set; } = "root";
    public int SshPort { get; set; } = 22;
    public string SshKeyPath { get; set; } = "";
    public string InstallDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ai-hub");
    public bool AutostartWithWindows { get; set; }
    public bool AutoconnectTunnel { get; set; }
    public Dictionary<string, bool> EnabledServices { get; set; } = new();

    // Pairing state
    public bool IsPaired { get; set; }

    // SHA256 fingerprints of the VPS's SSH host keys, captured at /pair time
    // (TOFU). SshTunnelService verifies the negotiated host key against this
    // list on every Connect — empty list means "not yet pinned, accept the
    // first observation" (legacy clients upgrading from < 0.8.0).
    public List<string> SshHostKeyFingerprints { get; set; } = new();

    // v0.9.0 — remote install requests.
    //
    // Master switch for the polling loop. Default OFF so a fresh pairing
    // doesn't silently grant the VPS the ability to queue downloads — user
    // has to flip this in Settings explicitly. Off ⇒ RemoteTaskService
    // never polls, the relay queue is invisible.
    public bool AllowRemoteInstallRequests { get; set; } = false;

    // Bearer token for /api/tasks/* on the relay. Issued at /pair time
    // (rotated on every re-pair). DPAPI-encrypted at rest by SettingsService
    // — the value on disk is "DPAPI:<base64>", decrypted only on Load().
    // Empty if the relay is < v1.3.0 (the field is absent from PairResponse
    // and JSON deserialization leaves it at default).
    public string RemoteTasksToken { get; set; } = "";

    // "Always reject from this source" persists across restarts so the user
    // doesn't see the same modal again after dismissing it. Compared against
    // RemoteTask.AgentLabel.
    public List<string> RejectedAgentLabels { get; set; } = new();

    // v1.5.0 — remote log retrieval. Off by default: logs may contain file
    // paths, model names, error messages with paths embedded; user has to
    // explicitly opt in. When ON, RemoteLogService responds to relay's
    // /api/logs/pending by bundling recent downloads-*.log files + crash.log
    // and uploading them. When OFF, every request is auto-rejected with a
    // reason the agent can read via /api/logs/{id}/info.
    public bool AllowRemoteLogRequests { get; set; } = false;

    // v0.9.27 / relay v1.6.0 — remote model cleanup. Off by default. When ON,
    // RemoteCleanupService pushes the list of installed model files (names,
    // sizes, models folder paths, free disk space) to the relay and surfaces
    // agent deletion requests in an approval window; nothing is deleted
    // without a click there. When OFF, no inventory is sent and deletion
    // requests are auto-rejected with a reason.
    public bool AllowRemoteModelCleanup { get; set; } = false;

    // v0.9.28 — command bridge ("super-bridge", modelled on the thechest
    // Unity MCP bridge). Off by default and the most powerful switch in the
    // app: when ON, a local HTTP server binds 127.0.0.1:<RemoteCommandBridge-
    // Port> and the running SSH tunnel forwards that port to the VPS, so an
    // agent on the VPS can run commands, move files, and start/stop services
    // on THIS PC. That is remote code execution on your machine by design —
    // it only runs while the app is open, only while this toggle is on, and
    // every request must carry the bearer token below. Treat the token like
    // a password; regenerating it in Settings instantly locks out the old one.
    public bool AllowRemoteCommandBridge { get; set; } = false;

    // Bearer token the VPS client must send on every bridge request
    // (Authorization: Bearer <token>). Generated locally the first time the
    // bridge is enabled, DPAPI-encrypted at rest like RemoteTasksToken. The
    // matching value lives on the VPS in ~/.tensorlay_bridge_token. Empty
    // means "no token issued yet" — the bridge refuses every request until
    // one exists.
    public string RemoteCommandBridgeToken { get; set; } = "";

    // Local port the bridge HTTP server binds (127.0.0.1 only) and the tunnel
    // forwards. Defaults to 6520 — distinct from the thechest Unity MCP bridge
    // on 6510 so both can ride the same machine. Must be in the relay's
    // permitlisten whitelist (see relay.py BRIDGE_PORT); changing it needs a
    // matching relay change + re-pair.
    public int RemoteCommandBridgePort { get; set; } = 6520;
}
