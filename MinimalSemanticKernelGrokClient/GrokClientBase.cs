using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Microsoft.SemanticKernel.Connectors.Grok.Core;

// Base class that provides HttpClient and basic request helpers using real HttpClient calls.
internal abstract class GrokClientBase
{
    private readonly Func<ValueTask<string>>? _bearerTokenProvider;
    private readonly string? _apiKey;
    protected ILogger Logger { get; }
    protected HttpClient HttpClient { get; }

    protected GrokClientBase(
        HttpClient httpClient,
        ILogger? logger,
        Func<ValueTask<string>> bearerTokenProvider)
        : this(httpClient, logger)
    {
        this._bearerTokenProvider = bearerTokenProvider ?? throw new ArgumentNullException(nameof(bearerTokenProvider));
    }

    protected GrokClientBase(
        HttpClient httpClient,
        ILogger? logger,
        string? apiKey = null)
    {
        this.HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.Logger = logger ?? NullLogger.Instance;
        this._apiKey = apiKey;
    }

    protected static void ValidateMaxTokens(int? maxTokens)
    {
        if (maxTokens is < 1)
        {
            throw new ArgumentException($"MaxTokens {maxTokens} is not valid, the value must be greater than zero");
        }
    }

    /// <summary>
    /// Sends the given HttpRequestMessage, ensures success, and returns the response body as string.
    /// Uses the underlying HttpClient.
    /// </summary>
    protected async Task<string> SendRequestAndGetStringBodyAsync(
        HttpRequestMessage httpRequestMessage,
        CancellationToken cancellationToken)
    {
        // Safely read and log outgoing request body (debug level)
        try
        {
            if (httpRequestMessage.Content != null)
            {
                var outgoing = await httpRequestMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                this.Logger.LogDebug("Outgoing HTTP request to {Uri}:\n{Body}", httpRequestMessage.RequestUri, outgoing);
            }
        }
        catch (Exception ex)
        {
            // Logging must never crash the request pipeline
            this.Logger.LogWarning(ex, "Failed to read outgoing request content for logging.");
        }

        using var response = await this.HttpClient.SendAsync(httpRequestMessage, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Include the outgoing request body in the exception message to help debugging.
            string outgoingSnippet = string.Empty;
            try
            {
                if (httpRequestMessage.Content != null)
                {
                    var full = await httpRequestMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    // avoid huge exceptions — keep first 32k chars
                    outgoingSnippet = full.Length > 32768 ? full.Substring(0, 32768) + "…(truncated)" : full;
                }
            }
            catch { /* ignore */ }

            var message = $"Request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}";
            if (!string.IsNullOrEmpty(outgoingSnippet))
            {
                message += $"{Environment.NewLine}Outgoing request body (truncated):{Environment.NewLine}{outgoingSnippet}";
            }

            throw new HttpRequestException(message);
        }

        return body;
    }


    /// <summary>
    /// Sends the request but returns the HttpResponseMessage as soon as headers are available.
    /// Caller is responsible for disposing the response and reading the content stream.
    /// </summary>
    protected async Task<HttpResponseMessage> SendRequestAndGetResponseImmediatelyAfterHeadersReadAsync(
        HttpRequestMessage httpRequestMessage,
        CancellationToken cancellationToken)
    {
        // Note: Caller must Dispose the returned HttpResponseMessage.
        var response = await this.HttpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Read small body for diagnostics (with a short timeout / cancellation token)
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException($"Request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}");
        }

        return response;
    }

    protected static T DeserializeResponse<T>(string body)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                // Grok uses lower-case json field names (choices, message, etc.)
                // while our C# types use PascalCase. Make matching case-insensitive.
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<T>(body, options)
                   ?? throw new JsonException("Response is null");
        }
        catch (JsonException exc)
        {
            throw new InvalidOperationException("Unexpected response from model", exc)
            {
                Data = { { "ResponseData", body } },
            };
        }
    }


    /// <summary>
    /// Create an HttpRequestMessage with JSON content and appropriate headers.
    /// Uses bearer token provider or API key header if present.
    /// </summary>
    protected async Task<HttpRequestMessage> CreateHttpRequestAsync(object requestData, Uri endpoint, CancellationToken cancellationToken = default)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(requestData, options);
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        message.Headers.UserAgent.ParseAdd("SemanticKernel-GrokConnector/1.0");
        message.Headers.Add("x-semantic-kernel-version", "1.0");

        if (this._bearerTokenProvider is not null)
        {
            var bearerKey = await this._bearerTokenProvider().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(bearerKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
            }
        }
        else if (!string.IsNullOrWhiteSpace(this._apiKey))
        {
            message.Headers.Add("x-api-key", this._apiKey);
        }

        return message;
    }


    protected static string GetApiVersionSubLink(GrokAIVersion apiVersion)
        => apiVersion switch
        {
            GrokAIVersion.V1 => "v1",
            GrokAIVersion.V1_Beta => "v1beta",
            _ => throw new NotSupportedException($"Grok API version {apiVersion} is not supported.")
        };
}

// Small enum for API version selection.
internal enum GrokAIVersion
{
    V1,
    V1_Beta
}
