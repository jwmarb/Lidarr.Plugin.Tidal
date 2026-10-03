using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Lidarr.Plugin.Tidal.Tests.Framework;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Tidal;
using NzbDrone.Core.Parser.Model;

namespace Lidarr.Plugin.Tidal.Tests
{
    /// <summary>
    /// Failing-first regression tests for the three defects found while investigating
    /// "Taylor Swift - Patient Zero returns nothing".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each test states the behaviour the plugin SHOULD have. They are expected to fail
    /// against the current implementation and to pass once the corresponding fix lands;
    /// each carries the file/line of the code responsible.
    /// </para>
    /// <para>
    /// Context for the headline symptom: Tidal has no standalone "Patient Zero" single.
    /// The track exists only inside the album "The Life of a Showgirl: The Encore", while
    /// MusicBrainz (Lidarr's catalogue source) does list "Patient Zero" as a Single. That
    /// catalogue disagreement, not a plugin defect, is why the search looks empty — see
    /// <see cref="TrackSearchContractTests"/> for the behaviour that is actually correct.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class TidalParserRegressionTests
    {
        [TearDown]
        public void TearDown() => TidalApiHarness.ResetSingleton();

        private static IndexerResponse ResponseFor(string payload)
        {
            var request = new IndexerRequest("https://api.tidal.com/v1/search?query=test", HttpAccept.Json);
            var headers = new HttpHeader { { "Content-Type", "application/json" } };

            return new IndexerResponse(request, new HttpResponse(request.HttpRequest, headers, payload));
        }

        // ------------------------------------------------------------------
        // Bug 1 was withdrawn: see TrackSearchContractTests.
        //
        // The original assertion here demanded that a track-name search emit a release whose
        // Title names the track. That premise was wrong. ReleaseInfo has no track concept
        // (ReleaseInfo.cs), the download path requires an album URL
        // (DownloadItem.cs:227), and Lidarr permanently rejects any release whose parsed
        // album title differs from the searched one
        // (SingleAlbumSearchMatchSpecification.cs:33-37). Encoding the old demand would have
        // made the plugin claim one release while delivering another.
        // ------------------------------------------------------------------

        [Test]
        public void Bug1_the_album_containing_the_track_should_still_be_returned()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(http);

            var releases = new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));

            // This part already works and must keep working: the parent album is reachable.
            releases.Select(r => r.Album)
                .Should().Contain("The Life of a Showgirl: The Encore",
                    "the album that actually contains Patient Zero must remain discoverable");
        }

        // ------------------------------------------------------------------
        // Bug 2 — duplicate releases and redundant HTTP calls.
        // The dedupe check at TidalParser.cs:38 only compares against ALBUM
        // results, never against albums already fetched in this same loop, so
        // N track hits on one unlisted album produce N identical release sets.
        // ------------------------------------------------------------------

        [Test]
        public void Bug2_an_album_should_be_fetched_at_most_once_per_response()
        {
            var http = new FakeHttpClient(Fixture.DuplicateAlbumSearch());
            TidalApiHarness.Install(http);

            new TidalParser().ParseResponse(ResponseFor(Fixture.DuplicateAlbumSearch()));

            http.RedundantAlbumFetches.Should().BeEmpty(
                "every track hit in this fixture belongs to the SAME album, so one GetAlbum call " +
                $"suffices; the parser issued {http.AlbumFetches.Count} calls for " +
                $"{http.AlbumFetches.Distinct().Count()} distinct album(s)");
        }

        [Test]
        public void Bug2_should_not_emit_duplicate_releases_for_the_same_album_and_quality()
        {
            var http = new FakeHttpClient(Fixture.DuplicateAlbumSearch());
            TidalApiHarness.Install(http);

            var releases = new TidalParser().ParseResponse(ResponseFor(Fixture.DuplicateAlbumSearch()));

            var duplicated = releases
                .GroupBy(r => r.Guid)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} x{g.Count()}")
                .ToList();

            duplicated.Should().BeEmpty(
                "Guid is album id + quality, so repeats are pure duplicates shown to the user: " +
                string.Join(", ", duplicated));
        }

        [Test]
        public void Bug2_duplicate_free_parsing_also_holds_for_the_patient_zero_search()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(http);

            var releases = new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));

            releases.Select(r => r.Guid).Should().OnlyHaveUniqueItems(
                "the realistic search response must not produce duplicate releases either");
        }

        // ------------------------------------------------------------------
        // Bug 3 — unparseable audioQuality throws and loses the whole page.
        // Enum.Parse<AudioQuality>(result.AudioQuality) at TidalParser.cs:61
        // throws on any value outside the enum, and its result is never used.
        // Tidal has historically served MQA / DOLBY_ATMOS in this field.
        // ------------------------------------------------------------------

        [Test]
        public void Bug3_an_unknown_audioQuality_should_not_discard_the_results()
        {
            var payload = SearchContainingAlbum(Fixture.Json("album-unknown-audioquality.json"));
            var http = new FakeHttpClient(payload);
            TidalApiHarness.Install(http);

            var act = () => new TidalParser().ParseResponse(ResponseFor(payload));

            act.Should().NotThrow(
                "audioQuality is informational and its parsed value is never used; an unrecognised " +
                "value such as MQA must not throw away every other release in the response");
        }

        [Test]
        public void Bug3_quality_variants_come_from_tags_not_from_the_audioQuality_field()
        {
            var payload = SearchContainingAlbum(Fixture.Json("album-unknown-audioquality.json"));
            var http = new FakeHttpClient(payload);
            TidalApiHarness.Install(http);

            IList<ReleaseInfo> releases = null;
            var act = () => releases = new TidalParser().ParseResponse(ResponseFor(payload));

            act.Should().NotThrow();

            // The fixture keeps its LOSSLESS + HIRES_LOSSLESS tags, so all four variants apply
            // even though audioQuality itself is unrecognised.
            releases.Should().NotBeNull();
            releases.Select(r => r.Container)
                .Should().Contain(new[] { "96", "320", "Lossless", "24bit Lossless" });
        }

        [Test]
        public void Bug3_a_missing_mediaMetadata_block_should_not_throw()
        {
            var payload = SearchContainingAlbum(Fixture.Json("album-null-mediametadata.json"));
            var http = new FakeHttpClient(payload);
            TidalApiHarness.Install(http);

            var act = () => new TidalParser().ParseResponse(ResponseFor(payload));

            act.Should().NotThrow(
                "MediaMetadata.Tags is dereferenced without a null guard (TidalParser.cs:53); " +
                "an album payload lacking mediaMetadata must degrade to the lossy variants " +
                "rather than killing the whole search");
        }

        /// <summary>
        /// Wraps a single album payload in the minimal search-response envelope the parser
        /// expects, so one album can be put under test in isolation.
        /// </summary>
        private static string SearchContainingAlbum(JObject album)
        {
            var response = new JObject
            {
                ["albums"] = new JObject
                {
                    ["limit"] = 100,
                    ["offset"] = 0,
                    ["totalNumberOfItems"] = 1,
                    ["items"] = new JArray { album },
                },
                ["tracks"] = new JObject
                {
                    ["limit"] = 100,
                    ["offset"] = 0,
                    ["totalNumberOfItems"] = 0,
                    ["items"] = new JArray(),
                },
            };

            return response.ToString();
        }
    }
}
