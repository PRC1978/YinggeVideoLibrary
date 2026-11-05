# PostgreSQL cutover and UI audit — 2026-11-05

## Current boundary

Normal startup still reads and writes the existing library files. The bundled
PostgreSQL runtime, `PostgresLibraryMigration`, `PostgresLibraryIndexStore`, and
`PostgresLibraryMetadataStore` have only been exercised with isolated data.
They are not a production cutover. An installer containing PostgreSQL has been
installed and uninstalled under a separate test AppId, not released.

Do not enable `BundlePostgres` for a normal release until every authoritative
writer and restore path uses the same storage mode. Moving only the video index
and metadata would allow review, queue, role, and configuration writes to diverge.

## Writer inventory for the cutover

| Data | Current authority | Cutover requirement |
| --- | --- | --- |
| Video index and folder roots | `library-index.json` | Existing PostgreSQL index store must be wired to normal reads and all scan, cleanup, relocation, and review writes. |
| Work metadata and provenance | `metadata.json` | Existing PostgreSQL metadata store must share the review transaction boundary. |
| Categories, tags, AI reviews, settings | `config.json` | Move configuration and review state together; preserve Windows Credential Manager secrets separately. |
| Scrape queue and source cooldown | `scrape-queue.json`, `scrape-source-backoff.json` | Durable scheduling, cancellation, retry, and restart semantics need transactional persistence. |
| People and work links | role profile store | Keep person identity and work links in one transaction; preserve manual edits and source provenance. |
| Review audit and undo | `review-history.jsonl` and transaction journals | Write result, history, and undo intent atomically; interrupted recovery must be idempotent. |
| Other user state | ratings, views, graph moves, duplicate history, site imports, AI budget and jobs | Explicitly migrate and verify; do not silently discard files outside the first four tables. |
| Backup and restore | `LibraryBackupService` file manifest | Add database-consistent backup/restore and rollback before switching authority. |

The cutover must use an explicit per-library storage marker. Startup must reject
a mixed or incomplete state instead of guessing which copy is newer. Keep the
original files and a verified backup until a complete isolated upgrade,
restart, restore, and rollback rehearsal passes.

## Isolated UI review

Generated a synthetic 60-video, 40-category fixture with 10 posters under
`%TEMP%\YinggeUiAudit-ed68f2898bfb4ea7a434cc943096ba97` and ran
`--diagnose-ui-theme` with `--data-dir` pointing to that fixture. The diagnostic
produced 237 layout records across nine themes and three window sizes, plus
238 screenshots and theme-switch/style probes. No real media library was read.

- No `cardPopulationTimedOut=true` or `sphereEveryNodeLinked=false` record was
  found in `theme-layout.json`.
- The 1240×780 glass category capture shows nodes, labels, and relationship
  lines. This does not establish interactive 3D rotation or performance at
  30,000 videos.
- The 800×550 glass settings capture shows a reachable theme selector with no
  horizontal clipping in the visible area. The 800×550 cinema review capture
  shows filters, paging, and actions, but the synthetic data does not prove
  candidate approval or rejection.
- The 1240×780 neumorphism library capture shows four full cards per row.
  Missing-file indicators appear because synthetic paths do not contain real
  videos; this is expected for the fixture.

Remaining UI acceptance: real mouse/keyboard review actions in an isolated
review fixture; long candidate titles; graph pan/zoom/rotation interactions;
theme switching while a large list is loading; scroll and layout timings for
30,000 synthetic videos. The current screenshot pass is visual and diagnostic,
not a claim that these interactions have passed.

## Verification this turn

- Desktop `dotnet build -c Release --no-restore`: zero warnings, zero errors.
- Cloud tests initially failed because the local PostgreSQL test binary was not
  selected. With `YINGGE_TEST_PG_BIN` pointed at an isolated PostgreSQL 18.6.1
  binary directory, `dotnet test cloud/tests/Yingge.Cloud.Tests -c Release
  --no-restore` passed 65/65.
- Cloud production service remains inactive and public HTTP still returns an
  empty response. This is independent of the local library cutover.
