using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Lidarr.Plugin.Tidal.Tests.Framework;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Plugin.Tidal;

namespace Lidarr.Plugin.Tidal.Tests.Integration
{
    /// <summary>
    /// Tests that talk to the real Tidal API using the credentials in <c>.env</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opt in with <c>TIDAL_INTEGRATION=1 dotnet test</c>. Without it every test here is
    /// skipped (not failed), so the default suite and CI stay offline and deterministic.
    /// </para>
    /// <para>
    /// These assert on the SHAPE of Tidal's responses rather than exact chart positions or
    /// counts, so they stay stable as the catalogue changes. Their job is to catch the two
    /// things fixtures cannot: credentials going stale, and Tidal changing its contract.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("Integration")]
    [Explicit("Hits the live Tidal API; run with TIDAL_INTEGRATION=1.")]
    public class LiveTidalApiTests
    {
        private const string PatientZero = "Patient Zero";
        private const string TaylorSwift = "Taylor Swift";

        private TidalAPI _api;

        [SetUp]
        public void SetUp()
        {
            var reason = LiveCredentials.SkipReason;

            if (reason != null)
            {
                Assert.Ignore(reason);
            }

            _api = LiveCredentials.InstallLiveApi(TestHttpClient.Create());
        }

        [TearDown]
        public void TearDown() => TidalApiHarness.ResetSingleton();

        [Test]
        public void Credentials_in_env_are_valid_and_resolve_a_session()
        {
            // If this fails, the token has expired and every other live test is noise.
            _api.Client.ActiveUser.Should().NotBeNull();
            _api.Client.ActiveUser!.CountryCode.Should().NotBeNullOrEmpty(
                "a successful GET /sessions populates the country code");
            _api.Client.ActiveUser.SessionID.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void Search_returns_the_patient_zero_track_for_taylor_swift()
        {
            var response = Search($"{TaylorSwift} {PatientZero}");

            var hits = response["tracks"]!["items"]!
                .Where(t => string.Equals(t["title"]?.ToString(), PatientZero, StringComparison.OrdinalIgnoreCase)
                            && t["artists"]!.Any(a => a["name"]?.ToString() == TaylorSwift))
                .ToList();

            hits.Should().NotBeEmpty(
                "Tidal does surface the track; if this fails the premise of the whole " +
                "investigation changed (the track was pulled, or search behaviour changed)");
        }

        [Test]
        public void The_patient_zero_track_is_an_album_track_not_a_standalone_single()
        {
            var response = Search($"{TaylorSwift} {PatientZero}");

            var albumTitles = response["tracks"]!["items"]!
                .Where(t => string.Equals(t["title"]?.ToString(), PatientZero, StringComparison.OrdinalIgnoreCase)
                            && t["artists"]!.Any(a => a["name"]?.ToString() == TaylorSwift))
                .Select(t => t["album"]!["title"]!.ToString())
                .Distinct()
                .ToList();

            albumTitles.Should().NotBeEmpty();

            // This is the root cause of the user-visible symptom, asserted against live data:
            // the track only exists inside an album, so an album-oriented indexer cannot
            // surface it under its own name.
            albumTitles.Should().OnlyContain(
                title => title.IndexOf("Showgirl", StringComparison.OrdinalIgnoreCase) >= 0,
                "Patient Zero ships only on \"The Life of a Showgirl: The Encore\"");
        }

        [Test]
        public void Tidal_has_no_standalone_patient_zero_release_by_taylor_swift()
        {
            var response = Search(PatientZero);

            var taylorAlbums = response["albums"]!["items"]!
                .Where(a => a["artists"]!.Any(artist => artist["name"]?.ToString() == TaylorSwift))
                .Select(a => a["title"]!.ToString())
                .ToList();

            taylorAlbums.Should().BeEmpty(
                "searching the album endpoint for \"Patient Zero\" must not yield a Taylor Swift " +
                "release; the name-matching results belong to other artists, which is exactly why " +
                "an album-titled search misleads the user");
        }

        [Test]
        public void Albums_holding_the_track_are_absent_from_the_album_results()
        {
            var response = Search($"{TaylorSwift} {PatientZero}");

            var trackAlbumIds = response["tracks"]!["items"]!
                .Where(t => string.Equals(t["title"]?.ToString(), PatientZero, StringComparison.OrdinalIgnoreCase))
                .Select(t => t["album"]!["id"]!.ToString())
                .Distinct()
                .ToList();

            var albumResultIds = response["albums"]!["items"]!
                .Select(a => a["id"]!.ToString())
                .ToHashSet(StringComparer.Ordinal);

            trackAlbumIds.Should().NotBeEmpty();

            // Proves the parser's track->GetAlbum branch is the only path to this album,
            // which is what makes the duplicate-fetch bug reachable in production.
            trackAlbumIds.Should().OnlyContain(id => !albumResultIds.Contains(id));
        }

        [Test]
        public void Album_lookup_returns_the_metadata_the_parser_depends_on()
        {
            var response = Search($"{TaylorSwift} {PatientZero}");

            var albumId = response["tracks"]!["items"]!
                .First(t => string.Equals(t["title"]?.ToString(), PatientZero, StringComparison.OrdinalIgnoreCase))["album"]!["id"]!
                .ToString();

            var album = _api.Client.API.GetAlbum(albumId).GetAwaiter().GetResult();

            album["title"].Should().NotBeNull();
            album["duration"].Should().NotBeNull("size estimation multiplies duration by a bitrate");
            album["artists"]!.Should().NotBeEmpty("the release title uses the first artist");
            album["mediaMetadata"].Should().NotBeNull("quality variants are derived from the tags");
        }

        [Test]
        public void Live_audioQuality_values_may_fall_outside_the_AudioQuality_enum()
        {
            // Documents the real-world risk behind the Enum.Parse crash: the field is a
            // free-form string upstream. Today's values parse, but DOLBY_ATMOS/MQA have
            // been observed, so the parser must not depend on it.
            var known = new HashSet<string>(Enum.GetNames<TidalSharp.Data.AudioQuality>(), StringComparer.Ordinal);

            var response = Search($"{TaylorSwift} {PatientZero}");

            var observed = response["albums"]!["items"]!
                .Select(a => a["audioQuality"]?.ToString())
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct()
                .ToList();

            observed.Should().NotBeEmpty();

            TestContext.WriteLine($"Observed audioQuality values: {string.Join(", ", observed)}");
            TestContext.WriteLine($"Unparseable by AudioQuality: {string.Join(", ", observed.Where(v => !known.Contains(v)))}");

            // Deliberately not asserted as a failure: this test records the contract rather
            // than policing Tidal. The parser-level guard is covered by the Bug3 unit tests.
            Assert.Pass("Recorded the live audioQuality vocabulary; see test output.");
        }

        private JObject Search(string query)
        {
            var url = TidalAPI.Instance.GetAPIUrl("search", new Dictionary<string, string>
            {
                ["query"] = query,
                ["limit"] = "100",
                ["types"] = "albums,tracks",
                ["offset"] = "0",
            });

            var request = new HttpRequest(url, HttpAccept.Json);
            request.Headers.Add(
                "Authorization",
                $"{TidalAPI.Instance.Client.ActiveUser.TokenType} {TidalAPI.Instance.Client.ActiveUser.AccessToken}");

            var response = TestHttpClient.Create().Execute(request);

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
                "a non-200 here usually means the token expired");

            return JObject.Parse(response.Content);
        }
    }
}
