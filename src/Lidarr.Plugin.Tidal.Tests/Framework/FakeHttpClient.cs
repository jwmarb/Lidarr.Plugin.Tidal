using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NzbDrone.Common.Http;

namespace Lidarr.Plugin.Tidal.Tests.Framework
{
    /// <summary>
    /// An <see cref="IHttpClient"/> that answers from recorded fixtures instead of the network,
    /// and records every request so tests can assert on call counts.
    /// </summary>
    /// <remarks>
    /// Only the members TidalSharp's <c>API.Call</c> actually uses are implemented;
    /// everything else throws so an unexpected code path fails loudly rather than silently.
    /// </remarks>
    public sealed class FakeHttpClient : IHttpClient
    {
        private static readonly Regex AlbumPath = new(@"/albums/(?<id>\d+)", RegexOptions.Compiled);

        private readonly IReadOnlyDictionary<string, string> _albumsById;
        private readonly string _searchPayload;

        public FakeHttpClient(string searchPayload, IReadOnlyDictionary<string, string> albumsById = null)
        {
            _searchPayload = searchPayload;
            _albumsById = albumsById ?? Fixture.AlbumsById();
        }

        /// <summary>Every URL requested, in order.</summary>
        public List<string> RequestedUrls { get; } = new();

        /// <summary>Album ids fetched via <c>albums/{id}</c>, in order (duplicates included).</summary>
        public List<string> AlbumFetches { get; } = new();

        /// <summary>Album ids that were fetched more than once.</summary>
        public IReadOnlyCollection<string> RedundantAlbumFetches =>
            AlbumFetches.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        /// <summary>Album ids for which no fixture exists; these answer 404.</summary>
        public List<string> MissingAlbumIds { get; } = new();

        public HttpResponse Execute(HttpRequest request)
        {
            var url = request.Url.FullUri;
            RequestedUrls.Add(url);

            var albumMatch = AlbumPath.Match(url);

            if (albumMatch.Success)
            {
                var id = albumMatch.Groups["id"].Value;
                AlbumFetches.Add(id);

                if (_albumsById.TryGetValue(id, out var albumJson))
                {
                    return Respond(request, albumJson);
                }

                MissingAlbumIds.Add(id);

                // Mirrors how Tidal reports an unknown album; TidalSharp turns this
                // into a ResourceNotFoundException.
                return Respond(
                    request,
                    "{\"status\":404,\"subStatus\":2001,\"userMessage\":\"not found\",\"errors\":[{\"detail\":\"Album not found\"}]}",
                    HttpStatusCode.NotFound);
            }

            if (url.Contains("/search", StringComparison.OrdinalIgnoreCase))
            {
                return Respond(request, _searchPayload);
            }

            throw new InvalidOperationException(
                $"FakeHttpClient received an unexpected request: {url}");
        }

        private static HttpResponse Respond(HttpRequest request, string content, HttpStatusCode status = HttpStatusCode.OK)
        {
            var headers = new HttpHeader { { "Content-Type", "application/json" } };
            return new HttpResponse(request, headers, content, status);
        }

        public Task<HttpResponse> ExecuteAsync(HttpRequest request) => Task.FromResult(Execute(request));

        public HttpResponse Get(HttpRequest request) => Execute(request);

        public Task<HttpResponse> GetAsync(HttpRequest request) => Task.FromResult(Execute(request));

        public HttpResponse<T> Get<T>(HttpRequest request)
            where T : new() => new(Execute(request));

        public async Task<HttpResponse<T>> GetAsync<T>(HttpRequest request)
            where T : new() => new(await ExecuteAsync(request));

        public HttpResponse Head(HttpRequest request) => Execute(request);

        public Task<HttpResponse> HeadAsync(HttpRequest request) => Task.FromResult(Execute(request));

        public HttpResponse Post(HttpRequest request) => Execute(request);

        public Task<HttpResponse> PostAsync(HttpRequest request) => Task.FromResult(Execute(request));

        public HttpResponse<T> Post<T>(HttpRequest request)
            where T : new() => new(Execute(request));

        public async Task<HttpResponse<T>> PostAsync<T>(HttpRequest request)
            where T : new() => new(await ExecuteAsync(request));

        public void DownloadFile(string url, string fileName) =>
            throw new NotSupportedException("DownloadFile is not used by the indexer search path.");

        public Task DownloadFileAsync(string url, string fileName) =>
            throw new NotSupportedException("DownloadFileAsync is not used by the indexer search path.");
    }
}
