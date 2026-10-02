using System.Security.Cryptography;

namespace Notch.Core.Updates;

/// <summary>
/// Signatures on installers, so an update is trusted because the maintainer signed it and not only
/// because it came from GitHub. ECDSA with P-256 and SHA-256, which .NET has built in. A signature
/// is the base64 of the 64-byte (r, s) pair, kept in a <c>.sig</c> file beside the installer.
/// </summary>
public static class UpdateSigning
{
    /// <summary>
    /// The key official installers are signed with, as the base64 of an X.509 SubjectPublicKeyInfo.
    /// Empty until the maintainer makes a key pair with <c>installer/new-update-key.ps1</c> and pastes
    /// the public half here; while it is empty, updates are checked against the size and digest GitHub
    /// lists for them, as before. Once it is set, an installer without a valid signature is never run.
    /// </summary>
    public const string PublicKey = "";

    public static bool IsConfigured(string? publicKey = PublicKey) => !string.IsNullOrWhiteSpace(publicKey);

    /// <summary>A new key pair: the public key to embed in the app and the private key to keep as a build secret.</summary>
    public static (string PublicKey, string PrivateKey) NewKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
    }

    /// <summary>Signs a file. Used by the build, never by the app.</summary>
    public static string Sign(string path, string privateKey)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey.Trim()), out _);
        using FileStream file = File.OpenRead(path);
        return Convert.ToBase64String(key.SignData(file, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>Whether <paramref name="signature"/> is the public key's holder's signature of the file. False for anything malformed.</summary>
    public static bool Verify(string path, string? signature, string publicKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            byte[] bytes = Convert.FromBase64String(signature.Trim());
            if (bytes.Length != 64)
            {
                return false;
            }

            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            using FileStream file = File.OpenRead(path);
            return key.VerifyData(file, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
