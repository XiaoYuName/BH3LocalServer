using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BH3.Launcher;

internal sealed class CertificateService : IDisposable
{
    private X509Certificate2? root;
    private readonly Dictionary<string, X509Certificate2> leaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    internal X509Certificate2 Root
    {
        get
        {
            lock (gate)
            {
                if (root is not null) return root;
                string file = Path.Combine(AppPaths.Data, "local-root.protected");
                if (File.Exists(file)) root = X509CertificateLoader.LoadPkcs12(DataProtection.Transform(File.ReadAllBytes(file), false), null, X509KeyStorageFlags.EphemeralKeySet);
                else
                {
                    string? legacy = AppPaths.Workspace is { } w ? Path.Combine(w, "BH3LocalServer", "certs") : null;
                    if (legacy is not null && File.Exists(Path.Combine(legacy, "ca.crt")) && File.Exists(Path.Combine(legacy, "ca.key")))
                        root = X509Certificate2.CreateFromPemFile(Path.Combine(legacy, "ca.crt"), Path.Combine(legacy, "ca.key"));
                    else root = CreateRoot();
                    File.WriteAllBytes(file, DataProtection.Transform(root.Export(X509ContentType.Pfx), true));
                }
                if (root.NotAfter <= DateTime.Now) throw new InvalidOperationException("本地证书已过期，请备份后重建证书。");
                return root;
            }
        }
    }
    internal static X509Certificate2 CreateRoot()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=BH3 Local Desktop CA, O=BH3 Local Launcher", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }
    internal bool Installed
    {
        get { using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadOnly); return store.Certificates.Find(X509FindType.FindByThumbprint, Root.Thumbprint, false).Count > 0; }
    }
    internal void Install()
    {
        using var publicCert = X509CertificateLoader.LoadCertificate(Root.Export(X509ContentType.Cert));
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadWrite); store.Add(publicCert);
        if (!Installed) throw new InvalidOperationException("证书未安装完成。");
    }
    internal X509Certificate2 ForHost(string host)
    {
        lock (gate)
        {
            if (leaves.TryGetValue(host, out var cached)) return cached;
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(host);
            req.CertificateExtensions.Add(names.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var begins = DateTimeOffset.UtcNow.AddMinutes(-5);
            var expires = DateTimeOffset.UtcNow.AddMonths(3);
            if (begins < Root.NotBefore.ToUniversalTime()) begins = Root.NotBefore.ToUniversalTime();
            if (expires > Root.NotAfter.ToUniversalTime()) expires = Root.NotAfter.ToUniversalTime();
            using var issued = req.Create(Root, begins, expires, RandomNumberGenerator.GetBytes(16));
            using var withKey = issued.CopyWithPrivateKey(key);
            // Schannel requires a user key container. Without PersistKeySet the import is
            // automatically removed when the cached certificate is disposed.
            var cert = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
            leaves[host] = cert; return cert;
        }
    }
    public void Dispose() { lock (gate) { foreach (var c in leaves.Values) c.Dispose(); root?.Dispose(); } }
}

internal static class DataProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "BH3 local CA", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
