using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VulcanScope;

/// <summary>
/// Request signing for the Hebe mobile API — a faithful port of the Python/hebece scheme.
/// For GET requests the signed value is canonicalUrl + vDate (no body, no digest).
/// </summary>
public static partial class Signing
{
    public const string UserAgent = "Dart/3.3 (dart:io)";
    public const string Os = "Android";
    public const string VersionCode = "621";
    public const string Vapi = "1";
    public const string DeviceModel = "Xiaomi MI 9";
    public const string Host = "lekcjaplus.vulcan.net.pl";

    [GeneratedRegex(@"(api/mobile/.+)")]
    private static partial Regex CanonRegex();

    public static string CanonicalUrl(string url)
    {
        var m = CanonRegex().Match(url);
        if (!m.Success)
            throw new ArgumentException($"URL does not contain an api/mobile/ path: {url}");
        // Mirrors Python quote(..., safe="").lower() — EscapeDataString encodes the same
        // unreserved set (A-Za-z0-9-._~) and we lowercase the whole thing (incl. %2f -> %2f).
        return Uri.EscapeDataString(m.Groups[1].Value).ToLowerInvariant();
    }

    /// <summary>Returns (canonicalUrl, vDate, signatureHeader) for a GET request.</summary>
    public static (string Canonical, string Date, string Signature) SignGet(
        string fingerprint, string privateKeyB64, string url)
    {
        var date = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture); // RFC1123 GMT
        var canonical = CanonicalUrl(url);
        var signValues = canonical + date; // headers order: vCanonicalUrl, vDate

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        var sig = rsa.SignData(Encoding.UTF8.GetBytes(signValues), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sigB64 = Convert.ToBase64String(sig);

        var header = $"keyId=\"{fingerprint}\",headers=\"vCanonicalUrl vDate\"," +
                     $"algorithm=\"sha256withrsa\",signature=Base64(sha256withrsa({sigB64}))";
        return (canonical, date, header);
    }
}
