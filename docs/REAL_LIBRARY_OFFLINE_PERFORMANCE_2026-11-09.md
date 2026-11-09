# Real metadata and cached-image offline performance — 2026-11-09

## Evidence boundary

This is **detached WPF measurement**, not foreground interaction, GPU frame rate,
or a claim that the real installed application's scrolling is smooth. No window
was shown; no UI automation, mouse, keyboard, video playback, or source scraping
was used. The runner instantiated a plain Application with application resources,
constructed MainWindow without Show, loaded the isolated index directly, and used
Measure/Arrange/UpdateLayout and RenderTargetBitmap. Unloaded lifecycle tasks with
no producer were not awaited.

Input was a read-only logical PostgreSQL export containing 33,038 video index
entries, 141 metadata entries, and 4,312 review records. Media paths/IDs were
remapped into a nonexistent fixture media root. No original media was read or
copied. Remote image/preview/trailer URLs and credentials were cleared. The
production diagnostic credential namespace was enabled before window creation;
the cloud configuration pointed only to a non-service loopback endpoint.

Only cache-owned image paths referenced by that snapshot were copied, read-only,
into the fixture: 600 files / 16,267,986 bytes, including 400 main-library poster
assignments. This does not cover every original video's poster or cold disk I/O.
Private titles, original media paths and images are deliberately absent here.

## Product changes and correctness

`dc8a93c` adds a bounded shared background decoder for review thumbnails and
enables it only at the review-row cover call. Detail/compare behavior retains the
default synchronous branch. Up to three decode workers share file-version keyed
jobs and frozen bitmaps; the independent LRU is limited to 32 MiB decoded bytes.
Unloaded rows cancel their subscription, not a shared job; late image callbacks
do not modify review transactions.

`1eb8a0f` calculates decoded memory from actual BitsPerPixel and aligned row
stride with checked arithmetic. Unknown or overflowing dimensions cannot enter
the cache. Independent source-linked tests passed 14 correctness assertions
(15 booleans including overall passed): Bgr24/Bgra32/Rgb48/Rgba64 odd-width
strides, overflow, 32-way request deduplication, frozen bitmap sharing, synchronous
hot hit, corrupt/missing images, changed file version and pixel refresh, memory
limit, and no pending jobs. Final cache bytes were 33,446,880 <= 33,554,432;
one corrupt-image failure was intentional.

## Final measurement identity

Both final processes exited 0 and wrote complete reports before their watchdog
was disposed and the isolated process terminated. Product identity in both:

- InformationalVersion: `1.7.3.3+1eb8a0f25d5f20166a248ce25b4259ac5ae9d51c`
- MVID: `e42a5f67-19ab-4253-914b-d79cf5eec084`
- Mode: detached offscreen; `foregroundInteractionVerified=false`;
  `realMediaRead=false`; isolated credentials enabled.

The main project was built once by the integration owner (Release, zero warnings
and errors). This task built only separate ignored diagnostic projects.

## Results (milliseconds)

**Performance acceptance is not passed.** The raw runner's `passed=true` means
the diagnostic completed, not that a latency target passed. In particular,
search hit-path scheduling gaps and the cover A/B do not meet the intended
smoothness claim.

| Measurement | Round 1 | Round 2 |
|---|---:|---:|
| MainWindow constructor | 3025.4 | 2966.8 |
| Isolated index load and first settled layout | 1038.8 | 1020.1 |
| First software paint | 53.8 | 52.0 |
| 600 image decodes at width 360, P50 / P95 | 3.24 / 11.97 | 3.15 / 11.88 |
| 90 detached virtual scroll layouts, P50 / P95 | 3.05 / 3.45 | 3.04 / 3.30 |
| Longest scroll layout | 33.4 | 31.2 |
| Review first settled layout (12 rows) | 138.1 | 131.5 |
| Review software paint | 18.3 | 17.8 |
| 12 review checkbox mutations + layout, P50 / max | 2.47 / 21.76 | 2.31 / 29.20 |
| Graph first settled layout | 1051.8 | 1050.5 |
| Graph command creation P95 | 6.34 | 6.21 |
| Graph software raster P95 | 6.57 | 6.35 |
| Process working set MiB | 360.2 | 357.2 |

Graph contains 19 nodes and every node remains linked. This is the structure
present in the snapshot's selected view, not a substitute for a large synthetic
all-node graph. The source configuration has 15,730 category assignments but
only nine distinct assigned category paths / ten paths including ancestors.
No nodes, edges, pagination, covers, or other features were removed.

### Review cover A/B: no latency improvement demonstrated

The same 12 real cached covers were constructed with explicit synchronous and
background options in one product binary. After warm-up, eight alternating-order
rounds included the same detached control Measure/Arrange work.

