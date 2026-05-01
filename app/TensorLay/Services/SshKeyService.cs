using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TensorLay.Services;

public class SshKeyService
{
    private readonly string _keyDir;

    // Two key formats — ed25519 (preferred, generated via Windows ssh-keygen)
    // and RSA-4096 (fallback when ssh-keygen isn't on PATH). Each gets its
    // own filename so the file extension matches its actual contents — the
    // previous version wrote RSA bytes into a file called id_ed25519, which
    // confused both humans and any tool inspecting %APPDATA%.
    private readonly string _ed25519PrivatePath;
    private readonly string _ed25519PublicPath;
    private readonly string _rsaPrivatePath;
    private readonly string _rsaPublicPath;

    public SshKeyService()
    {
        _keyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TensorLay");
        _ed25519PrivatePath = Path.Combine(_keyDir, "id_ed25519");
        _ed25519PublicPath = Path.Combine(_keyDir, "id_ed25519.pub");
        _rsaPrivatePath = Path.Combine(_keyDir, "id_rsa");
        _rsaPublicPath = Path.Combine(_keyDir, "id_rsa.pub");
    }

    /// <summary>
    /// Synchronous version. Prefer <see cref="GetOrCreateKeyPathAsync"/>
    /// from UI code — RSA-4096 generation can take 1-3 seconds on slow
    /// hardware and freezes the WPF dispatcher.
    /// </summary>
    public string GetOrCreateKeyPath()
    {
        // Existing keys (whichever format was generated last time)
        if (File.Exists(_ed25519PrivatePath) && File.Exists(_ed25519PublicPath))
            return _ed25519PrivatePath;
        if (File.Exists(_rsaPrivatePath) && File.Exists(_rsaPublicPath))
            return _rsaPrivatePath;

        Directory.CreateDirectory(_keyDir);

        // ssh-keygen ships with Windows 10 1803+ and is the right tool for
        // ed25519 (BCL has no equivalent that produces an OpenSSH-format key).
        if (TryGenerateEd25519())
            return _ed25519PrivatePath;

        // Fallback: RSA-4096 via .NET (Renci.SshNet PrivateKeyFile reads PEM).
        GenerateRsaKey();
        return _rsaPrivatePath;
    }

    public Task<string> GetOrCreateKeyPathAsync() => Task.Run(GetOrCreateKeyPath);

    public string GetPublicKey()
    {
        GetOrCreateKeyPath();
        if (File.Exists(_ed25519PublicPath))
            return File.ReadAllText(_ed25519PublicPath).Trim();
        if (File.Exists(_rsaPublicPath))
            return File.ReadAllText(_rsaPublicPath).Trim();
        return "";
    }

    public Task<string> GetPublicKeyAsync() => Task.Run(GetPublicKey);

    private bool TryGenerateEd25519()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh-keygen",
                Arguments = $"-t ed25519 -f \"{_ed25519PrivatePath}\" -N \"\" -q",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            return proc?.ExitCode == 0
                && File.Exists(_ed25519PrivatePath)
                && File.Exists(_ed25519PublicPath);
        }
        catch
        {
            return false;
        }
    }

    private void GenerateRsaKey()
    {
        using var rsa = RSA.Create(4096);
        var privateKey = rsa.ExportRSAPrivateKey();

        // Write private key in PEM format (PKCS#1 RSA — Renci.SshNet PrivateKeyFile reads this).
        var privatePem = "-----BEGIN RSA PRIVATE KEY-----\n"
            + Convert.ToBase64String(privateKey, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END RSA PRIVATE KEY-----\n";
        File.WriteAllText(_rsaPrivatePath, privatePem);

        // Write public key in OpenSSH RFC 4253 SSH wire format
        // (length-prefixed "ssh-rsa", exponent, modulus — all big-endian).
        var sshPub = BuildSshRsaPublicKey(rsa);
        File.WriteAllText(_rsaPublicPath, sshPub);
    }

    private static string BuildSshRsaPublicKey(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false); // public only
        using var ms = new MemoryStream();
        WriteSshString(ms, "ssh-rsa");
        WriteSshMpint(ms, parameters.Exponent!);
        WriteSshMpint(ms, parameters.Modulus!);
        return $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} tensorlay@{Environment.MachineName}";
    }

    private static void WriteSshString(Stream s, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUint32BE(s, (uint)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteSshMpint(Stream s, byte[] value)
    {
        // SSH mpint: prepend 0x00 if high bit of MSB is set (to keep it positive)
        if (value.Length > 0 && (value[0] & 0x80) != 0)
        {
            WriteUint32BE(s, (uint)(value.Length + 1));
            s.WriteByte(0);
            s.Write(value, 0, value.Length);
        }
        else
        {
            WriteUint32BE(s, (uint)value.Length);
            s.Write(value, 0, value.Length);
        }
    }

    private static void WriteUint32BE(Stream s, uint v)
    {
        s.WriteByte((byte)((v >> 24) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }
}
