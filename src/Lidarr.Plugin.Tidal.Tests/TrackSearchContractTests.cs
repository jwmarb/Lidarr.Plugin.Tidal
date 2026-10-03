using System;
using System.Linq;
using FluentAssertions;
using Lidarr.Plugin.Tidal.Tests.Framework;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Tidal;
using NzbDrone.Core.Parser;

namespace Lidarr.Plugin.Tidal.Tests
{
    /// <summary>
    /// Specifies what a track-name search is actually allowed to produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests replace a withdrawn assertion that a track-name search should emit a
    /// release naming the track. Investigating "Taylor Swift - Patient Zero returns nothing"
    /// showed that premise was wrong on three counts:
    /// </para>
    /// <list type="number">
    /// <item><description><c>ReleaseInfo</c> has no track or track-number field, so a track
    /// cannot be expressed as a release without lying about what it is.</description></item>
    /// <item><description><c>DownloadItem.SetTidalData</c> throws
    /// <c>InvalidOperationException</c> for any non-album URL and then downloads the whole
    /// album (<c>DownloadItem.cs:227</c>).</description></item>
    /// <item><description>Lidarr re-parses the release <c>Title</c> and permanently rejects
    /// any release whose parsed album title differs from the searched album title
    /// (<c>SingleAlbumSearchMatchSpecification.cs:33-37</c>).</description></item>
    /// </list>
    /// <para>
    /// The real cause of the user-visible symptom is a catalogue disagreement: MusicBrainz
    /// lists "Patient Zero" as a Single, Tidal ships it only inside an album. The correct
    /// outcome is therefore "no release satisfies that single", not a substituted album.
    /// These tests pin the honest behaviour so nobody later "fixes" it into a false match.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class TrackSearchContractTests
    {
        private const string ContainingAlbum = "The Life of a Showgirl: The Encore";

        [TearDown]
        public void TearDown() => TidalApiHarness.ResetSingleton();

        private static IndexerResponse ResponseFor(string payload)
        {
            var request = new IndexerRequest("https://api.tidal.com/v1/search?query=test", HttpAccept.Json);
            var headers = new HttpHeader { { "Content-Type", "application/json" } };

            return new IndexerResponse(request, new HttpResponse(request.HttpRequest, headers, payload));
        }

        private static System.Collections.Generic.IList<NzbDrone.Core.Parser.Model.ReleaseInfo> ParsePatientZero()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(http);

            return new TidalParser().ParseResponse(ResponseFor(Fixture.PatientZeroSearch()));
        }

        [Test]
        public void A_track_hit_makes_its_containing_album_discoverable()
        {
            var releases = ParsePatientZero();

            releases.Select(r => r.Album).Should().Contain(ContainingAlbum,
                "the album holding Patient Zero is absent from Tidal's album results, so the " +
                "track-to-album lookup is the only path that surfaces it");
        }

        [Test]
        public void Every_release_describes_an_album_never_a_single_track()
        {
            var releases = ParsePatientZero();

            releases.Should().NotBeEmpty();

            // No release may claim to BE the track. Claiming it would make Lidarr accept a
            // release it cannot import truthfully, since the downloaded file's tags name the
            // parent album.
            releases.Select(r => r.Album).Should().NotContain("Patient Zero");
            releases.Should().OnlyContain(r => !r.Title.Contains("Patient Zero", StringComparison.OrdinalIgnoreCase)
                                              || !r.Artist.Equals("Taylor Swift", StringComparison.OrdinalIgnoreCase),
                "no Taylor Swift release may be titled after the track");
        }

        [Test]
        public void Download_urls_always_point_at_an_album_never_a_track()
        {
            var releases = ParsePatientZero();

            // DownloadItem.SetTidalData throws on any non-album entity, so emitting a
            // /track/ URL would queue an item that always fails.
            releases.Should().OnlyContain(r => !r.DownloadUrl.Contains("/track/"),
                "the download client requires an album URL (DownloadItem.cs:227)");

            foreach (var release in releases)
            {
                TidalSharp.TidalURL.TryParse(release.DownloadUrl, out var parsedUrl).Should().BeTrue(
                    $"every DownloadUrl must be parseable by TidalURL, but '{release.DownloadUrl}' was not");
                parsedUrl.Should().NotBeNull();
            }
        }

        [Test]
        public void Emitted_download_urls_resolve_to_the_album_entity_type()
        {
            var releases = ParsePatientZero();

            foreach (var release in releases)
            {
                TidalSharp.TidalURL.TryParse(release.DownloadUrl, out var url).Should().BeTrue();
                url.EntityType.Should().Be(TidalSharp.Data.EntityType.Album,
                    $"'{release.DownloadUrl}' must be an album URL for the download client to accept it");
            }
        }

        [Test]
        public void Release_titles_parse_back_to_the_album_they_describe()
        {
            var releases = ParsePatientZero();

            var encore = releases.First(r => r.Album == ContainingAlbum);

            // Lidarr trusts the parsed Title, not ReleaseInfo.Album, when matching.
            var parsed = Parser.ParseAlbumTitle(encore.Title);

            parsed.Should().NotBeNull($"Lidarr must be able to parse '{encore.Title}'");
            parsed.AlbumTitle.Should().Be(ContainingAlbum,
                "the title must round-trip to the same album, or Lidarr will mis-match it");
        }

        [Test]
        public void A_search_for_the_standalone_single_cannot_be_satisfied_by_the_album()
        {
            var releases = ParsePatientZero();
            var encore = releases.First(r => r.Album == ContainingAlbum);

            var parsed = Parser.ParseAlbumTitle(encore.Title);

            // This mirrors SingleAlbumSearchMatchSpecification's comparison exactly.
            var searchedForSingle = Parser.CleanArtistName("Patient Zero");
            var offered = Parser.CleanArtistName(parsed.AlbumTitle);

            offered.Should().NotBe(searchedForSingle,
                "Lidarr rejects this as \"Wrong album\", and that is CORRECT: Tidal has no " +
                "release matching the MusicBrainz single, so substituting the parent album " +
                "would download and tag something the user did not ask for");
        }

        [Test]
        public void A_search_for_the_containing_album_is_satisfied_by_the_same_release()
        {
            var releases = ParsePatientZero();
            var encore = releases.First(r => r.Album == ContainingAlbum);

            var parsed = Parser.ParseAlbumTitle(encore.Title);

            // The other side of the boundary: when Lidarr asks for the album that really
            // exists on Tidal, this release must match.
            Parser.CleanArtistName(parsed.AlbumTitle)
                .Should().Be(Parser.CleanArtistName(ContainingAlbum),
                    "the release must satisfy a search for the album it actually is");
        }

        [Test]
        public void The_containing_album_is_offered_in_every_quality_tidal_advertises()
        {
            var releases = ParsePatientZero();

            var encore = releases.Where(r => r.Album == ContainingAlbum).ToList();

            encore.Should().NotBeEmpty();

            // A user who wants the track needs the album; it must be offered at the best
            // quality available, including lossless editions.
            encore.Select(r => r.Container).Should().Contain("Lossless",
                "the lossless edition of the containing album must be reachable");
        }
    }
}
