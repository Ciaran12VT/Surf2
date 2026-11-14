using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

/// <summary>Secrets are protected for this Windows user and bound to the exact source configuration.</summary>
public sealed class FolderCredentialStore
{
    private readonly string _root;
    public FolderCredentialStore(string? root = null) => _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Surf2", "SourceCredentials");
    public void Save(ResourceSourceDefinition source, string secret)
    {
        Directory.CreateDirectory(_root);
        string key = FolderSourcePaths.ConfigurationKey(source);
        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            byte[] cipher = ProtectedData.Protect(plaintext, Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser);
            string path = Path.Combine(_root, key + ".bin"), pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(pending, cipher);
            File.Move(pending, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public string? Read(ResourceSourceDefinition source)
    {
        string key = FolderSourcePaths.ConfigurationKey(source), path = Path.Combine(_root, key + ".bin");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("The local authentication binding is invalid.");
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Remove(ResourceSourceDefinition source) => File.Delete(Path.Combine(_root, FolderSourcePaths.ConfigurationKey(source) + ".bin"));
}
