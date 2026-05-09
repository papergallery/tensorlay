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
}
