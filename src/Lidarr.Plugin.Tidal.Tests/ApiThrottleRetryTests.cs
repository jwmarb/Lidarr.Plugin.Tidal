using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Lidarr.Plugin.Tidal.Tests.Framework;
using NUnit.Framework;
using TidalSharp;
using TidalSharp.Exceptions;

namespace Lidarr.Plugin.Tidal.Tests
{
    /// <summary>
    /// Covers how <c>TidalSharp.API.Call</c> handles HTTP 429 from Tidal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These exist because of a measured production failure. Trace logging inside a real
    /// Lidarr container showed one album search issuing <b>1471 requests for 25 distinct
    /// albums</b> (58.8x amplification), 109 of 132 in one search returning 429, and a
    /// single album being requested 59 times.
    /// </para>
    /// <para>
    /// The cause was a retry that recursed with a flat 100-1000ms delay, no backoff and no
    /// cap. Tidal's throttle on <c>/albums/{id}</c> is cumulative and survived six minutes
    /// of total silence, so retrying without backoff kept renewing it. Notably the throttle
    /// was endpoint-scoped: <c>/sessions</c>, <c>/search</c>, <c>/artists/{id}</c> and
    /// <c>/albums/{id}/tracks</c> all kept returning 200 while <c>/albums/{id}</c> did not.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class ApiThrottleRetryTests
    {
        /// <summary>
        /// Builds a client whose throttle backoff runs in milliseconds, so the retry
        /// behaviour can be asserted without the suite waiting real minutes. Scale is the
        /// only thing changed; the logic under test is identical.
        /// </summary>
        private static TidalClient ClientFor(ThrottlingHttpClient http, bool fastBackoff = true)
        {
            // TidalClient builds its own internal Session, so the API under test is
            // reachable through the public surface without reflection.
            var dir = Path.Combine(Path.GetTempPath(), "tidal-throttle-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var client = new TidalClient(dir, http);

            if (fastBackoff)
            {
                client.API.ThrottleBackoffUnit = TimeSpan.FromMilliseconds(20);
                client.API.ThrottleBackoffCeiling = TimeSpan.FromMilliseconds(500);
            }

            return client;
        }

        [Test]
        public async Task A_transient_throttle_is_retried_and_then_succeeds()
        {
            var http = new ThrottlingHttpClient(throttledResponses: 2);
            var client = ClientFor(http);

            var result = await client.API.GetAlbum("1");

            result["title"]!.ToString().Should().Be("ok");
            http.RequestCount.Should().Be(3, "two throttled attempts plus the successful one");
        }

        [Test]
        public async Task Retries_back_off_instead_of_hammering()
        {
            var http = new ThrottlingHttpClient(throttledResponses: 3);
            var client = ClientFor(http);

            await client.API.GetAlbum("1");

            // The old code used a flat 100-1000ms delay. Backoff must grow, so by the third
            // retry the gap is clearly larger than the first.
            var gaps = http.Gaps;
            gaps.Should().HaveCountGreaterOrEqualTo(3);

            TestContext.WriteLine($"gaps: {string.Join(", ", gaps.Select(g => $"{g.TotalMilliseconds:0}ms"))}");

            gaps[2].Should().BeGreaterThan(gaps[0],
                "delays must increase so the throttle has time to lapse");
            gaps[0].Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(15),
                "first backoff is ~1 backoff unit");
        }

        [Test]
        public void A_persistent_throttle_gives_up_rather_than_recursing_forever()
        {
            // More 429s than the retry cap allows.
            var http = new ThrottlingHttpClient(throttledResponses: 50);
            var client = ClientFor(http);

            var act = async () => await client.API.GetAlbum("1");

            act.Should().ThrowAsync<APIException>()
                .GetAwaiter().GetResult()
                .And.Message.Should().Contain("rate limiting");

            // This is the regression that matters: bounded attempts, not 59 per album.
            http.RequestCount.Should().BeLessOrEqualTo(6,
                $"a throttled call must stop after the retry cap, but it made {http.RequestCount} requests");
        }

        [Test]
        public async Task A_numeric_Retry_After_header_is_honoured()
        {
            // Retry-After is in seconds per RFC 9110, so this test uses real-time backoff
            // to prove the header wins over the (much shorter) exponential path.
            var http = new ThrottlingHttpClient(throttledResponses: 1, retryAfter: "2");
            var client = ClientFor(http, fastBackoff: false);

            var sw = Stopwatch.StartNew();
            await client.API.GetAlbum("1");
            sw.Stop();

            // Tidal asked for 2s; the exponential path would have waited ~1s.
            sw.Elapsed.Should().BeGreaterOrEqualTo(TimeSpan.FromSeconds(1.9),
                "Retry-After must take precedence over the default backoff");
            http.RequestCount.Should().Be(2);
        }

        [Test]
        public async Task An_absurd_Retry_After_is_capped()
        {
            // 86400s must be clamped to the ceiling, not honoured literally.
            var http = new ThrottlingHttpClient(throttledResponses: 1, retryAfter: "86400");
            var client = ClientFor(http);

            var sw = Stopwatch.StartNew();
            await client.API.GetAlbum("1");
            sw.Stop();

            // A hostile or buggy header must not stall a search; here the ceiling is 500ms.
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
                "Retry-After is clamped to the backoff ceiling");
        }

        [Test]
        public async Task A_malformed_Retry_After_falls_back_to_exponential_backoff()
        {
            var http = new ThrottlingHttpClient(throttledResponses: 1, retryAfter: "not-a-number");
            var client = ClientFor(http);

            await client.API.GetAlbum("1");

            http.RequestCount.Should().Be(2, "an unparseable header must not break the retry");
            http.Gaps.Single().Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(15));
        }

        [Test]
        public async Task Requests_carry_a_rate_limit_so_lidarr_paces_them()
        {
            var http = new ThrottlingHttpClient(throttledResponses: 0);
            var client = ClientFor(http);

            await client.API.GetAlbum("1");

            // Lidarr's HttpClient only waits when RateLimit != TimeSpan.Zero
            // (HttpClient.ExecuteRequestAsync). Without this the limiter skips these
            // requests entirely, which is what let them burst into a 429 penalty.
            http.RequestCount.Should().Be(1);
        }
    }
}
