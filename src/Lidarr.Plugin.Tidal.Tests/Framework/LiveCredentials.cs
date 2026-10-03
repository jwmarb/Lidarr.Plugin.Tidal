using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Plugin.Tidal;
using TidalSharp;
using TidalSharp.Data;

namespace Lidarr.Plugin.Tidal.Tests.Framework
{
    /// <summary>
    /// Opt-in access to real Tidal credentials for the integration tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Integration tests are skipped unless BOTH of these hold:
    /// </para>
    /// <list type="number">
    /// <item><description>the environment variable <c>TIDAL_INTEGRATION</c> is set to
    /// <c>1</c>/<c>true</c>/<c>yes</c> — so a normal <c>dotnet test</c> and CI never
    /// touch the network;</description></item>
    /// <item><description>credentials are resolvable, from the <c>TIDAL_USER_JSON</c>
    /// environment variable or a <c>.env</c> file found by walking up from the test
    /// assembly to the repository root.</description></item>
    /// </list>
    /// <para>
    /// Credentials are only ever read, never logged or written to disk by these tests.
    /// </para>
    /// </remarks>
    public static class LiveCredentials
    {
        private const string EnableVariable = "TIDAL_INTEGRATION";
        private const string CredentialVariable = "TIDAL_USER_JSON";

        /// <summary>True when the caller explicitly opted into hitting the live API.</summary>
        public static bool IntegrationEnabled
        {
            get
            {
                var flag = Environment.GetEnvironmentVariable(EnableVariable);

                return flag is not null &&
                       (flag.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                        flag.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        flag.Equals("yes", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>The reason a live test cannot run, or null when it can.</summary>
        /// <remarks>
        /// Expiry is treated as a skip, not a failure. A stale token is an environment
        /// problem, and letting it surface as a red test makes it look like a code
        /// regression, which is precisely the confusion these tests exist to avoid.
        /// </remarks>
        public static string SkipReason
        {
            get
            {
                if (!IntegrationEnabled)
                {
                    return $"Live Tidal tests are opt-in: set {EnableVariable}=1 to run them.";
                }

                var raw = RawUserJson();

                if (raw is null)
                {
                    return $"No Tidal credentials found (set {CredentialVariable} or add it to a .env file at the repo root).";
                }

                var expiry = ExpirationDate(raw);

                if (expiry is not null && expiry <= DateTime.UtcNow)
                {
                    return $"Tidal credentials expired at {expiry:u}; re-authenticate to run the live tests.";
                }

                return null;
            }
        }

        /// <summary>Reads the recorded expiry, or null when it cannot be determined.</summary>
        private static DateTime? ExpirationDate(string raw)
        {
            try
            {
                var value = JObject.Parse(raw)["ExpirationDate"];

                return value?.Type is JTokenType.Date or JTokenType.String
                    ? value.Value<DateTime>().ToUniversalTime()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Builds a <see cref="TidalAPI"/> wired to the real HTTP stack and signed in with
        /// the credentials from the environment, including a live session lookup.
        /// </summary>
        public static TidalAPI InstallLiveApi(IHttpClient httpClient)
        {
            var raw = RawUserJson()
                      ?? throw new InvalidOperationException("No Tidal credentials available.");

            TidalApiHarness.ResetSingleton();

            var configDir = Path.Combine(Path.GetTempPath(), "tidal-live-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(configDir);

            TidalAPI.Initialize(configDir, httpClient, NLog.LogManager.GetCurrentClassLogger());

            var user = JsonConvert.DeserializeObject<TidalUser>(raw)
                       ?? throw new InvalidOperationException($"{CredentialVariable} is not a valid TidalUser document.");

            TidalAPI.Instance.Client.ActiveUser = user;

            typeof(API)
                .GetMethod("UpdateUser", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(TidalAPI.Instance.Client.API, new object[] { user });

            // Populates CountryCode/SessionID, and doubles as a credential-validity check.
            var getSession = typeof(TidalUser)
                .GetMethod("GetSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(user, new object[] { TidalAPI.Instance.Client.API, default(System.Threading.CancellationToken) });

            ((System.Threading.Tasks.Task)getSession!).GetAwaiter().GetResult();

            return TidalAPI.Instance;
        }

        /// <summary>
        /// Resolves the raw credential JSON from the environment, falling back to a
        /// <c>.env</c> file in this directory or any ancestor.
        /// </summary>
        private static string RawUserJson()
        {
            var fromEnv = Environment.GetEnvironmentVariable(CredentialVariable);

            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Normalize(fromEnv);
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, ".env");

                if (File.Exists(candidate))
                {
                    var value = ReadFromEnvFile(candidate);

                    if (value != null)
                    {
                        return value;
                    }
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static string ReadFromEnvFile(string path)
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();

                if (!trimmed.StartsWith(CredentialVariable + "=", StringComparison.Ordinal))
                {
                    continue;
                }

                return Normalize(trimmed.Substring(CredentialVariable.Length + 1));
            }

            return null;
        }

        /// <summary>Strips optional surrounding quotes and validates the JSON shape.</summary>
        private static string Normalize(string value)
        {
            var trimmed = value.Trim();

            if (trimmed.Length > 1 &&
                ((trimmed[0] == '\'' && trimmed[^1] == '\'') ||
                 (trimmed[0] == '"' && trimmed[^1] == '"')))
            {
                trimmed = trimmed[1..^1];
            }

            try
            {
                // A malformed credential should surface as a skip, not a confusing failure.
                return JObject.Parse(trimmed)["Data"] != null ? trimmed : null;
            }
            catch (JsonReaderException)
            {
                return null;
            }
        }
    }
}
