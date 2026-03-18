using Microsoft.Data.Sqlite;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal static class Env
{
    public static string Require(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"Missing required env var '{name}'.");
        return v;
    }

    public static string? Optional(string name) => Environment.GetEnvironmentVariable(name);

    public static TimeSpan ReadIntervalSeconds(string name, TimeSpan fallback)
    {
        var raw = Optional(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw, out var seconds) && seconds > 0) return TimeSpan.FromSeconds(seconds);
        throw new InvalidOperationException($"Env var '{name}' must be a positive integer (seconds). Got '{raw}'.");
    }

    public static bool ReadBool(string name, bool fallback)
    {
        var raw = Optional(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Equals("1") || raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

internal record ShopwareProduct(string Id, string ProductNumber, string Name, int Stock, IReadOnlyList<string> Tags);

internal static class Program
{
    public static async Task<int> Main()
    {
        var ebayApiBaseUrl = Env.Optional("EBAY_API_BASE_URL")?.TrimEnd('/') ?? "https://api.ebay.com";
        var environmentName = Env.Optional("EBAY_ENVIRONMENT") ?? "production";
        var pollInterval = Env.ReadIntervalSeconds("EBAY_POLL_INTERVAL_SECONDS", TimeSpan.FromMinutes(1));
        var dryRun = Env.ReadBool("EBAY_DRY_RUN", true);
        var tagName = Env.Optional("SHOPWARE_EBAY_TAG") ?? "ebay";
        var stateDbPath = Env.Optional("STATE_DB_PATH") ?? "/data/state.db";

        // eBay OAuth2 refresh token flow
        var clientId = Env.Require("EBAY_CLIENT_ID");
        var clientSecret = Env.Require("EBAY_CLIENT_SECRET");
        var refreshToken = Env.Require("EBAY_REFRESH_TOKEN");
        var ebayScopes = Env.Optional("EBAY_OAUTH_SCOPES") ?? "https://api.ebay.com/oauth/api_scope";

        // Shopware Admin API
        var shopwareAdminApiBaseUrl = Env.Require("SHOPWARE_ADMIN_API_BASE_URL");
        var shopwareAdminApiToken = Env.Require("SHOPWARE_ADMIN_API_TOKEN");

        Console.WriteLine($"ebay-adapter starting (env={environmentName}, dryRun={dryRun}, shopware={shopwareAdminApiBaseUrl}, tag={tagName}, interval={pollInterval}).");

        using var ebayHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30), BaseAddress = new Uri(ebayApiBaseUrl + "/") };
        using var shopwareHttp = CreateShopwareClient(shopwareAdminApiBaseUrl, shopwareAdminApiToken);
        using var db = OpenStateDb(stateDbPath);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.IsCancellationRequested)
        {
            try
            {
                _ = await FetchEbayAccessTokenAsync(ebayHttp, clientId, clientSecret, refreshToken, ebayScopes, cts.Token);

                var candidates = await FetchShopwareEbayCandidatesAsync(shopwareHttp, tagName, cts.Token);
                var listable = candidates.Where(p => p.Stock >= 1).ToList();

                Console.WriteLine($"[{DateTimeOffset.UtcNow:u}] shopware: {candidates.Count} tagged, {listable.Count} listable (stock>=1).");

                foreach (var p in listable)
                {
                    var existing = GetListing(db, p.Id);
                    await EbayEnsureListedAsync(dryRun, p, existing?.sku, cts.Token);
                    UpsertListing(db, p.Id, p.ProductNumber, status: "listed", ebaySku: existing?.sku ?? $"sw-{p.ProductNumber}");
                }

                foreach (var p in candidates.Where(p => p.Stock <= 0))
                {
                    var existing = GetListing(db, p.Id);
                    if (existing is null) continue;
                    await EbayEnsureEndedAsync(dryRun, p, existing.Value.offerId, cts.Token);
                    UpsertListing(db, p.Id, p.ProductNumber, status: "ended", ebaySku: existing.Value.sku, ebayOfferId: existing.Value.offerId);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:u}] ERROR: {ex.Message}");
            }

            try { await Task.Delay(pollInterval, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        }

        Console.WriteLine("ebay-adapter stopped.");
        return 0;
    }

    static async Task<string> FetchEbayAccessTokenAsync(HttpClient http, string clientId, string clientSecret, string refreshToken, string scopes, CancellationToken ct)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        using var req = new HttpRequestMessage(HttpMethod.Post, "identity/v1/oauth2/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = scopes
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

    static HttpClient CreateShopwareClient(string baseUrl, string adminToken)
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    static async Task<IReadOnlyList<ShopwareProduct>> FetchShopwareEbayCandidatesAsync(HttpClient shopware, string tagName, CancellationToken ct)
    {
        var payload = new
        {
            limit = 200,
            page = 1,
            includes = new
            {
                product = new[] { "id", "productNumber", "stock", "name", "tags" },
                tag = new[] { "id", "name" }
            },
            filter = new object[]
            {
                new { type = "equals", field = "tags.name", value = tagName }
            },
            associations = new { tags = new { } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/search/product")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var resp = await shopware.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Shopware product search failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<ShopwareProduct>();

        var result = new List<ShopwareProduct>();
        foreach (var item in dataEl.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var attrs = item.GetProperty("attributes");
            var productNumber = attrs.TryGetProperty("productNumber", out var pn) ? pn.GetString() ?? id : id;
            var name = attrs.TryGetProperty("name", out var n) ? n.GetString() ?? productNumber : productNumber;
            var stock = attrs.TryGetProperty("stock", out var s) ? s.GetInt32() : 0;

            var tags = new List<string>();
            if (attrs.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out var tn))
                        tags.Add(tn.GetString() ?? "");
                }
            }

            result.Add(new ShopwareProduct(id, productNumber, name, stock, tags.Where(x => !string.IsNullOrWhiteSpace(x)).ToList()));
        }

        return result;
    }

    static SqliteConnection OpenStateDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                              CREATE TABLE IF NOT EXISTS listings (
                                shopware_product_id TEXT PRIMARY KEY,
                                product_number TEXT NOT NULL,
                                ebay_sku TEXT,
                                ebay_offer_id TEXT,
                                status TEXT NOT NULL,
                                updated_at_utc TEXT NOT NULL
                              );
                              CREATE TABLE IF NOT EXISTS kv (
                                k TEXT PRIMARY KEY,
                                v TEXT NOT NULL
                              );
                              """;
            cmd.ExecuteNonQuery();
        }

        return conn;
    }

    static void UpsertListing(SqliteConnection db, string shopwareProductId, string productNumber, string status, string? ebaySku = null, string? ebayOfferId = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO listings (shopware_product_id, product_number, ebay_sku, ebay_offer_id, status, updated_at_utc)
                          VALUES ($id, $pn, $sku, $offer, $status, $ts)
                          ON CONFLICT(shopware_product_id) DO UPDATE SET
                            product_number=excluded.product_number,
                            ebay_sku=COALESCE(excluded.ebay_sku, listings.ebay_sku),
                            ebay_offer_id=COALESCE(excluded.ebay_offer_id, listings.ebay_offer_id),
                            status=excluded.status,
                            updated_at_utc=excluded.updated_at_utc;
                          """;
        cmd.Parameters.AddWithValue("$id", shopwareProductId);
        cmd.Parameters.AddWithValue("$pn", productNumber);
        cmd.Parameters.AddWithValue("$sku", (object?)ebaySku ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$offer", (object?)ebayOfferId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("u"));
        cmd.ExecuteNonQuery();
    }

    static (string? sku, string? offerId, string status)? GetListing(SqliteConnection db, string shopwareProductId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT ebay_sku, ebay_offer_id, status FROM listings WHERE shopware_product_id=$id";
        cmd.Parameters.AddWithValue("$id", shopwareProductId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2));
    }

    static Task EbayEnsureListedAsync(bool dryRun, ShopwareProduct p, string? existingSku, CancellationToken ct)
    {
        var sku = existingSku ?? $"sw-{p.ProductNumber}";
        if (dryRun)
        {
            Console.WriteLine($"[DRY] would ensure eBay listing for productNumber={p.ProductNumber} sku={sku} (stock={p.Stock})");
            return Task.CompletedTask;
        }

        throw new NotImplementedException("Enable EBAY_DRY_RUN=true until eBay listing fields are configured.");
    }

    static Task EbayEnsureEndedAsync(bool dryRun, ShopwareProduct p, string? existingOfferId, CancellationToken ct)
    {
        if (dryRun)
        {
            Console.WriteLine($"[DRY] would end eBay offer for productNumber={p.ProductNumber} offerId={existingOfferId ?? "<unknown>"}");
            return Task.CompletedTask;
        }

        throw new NotImplementedException("Enable EBAY_DRY_RUN=true until eBay offer end is implemented.");
    }
}

