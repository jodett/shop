using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

static string Require(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(v))
        throw new InvalidOperationException($"Missing required env var '{name}'.");
    return v;
}

static string? Optional(string name) => Environment.GetEnvironmentVariable(name);

static TimeSpan ReadInterval(string name, TimeSpan fallback)
{
    var raw = Optional(name);
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    if (int.TryParse(raw, out var seconds) && seconds > 0) return TimeSpan.FromSeconds(seconds);
    throw new InvalidOperationException($"Env var '{name}' must be a positive integer (seconds). Got '{raw}'.");
}

var ebayApiBaseUrl = Optional("EBAY_API_BASE_URL")?.TrimEnd('/') ?? "https://api.ebay.com";
var environmentName = Optional("EBAY_ENVIRONMENT") ?? "production";

// OAuth2 refresh token flow is the safest default for headless workers.
var clientId = Require("EBAY_CLIENT_ID");
var clientSecret = Require("EBAY_CLIENT_SECRET");
var refreshToken = Require("EBAY_REFRESH_TOKEN");

// Placeholder: you can later wire this to your Shopware API to push inventory/orders.
var shopwareAdminApiBaseUrl = Optional("SHOPWARE_ADMIN_API_BASE_URL");
var shopwareAdminApiToken = Optional("SHOPWARE_ADMIN_API_TOKEN");

var pollInterval = ReadInterval("EBAY_POLL_INTERVAL_SECONDS", TimeSpan.FromMinutes(1));

Console.WriteLine($"ebay-adapter starting (env={environmentName}, api={ebayApiBaseUrl}, interval={pollInterval}).");

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30),
    BaseAddress = new Uri(ebayApiBaseUrl + "/")
};

static async Task<string> FetchAccessTokenAsync(HttpClient http, string clientId, string clientSecret, string refreshToken, CancellationToken ct)
{
    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

    using var req = new HttpRequestMessage(HttpMethod.Post, "identity/v1/oauth2/token");
    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
        // NOTE: adjust scopes as you implement features. For now keep it explicit and minimal.
        ["scope"] = "https://api.ebay.com/oauth/api_scope"
    });

    using var resp = await http.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"eBay token request failed ({(int)resp.StatusCode}): {body}");

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
        throw new InvalidOperationException($"eBay token response missing access_token: {body}");

    return tokenEl.GetString() ?? throw new InvalidOperationException("access_token was null.");
}

static async Task<JsonElement> GetJsonAsync(HttpClient http, string path, string accessToken, CancellationToken ct)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, path);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var resp = await http.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"GET {path} failed ({(int)resp.StatusCode}): {body}");

    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.Clone();
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    try
    {
        var accessToken = await FetchAccessTokenAsync(http, clientId, clientSecret, refreshToken, cts.Token);

        // Smoke call: get location. Replace with your real sync loop (orders, inventory, offers, etc.)
        // Using Sell Fulfillment or Inventory APIs will require additional scopes and endpoints.
        var user = await GetJsonAsync(http, "commerce/identity/v1/user/", accessToken, cts.Token);
        var username = user.TryGetProperty("username", out var u) ? u.GetString() : null;

        Console.WriteLine($"[{DateTimeOffset.UtcNow:u}] eBay OK. username={username ?? "<unknown>"}");

        if (!string.IsNullOrWhiteSpace(shopwareAdminApiBaseUrl))
        {
            Console.WriteLine($"Shopware configured: {shopwareAdminApiBaseUrl} (token {(string.IsNullOrWhiteSpace(shopwareAdminApiToken) ? "missing" : "present")})");
        }
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        // shutting down
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:u}] ERROR: {ex.Message}");
    }

    try
    {
        await Task.Delay(pollInterval, cts.Token);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        // shutting down
    }
}

Console.WriteLine("ebay-adapter stopped.");

