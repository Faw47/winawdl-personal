using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Winawdl.Engine;

/// <summary>The certificate the receiver and the sender present. The engine
/// would make it with OpenSSL, which a PC that only installed the app may not
/// have; made here first, it is found there as if it had.</summary>
public static class Identity
{
    // The names and the subject airdrop/identity.py uses.
    public const string Certificate = "certificate.self-signed.pem";
    public const string Key = "key.self-signed.pem";
    private const string Subject = "CN=AirDrop receiver";
    private const int ValidDays = 365;

    public static string DefaultFolder => Path.Combine(Repo.DataFolder, "keys");

    /// <summary>Makes the pair in `folder` unless it is there. Says whether it is there now.</summary>
    public static bool Ensure(string folder)
    {
        var certificate = Path.Combine(folder, Certificate);
        var key = Path.Combine(folder, Key);
        if (File.Exists(certificate) && File.Exists(key)) return true;
        try
        {
            Directory.CreateDirectory(folder);
            using var pair = RSA.Create(2048);
            var request = new CertificateRequest(Subject, pair, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                      DateTimeOffset.UtcNow.AddDays(ValidDays));
            File.WriteAllText(key, pair.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(certificate, made.ExportCertificatePem());
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // The engine still tries its own way, and says so if that fails too.
            return false;
        }
    }
}
