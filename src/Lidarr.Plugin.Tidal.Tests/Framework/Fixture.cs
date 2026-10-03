using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Lidarr.Plugin.Tidal.Tests.Framework
{
    /// <summary>
    /// Loads the credential-scrubbed Tidal API payloads recorded under <c>Fixtures/</c>.
    /// </summary>
    /// <remarks>
    /// Every fixture is a real response captured from api.tidal.com, with any
    /// session/token/user field replaced by "REDACTED" before being committed.
    /// Two fixtures are deliberately mutated to exercise defensive paths; they
    /// carry a <c>_derivedFrom</c> field explaining the mutation.
    /// </remarks>
    public static class Fixture
    {
        private const string PatientZeroSearchFile = "search-taylorswift-patientzero.json";
        private const string DuplicateSearchFile = "search-duplicate-album-trigger.json";

        public static string Directory
        {
            get
            {
                // Fixtures are copied next to the test assembly at build time.
                var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

                if (!System.IO.Directory.Exists(dir))
                {
                    throw new DirectoryNotFoundException(
                        $"Fixture directory not found at '{dir}'. Ensure the Fixtures/**/*.json items are copied to the output directory.");
                }

                return dir;
            }
        }

        public static string RawText(string fileName)
        {
            var path = Path.Combine(Directory, fileName);

            if (!File.Exists(path))
            {
                var available = string.Join(", ", System.IO.Directory.GetFiles(Directory, "*.json").Select(Path.GetFileName));
                throw new FileNotFoundException($"Fixture '{fileName}' not found. Available: {available}");
            }

            return File.ReadAllText(path);
        }

        public static JObject Json(string fileName) => JObject.Parse(RawText(fileName));

        /// <summary>
        /// The search response for "Taylor Swift Patient Zero" — the exact query Lidarr
        /// issues for artist "Taylor Swift" + album "Patient Zero". Contains three real
        /// "Patient Zero" track hits whose parent albums are absent from the album results.
        /// </summary>
        public static string PatientZeroSearch() => RawText(PatientZeroSearchFile);

        public static JObject PatientZeroSearchJson() => Json(PatientZeroSearchFile);

        /// <summary>
        /// A search response whose track hits all belong to ONE album that is absent from
        /// the album results — the shape that makes the parser fan out duplicate releases.
        /// </summary>
        public static string DuplicateAlbumSearch() => RawText(DuplicateSearchFile);

        public static JObject DuplicateAlbumSearchJson() => Json(DuplicateSearchFile);

        /// <summary>Album payloads keyed by Tidal album id, for the track-to-album lookup.</summary>
        public static IReadOnlyDictionary<string, string> AlbumsById()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var path in System.IO.Directory.GetFiles(Directory, "album-*.json"))
            {
                var json = JObject.Parse(File.ReadAllText(path));
                var id = json["id"]?.ToString();

                if (!string.IsNullOrEmpty(id))
                {
                    // Several fixtures describe the same album id (different editions);
                    // first one wins, which is enough for the lookup under test.
                    map.TryAdd(id, File.ReadAllText(path));
                }
            }

            return map;
        }
    }
}
