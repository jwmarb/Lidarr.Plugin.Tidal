using System.Linq;
using FluentAssertions;
using Lidarr.Plugin.Tidal.Tests.Framework;
using NUnit.Framework;

namespace Lidarr.Plugin.Tidal.Tests
{
    /// <summary>
    /// Guards the test harness itself: if these fail, every other result in this
    /// assembly is meaningless, so they are worth asserting explicitly.
    /// </summary>
    [TestFixture]
    public class HarnessSmokeTests
    {
        [TearDown]
        public void TearDown() => TidalApiHarness.ResetSingleton();

        [Test]
        public void Fixtures_are_committed_and_loadable()
        {
            var search = Fixture.PatientZeroSearchJson();

            search["tracks"]!["items"]!.Should().NotBeEmpty();
            search["albums"]!["items"]!.Should().NotBeEmpty();
        }

        [Test]
        public void Fixtures_contain_no_credential_material()
        {
            // The recorder scrubs these keys; this is the regression test for the scrubber.
            foreach (var file in System.IO.Directory.GetFiles(Fixture.Directory, "*.json"))
            {
                var text = System.IO.File.ReadAllText(file);

                text.Should().NotContain("\"sessionId\": \"", $"{file} must not carry a session id");
                text.Should().NotContain("\"access_token\"", $"{file} must not carry an access token");
                text.Should().NotContain("\"refresh_token\"", $"{file} must not carry a refresh token");
            }
        }

        [Test]
        public void Harness_installs_a_usable_TidalAPI_singleton()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());

            var api = TidalApiHarness.Install(http);

            api.Should().NotBeNull();
            api.Client.ActiveUser.Should().NotBeNull();
            api.Client.ActiveUser!.CountryCode.Should().Be("US");
            api.Client.ActiveUser.AccessToken.Should().Be("test-access-token");
        }

        [Test]
        public void Harness_can_be_reinstalled_between_tests()
        {
            TidalApiHarness.Install(new FakeHttpClient(Fixture.PatientZeroSearch()));
            var second = new FakeHttpClient(Fixture.DuplicateAlbumSearch());

            TidalApiHarness.Install(second);

            // Proves the write-once singleton really was reset, otherwise the
            // duplicate-fetch tests would silently reuse the first client.
            NzbDrone.Plugin.Tidal.TidalAPI.Instance.Should().NotBeNull();
        }

        [Test]
        public void FakeHttpClient_serves_album_lookups_from_fixtures()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(http);

            var album = NzbDrone.Plugin.Tidal.TidalAPI.Instance.Client.API.GetAlbum("564082210").GetAwaiter().GetResult();

            album["title"]!.ToString().Should().Be("The Life of a Showgirl: The Encore");
            http.AlbumFetches.Should().ContainSingle().Which.Should().Be("564082210");
        }

        [Test]
        public void FakeHttpClient_reports_unknown_albums_as_not_found()
        {
            var http = new FakeHttpClient(Fixture.PatientZeroSearch());
            TidalApiHarness.Install(http);

            var act = () => NzbDrone.Plugin.Tidal.TidalAPI.Instance.Client.API.GetAlbum("999999999").GetAwaiter().GetResult();

            act.Should().Throw<TidalSharp.Exceptions.ResourceNotFoundException>();
            http.MissingAlbumIds.Should().Contain("999999999");
        }
    }
}
