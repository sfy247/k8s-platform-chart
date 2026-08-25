namespace ClaudeTradingAgent.Execution;

public static class PaperEndpointGuard
{
    private const string ExpectedHost = "paper-api.alpaca.markets";

    public static Uri Validate(string configuredBaseUrl)
    {
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Broker base URL is invalid.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Broker endpoint must use HTTPS.");
        if (!string.Equals(uri.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Paper mode requires host '{ExpectedHost}'.");
        return uri;
    }
}
