# Lidarr.Plugin.Tidal.Tests

Unit and integration tests for the Tidal indexer plugin.

## Running the tests

The repo-wide `Directory.Build.props` and the vendored Lidarr submodule need three
extra MSBuild properties on the command line. From the repository root:

```bash
dotnet test src/Lidarr.Plugin.Tidal.Tests/Lidarr.Plugin.Tidal.Tests.csproj \
  -p:SolutionDir=$PWD/ext/Lidarr/src/ \
  -p:SkipILRepack=true \
  -p:NuGetAudit=false
```

| Property | Why it is needed |
|---|---|
| `SolutionDir` | The vendored Lidarr adds StyleCop via `$(SolutionDir)stylecop.json`. Building a project directly (not via the `.sln`) leaves it unset, and ~500 `SA1200` errors are raised against upstream's own source. |
| `SkipILRepack` | The shipping plugin build merges **and internalizes** `TidalSharp` into `Lidarr.Plugin.Tidal.dll`. The test assembly then binds to an internalized `TidalClient` and fails at runtime with `MissingMethodException: TidalAPI.get_Client()`. This flag leaves the assemblies un-merged for tests only; shipping builds are unaffected. |
| `NuGetAudit` | Upstream pins `MailKit` 4.14.0, which trips a NuGet audit warning, and upstream sets `TreatWarningsAsErrors`. Pre-existing and unrelated to the plugin. |

Prerequisites: .NET SDK 8.x and an initialised submodule
(`git submodule update --init --depth 1 ext/Lidarr`).

## Layout

| Path | Contents |
|---|---|
| `TidalParserTests.cs` | Characterization tests pinning parser behaviour that is already correct, so fixes cannot silently regress it. |
| `TidalParserRegressionTests.cs` | Regression tests for the fixed defects (duplicate fetches, `Enum.Parse` crash, null `mediaMetadata`). |
| `TrackSearchContractTests.cs` | What a track-name search *is allowed* to produce: albums only, album URLs only, titles that round-trip. Replaces the withdrawn `Bug1`. |
| `LidarrTitleParsingTests.cs` | Characterizes **upstream** Lidarr title parsing, as evidence for why track names are not injected into release titles. |
| `Integration/LiveTidalApiTests.cs` | Opt-in tests against the real Tidal API. |
| `Framework/` | Fixture loader, fake and real `IHttpClient`s, and the singleton harness. |
| `Fixtures/*.json` | Real, credential-scrubbed Tidal API payloads. |
| `Tools/TidalLogin/` | Standalone interactive PKCE login helper that mints `lastUser.json`. Not a test; excluded from the test assembly. |

## Expected state

A default (offline) run should be **fully green: 35 passed**.

### Defects fixed

| Was | Fix | Site |
|---|---|---|
| An album reachable only via a track hit was re-fetched once per matching track, emitting N identical release sets (measured: 9 calls, 18 releases where 2 were correct). | Dedupe against a `HashSet` of album ids seeded from the album results and extended as albums are fetched. | `TidalParser.cs` `ParseResponse` |
| `Enum.Parse<AudioQuality>(result.AudioQuality)` threw on any value outside the enum (`MQA`, `DOLBY_ATMOS`), discarding every release in the response — and its result was never used. | Line removed. Quality variants come from `mediaMetadata.tags`, as they always did. | `TidalParser.cs` `ProcessAlbumResult` |
| `MediaMetadata.Tags` was dereferenced unguarded, so an album payload without `mediaMetadata` threw `NullReferenceException`. | Null-coalesce to an empty tag array; degrades to the lossy variants. | `TidalParser.cs` `ProcessAlbumResult` |

### One "defect" was withdrawn

The original `Bug1` test asserted that a track-name search must emit a release whose title
names the track. **That premise was wrong**, and `TrackSearchContractTests` now pins the
opposite, with the reasoning recorded in its XML docs.

The user-visible symptom is a *catalogue disagreement*, not a plugin bug:

