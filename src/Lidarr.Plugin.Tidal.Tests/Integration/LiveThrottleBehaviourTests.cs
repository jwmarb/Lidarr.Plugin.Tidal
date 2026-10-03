using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FluentAssertions;
using Lidarr.Plugin.Tidal.Tests.Framework;
using NUnit.Framework;
using NzbDrone.Plugin.Tidal;
using TidalSharp.Exceptions;

namespace Lidarr.Plugin.Tidal.Tests.Integration
{
    /// <summary>
    /// Verifies the throttle fix against the live Tidal API.
    /// </summary>
    /// <remarks>
    /// Only meaningful while Tidal is actually throttling <c>/albums/{id}</c>. When the
    /// endpoint is healthy the call simply succeeds, which this reports rather than fails.
    /// </remarks>
    [TestFixture]
    [Category("Integration")]
    [Explicit("Hits the live Tidal API; run with TIDAL_INTEGRATION=1.")]
    public class LiveThrottleBehaviourTests
    {
        [SetUp]
        public void SetUp()
        {
            var reason = LiveCredentials.SkipReason;

            if (reason != null)
            {
                Assert.Ignore(reason);
            }
        }

        [TearDown]
        public void TearDown() => TidalApiHarness.ResetSingleton();

        [Test]
        public void A_throttled_endpoint_fails_fast_instead_of_recursing_forever()
        {
            var api = LiveCredentials.InstallLiveApi(TestHttpClient.Create());

            var sw = Stopwatch.StartNew();
            string outcome;

            try
            {
                var album = api.Client.API.GetAlbum("465501412").GetAwaiter().GetResult();
                outcome = $"200 OK ({album["title"]}) - endpoint is healthy, throttle has cleared";
            }
            catch (APIException ex)
            {
                outcome = $"APIException: {ex.Message}";
            }

            sw.Stop();

            TestContext.WriteLine($"elapsed: {sw.Elapsed.TotalSeconds:0.0}s");
            TestContext.WriteLine($"outcome: {outcome}");

            // The point of the fix: bounded time. Before, a throttled album recursed
            // indefinitely (observed: 59 requests for one album, searches taking ~7 min).
            // With 5 retries at 1/2/4/8/16s the worst case is ~31s plus request time.
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(90),
                "a throttled call must give up, not spin");
        }
    }
}
