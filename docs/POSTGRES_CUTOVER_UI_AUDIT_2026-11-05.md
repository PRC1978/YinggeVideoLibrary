# PostgreSQL cutover and UI audit — 2026-11-05

## Current boundary

Normal startup still reads and writes the existing library files. The bundled
PostgreSQL runtime, `PostgresLibraryMigration`, `PostgresLibraryIndexStore`, and
`PostgresLibraryMetadataStore` have only been exercised with isolated data.
They are not a production cutover. An installer containing PostgreSQL has been
installed and uninstalled under a separate test AppId, not released.

The current migration now imports a fixed allowlist of user-state documents
(`config`, ratings, review history/views, graph move history, duplicate history,
site import, role profiles, AI budget, Javinfo usage, source backoff, scrape
queue and snapshots, plugin candidates, missing cleanup) in the same database
transaction as the video index and work metadata. These documents preserve
their exact bytes in `library_document`; a compare-and-swap store can read,
replace, and delete them by hash. This is a migration bridge, not a final
relational schema or an active runtime writer. The import now holds an exclusive
offline library lease from the first source read through commit.

The writer inventory found three user-state files absent from ordinary backups:
plugin enablement/consent, saved plugin candidates, and missing-file cleanup
history. These now join the backup allowlist, and the PostgreSQL migration also
imports plugin enablement. An isolated feature diagnostic wrote all three,
created a verified backup, and compared their bytes in the package
(`backupPreservesPluginAndCleanupState=true`). This does not yet test restore
through the graphical settings flow. A separate isolated restore diagnostic
changed those three live files after backup, staged the restore, applied it
offline, and compared all three with the saved package
(`restoreReplacesPluginAndCleanupState=true`).

The migration and rollback export now include the managed `title-candidates`
and `ai-jobs` snapshots. Only their documented fixed-length hexadecimal JSON
names are accepted; the isolated probe round-tripped one of each and rejected
a path traversal name (`nestedDocumentPathsVerified=true`). Poster and thumbnail
assets remain file-backed and are not part of this database record export.

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

The legacy startup now refuses to open a directory containing
`storage-postgresql.active`, checking before recovery and again after acquiring
its session lease. The migration does not create this marker, and normal startup
does not use PostgreSQL yet. The bundled PostgreSQL probe verified that legacy
opening changes from allowed to blocked when the marker appears, then returns
to allowed after removal (`legacyModeGuardVerified=true`). A future cutover must
write the marker while holding the exclusive offline lease and only after its
database and rollback copy have been verified.

A private PostgreSQL process owner has also been added as an isolated runtime
component. It initializes a private cluster, stores its password in Windows
Credential Manager, binds to 127.0.0.1, rejects a second owner, checks the
cluster major version, stops on disposal, and can restart the same test cluster.
The bundled-runtime probe passed `privateClusterLifecycleVerified=true` on a
temporary library. This component is not connected to normal app startup.

At 30,000 synthetic index rows, the isolated full save took 1,913 ms and read
took 260 ms in the final run. A one-row missing-state change through the new
incremental writer took 379 ms; a subsequent remove/add pair and stale revision
rejection also passed (`scaleDeltaVerified=true`). These are storage calls, not
UI-thread timings, and this path is not wired to normal scanning yet.

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
  exposed a real overlap between the queue summary and AI/action buttons.
  The narrow review panel now stacks those controls while wider panels retain
  their original layout. The second capture shows no overlap. Four synthetic
  in-memory review records are now injected into redirected diagnostics so row
  content is actually drawn; they are never persisted. This does not prove
  candidate approval or rejection.
- The 1240×780 neumorphism library capture shows four full cards per row.
  Missing-file indicators appear because synthetic paths do not contain real
  videos; this is expected for the fixture.

Remaining UI acceptance: real mouse/keyboard review actions in an isolated
review fixture; long candidate titles; graph pan/zoom/rotation interactions;
theme switching while a large list is loading; scroll and layout timings for
30,000 synthetic videos. The current screenshot pass is visual and diagnostic,
not a claim that these interactions have passed.

The later short-window screenshot pass exposed a responsive-margin override:
`ShowView` replaced the margin chosen by the resize handler. Both now use the
same calculation. In the 800×550 cinema review capture, the queue card begins
about 42 pixels higher while the filter, AI, and pager controls remain visible.
The repeated theme diagnostic wrote its layout report with zero card-timeout
or missing-graph-link flags. This improves first-screen visibility; it does not
eliminate the need to scroll through review cards on a short window.

