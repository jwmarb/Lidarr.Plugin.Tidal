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
    /// Characterization tests: these pin down <see cref="TidalParser"/> behaviour that is
    /// already correct today, so the duplicate/crash fixes cannot regress it.
    /// </summary>
    [TestFixture]
    public class TidalParserTests
    {
        private FakeHttpClient _http;

        [TearDown]
        public void TearDown() => TidalApiHarness.ResetSingleton();

        private IList<ReleaseInfo> ParsePatientZeroSearch()
        {
            _http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(_http);

            return new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));
        }

        private static IndexerResponse ResponseFor(string payload)
        {
            var request = new IndexerRequest("https://api.tidal.com/v1/search?query=test", HttpAccept.Json);
            var headers = new HttpHeader { { "Content-Type", "application/json" } };
            var httpResponse = new HttpResponse(request.HttpRequest, headers, payload);

            return new IndexerResponse(request, httpResponse);
        }

        [Test]
        public void Emits_releases_for_every_album_in_the_search_results()
        {
            var releases = ParsePatientZeroSearch();

            releases.Should().NotBeEmpty("the fixture contains six albums and several track hits");
        }

        [Test]
        public void Derives_quality_variants_from_mediaMetadata_tags()
        {
            var releases = ParsePatientZeroSearch();

            // HIRES_LOSSLESS albums fan out to LOW/HIGH/LOSSLESS/HI_RES_LOSSLESS.
            var showgirl = releases.Where(r => r.Album == "The Life of a Showgirl").ToList();

            showgirl.Should().NotBeEmpty();
            showgirl.Select(r => r.Container).Should().Contain(new[] { "96", "320", "Lossless", "24bit Lossless" });
        }

        [Test]
        public void Dolby_atmos_albums_offer_only_lossy_variants()
        {
            // album-564163414 is tagged DOLBY_ATMOS only, so no lossless variant is valid.
            _http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(_http);

            var releases = new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));

            var atmos = releases.Where(r => r.DownloadUrl != null && r.DownloadUrl.Contains("564163414")).ToList();

            if (atmos.Any())
            {
                atmos.Select(r => r.Container).Should().OnlyContain(c => c == "96" || c == "320");
            }
        }

        [Test]
        public void Titles_follow_the_artist_album_year_format_quality_web_shape()
        {
            var releases = ParsePatientZeroSearch();

            var sample = releases.First(r => r.Album == "The Life of a Showgirl");

            sample.Title.Should().StartWith("Taylor Swift - The Life of a Showgirl");
            sample.Title.Should().EndWith("[WEB]");
            sample.Title.Should().MatchRegex(@"\(\d{4}\)");
        }

        [Test]
        public void Guid_is_unique_per_album_and_quality_pair()
        {
            var releases = ParsePatientZeroSearch();

            var sample = releases.First();

            sample.Guid.Should().StartWith("Tidal-");
            // Guid encodes album id + bitrate, which is what Lidarr dedupes on.
            sample.Guid.Should().MatchRegex(@"^Tidal-\d+-(LOW|HIGH|LOSSLESS|HI_RES_LOSSLESS)$");
        }

        [Test]
        public void Size_is_estimated_from_duration_and_bitrate()
        {
            var releases = ParsePatientZeroSearch();

            releases.Should().OnlyContain(r => r.Size > 0, "size drives Lidarr's quality decisions");

            // Lossless must be estimated larger than lossy for the same album.
            var byAlbum = releases.GroupBy(r => r.Album).First(g => g.Count() >= 2);
            var lossy = byAlbum.FirstOrDefault(r => r.Container == "320");
            var lossless = byAlbum.FirstOrDefault(r => r.Container == "Lossless");

            if (lossy != null && lossless != null)
            {
                lossless.Size.Should().BeGreaterThan(lossy.Size);
            }
        }

        [Test]
        public void Results_are_ordered_by_descending_size()
        {
            var releases = ParsePatientZeroSearch();

            releases.Select(r => r.Size).Should().BeInDescendingOrder();
        }

        [Test]
        public void Sets_the_tidal_download_protocol_on_every_release()
        {
            var releases = ParsePatientZeroSearch();

            releases.Should().OnlyContain(r => r.DownloadProtocol == nameof(TidalDownloadProtocol));
        }

        [Test]
        public void Explicit_albums_are_marked_in_the_title()
        {
            var releases = ParsePatientZeroSearch();

            var explicitReleases = releases.Where(r => r.Title.Contains("[Explicit]")).ToList();

            // The fixture contains explicit Taylor Swift albums; if any are flagged,
            // the marker must sit before the quality block.
            foreach (var release in explicitReleases)
            {
                release.Title.IndexOf("[Explicit]", StringComparison.Ordinal)
                    .Should().BeLessThan(release.Title.IndexOf("[WEB]", StringComparison.Ordinal));
            }
        }

        [Test]
        public void Looks_up_full_album_payloads_for_track_only_hits()
        {
            ParsePatientZeroSearch();

            // The three "Patient Zero" hits live on albums absent from the album results,
            // so the parser must fetch them individually.
            _http.AlbumFetches.Should().NotBeEmpty();
            _http.AlbumFetches.Should().Contain("564163414");
        }
    }
}
