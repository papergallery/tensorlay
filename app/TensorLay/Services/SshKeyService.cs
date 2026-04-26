using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

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
        var publicKey = rsa.ExportRSAPublicKey();

        // Write private key in PEM format
        var privatePem = "-----BEGIN RSA PRIVATE KEY-----\n"
            + Convert.ToBase64String(privateKey, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END RSA PRIVATE KEY-----\n";
        File.WriteAllText(_privateKeyPath, privatePem);

        // Write public key in OpenSSH format
        var pubKeyBlob = rsa.ExportSubjectPublicKeyInfo();
        var sshPub = "ssh-rsa " + Convert.ToBase64String(pubKeyBlob) + " tensorlay@" + Environment.MachineName;
        File.WriteAllText(_publicKeyPath, sshPub);
    }
}
