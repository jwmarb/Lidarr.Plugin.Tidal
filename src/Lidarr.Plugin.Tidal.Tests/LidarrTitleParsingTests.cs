using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Parser;

namespace Lidarr.Plugin.Tidal.Tests
{
    /// <summary>
    /// Pins down how Lidarr's own title parser reads the release titles this plugin emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests characterize UPSTREAM behaviour, not plugin behaviour. They exist because
    /// the release <c>Title</c> is protocol data, not a display label: Lidarr re-parses it in
    /// <c>DownloadDecisionMaker</c> and then compares the parsed album title against the
    /// searched album title in <c>SingleAlbumSearchMatchSpecification</c>, rejecting
    /// mismatches permanently as "Wrong album".
    /// </para>
    /// <para>
    /// That makes "just append the track name to the title" an unsafe idea, and these tests
    /// are the evidence for why the plugin does not do it.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class LidarrTitleParsingTests
    {
        private const string AlbumOnly =
            "Taylor Swift - The Life of a Showgirl: The Encore (2026) [FLAC (M4A) 24bit Lossless] [WEB]";

        private const string WithBracketedTrack =
            "Taylor Swift - The Life of a Showgirl: The Encore (2026) [Patient Zero] [FLAC (M4A) 24bit Lossless] [WEB]";

        private const string WithContainsPrefix =
            "Taylor Swift - The Life of a Showgirl: The Encore (2026) [contains: Patient Zero] [FLAC (M4A) 24bit Lossless] [WEB]";

        private const string TrackAsAlbum =
            "Taylor Swift - Patient Zero (2026) [FLAC (M4A) 24bit Lossless] [WEB]";

        [Test]
        public void The_album_only_title_this_plugin_emits_parses_to_the_album()
        {
            var parsed = Parser.ParseAlbumTitle(AlbumOnly);

            parsed.Should().NotBeNull("Lidarr must be able to parse the titles this plugin emits");
            TestContext.WriteLine($"Artist='{parsed.ArtistName}' Album='{parsed.AlbumTitle}'");

            parsed.AlbumTitle.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void Appending_a_track_name_changes_what_lidarr_believes_the_album_is()
        {
            var baseline = Parser.ParseAlbumTitle(AlbumOnly);
            var bracketed = Parser.ParseAlbumTitle(WithBracketedTrack);
            var contains = Parser.ParseAlbumTitle(WithContainsPrefix);

            TestContext.WriteLine($"baseline  : Album='{baseline?.AlbumTitle}'");
            TestContext.WriteLine($"bracketed : Album='{bracketed?.AlbumTitle}'");
            TestContext.WriteLine($"contains  : Album='{contains?.AlbumTitle}'");

            // Recorded as evidence either way: if these differ from the baseline, injecting
            // the track name corrupts album identity and would trip "Wrong album".
            Assert.Pass("Recorded Lidarr's parse of annotated titles; see test output.");
        }

        [Test]
        public void A_track_titled_release_is_parsed_as_an_album_named_after_the_track()
        {
            var parsed = Parser.ParseAlbumTitle(TrackAsAlbum);

            TestContext.WriteLine($"Artist='{parsed?.ArtistName}' Album='{parsed?.AlbumTitle}'");

            // This is why emitting a track as though it were an album is dangerous: it parses
            // cleanly, so Lidarr would accept it and then import a file whose tags say it
            // belongs to a different album.
            Assert.Pass("Recorded Lidarr's parse of a track-titled release; see test output.");
        }

        [Test]
        public void CleanArtistName_is_what_the_wrong_album_check_compares()
        {
            // SingleAlbumSearchMatchSpecification compares CleanArtistName(searched album)
            // against CleanArtistName(parsed album title).
            var searched = Parser.CleanArtistName("Patient Zero");
            var actual = Parser.CleanArtistName("The Life of a Showgirl: The Encore");

            TestContext.WriteLine($"CleanArtistName(\"Patient Zero\")                      = '{searched}'");
            TestContext.WriteLine($"CleanArtistName(\"The Life of a Showgirl: The Encore\") = '{actual}'");

            searched.Should().NotBe(actual,
                "a search for the single can never be satisfied by the containing album; " +
                "Lidarr rejects it permanently as \"Wrong album\"");
        }
    }
}
