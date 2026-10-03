using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NzbDrone.Common.Http;

namespace Lidarr.Plugin.Tidal.Tests.Framework
{
    /// <summary>
    /// A real-network <see cref="IHttpClient"/> for the integration tests.
    /// </summary>
    /// <remarks>
    /// Lidarr's own <c>HttpClient</c> needs a five-dependency DI graph (interceptors, cache
    /// manager, rate limiter, dispatcher, logger) that a test cannot assemble cheaply, so
    /// this adapter maps the handful of members TidalSharp uses onto
    /// <see cref="System.Net.Http.HttpClient"/>.
    /// </remarks>
    public sealed class TestHttpClient : IHttpClient
    {
        private static readonly System.Net.Http.HttpClient Transport = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        public static TestHttpClient Create() => new();

        public HttpResponse Execute(HttpRequest request) => ExecuteAsync(request).GetAwaiter().GetResult();

        public async Task<HttpResponse> ExecuteAsync(HttpRequest request)
        {
            using var message = new HttpRequestMessage(
                new System.Net.Http.HttpMethod(request.Method.ToString()),
                request.Url.FullUri);

            foreach (var header in request.Headers)
            {
                // Content headers are rejected on the request message; only Tidal's
                // Authorization/Accept style headers matter here.
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.ContentData != null)
            {
                message.Content = new ByteArrayContent(request.ContentData);

                // The OAuth token endpoint requires the form content type. Lidarr's
                // HttpRequestBuilder records it on request.Headers.ContentType, which is a
                // content header and is therefore rejected on HttpRequestMessage.Headers
                // above - it must be set on the content instead or the exchange fails.
                var contentType = request.Headers.ContentType;

                if (!string.IsNullOrEmpty(contentType))
                {
                    message.Content.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                }
            }

            using var response = await Transport.SendAsync(message).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var headers = new HttpHeader();

            foreach (var header in response.Headers)
            {
                headers.Add(header.Key, string.Join(",", header.Value));
            }

            return new HttpResponse(request, headers, body, response.StatusCode);
        }

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

        public void DownloadFile(string url, string fileName) =>
            throw new NotSupportedException("Integration tests do not download files.");

        public Task DownloadFileAsync(string url, string fileName) =>
            throw new NotSupportedException("Integration tests do not download files.");
    }
}
