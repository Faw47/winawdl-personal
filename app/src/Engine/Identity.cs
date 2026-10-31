using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

namespace Winawdl.Engine;

/// <summary>The certificate the receiver and the sender present. The engine
/// would make it with OpenSSL, which a PC that only installed the app may not
/// have; made here first, it is found there as if it had.</summary>
public static class Identity
{
    // The names and the subject airdrop/identity.py uses.
    public const string Certificate = "certificate.self-signed.pem";
    public const string Key = "key.self-signed.pem";
    public const string Secret = "key.self-signed.secret";
    private const string Subject = "CN=AirDrop receiver";
    private const int ValidDays = 365;

    public static string DefaultFolder => Path.Combine(Repo.DataFolder, "keys");

    /// <summary>Makes the pair in `folder` unless it is there. Says whether it is there now.</summary>
    public static bool Ensure(string folder, Action<string>? log = null)
    {
        var certificate = Path.Combine(folder, Certificate);
        var key = Path.Combine(folder, Key);
        try
        {
            Directory.CreateDirectory(folder);
            Restrict(folder);
            if (File.Exists(certificate) && File.Exists(key))
            {
                RSA? existing = null;
                try
                {
                    var pem = File.ReadAllText(key);
                    if (pem.Contains("BEGIN ENCRYPTED PRIVATE KEY"))
                    {
                        var password = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, Secret)),
                                                               null, DataProtectionScope.CurrentUser);
                        try
                        {
                            if (password.Length == 0) throw new CryptographicException("Empty identity secret");
                            using var loaded = X509Certificate2.CreateFromEncryptedPemFile(certificate,
                                                Encoding.ASCII.GetString(password), key);
                            return loaded.HasPrivateKey;
                        }
                        finally { CryptographicOperations.ZeroMemory(password); }
                    }
                    using var legacy = X509Certificate2.CreateFromPemFile(certificate, key);
                    existing = legacy.GetRSAPrivateKey();
                }
                catch (Exception error) when (error is IOException or CryptographicException or ArgumentException)
                {
                    log?.Invoke($"Stored AirDrop identity cannot be loaded ({error.GetType().Name}); creating a new identity");
                }
                if (existing is not null)
                {
                    using (existing) Store(folder, existing, null);
                    log?.Invoke("Stored AirDrop identity encrypted for the current Windows user");
                    return true;
                }
            }
            else if (File.Exists(key) || File.Exists(certificate) || File.Exists(Path.Combine(folder, Secret)))
                log?.Invoke("Stored AirDrop identity is incomplete; creating a new identity");
            using var pair = RSA.Create(2048);
            var request = new CertificateRequest(Subject, pair, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                      DateTimeOffset.UtcNow.AddDays(ValidDays));
            Store(folder, pair, made.ExportCertificatePem());
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // The engine still tries its own way, and says so if that fails too.
            log?.Invoke($"Could not protect the AirDrop identity ({error.GetType().Name})");
            return false;
        }
    }

    private static void Restrict(string folder)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var directory = new DirectorySecurity();
        directory.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        directory.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(directory);
        foreach (var name in new[] { Key, Certificate, Secret })
        {
            var file = new FileInfo(Path.Combine(folder, name));
            if (!file.Exists) continue;
            var access = new FileSecurity();
            access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(access);
        }
    }

    private static void Store(string folder, RSA pair, string? certificate)
    {
        var password = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        try
        {
            var pem = pair.ExportEncryptedPkcs8PrivateKeyPem(Encoding.ASCII.GetString(password),
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
            var secret = ProtectedData.Protect(password, null, DataProtectionScope.CurrentUser);
            Atomic(Path.Combine(folder, Secret), secret);
            // No plaintext temporary file: replace the old key only after protection succeeds.
            Atomic(Path.Combine(folder, Key), Encoding.ASCII.GetBytes(pem));
            if (certificate is not null)
                Atomic(Path.Combine(folder, Certificate), Encoding.ASCII.GetBytes(certificate));
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    private static void Atomic(string path, byte[] data)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
