using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IdentityFlowValidator
{
    /// <summary>
    /// Minimal Blizzard API client that implements client-credentials OAuth token retrieval
    /// and a simple GET-based lookup against a configurable endpoint.
    /// Note: public Battle.net APIs do not expose phone->account lookups. This class expects
    /// a custom/private endpoint if you set Config.BLIZZARD_ACCOUNT_LOOKUP_ENDPOINT.
    /// </summary>
    public class ProviderApiClient
    {
        private readonly HttpClient _http;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _tokenUrl;

        private string? _accessToken;
        private DateTime _tokenExpiresAt = DateTime.MinValue;

        public ProviderApiClient(HttpClient http, string clientId, string clientSecret, string tokenUrl)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
            _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
            _tokenUrl = tokenUrl ?? throw new ArgumentNullException(nameof(tokenUrl));
        }

        /// <summary>
        /// Obtain (and cache) an OAuth access token via client_credentials.
        /// </summary>
        public async Task<string> GetAccessTokenAsync()
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiresAt.AddSeconds(-60))
                return _accessToken;

            using var req = new HttpRequestMessage(HttpMethod.Post, _tokenUrl);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            });

            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("access_token", out var tokenElem) || tokenElem.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Failed to obtain access token from Blizzard OAuth endpoint.");

            _accessToken = tokenElem.GetString();

            if (root.TryGetProperty("expires_in", out var expiresElem) && expiresElem.ValueKind == JsonValueKind.Number)
            {
                var expiresIn = expiresElem.GetInt32();
                _tokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
            }
            else
            {
                // fallback: set a sensible default
                _tokenExpiresAt = DateTime.UtcNow.AddMinutes(30);
            }

            return _accessToken!;
        }

        /// <summary>
        /// Calls a configured lookup endpoint (replace {phone} in the endpoint template with the URL-encoded phone).
        /// Returns:
        /// - true  => account exists
        /// - false => account does not exist
        /// - null  => ambiguous / unknown response
        /// Throws NotSupportedException if no lookup endpoint is configured.
        /// </summary>
        public async Task<bool?> CheckAccountByPhoneAsync(string phone, string apiBase, string lookupEndpointTemplate)
        {
            if (string.IsNullOrWhiteSpace(lookupEndpointTemplate))
                throw new NotSupportedException("No account-lookup endpoint configured. Public Blizzard APIs do not provide phone->account lookup.");

            var token = await GetAccessTokenAsync();

            var endpoint = lookupEndpointTemplate.Replace("{phone}", WebUtility.UrlEncode(phone));
            var url = apiBase.TrimEnd('/') + "/" + endpoint.TrimStart('/');

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return false;

            if (resp.StatusCode == HttpStatusCode.NoContent)
                return false;

            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync();

            // Best-effort: expect JSON like { "exists": true } or { "exists": false }
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("exists", out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.True) return true;
                    if (prop.ValueKind == JsonValueKind.False) return false;
                }

                // Some endpoints might return account object or error structure; try heuristics:
                if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any())
                {
                    // if there's an account id or name, consider it exists
                    if (root.TryGetProperty("id", out _) || root.TryGetProperty("account", out _) || root.TryGetProperty("battletag", out _))
                        return true;
                }
            }
            catch
            {
                // ignore parse errors — return ambiguous
            }

            return null;
        }
    }
}