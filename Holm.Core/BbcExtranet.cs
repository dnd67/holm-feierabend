using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch;

/// <summary>One read of the extranet: the total flexitime, today's bookings, and the refresh token to keep.</summary>
internal sealed record BbcReading(TimeSpan Saldo, IReadOnlyList<WorkBlock> Today, string RefreshToken, DateTimeOffset ReadAt);

/// <summary>The extranet rejected the saved sign-in, or the read could not be understood.</summary>
internal sealed class BbcExtranetException : Exception
{
    /// <summary>The saved sign-in no longer works. The password is still not kept.</summary>
    public bool NeedsSignIn { get; }

    public BbcExtranetException(string message, bool needsSignIn = false) : base(message) => NeedsSignIn = needsSignIn;
}

/// <summary>
/// Reads the BBC extranet. Sign-in exchanges the password for a refresh token and drops the password.
/// Bookings are only ever read. Create, change and delete exist on the API and are not called.
/// </summary>
internal sealed class BbcExtranet : IDisposable
{
    /// <summary>The extranet website's public sign-in client. It is not a secret; each person uses their own account.</summary>
    public const string ClientId = "7R6bulmurfMbXsYQUdOKa1Hg9Z3I5QNq";

    public const string ApiRoot = "https://api.extranet.bbcag.ch";
    const string TokenPath = "/oauth/token";
    const string LegacyRoot = "https://legacy.extranet.bbcag.ch/";

    static readonly string[] BalancePaths =
    {
        "/api/timesheet/v1/balance",
        "/api/timesheet/v1/summary",
        "/api/timesheet/v1/month",
    };

    readonly HttpClient _http;
    readonly bool _ownsClient;

    public BbcExtranet(HttpMessageHandler? handler = null)
    {
        _ownsClient = handler == null;
        _http = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Holm-Gleitzeit/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    public async Task<string> LoginAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new BbcExtranetException("E-Mail und Passwort werden zum Anmelden gebraucht.", needsSignIn: true);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = ClientId,
            ["username"] = email.Trim(),
            ["password"] = password,
            ["redirect_uri"] = "https://extranet.bbcag.ch",
        };
        string json = await PostFormAsync(TokenPath, form, bearer: null, ct).ConfigureAwait(false);
        return ReadRefresh(json);
    }

    public async Task<BbcReading> ReadAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new BbcExtranetException("Noch nicht am Extranet angemeldet.", needsSignIn: true);
        var (refresh, token) = await RefreshAsync(refreshToken, ct).ConfigureAwait(false);
        string today = await GetAsync("/api/timesheet/v1/records/today", token, ct).ConfigureAwait(false);
        string? balance = null, summary = null;
        if (!GleitzeitParser.TryParse(today, null, null, null, out _))
        {
            foreach (var path in BalancePaths)
            {
                string? body = await TryGetAsync(path, token, ct).ConfigureAwait(false);
                if (body == null) continue;
                if (balance == null) balance = body;
                else if (summary == null) summary = body;
                if (GleitzeitParser.TryParse(today, balance, summary, null, out _)) break;
            }
        }
        string? html = null;
        if (!GleitzeitParser.TryParse(today, balance, summary, null, out _))
            html = await TryGetAsync(LegacyRoot, token, ct).ConfigureAwait(false);
        if (!GleitzeitParser.TryParse(today, balance, summary, html, out var parsed))
            throw new BbcExtranetException("Der Gleitzeit-Saldo steht in der Antwort des Extranets nicht.");
        return new BbcReading(parsed.Saldo, parsed.Today, refresh, DateTimeOffset.Now);
    }

    async Task<(string Refresh, string Access)> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        };
        string json = await PostFormAsync(TokenPath, form, bearer: null, ct).ConfigureAwait(false);
        string refresh = ReadRefresh(json);
        return (refresh, ReadAccess(json));
    }

    static string ReadRefresh(string json)
    {
        string refresh = ReadToken(json, "refresh_token");
        if (refresh.Length == 0) throw new BbcExtranetException("Die Sitzung konnte nicht erneuert werden.", needsSignIn: true);
        return refresh;
    }

    static string ReadAccess(string json)
    {
        string access = ReadToken(json, "access_token");
        if (access.Length == 0) throw new BbcExtranetException("Die Sitzung konnte nicht erneuert werden.", needsSignIn: true);
        return access;
    }

    static string ReadToken(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }
        catch (JsonException)
        {
            throw new BbcExtranetException("Die Anmeldung hat keine Sitzung zurückgegeben.", needsSignIn: true);
        }
        return "";
    }

    async Task<string> PostFormAsync(string path, Dictionary<string, string> form, string? bearer, CancellationToken ct)
    {
        if (path != TokenPath) throw new InvalidOperationException("Gleitzeit schreibt nur die Anmeldung.");
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoot + path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            throw new BbcExtranetException("Die Anmeldung wurde abgelehnt.", needsSignIn: true);
        if (!response.IsSuccessStatusCode)
            throw new BbcExtranetException("Die Sitzung konnte nicht erneuert werden.", needsSignIn: true);
        return body;
    }

    async Task<string> GetAsync(string path, string accessToken, CancellationToken ct)
    {
        string? body = await TryGetAsync(path, accessToken, ct, missingOk: false).ConfigureAwait(false);
        return body ?? throw new BbcExtranetException("Das Extranet hat die Zeiten nicht geliefert.");
    }

    async Task<string?> TryGetAsync(string path, string accessToken, CancellationToken ct, bool missingOk = true)
    {
        if (HttpMethod.Get.Method != "GET") throw new InvalidOperationException();
        if (path.Contains("create", StringComparison.OrdinalIgnoreCase)
            || path.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || path.Contains("patch", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gleitzeit ändert keine Buchung.");
        var uri = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(path) : new Uri(ApiRoot + path);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (missingOk && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BbcExtranetException("Die Anmeldung ist abgelaufen. Einmal neu anmelden.", needsSignIn: true);
        if (!response.IsSuccessStatusCode)
            throw new BbcExtranetException("Das Extranet hat die Zeiten nicht geliefert.");
        return body;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
