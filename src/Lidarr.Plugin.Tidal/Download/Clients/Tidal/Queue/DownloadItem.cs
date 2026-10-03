using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Plugins;
using NzbDrone.Plugin.Tidal;
using TidalSharp;
using TidalSharp.Data;

namespace NzbDrone.Core.Download.Clients.Tidal.Queue
{
    public class DownloadItem
    {
        public static async Task<DownloadItem> From(RemoteAlbum remoteAlbum)
        {
            var url = remoteAlbum.Release.DownloadUrl.Trim();
            var quality = remoteAlbum.Release.Container switch
            {
                "96" => AudioQuality.LOW,
                "320" => AudioQuality.HIGH,
                "Lossless" => AudioQuality.LOSSLESS,
                "24bit Lossless" => AudioQuality.HI_RES_LOSSLESS,
                _ => AudioQuality.HIGH,
            };

            DownloadItem item = null;
            if (url.Contains("tidal", StringComparison.CurrentCultureIgnoreCase))
            {
                if (TidalURL.TryParse(url, out var tidalUrl))
                {
                    item = new()
                    {
                        ID = Guid.NewGuid().ToString(),
                        Status = DownloadItemStatus.Queued,
                        Bitrate = quality,
                        RemoteAlbum = remoteAlbum,
                        _tidalUrl = tidalUrl,
                    };

                    await item.SetTidalData();
                }
            }

            return item;
        }

        public string ID { get; private set; }

        public string Title { get; private set; }
        public string Artist { get; private set; }
        public bool Explicit { get; private set; }

        public RemoteAlbum RemoteAlbum {  get; private set; }

        public string DownloadFolder { get; private set; }

        public AudioQuality Bitrate { get; private set; }
        public DownloadItemStatus Status { get; set; }

        public float Progress { get => DownloadedSize / (float)Math.Max(TotalSize, 1); }
        public long DownloadedSize { get; private set; }
        public long TotalSize { get; private set; }

        public int FailedTracks { get; private set; }

        /// <summary>
        /// Fallback bitrate for an AAC re-encode when the source container does not report
        /// one. Tidal's lossy tier is 320 kbps, so this matches it rather than guessing low.
        /// </summary>
        private const int DefaultReEncodeBitrate = 320;

        private (string id, int chunks)[] _tracks;
        private TidalURL _tidalUrl;
        private JObject _tidalAlbum;

