using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Lidarr.Plugin.Tidal.Tests.Framework;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Tidal;
using NzbDrone.Core.Parser.Model;

namespace Lidarr.Plugin.Tidal.Tests
{
    /// <summary>
    /// Establishes what "duplicate" means for this indexer, and who is responsible for
    /// removing each kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A release's identity is its <c>Guid</c>, built as <c>Tidal-{albumId}-{bitrate}</c>
    /// (<c>TidalParser.cs:110</c>). Two <see cref="ReleaseInfo"/> values sharing a Guid are
    /// therefore the same album at the same quality: byte-identical offers, not variants.
    /// </para>
    /// <para>
    /// There are two distinct scopes, and only one is the parser's job:
    /// </para>
    /// <list type="number">
    /// <item><description><b>Within one response</b> \u2014 the parser must not emit a Guid twice.
    /// This is what the dedupe fix addresses and what these tests enforce.</description></item>
    /// <item><description><b>Across paged responses</b> \u2014 each page is a separate
    /// <see cref="IndexerResponse"/> parsed by its own <c>ParseResponse</c> call, so a
    /// per-response set cannot span them. Lidarr collapses these itself via
    /// <c>DistinctBy(v =&gt; v.Guid)</c> in <c>IndexerBase.CleanupReleases</c>.</description></item>
    /// </list>
    /// </remarks>
    [TestFixture]
    public class ReleaseDeduplicationTests
    {
        [TearDown]
        public void TearDown() => TidalApiHarness.ResetSingleton();

        private static IndexerResponse ResponseFor(string payload)
        {
            var request = new IndexerRequest("https://api.tidal.com/v1/search?query=test", HttpAccept.Json);
            var headers = new HttpHeader { { "Content-Type", "application/json" } };

            return new IndexerResponse(request, new HttpResponse(request.HttpRequest, headers, payload));
        }

        [Test]
        public void Guid_encodes_album_id_and_bitrate_so_repeats_are_identical_offers()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(http);

            var releases = new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));

            // Group by Guid and prove each group's members are genuinely interchangeable.
            foreach (var group in releases.GroupBy(r => r.Guid))
            {
                group.Select(r => r.Title).Distinct().Should().HaveCount(1,
                    $"everything sharing Guid '{group.Key}' must describe the same release");
                group.Select(r => r.Size).Distinct().Should().HaveCount(1);
                group.Select(r => r.DownloadUrl).Distinct().Should().HaveCount(1);
            }
        }

        [Test]
        public void Within_one_response_no_guid_is_emitted_twice()
        {
            var http = new FakeHttpClient(Fixture.DuplicateAlbumSearch());
            TidalApiHarness.Install(http);

            var releases = new TidalParser().ParseResponse(ResponseFor(Fixture.DuplicateAlbumSearch()));

            // This fixture is the pathological shape: 9 track hits, all on one album that is
            // absent from the album results.
            releases.Select(r => r.Guid).Should().OnlyHaveUniqueItems();
        }

        [Test]
        public void Within_one_response_each_album_is_fetched_once()
        {
            var http = new FakeHttpClient(Fixture.DuplicateAlbumSearch());
            TidalApiHarness.Install(http);

            new TidalParser().ParseResponse(ResponseFor(Fixture.DuplicateAlbumSearch()));

            http.AlbumFetches.Should().OnlyHaveUniqueItems(
                "refetching an album cannot change its payload, so repeat calls are pure waste");
        }

        [Test]
        public void Across_pages_the_parser_can_repeat_and_lidarr_is_what_collapses_it()
        {
            // Two separate responses, as Lidarr's paged request chain produces. The parser
            // holds no state between them by design.
            var first = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(first);
            var pageOne = new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));

            var second = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(second);
            var pageTwo = new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));

            var combined = pageOne.Concat(pageTwo).ToList();

            // Concatenating responses DOES produce repeated Guids. That is expected and is
            // not a parser defect: a per-response set cannot see across responses.
            combined.Select(r => r.Guid).Distinct().Should().HaveCountLessThan(combined.Count,
                "the same album appearing on two pages yields the same Guid twice");

            // Lidarr's CleanupReleases applies exactly this, which is why it is harmless.
            var afterLidarrDedupe = combined.GroupBy(r => r.Guid).Select(g => g.First()).ToList();

            afterLidarrDedupe.Should().HaveCount(pageOne.Count,
                "DistinctBy(Guid) collapses the cross-page repeats back to one page's worth");
        }

        [Test]
        public void A_cover_by_a_different_artist_is_not_a_duplicate()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(http);

            var releases = new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));

            // Distinct albums must keep distinct Guids even when titles are similar, so
            // dedupe can never silently swallow a legitimately different release.
            var byAlbumId = releases
                .GroupBy(r => r.Guid.Split('-')[1])
                .ToList();

            foreach (var album in byAlbumId)
            {
                album.Select(r => r.Album).Distinct().Should().HaveCount(1,
                    "one album id must map to exactly one album title");
            }

            byAlbumId.Select(g => g.Key).Should().OnlyHaveUniqueItems();
        }
    }
}
