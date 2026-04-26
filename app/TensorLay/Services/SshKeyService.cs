using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TensorLay.Services;

public class SshKeyService
{
    private readonly string _keyDir;
    private readonly string _privateKeyPath;
    private readonly string _publicKeyPath;

    public SshKeyService()
    {
        _keyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TensorLay");
        _privateKeyPath = Path.Combine(_keyDir, "id_ed25519");
        _publicKeyPath = Path.Combine(_keyDir, "id_ed25519.pub");
    }

    public string GetOrCreateKeyPath()
    {
        if (File.Exists(_privateKeyPath) && File.Exists(_publicKeyPath))
            return _privateKeyPath;

        Directory.CreateDirectory(_keyDir);

        // Try ssh-keygen first (available on Windows 10+)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh-keygen",
                Arguments = $"-t ed25519 -f \"{_privateKeyPath}\" -N \"\" -q",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            if (proc?.ExitCode == 0 && File.Exists(_privateKeyPath))
                return _privateKeyPath;
        }
        catch
        {
            // ssh-keygen not available, fall back to RSA
        }

        // Fallback: generate RSA key via .NET (SSH.NET can read PEM)
        GenerateRsaKey();
        return _privateKeyPath;
    }

    public string GetPublicKey()
    {
        GetOrCreateKeyPath();
        return File.Exists(_publicKeyPath)
            ? File.ReadAllText(_publicKeyPath).Trim()
            : "";
    }

    private void GenerateRsaKey()
    {
        using var rsa = RSA.Create(4096);
        var privateKey = rsa.ExportRSAPrivateKey();

        // Write private key in PEM format (PKCS#1 RSA — Renci.SshNet PrivateKeyFile reads this).
        var privatePem = "-----BEGIN RSA PRIVATE KEY-----\n"
            + Convert.ToBase64String(privateKey, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END RSA PRIVATE KEY-----\n";
        File.WriteAllText(_privateKeyPath, privatePem);

        // Write public key in OpenSSH RFC 4253 SSH wire format
        // (length-prefixed "ssh-rsa", exponent, modulus — all big-endian).
        var sshPub = BuildSshRsaPublicKey(rsa);
        File.WriteAllText(_publicKeyPath, sshPub);
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