- MusicBrainz (Lidarr's catalogue source) lists "Patient Zero" as a **Single**.
- Tidal ships it only inside **The Life of a Showgirl: The Encore**.

Three upstream constraints make a track-level release impossible to represent honestly:

1. `ReleaseInfo` has no track or track-number field.
2. `DownloadItem.SetTidalData` throws on any non-album URL and then downloads the whole
   album (`DownloadItem.cs:227`).
3. Lidarr re-parses the release `Title` and permanently rejects a release whose parsed album
   title differs from the searched one (`SingleAlbumSearchMatchSpecification.cs:33-37`).

Constraint 3 was verified empirically — see `LidarrTitleParsingTests`, which runs Lidarr's
own parser. Two findings worth recording:

- Appending `[Patient Zero]` to the title does **not** corrupt album identity: it still
  parses to `The Life of a Showgirl: The Encore` (one scoring function strips brackets).
  So annotating is *harmless* — but also *useless*.
- `CleanArtistName("Patient Zero")` = `patientzero` vs
  `CleanArtistName("The Life of a Showgirl: The Encore")` = `thelifeshowgirlencore`.
  The album can therefore **never** satisfy a search for the single; Lidarr rejects it as
  `Wrong album` no matter what the title says.

That rejection is **correct**. Substituting the parent album would download and tag
something the user did not ask for. The honest answer is "Tidal has no release matching
that single" — so a user who wants the song should search for the containing album.

## Integration tests

Skipped by default. They are `[Explicit]` and `[Category("Integration")]`, and run only
when `TIDAL_INTEGRATION` is `1`/`true`/`yes`:

```bash
TIDAL_INTEGRATION=1 dotnet test src/Lidarr.Plugin.Tidal.Tests/Lidarr.Plugin.Tidal.Tests.csproj \
  -p:SolutionDir=$PWD/ext/Lidarr/src/ -p:SkipILRepack=true -p:NuGetAudit=false \
  --filter "Category=Integration"
```

Credentials are read from `TIDAL_USER_JSON`, or from a `.env` file discovered by walking up
from the test assembly to the repository root. **Credentials are only read — never logged,
copied, or committed.** If they are missing, malformed, or **expired**, the tests **skip**
with a diagnostic rather than failing, so a stale token never looks like a code regression.
(This was confirmed the hard way: the token expired mid-session, the live tests went red, and
the expiry pre-check was added so it reports `credentials expired at <time>` instead.)

### Getting a token (`Tools/TidalLogin`)

```bash
dotnet run --project src/Lidarr.Plugin.Tidal.Tests/Tools/TidalLogin \
  -p:SolutionDir=$PWD/ext/Lidarr/src/ -p:NuGetAudit=false -p:SkipILRepack=true
```

It prints a login URL, waits while you authenticate, then takes the pasted redirect URL and
writes `lastUser.json`. Use `--config-dir <path>` or `TIDAL_CONFIG_DIR` to choose where
(default `~/.tidal-auth`). Then:

```bash
export TIDAL_USER_JSON="$(cat ~/.tidal-auth/lastUser.json)"
export TIDAL_INTEGRATION=1
```

**Why a tool is required, and why pasting a redirect URL into `.env` cannot work.**
Tidal uses PKCE. `Session.RegenerateCodes` (`Session.cs:123-126`) generates a random
`code_verifier` in memory and sends only its SHA-256 `code_challenge` to Tidal. Redeeming the
authorization code requires presenting that *original* verifier, which is never persisted.
The code itself carries `challengeId` and `cuk` claims binding it to the exact `Session`
instance that produced the login URL, so **a code minted by one process cannot be redeemed by
another** — regardless of how quickly it is relayed. This tool holds one `Session` open across
both halves, which is the only way to close that gap outside Lidarr's own UI.

Authorization codes expire within minutes, so paste promptly. If the exchange reports
`The token has expired`, just re-run it.

The tool calls the plugin's real `TidalClient.Login`, so a success also proves that code path
end to end.

These assert on response *shape*, not chart positions or result counts, so they stay stable
as the catalogue changes. They exist to catch the two things fixtures cannot: credentials
going stale, and Tidal changing its API contract.

## Fixtures

Recorded from the live API and scrubbed before committing: any `sessionId`, `access_token`,
`refresh_token`, or `userId` field is replaced with `REDACTED`. `HarnessSmokeTests`
asserts this, so the scrubber itself is covered.

Two fixtures are deliberately mutated to reach defensive paths and carry a `_derivedFrom`
field recording the change:

- `album-unknown-audioquality.json` — `audioQuality` set to `MQA`, a value absent from the enum.
- `album-null-mediametadata.json` — `mediaMetadata` removed entirely.

To re-record after a Tidal API change, capture fresh payloads, apply the same scrubbing, and
confirm `Fixtures_contain_no_credential_material` still passes.

## A note on the harness

`Framework/TidalApiHarness.cs` uses reflection in two places, each working around a
production design choice rather than a test shortcut:

- `TidalAPI.Instance` is a write-once static singleton (`Initialize` returns early when already
  set), so its backing field must be cleared between tests to swap the HTTP client.
- `TidalUser`'s constructor is `internal` and `CountryCode`/`SessionID` project from a private
  `_sessionInfo` field normally filled by a live `GET /sessions`.

Making `TidalAPI` injectable and `TidalUser` test-constructible would remove the reflection
entirely. Worth considering if this area is refactored.
