namespace TensorLay.Services;

// The pairing handshake behind the "Connect to VPS" dialog, shared by both
// front ends so they persist exactly the same fields: relay health check ->
// SSH key -> POST /pair -> save host, user, port, key path, pinned host keys
// and the remote-tasks token. The dialog only collects input and shows the
// status lines this reports.
public static class PairingFlow
{
    // Returns true when pairing succeeded and settings were saved. Throws
    // OperationCanceledException when `ct` is cancelled mid-flight.
    public static async Task<bool> RunAsync(
        PairingService pairingService,
        SshKeyService sshKeyService,
        SettingsService settingsService,
        string vpsIp,
        string code,
        Action<string, bool> setStatus,
        CancellationToken ct)
    {
        setStatus("Checking relay...", false);
        var healthy = await pairingService.CheckRelayHealthAsync(vpsIp, ct);
        if (!healthy)
        {
            setStatus("Cannot reach relay on port 8090. Is it installed?", true);
            return false;
        }

        setStatus("Generating SSH key...", false);
        string publicKey;
        string keyPath;
        try
        {
            // RSA-4096 fallback can take seconds — keep it off the UI thread.
            keyPath = await sshKeyService.GetOrCreateKeyPathAsync();
            publicKey = await sshKeyService.GetPublicKeyAsync();
        }
        catch (Exception ex)
        {
            setStatus($"Key error: {ex.Message}", true);
            return false;
        }

        setStatus("Pairing...", false);
        var result = await pairingService.PairAsync(vpsIp, code, publicKey, ct);
        if (!result.Success)
        {
            setStatus(result.Error, true);
            return false;
        }

        var settings = settingsService.Load();
        settings.VpsHost = vpsIp;
        settings.VpsUser = result.SshUser;
        settings.SshPort = result.SshPort;
        settings.SshKeyPath = keyPath;
        settings.IsPaired = true;
        settings.AutoconnectTunnel = true;
        // Pin the VPS's SSH host keys so any future Connect that negotiates
        // a different key (= different server) is rejected.
        settings.SshHostKeyFingerprints = result.HostKeyFingerprints ?? new List<string>();
        // v0.9.0 — store the rotated remote-tasks bearer. Empty when the
        // relay is older than 1.3.0; the polling loop checks for this and
        // stays idle until the user repairs against a newer relay. We don't
        // auto-enable AllowRemoteInstallRequests — user has to flip it in
        // Settings explicitly.
        settings.RemoteTasksToken = result.RemoteTasksToken ?? "";
        settingsService.Save(settings);

        setStatus("Paired successfully!", false);
        return true;
    }
}
