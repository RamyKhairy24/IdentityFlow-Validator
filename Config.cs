namespace IdentityFlowValidator
{
    public static class Config
    {
        // Target URLs for the identity provider (Replace with your target environment)
        public const string TARGET_RESET_URL = "https://identity.example-provider.com/support/password-reset";

        public const int MIN_DELAY_MS = 3000;
        public const int MAX_DELAY_MS = 6000;
        public const int RATE_LIMIT_DELAY_MS = 10000;
        public const int MAX_RETRIES = 3;
        public const int HTTP_TIMEOUT_SECONDS = 30;

        // User agent rotation for better stealth and realistic request simulation
        public static readonly string[] USER_AGENTS = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Edge/120.0.0.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:120.0) Gecko/20100101 Firefox/120.0"
        };

        // OAuth / API configuration
        // Fill these with values from your API application if required.
        public const string OAUTH_CLIENT_ID = "YOUR_CLIENT_ID";
        public const string OAUTH_CLIENT_SECRET = "YOUR_CLIENT_SECRET";
        public const string OAUTH_TOKEN_URL = "https://oauth.example-provider.com/token";
        public const string API_BASE_URL = "https://api.example-provider.com";

        // Optional: an API endpoint template (relative to API_BASE_URL) that accepts a phone parameter.
        // Example: "/account/lookup?phone={phone}&namespace=profile-prod"
        // Replace {phone} with URL-encoded phone string. Leave empty if using web-login flow.
        public const string ACCOUNT_LOOKUP_ENDPOINT = "";
    }
}