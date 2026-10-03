using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace TidalSharp;

public class API
{
    internal API(IHttpClient client, Session session)
    {
        _httpClient = client;
        _session = session;
    }

    /// <summary>
    /// Minimum spacing between calls, enforced by Lidarr's rate limiter.
    /// </summary>
    /// <remarks>
    /// Tidal tolerates bursts on most endpoints but penalises sustained traffic to
    /// <c>/albums/{id}</c>, and that penalty outlives several minutes of silence. A small
    /// pace costs little on a cold cache and avoids tripping it at all.
    /// </remarks>
    private const double RequestRateLimitSeconds = 0.2;

    /// <summary>How many times a throttled call is retried before giving up.</summary>
    internal int MaxThrottleRetries { get; set; } = 5;

    /// <summary>
    /// Base unit for the exponential backoff, and the ceiling applied to
    /// <c>Retry-After</c>. Overridden in tests so throttle behaviour can be asserted
    /// without the suite waiting real minutes.
    /// </summary>
    internal TimeSpan ThrottleBackoffUnit { get; set; } = TimeSpan.FromSeconds(1);

    internal TimeSpan ThrottleBackoffCeiling { get; set; } = TimeSpan.FromSeconds(60);

    private IHttpClient _httpClient;
    private Session _session;
    private TidalUser? _activeUser;

