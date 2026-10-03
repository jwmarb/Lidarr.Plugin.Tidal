using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NzbDrone.Common.Http;

namespace Lidarr.Plugin.Tidal.Tests.Framework
{
    /// <summary>
    /// An <see cref="IHttpClient"/> that can be scripted to return specific status codes,
    /// so throttle handling can be exercised without the network.
    /// </summary>
    /// <remarks>
    /// Records the wall-clock time of every request, which is what lets the tests assert
    /// that retries actually back off instead of hammering.
    /// </remarks>
    public sealed class ThrottlingHttpClient : IHttpClient
    {
        private readonly Queue<HttpStatusCode> _scriptedStatuses = new();
        private readonly string _okPayload;
        private readonly string _retryAfter;

        /// <param name="throttledResponses">How many leading 429s to return.</param>
        /// <param name="okPayload">Body served once the throttle clears.</param>
        /// <param name="retryAfter">Optional Retry-After header value on the 429s.</param>
        public ThrottlingHttpClient(int throttledResponses, string okPayload = "{\"id\":\"1\",\"title\":\"ok\"}", string retryAfter = null)
        {
            for (var i = 0; i < throttledResponses; i++)
            {
                _scriptedStatuses.Enqueue(HttpStatusCode.TooManyRequests);
            }

            _okPayload = okPayload;
            _retryAfter = retryAfter;
        }

        /// <summary>Timestamp of each request, in order.</summary>
        public List<DateTime> RequestTimes { get; } = new();

        public int RequestCount => RequestTimes.Count;

        /// <summary>Gaps between consecutive requests.</summary>
        public IReadOnlyList<TimeSpan> Gaps =>
            RequestTimes.Zip(RequestTimes.Skip(1), (a, b) => b - a).ToList();

        public HttpResponse Execute(HttpRequest request)
        {
            RequestTimes.Add(DateTime.UtcNow);

            var status = _scriptedStatuses.Count > 0 ? _scriptedStatuses.Dequeue() : HttpStatusCode.OK;
            var headers = new HttpHeader { { "Content-Type", "application/json" } };

            if (status == HttpStatusCode.TooManyRequests)
            {
                if (_retryAfter != null)
                {
                    headers.Add("Retry-After", _retryAfter);
                }

                // Tidal returns an empty body on 429; TidalSharp must cope with that.
                return new HttpResponse(request, headers, "{}", status);
            }

            return new HttpResponse(request, headers, _okPayload, status);
        }

        public Task<HttpResponse> ExecuteAsync(HttpRequest request) => Task.FromResult(Execute(request));

        public HttpResponse Get(HttpRequest request) => Execute(request);

        public Task<HttpResponse> GetAsync(HttpRequest request) => ExecuteAsync(request);

        public HttpResponse<T> Get<T>(HttpRequest request)
            where T : new() => new(Execute(request));

        public async Task<HttpResponse<T>> GetAsync<T>(HttpRequest request)
            where T : new() => new(await ExecuteAsync(request).ConfigureAwait(false));

        public HttpResponse Head(HttpRequest request) => Execute(request);

        public Task<HttpResponse> HeadAsync(HttpRequest request) => ExecuteAsync(request);

        public HttpResponse Post(HttpRequest request) => Execute(request);

        public Task<HttpResponse> PostAsync(HttpRequest request) => ExecuteAsync(request);

        public HttpResponse<T> Post<T>(HttpRequest request)
            where T : new() => new(Execute(request));

        public async Task<HttpResponse<T>> PostAsync<T>(HttpRequest request)
            where T : new() => new(await ExecuteAsync(request).ConfigureAwait(false));

        public void DownloadFile(string url, string fileName) => throw new NotSupportedException();

        public Task DownloadFileAsync(string url, string fileName) => throw new NotSupportedException();
    }
}
