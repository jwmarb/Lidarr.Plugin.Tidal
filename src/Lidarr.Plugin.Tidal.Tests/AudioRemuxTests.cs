using System;
using System.IO;
using FluentAssertions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download.Clients.Tidal;
using NzbDrone.Core.Download.Clients.Tidal.Queue;

namespace Lidarr.Plugin.Tidal.Tests
{
    /// <summary>
    /// Covers the FLAC remux that normalises Tidal's fragmented MP4 downloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tidal streams lossless audio as DASH segments which the downloader concatenates
    /// verbatim, producing a fragmented MP4. Its top-level <c>mvhd</c>/<c>mdhd</c> duration
    /// is 0 because fMP4 keeps timing in the per-fragment <c>moof</c> headers, so TagLib
    /// reports duration 0 and bitrate 0. Lidarr then estimates the bitrate as
    /// <c>(size * 8) / (duration * 1024)</c>, divides by zero, and casts Infinity to
    /// <c>int.MinValue</c> — the <c>-2147483648 kbps</c> shown in the UI. With bitrate and
    /// bit depth both 0, the quality parser also mislabels lossless FLAC as AAC.
    /// </para>
    /// <para>
    /// <c>Fixtures/Audio/fragmented-flac.m4a</c> is a real fragmented MP4 carrying FLAC
    /// audio, generated with ffmpeg's <c>frag_keyframe+empty_moov</c> flags. It reproduces
    /// the defect exactly: TagLib reads 0s/0kbps from it and Lidarr's own estimator returns
    /// <c>int.MinValue</c>.
    /// </para>
    /// <para>
    /// Tests needing ffmpeg skip rather than fail when it is absent, so the suite stays
    /// green on machines without it.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class AudioRemuxTests
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private string _workDir;