    public async Task<JObject> GetTrack(string id, CancellationToken token = default) => await Call(HttpMethod.Get, $"tracks/{id}", token: token);
    public async Task<TidalLyrics?> GetTrackLyrics(string id, CancellationToken token = default)
    {
        try
        {
            return (await Call(HttpMethod.Get, $"tracks/{id}/lyrics", token: token)).ToObject<TidalLyrics>()!;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    public async Task<JObject> GetAlbum(string id, CancellationToken token = default) => await Call(HttpMethod.Get, $"albums/{id}", token: token);
    public async Task<JObject> GetAlbumTracks(string id, CancellationToken token = default) => await Call(HttpMethod.Get, $"albums/{id}/tracks", token: token);

    public async Task<JObject> GetArtist(string id, CancellationToken token = default) => await Call(HttpMethod.Get, $"artists/{id}", token: token);
    public async Task<JObject> GetArtistAlbums(string id, FilterOptions filter = FilterOptions.ALL, CancellationToken token = default) => await Call(HttpMethod.Get, $"artists/{id}/albums",
        urlParameters: new()
        {
            { "filter", filter.ToString() }
        },
        token: token
    );

    public async Task<JObject> GetPlaylist(string id, CancellationToken token = default) => await Call(HttpMethod.Get, $"playlists/{id}", token: token);
    public async Task<JObject> GetPlaylistTracks(string id, CancellationToken token = default) => await Call(HttpMethod.Get, $"playlists/{id}/tracks", token: token);

    public async Task<JObject> GetVideo(string id, CancellationToken token = default) => await Call(HttpMethod.Get, $"videos/{id}", token: token);

    public async Task<JObject> GetMix(string id, CancellationToken token = default)
    {
        var result = await Call(HttpMethod.Get, "pages/mix",
            urlParameters: new()
            {
                { "mixId", id },
                { "deviceType", "BROWSER" }
            },
            token: token
        );

        var refactoredObj = new JObject()
        {
            { "mix", result["rows"]![0]!["modules"]![0]!["mix"] },
            { "tracks", result["rows"]![1]!["modules"]![0]!["pagedList"] }
        };

        return refactoredObj;
    }

    internal void UpdateUser(TidalUser user) => _activeUser = user;

    internal async Task<JObject> Call(
        HttpMethod method,
        string path,
        Dictionary<string, string>? formParameters = null,
        Dictionary<string, string>? urlParameters = null,
        Dictionary<string, string>? headers = null,
        string? baseUrl = null,
        CancellationToken token = default,
        int attempt = 0
    )
    {
        // currently the method is ignored, but that doesn't matter much since it's all GET

        baseUrl ??= Globals.API_V1_LOCATION;

        // Pace these calls through Lidarr's rate limiter. Without a RateLimit the limiter
        // skips the request entirely (HttpClient.ExecuteRequestAsync only waits when
        // RateLimit != TimeSpan.Zero), so album lookups used to fire back-to-back and
        // provoke a sustained 429 penalty on /albums/{id}.
        var request = _httpClient.BuildRequest(baseUrl).Resource(path).WithRateLimit(RequestRateLimitSeconds);

        headers ??= [];
        urlParameters ??= [];
        urlParameters["sessionId"] = _activeUser?.SessionID ?? "";
        urlParameters["countryCode"] = _activeUser?.CountryCode ?? "";
        urlParameters["limit"] = _session.ItemLimit.ToString();

        if (_activeUser != null)
            headers["Authorization"] = $"{_activeUser.TokenType} {_activeUser.AccessToken}";

        foreach (var param in urlParameters)
            request = request.AddQueryParam(param.Key, param.Value, true);

        if (formParameters != null)
        {
            request = request.Post();
            foreach (var param in formParameters)
                request = request.AddFormParameter(param.Key, param.Value);
        }

        foreach (var header in headers)
            request = request.SetHeader(header.Key, header.Value);

        var response = await _httpClient.ProcessRequestAsync(request);

        // Tidal throttles per-endpoint and the penalty is cumulative: retrying without
        // backoff keeps it alive, turning a transient 429 into a self-sustaining one.
        // Back off exponentially and give up rather than recursing unbounded.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (attempt >= MaxThrottleRetries)
            {
                throw new APIException(
                    $"Tidal is rate limiting {path} and did not recover after {MaxThrottleRetries} retries.");
            }

            await Task.Delay(GetThrottleDelay(response, attempt), token);

            return await Call(method, path, formParameters, urlParameters, headers, baseUrl, token, attempt + 1);
        }


        string resp = response.Content;
        JObject json = JObject.Parse(resp);

        if (response.HasHttpError && !string.IsNullOrEmpty(_activeUser?.RefreshToken))
        {
            string? userMessage = json.GetValue("userMessage")?.ToString();
            if (userMessage != null && userMessage.Contains("The token has expired."))
            {
                bool refreshed = await _session.AttemptTokenRefresh(_activeUser, token);
                if (refreshed)
                    return await Call(method, path, formParameters, urlParameters, headers, baseUrl, token, attempt);
            }
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            JToken? errors = json["errors"];
            if (errors != null && errors.Any())
                throw new ResourceNotFoundException(errors[0]!["detail"]!.ToString());

            JToken? userMessage = json["userMessage"];
            if (userMessage != null)
                throw new ResourceNotFoundException(userMessage.ToString());

            throw new ResourceNotFoundException(json.ToString());
        }

        if (response.HasHttpError)
        {
            JToken? errors = json["errors"];
            if (errors != null && errors.Any())
                throw new APIException(errors[0]!["detail"]!.ToString());

            JToken? userMessage = json["userMessage"];
            if (userMessage != null)
                throw new APIException(userMessage.ToString());

            throw new APIException(json.ToString());
        }

        return json;
    }

    /// <summary>
    /// Delay before retrying a throttled request: honours <c>Retry-After</c> when Tidal
    /// sends it, otherwise backs off exponentially with jitter.
    /// </summary>
    private TimeSpan GetThrottleDelay(HttpResponse response, int attempt)
    {
        var retryAfter = response.Headers.GetSingleValue("Retry-After");

        if (!string.IsNullOrWhiteSpace(retryAfter))
        {
            // Either delta-seconds or an HTTP-date, per RFC 9110.
            if (int.TryParse(retryAfter, out var seconds) && seconds > 0)
            {
                var requested = TimeSpan.FromSeconds(seconds);
                return requested > ThrottleBackoffCeiling ? ThrottleBackoffCeiling : requested;
            }

            if (DateTimeOffset.TryParse(retryAfter, out var when))
            {
                var delta = when - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    return delta > ThrottleBackoffCeiling ? ThrottleBackoffCeiling : delta;
                }
            }
        }

        // 1x, 2x, 4x, 8x, 16x the backoff unit (+ jitter) - enough for the penalty to
        // lapse, while the retry cap stops a search from spinning indefinitely.
        var backoff = ThrottleBackoffUnit * Math.Pow(2, attempt);
        var jitter = ThrottleBackoffUnit * (Random.Shared.NextDouble() * 0.5);

        var delay = backoff + jitter;

        return delay > ThrottleBackoffCeiling ? ThrottleBackoffCeiling : delay;
    }

    public static string CompleteTitleFromPage(JToken page)
    {
        var title = page["title"]!.ToString();
        var version = page["version"]?.ToString();
        // we do the contains check as for whatever reason some albums (at least the one i looked at; 311544258) have the version already
        if (!string.IsNullOrEmpty(version) && !title.Contains(version, StringComparison.InvariantCulture))
            title = $"{title} ({version})";
        return title;
    }
}
