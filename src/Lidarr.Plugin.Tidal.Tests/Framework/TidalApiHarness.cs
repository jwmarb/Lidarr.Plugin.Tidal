using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Plugin.Tidal;
using TidalSharp;
using TidalSharp.Data;

namespace Lidarr.Plugin.Tidal.Tests.Framework
{
    /// <summary>
    /// Installs a <see cref="TidalAPI"/> singleton backed by a caller-supplied
    /// <see cref="IHttpClient"/> so the indexer can be exercised without the network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two production design choices force the reflection below, and both are worth
    /// noting as testability smells rather than problems with these tests:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="TidalAPI.Instance"/> is a write-once static singleton
    /// (<c>Initialize</c> returns early when already set), so a test cannot swap the HTTP
    /// client between cases without resetting the backing field.</description></item>
    /// <item><description><c>TidalUser</c>'s constructor is <c>internal</c>, so a logged-in
    /// user is built by deserializing the same JSON shape the plugin persists.</description></item>
    /// </list>
    /// </remarks>
    public static class TidalApiHarness
    {
        /// <summary>
        /// Points <see cref="TidalAPI.Instance"/> at <paramref name="httpClient"/> and
        /// attaches a non-expired fake user so request building succeeds.
        /// </summary>
        public static TidalAPI Install(IHttpClient httpClient)
        {
            ResetSingleton();

            var configDir = Path.Combine(Path.GetTempPath(), "tidal-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(configDir);

            TidalAPI.Initialize(configDir, httpClient, LogManager.GetCurrentClassLogger());

            if (TidalAPI.Instance == null)
            {
                throw new InvalidOperationException("TidalAPI.Initialize did not install an instance.");
            }

            TidalAPI.Instance.Client.ActiveUser = CreateFakeUser();

            // API.Call reads the user from its own field, set via an internal method.
            typeof(API)
                .GetMethod("UpdateUser", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(TidalAPI.Instance.Client.API, new object[] { TidalAPI.Instance.Client.ActiveUser });

            return TidalAPI.Instance;
        }

        /// <summary>Clears the singleton so each test starts from a known state.</summary>
        public static void ResetSingleton()
        {
            // Auto-property backing field for the private setter on TidalAPI.Instance.
            var field = typeof(TidalAPI).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);

            if (field == null)
            {
                throw new InvalidOperationException(
                    "Could not locate the TidalAPI.Instance backing field; the singleton shape changed.");
            }

            field.SetValue(null, null);
        }

        /// <summary>
        /// Builds a <c>TidalUser</c> with a far-future expiry so <c>GetRequests</c> never
        /// attempts a token refresh during a test, and with session info pre-filled so
        /// <c>CountryCode</c>/<c>SessionID</c> resolve without a live "sessions" call.
        /// </summary>
        private static TidalUser CreateFakeUser()
        {
            var payload = new
            {
                Data = new
                {
                    scope = "r_usr w_usr",
                    token_type = "Bearer",
                    access_token = "test-access-token",
                    refresh_token = "test-refresh-token",
                    expires_in = 86400,
                    user_id = 1,
                    clientName = "TEST",
                    user = new { userId = 1, countryCode = "US" },
                },
                ExpirationDate = DateTime.UtcNow.AddYears(1),
                IsPkce = true,
            };

            var user = JsonConvert.DeserializeObject<TidalUser>(JsonConvert.SerializeObject(payload));

            if (user == null)
            {
                throw new InvalidOperationException("Failed to construct a fake TidalUser.");
            }

            // CountryCode and SessionID are projected from a private _sessionInfo field that
            // is normally filled by a live GET /sessions. Seed it directly for offline runs.
            var sessionInfo = new SessionInfo
            {
                SessionId = "test-session-id",
                UserId = 1,
                CountryCode = "US",
            };

            typeof(TidalUser)
                .GetField("_sessionInfo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(user, sessionInfo);

            return user;
        }
    }
}
