using System.Security.Cryptography;

namespace Debloater.Core.Updates;

/// <summary>A public key trusted to sign catalog bundles (ECDSA P-256, SubjectPublicKeyInfo PEM).</summary>
public sealed record TrustedKey(string Name, string PublicKeyPem)
{
    /// <summary>Short key id: the first 8 bytes (hex) of SHA-256 over the SubjectPublicKeyInfo.</summary>
    public string Id => CatalogSignature.KeyId(PublicKeyPem);
}

/// <summary>
/// Signature scheme of catalog bundles: ECDSA P-256 over SHA-256 of the exact zip bytes, DER-encoded
/// (RFC 3279, the format of <c>openssl dgst -sha256 -sign</c>), stored as the binary file
/// <c>catalog-&lt;version&gt;.zip.sig</c>.
/// </summary>
public static class CatalogSignature
{
    /// <summary>Upper bound for a .sig file (a DER P-256 signature is at most 72 bytes).</summary>
    public const int MaxSignatureSize = 1024;

    /// <summary>
    /// Keys trusted by this build. Several keys may be listed during a rotation: add the new key, publish with
    /// it, and remove the old one in a later release (see the key-rotation section of the docs).
    /// </summary>
    public static IReadOnlyList<TrustedKey> TrustedKeys { get; } =
    [
        // Key id 6df85a7d86ca89c0; private key in the CATALOG_SIGNING_KEY repository secret.
        new("catalog-2026-10", """
            -----BEGIN PUBLIC KEY-----
            MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgvvymslDW56z0dMNjxCc1CKqyujz
            XQybpwjb4bwAj0PqAzevqpNzAIp8+MWsPxCK7Khc1pREqHhMAhiyfZShHg==
            -----END PUBLIC KEY-----
            """),
    ];

    public static byte[] Sign(ReadOnlySpan<byte> data, ECDsa privateKey)
    {
        if (privateKey.KeySize != 256) throw new ArgumentException("the catalog signing key must be ECDSA P-256.");
        return privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>The trusted key that verifies <paramref name="signature"/> over <paramref name="data"/>, or null.</summary>
    public static TrustedKey? Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, IEnumerable<TrustedKey>? keys = null)
    {
        if (signature.Length is 0 or > MaxSignatureSize) return null;
        foreach (var key in keys ?? TrustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportFromPem(key.PublicKeyPem);
            }
            catch (ArgumentException)
            {
                continue; // unusable key entry
            }
            catch (CryptographicException)
            {
                continue;
            }
            if (ecdsa.KeySize != 256) continue;
            try
            {
                if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    return key;
            }
            catch (CryptographicException)
            {
                // malformed signature for this key: try the next one
            }
        }
        return null;
    }

    public static string KeyId(string publicKeyPem)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            var spki = ecdsa.ExportSubjectPublicKeyInfo();
            return Convert.ToHexStringLower(SHA256.HashData(spki).AsSpan(0, 8));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return "invalid";
        }
    }

    /// <summary>Public key (SubjectPublicKeyInfo PEM) of a private key.</summary>
    public static string PublicPem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();
}
