using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace VulcanScope;

/// <summary>
/// Headless account pairing — C# port of register.py (eduVulcan JWT-bridge flow).
/// Generates an RSA keypair + self-signed X.509 cert, registers it via /register/jwt,
/// fetches the pupil via /register/hebe, and writes credentials.json.
/// Login still happens in the user's browser; the /api/ap page is supplied via paste or --ap-file.
/// </summary>
public static partial class Register
{
    private const string BaseUrl = "https://lekcjaplus.vulcan.net.pl";
    private const string AppVersion = "25.02.14 (G)";

    [GeneratedRegex(@"id='ap'[^>]*value='([^']+)'")] private static partial Regex Ap1();
    [GeneratedRegex(@"value='([^']+)'[^>]*id='ap'")] private static partial Regex Ap2();
    [GeneratedRegex("id=\"ap\"[^>]*value=\"([^\"]+)\"")] private static partial Regex Ap3();
    [GeneratedRegex("value=\"([^\"]+)\"[^>]*id=\"ap\"")] private static partial Regex Ap4();

    // ---- crypto ----
    public static (string Fingerprint, string PrivateKey, string Certificate) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=APP_CERTIFICATE CA Certificate", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(20));
        var der = cert.RawData;
        var fingerprint = Convert.ToHexString(SHA1.HashData(der)).ToLowerInvariant();
        return (fingerprint, Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()), Convert.ToBase64String(der));
    }

    private static (string canonical, string date, string digest, string signature)
        SignPost(string fp, string pk, byte[] body, string url)
    {
        var date = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        var canonical = Signing.CanonicalUrl(url);
        var digest = Convert.ToBase64String(SHA256.HashData(body));
        var signValues = canonical + digest + date;
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pk), out _);
        var sig = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(signValues),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var header = $"keyId=\"{fp}\",headers=\"vCanonicalUrl Digest vDate\"," +
                     $"algorithm=\"sha256withrsa\",signature=Base64(sha256withrsa({sig}))";
        return (canonical, date, "SHA-256=" + digest, header);
    }

    private static void AddCommon(HttpRequestMessage req, string date)
    {
        void H(string k, string v) => req.Headers.TryAddWithoutValidation(k, v);
        H("accept", "*/*"); H("accept-charset", "UTF-8"); H("accept-encoding", "gzip"); H("connection", "Keep-Alive");
        H("user-agent", Signing.UserAgent); H("vapi", Signing.Vapi); H("vdate", date);
        H("vdevicemodel", Signing.DeviceModel); H("vos", Signing.Os); H("vversioncode", Signing.VersionCode);
    }

    private static async Task<JsonNode?> PostAsync(HttpClient http, string fp, string pk, string url, byte[] body)
    {
        var (canonical, date, digest, signature) = SignPost(fp, pk, body, url);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        AddCommon(req, date);
        req.Headers.TryAddWithoutValidation("signature", signature);
        req.Headers.TryAddWithoutValidation("vcanonicalurl", canonical);
        req.Headers.TryAddWithoutValidation("digest", digest);
        req.Content.Headers.TryAddWithoutValidation("content-type", "application/json");
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        if ((json.Int("Status", "Code") ?? 0) != 0)
            throw new HebeException(json.Int("Status", "Code") ?? -1, json.Str("Status", "Message"), url);
        return json["Envelope"];
    }

    private static async Task<JsonNode?> GetAsync(HttpClient http, string fp, string pk, string url)
    {
        var (canonical, date, signature) = Signing.SignGet(fp, pk, url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddCommon(req, date);
        req.Headers.TryAddWithoutValidation("content-type", "application/json");
        req.Headers.TryAddWithoutValidation("signature", signature);
        req.Headers.TryAddWithoutValidation("vcanonicalurl", canonical);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        if ((json.Int("Status", "Code") ?? 0) != 0)
            throw new HebeException(json.Int("Status", "Code") ?? -1, json.Str("Status", "Message"), url);
        return json["Envelope"];
    }

    public static JsonObject ParseAp(string html)
    {
        foreach (var re in new[] { Ap1(), Ap2(), Ap3(), Ap4() })
        {
            var m = re.Match(html);
            if (m.Success)
            {
                var raw = m.Groups[1].Value.Replace("&quot;", "\"").Replace("&#34;", "\"");
                return JsonNode.Parse(raw)!.AsObject();
            }
        }
        throw new InvalidOperationException("Nie znalazłem <input id='ap'> — czy to strona /api/ap po zalogowaniu?");
    }

    public static string JwtTenant(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return "";
        var p = parts[1].Replace('-', '+').Replace('_', '/');
        p += (p.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
        return json.Str("tenant");
    }

    private static byte[] BuildJwtBody(string fp, string cert, JsonArray tokens)
    {
        var now = DateTimeOffset.UtcNow;
        var body = new JsonObject
        {
            ["AppName"] = "DzienniczekPlus 3.0",
            ["AppVersion"] = AppVersion,
            ["Envelope"] = new JsonObject
            {
                ["OS"] = Signing.Os,
                ["DeviceModel"] = Signing.DeviceModel,
                ["Certificate"] = cert,
                ["CertificateType"] = "X509",
                ["CertificateThumbprint"] = fp,
                ["Tokens"] = J.Clone(tokens),
                ["selfIdentifier"] = Guid.NewGuid().ToString(),
            },
            ["NotificationToken"] = "",
            ["API"] = int.Parse(Signing.Vapi),
            ["RequestId"] = Guid.NewGuid().ToString(),
            ["Timestamp"] = now.ToUnixTimeSeconds(),
            ["TimestampFormatted"] = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        return JsonSerializer.SerializeToUtf8Bytes(body,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // ---- command ----
    public static async Task<int> RunAsync(Cli.Opts o)
    {
        if (o.Has("selftest")) return await SelfTestAsync(o);

        AnsiConsole.Write(new FigletText("Register").Color(Color.MediumPurple));
        string html;
        var apFile = o.Get("ap-file");
        if (apFile != null)
        {
            html = await File.ReadAllTextAsync(apFile);
        }
        else
        {
            AnsiConsole.MarkupLine("[bold]1.[/] Otwieram [underline]https://eduvulcan.pl/logowanie[/] — zaloguj się.");
            try { Process.Start(new ProcessStartInfo("https://eduvulcan.pl/logowanie") { UseShellExecute = true }); } catch { }
            AnsiConsole.MarkupLine("[bold]2.[/] Po zalogowaniu otwórz [underline]https://eduvulcan.pl/api/ap[/], zaznacz wszystko (Ctrl+A) i skopiuj (Ctrl+C).");
            AnsiConsole.MarkupLine("[bold]3.[/] Wklej tutaj, a następnie naciśnij [grey]Ctrl+Z, Enter[/]:\n");
            html = await Console.In.ReadToEndAsync();
        }

        var ap = ParseAp(html);
        AnsiConsole.MarkupLine($"\n[green]✓[/] Zalogowano jako [bold]{Markup.Escape($"{ap.Str("GivenName")} {ap.Str("Surname")}".Trim())}[/]");

        var tokens = new JsonArray();
        foreach (var t in ap["Tokens"].Arr()) if (t is not null) tokens.Add(t.Str());
        var tenants = new List<string>();
        foreach (var t in tokens)
        {
            var s = t.Str();
            if (!s.Contains('.')) continue;
            var tn = JwtTenant(s);
            if (tn.Length > 0 && !tenants.Contains(tn)) tenants.Add(tn);
        }
        if (tenants.Count == 0) throw new InvalidOperationException("Nie udało się wyciągnąć tenant z tokenów JWT.");

        var (fp, pk, cert) = GenerateKeyPair();
        AnsiConsole.MarkupLine($"[grey]Wygenerowano klucz RSA — fingerprint {fp}[/]");

        using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var restUrls = new List<string>();
        foreach (var tenant in tenants)
        {
            var env = await PostAsync(http, fp, pk, $"{BaseUrl}/{tenant}/api/mobile/register/jwt", BuildJwtBody(fp, cert, tokens));
            var rest = env.Str("RestURL");
            if (rest.Length > 0 && !restUrls.Contains(rest)) restUrls.Add(rest);
        }
        if (restUrls.Count == 0) restUrls.Add($"{BaseUrl}/{tenants[0]}/");

        JsonArray? pupils = null;
        var chosenRest = restUrls[0];
        foreach (var rest in restUrls)
        {
            try
            {
                pupils = (await GetAsync(http, fp, pk, $"{rest.TrimEnd('/')}/api/mobile/register/hebe?mode=2")).Arr();
                chosenRest = rest;
                if (pupils.Count > 0) break;
            }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[grey]{Markup.Escape(rest)} → {Markup.Escape(ex.Message)}[/]"); }
        }
        if (pupils is null || pupils.Count == 0) throw new InvalidOperationException("Nie udało się pobrać danych ucznia.");

        int idx = 0;
        if (pupils.Count > 1)
        {
            var choices = new List<string>();
            for (int i = 0; i < pupils.Count; i++)
                choices.Add($"{i}: {pupils[i].Str("Pupil", "FirstName")} {pupils[i].Str("Pupil", "Surname")} — {pupils[i].Str("Unit", "Name")}");
            var sel = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Wybierz ucznia:").AddChoices(choices));
            idx = int.Parse(sel.Split(':')[0]);
        }

        var creds = new JsonObject
        {
            ["fingerprint"] = fp,
            ["privateKey"] = pk,
            ["certificate"] = cert,
            ["restUrl"] = chosenRest,
            ["pupil"] = J.Clone(pupils[idx]!),
        };
        var outPath = o.Get("credentials") ?? Path.Combine(Roots.FindRoot(null), "credentials.json");
        await File.WriteAllTextAsync(outPath, creds.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        AnsiConsole.MarkupLine($"\n[green]✓[/] Zapisano [bold]{Markup.Escape(outPath)}[/]. Teraz: [grey]vulcanscope export[/]");
        return 0;
    }

    private static async Task<int> SelfTestAsync(Cli.Opts o)
    {
        AnsiConsole.Write(new Rule("[bold mediumpurple]register --selftest[/]").LeftJustified());

        // 1) keygen → local sign/verify roundtrip + POST digest/signature shape
        var (fp, pk, cert) = GenerateKeyPair();
        using (var rsa = RSA.Create())
        {
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pk), out _);
            var data = Encoding.UTF8.GetBytes("vulcanscope-selftest");
            var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certObj = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(cert));
            using var pub = certObj.GetRSAPublicKey()!;
            bool ok = pub.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            AnsiConsole.MarkupLine($"Keygen fingerprint : [bold]{fp}[/]");
            AnsiConsole.MarkupLine($"PKCS8 import/sign/verify roundtrip : {(ok ? "[green]✓ OK[/]" : "[red]✗ FAIL[/]")}");
            var body = BuildJwtBody(fp, cert, new JsonArray { "header.payload.sig" });
            var (_, _, digest, header) = SignPost(fp, pk, body, $"{BaseUrl}/x/api/mobile/register/jwt");
            AnsiConsole.MarkupLine($"POST digest        : [grey]{Markup.Escape(digest)}[/]");
            AnsiConsole.MarkupLine($"POST signature     : [grey]{Markup.Escape(header[..Math.Min(54, header.Length)])}…[/]");
        }

        // 2) LIVE check of the register GET path using the EXISTING (already paired) key
        try
        {
            var creds = JsonNode.Parse(await File.ReadAllTextAsync(Roots.CredentialsPath(o.Get("credentials"))))!;
            using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var url = $"{creds.Str("restUrl").TrimEnd('/')}/api/mobile/register/hebe?mode=2";
            var pupils = (await GetAsync(http, creds.Str("fingerprint"), creds.Str("privateKey"), url)).Arr();
            AnsiConsole.MarkupLine($"\nLive [italic]register/hebe[/] : [green]✓[/] {pupils.Count} ucznia(ów):");
            foreach (var p in pupils)
                AnsiConsole.MarkupLine($"  • {Markup.Escape(p.Str("Pupil", "FirstName"))} {Markup.Escape(p.Str("Pupil", "Surname"))} — {Markup.Escape(p.Str("Unit", "Name"))}");
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Live register/hebe pominięty:[/] {Markup.Escape(ex.Message)}"); }
        return 0;
    }
}
