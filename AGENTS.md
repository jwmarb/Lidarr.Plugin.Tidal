# Lidarr.Plugin.Tidal — Project Knowledge Base

## OVERVIEW
A fork of TrevTV/Lidarr.Plugin.Tidal (maintained by jwmarb) that registers Tidal as both an
indexer and a download client in Lidarr, so a Tidal subscription becomes a monitored music
library: album search, quality-profile gating, lossless download (FLAC up to 24-bit Hi-Res),
tagging, renaming, and import all run through Lidarr's normal pipeline. This fork adds a
64-test suite and fixes three parser bugs (per-track album re-fetch amplification, a fatal
`Enum.Parse` on the free-form `audioQuality` string, unguarded `mediaMetadata`) and a 429
retry storm (flat-delay, uncapped retries that renewed Tidal's cumulative throttle).

## STRUCTURE
- `src/Lidarr.Plugin.Tidal/` — the plugin: indexer, download client, FFmpeg remux, `TidalAPI` singleton
- `src/TidalSharp/` — fork of TidalSharp, the Tidal HTTP API client (PKCE auth, sessions, streaming, 429 backoff)
- `src/Lidarr.Plugin.Tidal.Tests/` — NUnit suite (56 offline + 8 opt-in live) and `Tools/TidalLogin`
- `src/Lidarr.Plugin.Tidal.sln` — plugin + TidalSharp + tests + TidalLogin + Lidarr.Core/Common from the submodule
- `ext/Lidarr/` — git submodule of upstream Lidarr (branch `plugins`), vendored only for the `Lidarr.Core` reference
- `src/Directory.Build.props` — repo-wide MSBuild (forked from Lidarr's: output paths, version stamp, warnings-as-errors)
- `_plugins/`, `_tests/`, `_temp/` — gitignored build outputs; the shipping DLL lands in `_plugins/net8.0/Lidarr.Plugin.Tidal/`
- `docs/SETUP.md` — end-user setup guide (PKCE login, FFmpeg image, delay/quality profiles); its Test/log messages and field labels are quoted verbatim from the code, so update it when you change a user-facing string. Stage 3 and the README's "Which build you have" tell old builds apart by the `Remux To FLAC` / `Extract FLAC From M4A` label and work around the GitHub-URL install picking an old release (Lidarr's `PluginService` needs a `*net8.0.zip` asset); revise both once a correctly named release ships

## WHERE TO LOOK
| Task | Path |
| --- | --- |
| Indexer search / release construction | `src/Lidarr.Plugin.Tidal/Indexers/Tidal/TidalParser.cs` (+ `Tidal.cs`, `TidalRequestGenerator.cs`) |
| "TidalDownloadProtocol is not enabled" gating | `src/Lidarr.Plugin.Tidal/Indexers/TidalDownloadProtocol.cs` |
| Download queue, file writes, lyrics | `src/Lidarr.Plugin.Tidal/Download/Clients/Tidal/TidalProxy.cs`, `Queue/DownloadItem.cs` |
| FLAC remux / AAC→MP3 conversion | `src/Lidarr.Plugin.Tidal/FFMPEG.cs`, `Download/Clients/Tidal/MetadataUtilities.cs` |
| Tidal HTTP client, auth, 429 backoff | `src/TidalSharp/API.cs`, `Session.cs` (backoff knobs are `internal`, exposed to tests) |
| Shared API singleton (indexer + download client) | `src/Lidarr.Plugin.Tidal/TidalAPI.cs` |
| Plugin identity / GitHub URL | `src/Lidarr.Plugin.Tidal/Plugin.cs` |
| Test harness (singleton reset, fake/real HTTP) | `src/Lidarr.Plugin.Tidal.Tests/Framework/` |
| Recorded API payloads (credential-scrubbed) | `src/Lidarr.Plugin.Tidal.Tests/Fixtures/` |
| Minting an expired credential file | `src/Lidarr.Plugin.Tidal.Tests/Tools/TidalLogin/` |
| Build / merge / version config | `src/Directory.Build.props`, `src/Lidarr.Plugin.Tidal/ILRepack.targets` |
| CI (build + GitHub release) | `.github/workflows/build.yml` |

## CONVENTIONS
- Plugin code lives in Lidarr's legacy `NzbDrone.Core.*` namespaces (e.g. `NzbDrone.Core.Indexers.Tidal`)
  even though the assembly is `Lidarr.Plugin.Tidal` — that is how Lidarr's loader discovers the types.
  Do not "fix" them to `Lidarr.*`.
- `TreatWarningsAsErrors` is on repo-wide (`src/Directory.Build.props`); the test project alone
  relaxes it and skips StyleCop, because test code is not a shipping artifact.
- Test fixtures are real Tidal payloads with every token/session/user id replaced by `REDACTED`;
  a test asserts the scrubbing. Keep any new fixture scrubbed the same way.
- Both `Lidarr.Plugin.Tidal` and `TidalSharp` declare `InternalsVisibleTo` for the test assembly;
  tests reach `internal` members (e.g. the throttle knobs) deliberately.
- `TidalSharp` compiles with `Nullable enable`; the plugin and test projects do not.

## COMMANDS
From the repo root; needs .NET SDK 8 and an initialised submodule
(`git submodule update --init --depth 1 ext/Lidarr`).

```sh
# Build the shipping plugin (version must match the target Lidarr instance — see NOTES)
dotnet build src/*.sln -c Release -f net8.0 \
  -p:WarningsNotAsErrors=NU1902 -p:AssemblyVersion=3.1.2.4913 -p:FileVersion=3.1.2.4913 -p:Deterministic=true

# Offline test suite (56 tests)
dotnet test src/Lidarr.Plugin.Tidal.Tests/Lidarr.Plugin.Tidal.Tests.csproj \
  -p:SolutionDir=$PWD/ext/Lidarr/src/ -p:SkipILRepack=true -p:NuGetAudit=false

# Live API tests (8; skipped with a diagnostic, not failed, without valid credentials)
TIDAL_INTEGRATION=1 dotnet test ... --filter "Category=Integration"

# One test class
dotnet test src/Lidarr.Plugin.Tidal.Tests/Lidarr.Plugin.Tidal.Tests.csproj \
  -p:SolutionDir=$PWD/ext/Lidarr/src/ -p:SkipILRepack=true -p:NuGetAudit=false \
  --filter "FullyQualifiedName~TidalParserTests"

# Mint a fresh credential file when the token expires
dotnet run --project src/Lidarr.Plugin.Tidal.Tests/Tools/TidalLogin \
  -p:SolutionDir=$PWD/ext/Lidarr/src/ -p:NuGetAudit=false -p:SkipILRepack=true
```

The three `-p:` flags on `dotnet test` are not optional: `SolutionDir` suppresses ~500 StyleCop
errors in upstream Lidarr source, `SkipILRepack` keeps TidalSharp un-internalized so the test
assembly can bind, `NuGetAudit` silences an upstream MailKit warning that would become an error.
Full rationale in `src/Lidarr.Plugin.Tidal.Tests/README.md`.

## NOTES
- **`dotnet test` destroys the shipping DLL.** It builds with `SkipILRepack=true` and overwrites
  `_plugins/net8.0/.../Lidarr.Plugin.Tidal.dll` with the un-merged one. Rebuild before packaging.
  Verify the merge: `strings -a _plugins/net8.0/Lidarr.Plugin.Tidal/Lidarr.Plugin.Tidal.dll | grep -c TidalSharp`
  (non-zero = dependencies merged, which the plugin loader requires).
- **Version pinning is not optional.** `ext/Lidarr` stamps its assemblies `10.0.0.*`; an unpinned
  local build records a `Lidarr.Core` reference no released Lidarr has, and Lidarr scans types
  across *every* plugin assembly at registration — a mismatched reference takes all installed
  plugins down, not just this one (log names the missing assembly, not the at-fault plugin).
- **Never edit `ext/Lidarr`.** It is a submodule tracking upstream's `plugins` branch; edits are
  lost on `git submodule update`. Pass overrides as `-p:` flags (CI rewrites the two
  `Directory.Build.props` files with `sed` instead).
- ILRepack (`src/Lidarr.Plugin.Tidal/ILRepack.targets`) merges TidalSharp, TagLibSharp, and
  Newtonsoft.Json into the plugin DLL with `Internalize=true`, working around a bug in
  Lidarr's plugin loader. TidalSharp is GPL-3.0, so the merged artifact is under GPL-3.0 terms.
- `TidalAPI.Instance` is a write-once static singleton; the test harness resets it via
  reflection (documented testability smell — don't "clean up" the harness around it).
- Upstream leftovers, harmless: the sln contains a Windows-only debugger project pointing at
  `X:\Projects\GitHub\Lidarr\_artifacts\...`; the plugin csproj removes a hardcoded
  `E:\Projects\...` stylecop path; a Windows-only `PostBuild` target copies Debug builds to
  `C:\ProgramData\Lidarr\plugins\jwmarb`.
- `src/Lidarr.Plugin.Tidal.Tests/README.md` is stale: it says "35 passed" and its layout table
  predates `ReleaseDeduplicationTests`, `ApiThrottleRetryTests`, and `HarnessSmokeTests`
  (the suite is now 64: 56 offline + 8 live).
- Live tests gate on `TIDAL_INTEGRATION=1` plus credentials from `TIDAL_USER_JSON` or a `.env`
  file (gitignored) found by walking up from the test assembly; a stale token is a skip, never a failure.
- FFmpeg must exist inside the Lidarr container for the remux (custom image, see README);
  without it, lossless imports report `-2147483648 kbps` and are mislabelled as AAC.
- Tidal throttles `/albums/{id}` cumulatively; the plugin backs off (`MaxThrottleRetries`,
  `ThrottleBackoffUnit`, `ThrottleBackoffCeiling`), but an already-throttled account stays
  throttled for several minutes regardless.
- `global.json` pins SDK 8.0.404 with `rollForward: latestMinor`; CI writes its own at 8.0.405.
- `NuGet.config` adds Servarr/Lidarr Azure DevOps feeds (Taglib, SQLite, FluentMigrator) that
  the submodule's restore depends on.
