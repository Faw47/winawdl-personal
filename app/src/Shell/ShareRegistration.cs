using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Windows.Management.Deployment;

namespace Winawdl.Shell;

/// <summary>Puts AirDrop in Windows' Share sheet: the package in installer\package
/// gives winawdl-share.exe, beside winawdl.exe, the identity a share target needs.
/// install.ps1 signs it with the certificate it makes on the PC; each account that
/// should see AirDrop there registers it for itself, as with Explorer's verbs.</summary>
public static class ShareRegistration
{
    public const string SignFlag = "--share-sign", InstallFlag = "--share-install", RemoveFlag = "--share-remove";
    public const string PackageName = "winawdl";
    public const string Publisher = "CN=winawdl driver (signed on this PC)";
    public static string PackagePath(string folder) => Path.Combine(folder, "package", "winawdl.msix");

    /// <summary>Signs the package with a certificate in the current user's store, by
    /// thumbprint. Windows' own signer, as signtool uses, which every PC has.</summary>
    public static int Sign(string package, string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        using var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false)
            .OfType<X509Certificate2>().FirstOrDefault();
        if (certificate is null || !certificate.HasPrivateKey || certificate.Subject != Publisher) return 1;
        return Signer.Sign(Path.GetFullPath(package), certificate.Handle) == 0 ? 0 : 1;
    }

    public static int Register(bool enabled)
    {
        try
        {
            var manager = new PackageManager();
            // Whatever was there before is removed first: each install signs anew,
            // and Windows will not take the same version with a new signature.
            foreach (var package in manager.FindPackagesForUser("", PackageName, Publisher))
                Wait(manager.RemovePackageAsync(package.Id.FullName));
            if (!enabled) return 0;
            var folder = AppContext.BaseDirectory;
            var options = new AddPackageOptions { ExternalLocationUri = new Uri(folder) };
            Wait(manager.AddPackageByUriAsync(new Uri(PackagePath(folder)), options));
            return 0;
        }
        catch (Exception error)
        {
            // Run by the installer: any failure is an exit code it reports, not a crash.
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void Wait(Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation)
    {
        var result = operation.AsTask().GetAwaiter().GetResult();
        if (result.ExtendedErrorCode is { } error) throw new InvalidOperationException(result.ErrorText, error);
    }

    /// <summary>SignerSignEx2 with the package signer's state, as Microsoft's
    /// "How to programmatically sign an app package" does.</summary>
    private static class Signer
    {
        private const uint SubjectFile = 1, CertStore = 2, CertPolicyChain = 2, NoAttributes = 0;
        private const uint Sha256 = 0x0000800c;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfo { public uint Size; public IntPtr Name; public IntPtr Handle; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SubjectInfo { public uint Size; public IntPtr Index; public uint Choice; public IntPtr File; }
        [StructLayout(LayoutKind.Sequential)]
        private struct StoreInfo { public uint Size; public IntPtr Certificate; public uint Policy; public IntPtr Store; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Cert { public uint Size; public uint Choice; public IntPtr Store; public IntPtr Window; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SignatureInfo
        {
            public uint Size; public uint Hash; public uint AttributeChoice; public IntPtr Attributes;
            public IntPtr Authenticated; public IntPtr Unauthenticated;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SignParams
        {
            public uint Flags; public IntPtr Subject; public IntPtr Cert; public IntPtr Signature; public IntPtr Provider;
            public uint TimestampFlags; public IntPtr TimestampOid; public IntPtr TimestampUrl; public IntPtr Attributes;
            public IntPtr SipData; public IntPtr Context; public IntPtr Policy; public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct AppxSipClientData { public IntPtr Params; public IntPtr AppxSipState; }

        [DllImport("mssign32.dll", ExactSpelling = true)]
        private static extern int SignerSignEx2(uint flags, IntPtr subject, IntPtr cert, IntPtr signature, IntPtr provider,
            uint timestampFlags, IntPtr timestampOid, IntPtr timestampUrl, IntPtr attributes, IntPtr sipData,
            IntPtr context, IntPtr policy, IntPtr reserved);

        [DllImport("mssign32.dll", ExactSpelling = true)]
        private static extern int SignerFreeSignerContext(IntPtr context);

        public static int Sign(string path, IntPtr certificate)
        {
            var blocks = new List<IntPtr>();
            IntPtr Put<T>(T value) where T : struct
            {
                var block = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
                Marshal.StructureToPtr(value, block, false);
                blocks.Add(block);
                return block;
            }
            var name = Marshal.StringToHGlobalUni(path);
            blocks.Add(name);
            var sipData = IntPtr.Zero;
            try
            {
                var index = Put(0u);
                var file = Put(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Name = name });
                var subject = Put(new SubjectInfo
                    { Size = (uint)Marshal.SizeOf<SubjectInfo>(), Index = index, Choice = SubjectFile, File = file });
                var storeInfo = Put(new StoreInfo
                    { Size = (uint)Marshal.SizeOf<StoreInfo>(), Certificate = certificate, Policy = CertPolicyChain });
                var cert = Put(new Cert { Size = (uint)Marshal.SizeOf<Cert>(), Choice = CertStore, Store = storeInfo });
                var signature = Put(new SignatureInfo
                    { Size = (uint)Marshal.SizeOf<SignatureInfo>(), Hash = Sha256, AttributeChoice = NoAttributes });
                var context = Put(IntPtr.Zero);
                sipData = Put(new AppxSipClientData());
                var parameters = Put(new SignParams
                    { Subject = subject, Cert = cert, Signature = signature, SipData = sipData, Context = context });
                Marshal.StructureToPtr(new AppxSipClientData { Params = parameters }, sipData, false);
                var result = SignerSignEx2(0, subject, cert, signature, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero,
                                           IntPtr.Zero, sipData, context, IntPtr.Zero, IntPtr.Zero);
                if (Marshal.ReadIntPtr(context) is var made && made != IntPtr.Zero) SignerFreeSignerContext(made);
                return result;
            }
            finally
            {
                if (sipData != IntPtr.Zero && Marshal.PtrToStructure<AppxSipClientData>(sipData).AppxSipState is var state
                    && state != IntPtr.Zero) Marshal.Release(state);
                foreach (var block in blocks) Marshal.FreeHGlobal(block);
            }
        }
    }
}
