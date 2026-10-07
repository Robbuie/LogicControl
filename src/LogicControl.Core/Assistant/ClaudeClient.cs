using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogicControl.Core.Assistant;

/// <summary>
/// A small client for Anthropic's Messages API: one streaming request, read as server-sent events.
///
/// <para>Hand-written over HttpClient rather than an SDK, for the same reason the rest of src/ has
/// no packages: the repository builds on a machine with no NuGet, and what is needed - post JSON,
/// read an event stream - is a page of code. The request body is built by
/// <see cref="ConversationSession"/>; this class only moves bytes and turns events into
/// <see cref="StreamEvent"/>s.</para>
///
/// <para>Transient failures - 429 rate limits, 529 overloaded, 5xx, a dropped connection before any
/// event arrived - are retried twice with backoff, honouring retry-after. Anything else, and
/// anything once events have started, is a <see cref="ClaudeApiException"/> with a sentence a
/// person can act on.</para>
/// </summary>
public sealed class ClaudeClient : IDisposable
{
    public const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _apiKey;
    private readonly Uri _endpoint;

    public ClaudeClient(string apiKey, HttpMessageHandler? handler = null, string? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey.Trim();
        _endpoint = new Uri(endpoint ?? DefaultEndpoint);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;

        // A long answer with several tool calls can stream for minutes; the per-read cancellation
        // token is what stops a stalled stream, not a whole-request timeout.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Delays between retries. Settable so tests do not wait.</summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    /// <summary>Sends one request with "stream": true and yields its events until message_stop.</summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        JsonObject body,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        body["stream"] = true;
        string json = body.ToJsonString();

        HttpResponseMessage response = await SendWithRetryAsync(json, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var data = new StringBuilder();
            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        StreamEvent e = Parse(data.ToString());
                        data.Clear();
                        if (e.Type == "error")
                        {
                            throw new ClaudeApiException(ErrorText(e.Data), retryable: false);
                        }

                        yield return e;
                        if (e.Type == "message_stop")
                        {
                            yield break;
                        }
                    }

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart());
                }

                // "event:" lines repeat the type that is also inside data; ":" lines are keep-alives.
            }

            if (data.Length > 0)
            {
                yield return Parse(data.ToString());
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string json, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ClaudeApiException($"Could not reach Anthropic's API: {ex.Message}. Check the internet connection, or a proxy that blocks api.anthropic.com.", retryable: true, ex);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            HttpStatusCode code = response.StatusCode;
            int status = (int)code;
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bool transient = status is 429 or 529 or >= 500;
            TimeSpan? wait = response.Headers.RetryAfter?.Delta;
            response.Dispose();

            if (transient && attempt < RetryDelays.Length)
            {
                TimeSpan delay = wait is { } w && w < TimeSpan.FromSeconds(30) ? w : RetryDelays[attempt];
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new ClaudeApiException(StatusText(code, body), transient);
        }
    }

    private static StreamEvent Parse(string data)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(data);
            string type = node?["type"]?.GetValue<string>() ?? "unknown";
            return new StreamEvent(type, node as JsonObject ?? []);
        }
        catch (JsonException ex)
        {
            throw new ClaudeApiException($"The API sent something that is not JSON: {ex.Message}", retryable: true, ex);
        }
    }

    private static string StatusText(HttpStatusCode status, string body)
    {
        string detail = ErrorText(TryParse(body));
        return (int)status switch
        {
            401 => "The API key was refused. Check it in the assistant's settings - it starts with sk-ant-.",
            403 => $"The API key is not allowed to do this: {detail}",
            404 => $"Not found - most often a model name this key cannot use: {detail}",
            413 => "The request was too large. Start a new chat, or ask about a smaller part of the project.",
            429 => $"Rate limited by the API: {detail} Wait a minute and try again.",
            529 => "Anthropic's API is overloaded right now. Try again in a minute.",
            >= 500 => $"Anthropic's API had an error ({(int)status}). Try again in a minute.",
            _ => $"The API answered {(int)status}: {detail}",
        };
    }

    private static JsonObject TryParse(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return new JsonObject { ["error"] = new JsonObject { ["message"] = body.Length > 300 ? body[..300] : body } };
        }
    }

    private static string ErrorText(JsonObject data) =>
        data["error"]?["message"]?.GetValue<string>() ?? data.ToJsonString();

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>One server-sent event: its type and the whole JSON object it carried.</summary>
public sealed record StreamEvent(string Type, JsonObject Data);

/// <summary>An API failure, worded for the person reading the chat.</summary>
public sealed class ClaudeApiException : Exception
{
    public ClaudeApiException()
    {
    }

    public ClaudeApiException(string message)
        : base(message)
    {
    }

    public ClaudeApiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ClaudeApiException(string message, bool retryable, Exception? inner = null)
        : base(message, inner)
    {
        Retryable = retryable;
    }

    /// <summary>Worth pressing Send again: a rate limit or an outage rather than a bad key.</summary>
    public bool Retryable { get; }
}

/// <summary>Token counts for one turn, for the cost line under the chat.</summary>
public sealed record Usage(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens)
{
    public static Usage Zero { get; } = new(0, 0, 0, 0);

    public Usage Add(Usage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadTokens + other.CacheReadTokens,
        CacheWriteTokens + other.CacheWriteTokens);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{InputTokens + CacheReadTokens + CacheWriteTokens:N0} in ({CacheReadTokens:N0} cached) · {OutputTokens:N0} out");
}