## Verification this turn

- Desktop `dotnet build -c Release --no-restore`: zero warnings, zero errors.
- Cloud tests initially failed because the local PostgreSQL test binary was not
  selected. With `YINGGE_TEST_PG_BIN` pointed at an isolated PostgreSQL 18.6.1
  binary directory, `dotnet test cloud/tests/Yingge.Cloud.Tests -c Release
  --no-restore` passed 65/65.
- The expanded bundled PostgreSQL probe passed: loopback only, credential
  roundtrip, active-session import blocked, synthetic index/metadata and
  user-state document import verified, stale compare-and-swap write rejected,
  and a consistent rollback export compared against the database. The export
  uses a fresh staging directory and only renames it to the requested path
  after the database snapshot is complete; it refuses to overwrite an existing
  directory. The export writes a per-file SHA-256 manifest and verifies it
  before rename; the probe also changed one exported file and confirmed offline
  verification rejects it, then restored the bytes and passed again. A failed
  export can leave a `.partial-*` directory for diagnosis.
  `realLibraryRead=false`; server stopped after the probe.
- An isolated PostgreSQL transaction now saves review-related config bytes,
  work metadata, and review history bytes together. The probe confirmed all
  three after a successful commit, then supplied a stale config revision and
  verified the attempted metadata change rolled back (`atomicReviewStateVerified=true`).
  This is a storage primitive only: the live review UI is not wired to it yet.
- After the UI correction, 237 layout records across nine themes and three
  window sizes passed the existing timeout/graph-link checks. The redirected
  diagnostic rendered four review rows in each non-classic mode.
- Isolated motion diagnostic passed for smooth, balanced, and animated modes.
  The 60-video search diagnostic returned identical synchronous/asynchronous
  results and visible cards in all seven query cases. Its first title query
  still recorded a 21.18 ms UI gap, so this fixture does not establish a
  consistently sub-frame search experience.
- Cloud production service remains inactive and public HTTP still returns an
  empty response. This is independent of the local library cutover.

## 2026-11-05 review transaction continuation

- Added a typed PostgreSQL review transaction writer. It checks each before
  snapshot, changes reviews, tags, automatic-tag state and metadata, then
  commits config, metadata and history in one database transaction. Review
  history is appended to the existing JSONL document. Config fields unknown
  to this client are preserved.
- Isolated probe passed approval, restart-style document reload, stale-preview
  rejection and undo (`typedReviewRoundTripVerified=true`,
  `typedReviewUndoVerified=true`, `passed=true`). Release build: zero warnings
  and errors. The probe uses a temporary library and temporary PostgreSQL
  cluster and reports no real-library read.
- This is not a live cutover. The normal review screen still uses the existing
  file journal. Startup, background queue writers, backup/restore and all
  remaining authoritative writes must use one storage mode before enabling
  PostgreSQL for a real library.

## Fresh-database recovery continuation

- Added `PostgresLibraryRecovery.RestoreIntoFreshDatabaseAsync`: it validates
  every exported file against the manifest before import, imports only into an
  empty database, then compares imported content with the snapshot. It refuses
  a second import into the same database. No active database is overwritten.
- The first probe exposed that the ordinary import created a session lock
  inside the immutable export directory. Recovery now places its exclusive
  lease beside that directory; the export remains byte-for-byte verifiable.
- The isolated probe restored the export to a separate private cluster,
  rejected a second restore, stopped and reopened that cluster, and verified
  the restored content again (`freshDatabaseRestoreVerified=true`,
  `privateClusterLifecycleVerified=true`, `passed=true`). This covers records
  only; posters and thumbnails require a separate backup policy.

## Review history read path

- The existing history parser now accepts database document bytes as well as
  file streams, so both modes apply the same hash checks and legacy-record
  compatibility rules. The PostgreSQL review store exposes a read operation
  with records, invalid-line count and document revision.
- The isolated probe reloaded approval and undo from PostgreSQL, confirmed
  both IDs were present and that no action remained undoable after undo.
  Release build remained at zero warnings and errors; full bundled database
  probe returned `passed=true`.

## Queue migration integrity