        [SetUp]
        public void SetUp()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "tidal-remux-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (_workDir != null && Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, true);
            }
        }

        private static string FixturePath =>
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "fragmented-flac.m4a");

        /// <summary>A disposable copy, since a successful remux deletes its input.</summary>
        private string CopyFixture()
        {
            var dest = Path.Combine(_workDir, "01 - 01 - Track.m4a");
            File.Copy(FixturePath, dest);
            return dest;
        }

        private static void RequireFFMpeg()
        {
            if (!DownloadItem.IsFFMpegAvailable(out var error))
            {
                Assert.Ignore($"ffmpeg/ffprobe not available: {error}");
            }
        }

        [Test]
        public void The_fixture_reproduces_the_zero_duration_defect()
        {
            // Guards the premise of every other test here. If this fails, the fixture is no
            // longer a fragmented MP4 and the rest prove nothing.
            File.Exists(FixturePath).Should().BeTrue($"missing fixture at {FixturePath}");

            var file = TagLib.File.Create(FixturePath);
            var duration = file.Properties.Duration.TotalSeconds;
            var bitrate = file.Properties.AudioBitrate;
            file.Dispose();

            duration.Should().Be(0, "a fragmented MP4 reports no top-level duration");
            bitrate.Should().Be(0);

            // Lidarr's EstimateBitrate (AudioTag.cs) verbatim, to show what it produces.
            var size = new FileInfo(FixturePath).Length;
            var estimated = (int)((size * 8L) / (duration * 1024));

            estimated.Should().Be(int.MinValue,
                "this is the -2147483648 kbps users see in Audio Info");
        }

        [Test]
        public void Remuxing_produces_a_file_with_a_readable_duration_and_bitrate()
        {
            RequireFFMpeg();

            var source = CopyFixture();
            var settings = new TidalSettings { ExtractFlac = true };

            var result = DownloadItem.HandleAudioConversion(source, settings, Log);

            result.Should().EndWith(".flac");
            File.Exists(result).Should().BeTrue();

            var file = TagLib.File.Create(result);
            var duration = file.Properties.Duration.TotalSeconds;
            var bitrate = file.Properties.AudioBitrate;
            file.Dispose();

            TestContext.WriteLine($"after remux: duration={duration:0.00}s bitrate={bitrate}kbps");

            duration.Should().BeGreaterThan(0, "the remuxed container carries real timing");
            bitrate.Should().BeGreaterThan(0, "which is what stops Lidarr estimating");

            // The whole point: Lidarr's estimator now returns something sane.
            var size = new FileInfo(result).Length;
            var estimated = (int)((size * 8L) / (duration * 1024));

            estimated.Should().BePositive();
        }

        [Test]
        public void Remuxing_preserves_the_audio_losslessly()
        {
            RequireFFMpeg();

            var source = CopyFixture();
            var sourceSize = new FileInfo(source).Length;
            var settings = new TidalSettings { ExtractFlac = true };

            var result = DownloadItem.HandleAudioConversion(source, settings, Log);

            var file = TagLib.File.Create(result);
            var codec = string.Empty;

            foreach (var c in file.Properties.Codecs)
            {
                if (c is TagLib.IAudioCodec audio)
                {
                    codec = audio.Description;
                }
            }

            file.Dispose();

            // TagLib names the fragmented input "MPEG-4 Audio (fLaC)" and a real FLAC
            // container "Flac Audio"; the latter is what proves the remux landed.
            codec.Should().Be("Flac Audio", "the stream is copied into a FLAC container, never re-encoded");

            // A re-encode would change the payload size substantially; a remux only
            // rewrites container headers.
            new FileInfo(result).Length.Should().BeCloseTo(sourceSize, (ulong)(sourceSize * 0.2),
                "remuxing rewrites headers, it does not recompress audio");
        }

        [Test]
        public void Remuxing_replaces_the_original_file()
        {
            RequireFFMpeg();

            var source = CopyFixture();
            var settings = new TidalSettings { ExtractFlac = true };

            var result = DownloadItem.HandleAudioConversion(source, settings, Log);

            File.Exists(source).Should().BeFalse("the fragmented original is no longer useful");
            result.Should().NotBe(source);
        }

        [Test]
        public void Conversion_is_skipped_when_the_setting_is_off()
        {
            var source = CopyFixture();
            var settings = new TidalSettings { ExtractFlac = false, ReEncodeAAC = false };

            var result = DownloadItem.HandleAudioConversion(source, settings, Log);

            result.Should().Be(source, "the file must be left exactly as downloaded");
            File.Exists(source).Should().BeTrue();
        }

        [Test]
        public void A_missing_ffmpeg_leaves_the_download_intact()
        {
            var source = CopyFixture();
            var settings = new TidalSettings { ExtractFlac = true };

            // Emptying PATH makes ffprobe unlaunchable, which throws Win32Exception rather
            // than FFMPEGException - the case the original code did not catch.
            var savedPath = Environment.GetEnvironmentVariable("PATH");

            try
            {
                Environment.SetEnvironmentVariable("PATH", _workDir);

                var act = () => DownloadItem.HandleAudioConversion(source, settings, Log);

                act.Should().NotThrow("a missing ffmpeg must not fail the track");
                act().Should().Be(source, "and the downloaded file must survive");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", savedPath);
            }

            File.Exists(source).Should().BeTrue();
        }

        [Test]
        public void A_corrupt_input_is_left_alone_rather_than_destroyed()
        {
            RequireFFMpeg();

            // Not audio at all; ffmpeg will refuse it.
            var source = Path.Combine(_workDir, "broken.m4a");
            File.WriteAllText(source, "this is not an mp4");

            var settings = new TidalSettings { ExtractFlac = true };

            var result = DownloadItem.HandleAudioConversion(source, settings, Log);

            result.Should().Be(source, "a failed conversion must not lose the download");
            File.Exists(source).Should().BeTrue();
            File.Exists(Path.ChangeExtension(source, "flac")).Should().BeFalse(
                "a half-written output must be cleaned up");
        }

        [Test]
        public void IsUsableAudioFile_rejects_a_zero_duration_file()
        {
            // This guard is what stops a bad remux replacing a good download.
            DownloadItem.IsUsableAudioFile(FixturePath).Should().BeFalse(
                "a fragmented MP4 reports no duration, so it is not a usable result");

            var empty = Path.Combine(_workDir, "empty.flac");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            DownloadItem.IsUsableAudioFile(empty).Should().BeFalse();

            DownloadItem.IsUsableAudioFile(Path.Combine(_workDir, "nope.flac")).Should().BeFalse();
        }

        [Test]
        public void IsFFMpegAvailable_reports_a_helpful_message_when_missing()
        {
            var savedPath = Environment.GetEnvironmentVariable("PATH");

            try
            {
                Environment.SetEnvironmentVariable("PATH", _workDir);

                DownloadItem.IsFFMpegAvailable(out var error).Should().BeFalse();
                error.Should().NotBeNullOrEmpty();
                error.Should().ContainAny("ffmpeg", "ffprobe");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", savedPath);
            }
        }
    }
}
