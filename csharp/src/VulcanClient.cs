using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace VulcanScope;

/// <summary>Read-only client for the Hebe journal API (RSA-signed GET requests).</summary>
public sealed class VulcanClient : IDisposable
{
    private readonly string _fingerprint;
    private readonly string _privateKey;
    private readonly string _restUrl;
    private readonly HttpClient _http;

    public JsonObject Pupil { get; }

    public VulcanClient(string credentialsPath)
    {
        var creds = JsonNode.Parse(File.ReadAllText(credentialsPath))!.AsObject();
        _fingerprint = creds["fingerprint"].Str();
        _privateKey = creds["privateKey"].Str();
        _restUrl = creds["restUrl"].Str().TrimEnd('/');
        Pupil = creds["pupil"]!.AsObject();

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ---- identity ----
    public string Symbol => Pupil.Str("Unit", "Symbol");
    public int UnitId => Pupil.Int("Unit", "Id") ?? 0;
    public int PupilId => Pupil.Int("Pupil", "Id") ?? 0;
    public int ConstituentId => Pupil.Int("ConstituentUnit", "Id") ?? 0;
    public string MessageBox => Pupil.Str("MessageBox", "GlobalKey");
    public JsonArray Periods => Pupil["Periods"].Arr();

    public JsonNode CurrentPeriod()
    {
        foreach (var p in Periods)
            if (p.Bool("Current")) return p!;
        return Periods[^1]!;
    }

    public (string Start, string End) YearRange()
    {
        var start = Pupil.Str("Journal", "StartAt");
        var end = Pupil.Str("Journal", "EndAt");
        if (string.IsNullOrEmpty(start)) start = Periods[0].PeriodBounds().Start;
        if (string.IsNullOrEmpty(end)) end = Periods[^1].PeriodBounds().End;
        return (start, end);
    }

    // ---- core ----
    private static string Q(params (string k, object v)[] pairs) =>
        string.Join("&", pairs.Select(p =>
            $"{p.k}={Uri.EscapeDataString(Convert.ToString(p.v, CultureInfo.InvariantCulture) ?? "")}"));

    public async Task<JsonNode?> GetAsync(string path, string query = "")
    {
        var url = $"{_restUrl}/{Symbol}/api/mobile/{path}";
        if (query.Length > 0) url += "?" + query;

        var (canonical, date, signature) = Signing.SignGet(_fingerprint, _privateKey, url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        void H(string k, string v) => req.Headers.TryAddWithoutValidation(k, v);
        H("accept", "*/*"); H("accept-charset", "UTF-8"); H("accept-encoding", "gzip");
        H("connection", "Keep-Alive"); H("content-type", "application/json");
        H("user-agent", Signing.UserAgent); H("vapi", Signing.Vapi); H("vdate", date);
        H("vdevicemodel", Signing.DeviceModel); H("vos", Signing.Os); H("vversioncode", Signing.VersionCode);
        H("signature", signature); H("vcanonicalurl", canonical);

        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;
        var code = json.Int("Status", "Code") ?? 0;
        if (code != 0)
            throw new HebeException(code, json.Str("Status", "Message"), url);
        return json["Envelope"];
    }

    // ---- resources ----
    public async Task<double?> GetLuckyNumberAsync()
    {
        var env = await GetAsync("school/lucky",
            Q(("pupilId", PupilId), ("constituentId", ConstituentId), ("day", DateTime.Now.ToString("yyyy-MM-dd"))));
        return env.Num("Number");
    }

    public Task<JsonNode?> GetGradesAsync(int periodId) =>
        GetAsync("grade/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("periodId", periodId), ("pageSize", 500)));

    public Task<JsonNode?> GetGradesSummaryAsync(int periodId) =>
        GetAsync("grade/summary/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("periodId", periodId), ("pageSize", 500)));

    public Task<JsonNode?> GetScheduleAsync(string from, string to, int periodId) =>
        GetAsync("schedule/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("periodId", periodId), ("dateFrom", from), ("dateTo", to), ("pageSize", 500)));

    public Task<JsonNode?> GetScheduleChangesAsync(string from, string to, int periodId) =>
        GetAsync("schedule/withchanges/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("periodId", periodId), ("dateFrom", from), ("dateTo", to), ("pageSize", 500)));

    public Task<JsonNode?> GetAttendanceAsync(string from, string to) =>
        GetAsync("lesson/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("dateFrom", from), ("dateTo", to), ("pageSize", 1000)));

    public Task<JsonNode?> GetExamsAsync(string from, string to) =>
        GetAsync("exam/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("dateFrom", from), ("dateTo", to), ("pageSize", 500)));

    public Task<JsonNode?> GetHomeworkAsync(string from, string to) =>
        GetAsync("homework/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("dateFrom", from), ("dateTo", to), ("pageSize", 500)));

    public Task<JsonNode?> GetNotesAsync() =>
        GetAsync("note/byPupil", Q(("unitId", UnitId), ("pupilId", PupilId), ("pageSize", 500)));

    public void Dispose() => _http.Dispose();
}