| 12-cover component batch | Round 1 | Round 2 |
|---|---:|---:|
| Synchronous warm P50 | 21.97 | 20.36 |
| Background cache warm P50 | 27.73 | 26.04 |
| Background first call, including cold code/JIT | 54.32 | 52.66 |
| All background covers ready from first request | 95.62 | 98.82 |

All 12 covers became images; both unloaded-row late-update assertions passed.
The measured warm component time is **slower** in the new path. Consequently this
change must not be advertised as an achieved rendering-speed gain. Its proven
properties are worker decode, sharing, invalidation, bounded retention, and
cancellation. File/path metadata checks and control creation still run on the
dispatcher. Whole-page before/after, cold disk, sustained visible interaction,
and a demonstrated user-perceived improvement remain unverified.

### Search remains an optimization target

Each query class was repeated three times using the production async search
method. Query text is omitted for privacy. The title-fragment sample matched all
33,038 fixture entries, so it measures a broad-hit path rather than selectivity.

| Query class (hits) | Total P50 rounds 1 / 2 | Dispatcher pulse P95 rounds 1 / 2 |
|---|---:|---:|
| No hit (0) | 240.4 / 242.3 | 32.6 / 32.6 |
| Existing tag (3,969) | 287.2 / 282.0 | 112.9 / 120.0 |
| Existing title fragment (33,038) | 379.3 / 384.2 | 94.0 / 94.5 |

The 16 ms scheduled dispatcher pulse is a scheduling-gap indicator, not an exact
UI blocking duration or frame rate. Nonetheless the 94–120 ms P95 hit-path gaps
mean this measurement does not establish an input-responsiveness pass. A later
change should separately profile immutable filtering/sorting, dispatcher result
application, view rebuilding, and layout before choosing an optimization.

### Read-only code localization for the next change

These are candidate costs from code inspection, not individually timed causes:

1. `MainWindow.SearchAsync.cs:40-47` materializes eligibility across the entire
   mutable library on the dispatcher before the bounded copy loop. The copies
   yield every 128 entries (`:51-67`), but eligibility has no such slice. The
   smallest next experiment is time-boxing that first pass as well, keeping the
   current version/source/view guards and immutable worker input.
2. `MainWindow.xaml.cs:1892-1893` constructs row lists for every hit and replaces
   the whole virtual ItemsSource after each search, including all-hit queries.
   WPF realization/rebinding then happens during layout. Measure preparation
   and ItemsSource assignment separately before introducing a reusable row
   source; preserve ordering, selection, virtualization and poster scheduling.
3. `MainWindow.MissingFiles.cs:25-26,37-38` scans all videos twice to compute
   counts before its unchanged-value fast return. A data-version keyed count
   snapshot would remove repeated scans if profiling shows meaningful cost.
4. `ApplySavedFields` (`MainWindow.xaml.cs:1135`) runs on load, visits all items,
   and performs a File.Exists for each custom poster. It is not called by the
   normal search path, so blaming it for per-keystroke gaps would be incorrect.
5. `MainWindow.SearchAsync.cs` already maintains eligibility/capture/apply and
   rendering-stage timings. The next diagnostic should persist those fields
   alongside pulses rather than treating a pulse as a uniquely located block.

No product code was changed following the final measurement freeze.

## Evidence and runner corrections

Ignored local evidence under `artifacts/1733-p0/perf-real/`:

- `fixture-ready/fixture-summary.json`: input isolation and image counts.
- `offscreen-real-final-round1.json` and `offscreen-real-final-round2.json`:
  final full aggregate results, version/MVID and progress sidecars.
- `cache-test-result-bpp.json`: source-linked cache correctness assertions.
- `probe/Program.cs`: independent reflection runner, no product build reference.
- `prepare_fixture.py`: metadata/path/credential sanitization and cache copying.

Exploratory `offscreen-real-round1.json` was invalidated: although measurement
finished, the no-window Dispatcher lifecycle did not exit and its watchdog later
overwrote the full report. It is not counted as passing evidence. The runner now
disposes its watchdog after writing final results and exits only its own isolated
process. Both final reports remained complete after process exit. Earlier
production-App startup and waiting for a task without an offscreen producer were
also removed from the runner; those failures are not classified as product
performance defects.

## Not established

- Foreground mouse/keyboard latency, real scrolling FPS, GPU composition or a
  real-window theme comparison.
- Every original poster, raw video I/O, thumbnail generation, or cold disk/cache.
- Full large-node graph behavior from this relatively small relationship view.
- Bulk review transaction throughput, DB restore correctness or scrape accuracy
  (owned by separate acceptance lines).
- A latency improvement from the new review cover cache.