        public async Task DoDownload(TidalSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            List<Task> tasks = new();
            using SemaphoreSlim semaphore = new(3, 3);
            foreach (var (trackId, trackSize) in _tracks)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellation);
                    try
                    {
                        await DoTrackDownload(trackId, settings, logger, cancellation);
                        if (settings.DownloadDelay)
                        {
                            var delay = (float)Random.Shared.NextDouble() * (settings.DownloadDelayMax - settings.DownloadDelayMin) + settings.DownloadDelayMin;
                            await Task.Delay((int)(delay * 1000));
                        }
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        logger.Error("Error while downloading Tidal track " + trackId);
                        logger.Error(ex.ToString());
                        FailedTracks++;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellation));
            }

            await Task.WhenAll(tasks);
            if (FailedTracks > 0)
                Status = DownloadItemStatus.Failed;
            else
                Status = DownloadItemStatus.Completed;
        }

        private async Task DoTrackDownload(string track, TidalSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            var page = await TidalAPI.Instance.Client.API.GetTrack(track, cancellation);
            var songTitle = API.CompleteTitleFromPage(page);
            var artistName = page["artist"]!["name"]!.ToString();
            var albumTitle = page["album"]!["title"]!.ToString();
            var duration = page["duration"]!.Value<int>();

            var ext = (await TidalAPI.Instance.Client.Downloader.GetExtensionForTrack(track, Bitrate)).TrimStart('.');
            var outPath = Path.Combine(settings.DownloadPath, MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/", ext, page, _tidalAlbum), MetadataUtilities.GetFilledTemplate("%volume% - %track% - %title%.%ext%", ext, page, _tidalAlbum));
            var outDir = Path.GetDirectoryName(outPath)!;

            DownloadFolder = outDir;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            await TidalAPI.Instance.Client.Downloader.WriteRawTrackToFile(track, Bitrate, outPath, (i) => DownloadedSize++, cancellation);
            outPath = HandleAudioConversion(outPath, settings, logger);

            var plainLyrics = string.Empty;
            string syncLyrics = null;

            var lyrics = await TidalAPI.Instance.Client.Downloader.FetchLyricsFromTidal(track, cancellation);
            if (lyrics.HasValue)
            {
                plainLyrics = lyrics.Value.plainLyrics;

                if (settings.SaveSyncedLyrics)
                    syncLyrics = lyrics.Value.syncLyrics;
            }

            if (settings.UseLRCLIB && (string.IsNullOrWhiteSpace(plainLyrics) || (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))))
            {
                lyrics = await TidalAPI.Instance.Client.Downloader.FetchLyricsFromLRCLIB("lrclib.net", songTitle, artistName, albumTitle, duration, cancellation);
                if (lyrics.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(plainLyrics))
                        plainLyrics = lyrics.Value.plainLyrics;
                    if (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))
                        syncLyrics = lyrics.Value.syncLyrics;
                }
            }

            await TidalAPI.Instance.Client.Downloader.ApplyMetadataToFile(track, outPath, MediaResolution.s640, plainLyrics, token: cancellation);

            if (syncLyrics != null)
                await CreateLrcFile(Path.Combine(outDir, MetadataUtilities.GetFilledTemplate("%volume% - %track% - %title%.%ext%", "lrc", page, _tidalAlbum)), syncLyrics);

            // TODO: this is currently a waste of resources, if this pr ever gets merged, it can be reenabled
            // https://github.com/Lidarr/Lidarr/pull/4370
            /* try
            {
                string artOut = Path.Combine(outDir, "folder.jpg");
                if (!File.Exists(artOut))
                {
                    byte[] bigArt = await TidalAPI.Instance.Client.Downloader.GetArtBytes(page["DATA"]!["ALB_PICTURE"]!.ToString(), 1024, cancellation);
                    await File.WriteAllBytesAsync(artOut, bigArt, cancellation);
                }
            }
            catch (UnavailableArtException) { } */
        }

        /// <summary>
        /// Reports whether FFMPEG and FFPROBE can actually be invoked, so the download
        /// client can fail its configuration test instead of silently skipping every remux.
        /// </summary>
        internal static bool IsFFMpegAvailable(out string error)
        {
            try
            {
                FFMPEG.AssertAvailable();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Normalises the file Tidal gave us into a container Lidarr can read correctly.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Tidal streams lossless audio as DASH segments, and the downloader concatenates
        /// those segments verbatim. The result is a <i>fragmented</i> MP4: playable, but its
        /// top-level <c>mvhd</c>/<c>mdhd</c> duration is 0 because in fMP4 the real timing
        /// lives in the per-fragment <c>moof</c> headers.
        /// </para>
        /// <para>
        /// That zero propagates badly. TagLib reports duration 0 and bitrate 0, so Lidarr
        /// falls back to estimating the bitrate as <c>(size * 8) / (duration * 1024)</c>
        /// (<c>AudioTag.cs</c>), which divides by zero, yields Infinity, and casts to
        /// <c>int.MinValue</c> - the -2147483648 kbps users see. With both bitrate and bit
        /// depth reading 0, the quality parser also mislabels lossless FLAC as AAC.
        /// </para>
        /// <para>
        /// Remuxing with <c>-acodec copy</c> rebuilds the container headers without touching
        /// a single audio sample, so duration, bitrate and bit depth all read correctly.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The path to use going forward: the remuxed file on success, or the original file
        /// unchanged on any failure. Conversion is best-effort and never fails a download.
        /// </returns>
        internal static string HandleAudioConversion(string filePath, TidalSettings settings, Logger logger)
        {
            if (!settings.ExtractFlac && !settings.ReEncodeAAC)
                return filePath;

            string[] codecs;

            try
            {
                codecs = FFMPEG.ProbeCodecs(filePath);
            }
            catch (Exception ex)
            {
                // A missing ffprobe surfaces as Win32Exception, not FFMPEGException, so the
                // catch below would not cover it and the whole track would fail.
                logger.Warn($"Could not probe '{Path.GetFileName(filePath)}'; leaving it as-is. " +
                            $"Is FFPROBE installed and on PATH? ({ex.Message})");
                return filePath;
            }

            if (codecs.Contains("flac") && settings.ExtractFlac)
            {
                var newFilePath = Path.ChangeExtension(filePath, "flac");

                try
                {
                    FFMPEG.ConvertWithoutReencode(filePath, newFilePath);

                    if (!IsUsableAudioFile(newFilePath))
                    {
                        // Never trade a working file for a broken one.
                        logger.Warn($"Remux of '{Path.GetFileName(filePath)}' produced an unusable " +
                                    "file; keeping the original M4A.");

                        if (File.Exists(newFilePath))
                            File.Delete(newFilePath);

                        return filePath;
                    }

                    if (File.Exists(filePath))
                        File.Delete(filePath);

                    logger.Debug($"Remuxed '{Path.GetFileName(filePath)}' to FLAC.");

                    return newFilePath;
                }
                catch (Exception ex)
                {
                    logger.Warn($"Remux to FLAC failed for '{Path.GetFileName(filePath)}'; " +
                                $"keeping the original M4A. ({ex.Message})");

                    if (File.Exists(newFilePath))
                        File.Delete(newFilePath);

                    return filePath;
                }
            }

            if (codecs.Contains("aac") && settings.ReEncodeAAC)
            {
                var newFilePath = Path.ChangeExtension(filePath, "mp3");

                try
                {
                    var tagFile = TagLib.File.Create(filePath);
                    var bitrate = tagFile.Properties.AudioBitrate;
                    tagFile.Dispose();

                    // AAC from Tidal arrives in the same fragmented container, so TagLib can
                    // report 0 here too. Re-encoding at 0 kbps would destroy the audio.
                    if (bitrate <= 0)
                    {
                        bitrate = DefaultReEncodeBitrate;
                        logger.Debug($"Could not read a bitrate from '{Path.GetFileName(filePath)}'; " +
                                     $"re-encoding at {bitrate} kbps.");
                    }

                    FFMPEG.Reencode(filePath, newFilePath, bitrate);

                    if (!IsUsableAudioFile(newFilePath))
                    {
                        logger.Warn($"Re-encode of '{Path.GetFileName(filePath)}' produced an " +
                                    "unusable file; keeping the original M4A.");

                        if (File.Exists(newFilePath))
                            File.Delete(newFilePath);

                        return filePath;
                    }

                    if (File.Exists(filePath))
                        File.Delete(filePath);

                    return newFilePath;
                }
                catch (Exception ex)
                {
                    logger.Warn($"Re-encode to MP3 failed for '{Path.GetFileName(filePath)}'; " +
                                $"keeping the original M4A. ({ex.Message})");

                    if (File.Exists(newFilePath))
                        File.Delete(newFilePath);

                    return filePath;
                }
            }

            return filePath;
        }

        /// <summary>
        /// Confirms a converted file exists and reports a readable duration, which is the
        /// property whose absence caused the original bug.
        /// </summary>
        internal static bool IsUsableAudioFile(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return false;

            try
            {
                var file = TagLib.File.Create(path);
                var seconds = file.Properties.Duration.TotalSeconds;
                file.Dispose();

                return seconds > 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task SetTidalData(CancellationToken cancellation = default)
        {
            if (_tidalUrl.EntityType != EntityType.Album)
                throw new InvalidOperationException();

            var album = await TidalAPI.Instance.Client.API.GetAlbum(_tidalUrl.Id, cancellation);
            var albumTracks = await TidalAPI.Instance.Client.API.GetAlbumTracks(_tidalUrl.Id, cancellation);

            var tracksTasks = albumTracks["items"]!.Select(async t =>
            {
                var chunks = await TidalAPI.Instance.Client.Downloader.GetChunksInTrack(t["id"]!.ToString(), Bitrate, cancellation);
                return (t["id"]!.ToString(), chunks);
            }).ToArray();

            var tracks = await Task.WhenAll(tracksTasks);
            _tracks ??= tracks;

            _tidalAlbum = album;

            Title = album["title"]!.ToString();
            Artist = album["artist"]!["name"]!.ToString();
            Explicit = album["explicit"]!.Value<bool>();
            TotalSize = _tracks.Sum(t => t.chunks);
        }

        private static async Task CreateLrcFile(string lrcFilePath, string syncLyrics)
        {
            await File.WriteAllTextAsync(lrcFilePath, syncLyrics);
        }
    }
}
