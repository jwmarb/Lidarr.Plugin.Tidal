using System;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Tidal.Tests.Framework;
using NzbDrone.Plugin.Tidal;

namespace Lidarr.Plugin.Tidal.Tests.Tools
{
    /// <summary>
    /// Interactive PKCE login helper that mints a fresh <c>lastUser.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because a Tidal PKCE login cannot be completed by relaying a redirect URL
    /// between processes. <c>Session.RegenerateCodes</c> creates a random <c>code_verifier</c>
    /// in memory and sends only its SHA-256 challenge to Tidal; the token exchange must
    /// present that original verifier. The authorization code's <c>challengeId</c> and
    /// <c>cuk</c> claims bind it to the exact <c>Session</c> instance that produced the login
    /// URL, and the verifier is never persisted. A code minted by one process therefore
    /// cannot be redeemed by another, no matter how quickly it is pasted.
    /// </para>
    /// <para>
    /// So this tool holds ONE <see cref="TidalAPI"/> instance open across both halves: it
    /// prints the login URL, waits on stdin while you authenticate, then exchanges the pasted
    /// redirect against the still-live verifier and writes the credential file.
    /// </para>
    /// <para>
    /// Authorization codes expire in minutes, so paste promptly.
    /// </para>
    /// <para>Run it with:</para>
    /// <code>
    /// dotnet run --project src/Lidarr.Plugin.Tidal.Tests/Tools/TidalLogin \
    ///   -p:SolutionDir=$PWD/ext/Lidarr/src/ -p:NuGetAudit=false
    /// </code>
    /// </remarks>
    public static class TidalLogin
    {
        public static async Task<int> Main(string[] args)
        {
            var configDir = ResolveConfigDir(args);
            Directory.CreateDirectory(configDir);

            Console.WriteLine("Tidal PKCE login");
            Console.WriteLine("================");
            Console.WriteLine($"Config directory: {configDir}");
            Console.WriteLine();

            // One client, one Session, one code_verifier - kept alive for both steps below.
            TidalApiHarness.ResetSingleton();
            TidalAPI.Initialize(configDir, TestHttpClient.Create(), NLog.LogManager.GetCurrentClassLogger());

            var client = TidalAPI.Instance.Client;

            // A stored user would short-circuit Login() and skip the exchange entirely.
            var userJson = Path.Combine(configDir, "lastUser.json");

            if (File.Exists(userJson))
            {
                Console.WriteLine($"An existing {userJson} would be reused instead of logging in.");
                Console.Write("Delete it and continue? [y/N] ");

                if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Aborted; nothing changed.");
                    return 1;
                }

                File.Delete(userJson);
                Console.WriteLine();
            }

            Console.WriteLine("STEP 1 - open this URL and sign in:");
            Console.WriteLine();
            Console.WriteLine(client.GetPkceLoginUrl());
            Console.WriteLine();
            Console.WriteLine("You will land on a https://tidal.com/android/login/auth?code=... page.");
            Console.WriteLine("It may show a blank page or an error - that is expected; the URL is what matters.");
            Console.WriteLine();
            Console.WriteLine("STEP 2 - paste that full URL here and press Enter (codes expire within minutes):");
            Console.Write("> ");

            var redirectUrl = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                Console.Error.WriteLine("No URL provided; aborting.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Exchanging the authorization code...");

            try
            {
                if (!await client.Login(redirectUrl))
                {
                    Console.Error.WriteLine("Login returned false; the redirect URL was not accepted.");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Login failed: {ex.Message}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("If this says \"The token has expired\", the code timed out between");
                Console.Error.WriteLine("Step 1 and Step 2. Re-run and paste more quickly.");
                return 1;
            }

            var user = client.ActiveUser;

            if (user == null)
            {
                Console.Error.WriteLine("Login reported success but no active user was set.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Success.");
            Console.WriteLine($"  user id      : {user.UserId}");
            Console.WriteLine($"  country      : {user.CountryCode}");
            Console.WriteLine($"  token expires: {user.ExpirationDate:u}");
            Console.WriteLine($"  written to   : {userJson}");
            Console.WriteLine();

            if (!File.Exists(userJson))
            {
                Console.Error.WriteLine($"WARNING: expected {userJson} to exist but it does not.");
                return 1;
            }

            Console.WriteLine("To run the live integration tests with these credentials:");
            Console.WriteLine();
            Console.WriteLine($"  export TIDAL_USER_JSON=\"$(cat '{userJson}')\"");
            Console.WriteLine("  export TIDAL_INTEGRATION=1");
            Console.WriteLine();
            Console.WriteLine("Or copy that file's contents into .env as TIDAL_USER_JSON=<json>");
            Console.WriteLine("(.env is gitignored; never commit credentials.)");

            return 0;
        }

        /// <summary>
        /// Chooses where <c>lastUser.json</c> is written: <c>--config-dir</c>, else
        /// <c>TIDAL_CONFIG_DIR</c>, else a <c>.tidal-auth</c> folder beside the repo.
        /// </summary>
        private static string ResolveConfigDir(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] is "--config-dir" or "-c")
                {
                    return Path.GetFullPath(args[i + 1]);
                }
            }

            var fromEnv = Environment.GetEnvironmentVariable("TIDAL_CONFIG_DIR");

            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Path.GetFullPath(fromEnv);
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tidal-auth");
        }
    }
}