- Found a migration data-loss gap: the queue manifest and JSON snapshot were
  imported, but post-snapshot events in `scrape-queue.journal.*.bin` were not.
  Added the exact eight-digit journal filenames and recovery sidecars to the
  database document allowlist. The import/export retains their original bytes.
- The isolated probe created two tasks, with the second written after the
  checkpoint into the binary journal. It verified both tasks after database
  import, export, restore to a fresh private database and a second export.
  `queueJournalMigrationVerified=true`, `freshDatabaseRestoreVerified=true`,
  `passed=true`. Release build had zero warnings and errors.
- The live queue still writes files. This closes a migration/rollback integrity
  gap; a database-backed queue writer and coordinated mode switch remain open.

## Isolated interface and image-backup continuation

- Re-ran the synthetic UI fixture (60 videos, 40 category leaves, 10 posters).
  Nine themes at three window sizes produced 237 layout records and 238
  screenshots. No card population timeout or disconnected category node was
  reported; 24 review-layout records rendered rows. The 800x550 cinema review
  and glass settings screenshots were inspected. This does not validate a
  live PostgreSQL-backed UI or all interactive paths.
- PostgreSQL export can now include posters and user custom thumbnails while
  holding an exclusive offline library lease. The export manifest hashes those
  files; unknown names and links are rejected. Recovery copies verified images
  into a new directory without replacing existing assets.
- The isolated probe exported both asset types, rejected a deliberately altered
  poster, restored the images and database to new locations, then exported
  again. `assetBackupVerified=true`, `freshDatabaseRestoreVerified=true`,
  `passed=true`; Release build had zero warnings and errors.
- Image inclusion is opt-in at the export call. Normal settings backup and
  startup restore have not been switched to database mode.

## 30,000-item isolation check

- An isolated file library with 30,000 index entries and no real media was
  imported into a fresh private PostgreSQL cluster, exported, checked against
  its SHA-256 manifest, and compared with the database. The import took 1,259
  ms on this machine; `scaleFileRoundTripVerified=true` and the full probe
  returned `passed=true`. The separate 30,000-row index write and delta checks
  also passed.
- The synthetic 30,000-video UI search diagnostic completed. Three queries
  with about 5,000 matches measured layout P50 of 327.54, 324.18 and 296.97
  ms; the longest UI gaps were 52.06, 40.2 and 48.67 ms. Search and card
  rendering still miss a 16 ms frame budget. The fixture has no poster decode
  or real disk I/O, so these numbers do not establish real-library performance.
- The normal app still does not use PostgreSQL as its authoritative store.
  Neither these tests nor the new backup API justify switching the storage
  marker for an existing library.

## Front-end contrast correction

- In the 800x550 cinema review screenshot, the disabled AI action used a dark
  red surface with nearly black lettering. The shared primary-button style
  now uses a neutral surface and secondary text while disabled. A fresh
  nine-theme, three-size synthetic diagnostic completed (237 layout records),
  and the cinema review screenshot was visually checked. This fixes the
  observed disabled-state contrast; it does not resolve the search layout
  pauses or prove live interaction with a PostgreSQL-backed library.

## Database configuration writer

- Added an isolated PostgreSQL configuration writer that applies the existing
  three-way merge rules, validates audit history, retries compare-and-swap
  conflicts, preserves unknown fields, and excludes API credentials from the
  persisted document. This avoids replacing concurrent edits with a stale UI
  snapshot when the eventual runtime cutover occurs.
- The isolated probe verified two separate favorite edits survived one merge,
  an unknown future setting remained, and all three credential fields stayed
  empty (`typedConfigMergeVerified=true`, `passed=true`). Release build had
  zero warnings and errors. The normal window still calls the file writer;
  no database mode has been enabled for an existing library.

## Damaged-snapshot recovery check

- The isolated recovery probe copied a valid export, altered `metadata.json`,
  and attempted to restore it into a fresh private database. Restore rejected
  the altered snapshot before creating the migration table. The unaltered
  export then restored successfully to that same database. The final probe
  reported `corruptRestoreStoppedBeforeDatabaseWrite=true` and `passed=true`.
- A cold startup produced one transient connection timeout with the probe's
  five-second connection budget. The isolated probe now allows 30 seconds;
  Release build completed with zero warnings and errors, and the full probe
  passed on rerun. This does not establish production startup reliability.
