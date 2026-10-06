# Query Access Foundation

Implemented pure primitives in `Surf2.Storage.Relational.Access`, not runtime
activation or a replacement repository. Existing Snapshot, Capture, Index and
State stores keep their summaries, pages, Ready checks, SQL cancellation and owner
tokens. No SQL, filesystem provider, schema change, WPF dependency or new package
is introduced here.

## Public API

- `SessionLocator` and its sealed `SnapshotResourceLocator`,
  `CapturedDataSetLocator`, `IndexedDocumentLocator`, `DiagramRevisionLocator`
  variants hold resolved domain keys and `RelationalSession.Epoch`. `From(...)`
  consumes existing summaries/descriptors. `RequireSession`/`RequireEpoch` rejects
  a reconnect with overlapping numeric keys. Historical snapshot version and
  data-companion source, dataset display format and immutable revisions are part
  of equality. No alias, content, rows, assets or owner byte token is retained.
- `ByteBoundedCache<TKey,TValue>(maximumBytes, maximumEntries, comparer?)` offers
  `TryStore`, `TryAcquire`, `Invalidate`, `Clear`, `Usage` and `Dispose`.
  `CacheLease<TValue>.Value` stays pinned until explicit lease disposal.
- `SingleFlight<TKey,TValue>(maximumFlights, maximumConsumersPerFlight, comparer?)`
  offers `RunAsync(key, read, consumerCancellation)`, `ActiveCount` and
  `DisposeAsync`. It deduplicates concurrent reads, not completed results.
- `QueryLifetime(epoch, scopeKey?)` offers `BeginRequest`, `InvalidateContext`,
  `ChangeContext`, `IsCurrent`, `TryPublish`, `ActiveRequests`, `Dispose` and `DisposeAsync`.
  Each `QueryRequest` owns a `QueryStamp` and cancellation token.
- `QueryDiagnostics(sink, timeProvider?)` offers `Begin`, `ActiveOperations` and
  `SinkFailures`. A `QueryMeasurement` records counts/cache hits and completes as
  `Completed`, `Cancelled`, `Failed`, `Stale` or `Abandoned`.
- `await AccessContractChecks.RunAsync(ct)` returns 60 passing-case labels or
  throws. These are pure deterministic checks; they do not establish SQL/WPF
  behavior, profiler-calibrated bytes or rollout readiness.

## Memory and Lifetime Rules

Use separate cache instances/budgets for text, pages, previews and decoded assets.
Every cache admission supplies a positive retained-byte estimate including key,
value, entry overhead and relevant decoded structures. Entry count is independently
bounded. Oversized or pinned-pressure admissions return false; the caller may
still open an explicitly supported selected value without caching it. No truncation
or automatic payload disposal occurs. Values must be immutable and must not own
readers/connections. A lease does not enforce immutability of an arbitrary `TValue`.

LRU eviction touches only unpinned entries. Invalidation/clear/dispose hide pinned
entries immediately but keep their byte and entry charges until the last lease
closes. A replacement and its retired predecessor are charged independently.
Disposed leases clear their own value reference. Open controls, overlays, undo,
transient buffers and caller copies remain separate memory owners. These estimates
need measurement; the cache is not a total-process memory bound.

Cache/single-flight keys must include the session-bound locator and ALL relevant
policy: renderer version for generated text, filter/sort/page for grid pages, and
membership/unloaded/alias generation for mutable metadata. Do not share a single
flight key between different reads. The first consumer supplies the provider
factory; later equivalent consumers join it. Canceling one consumer only cancels
that wait. The last departing consumer removes the joinable flight and cancels
its provider. Actual running jobs still count toward capacity until the provider
task AND cancellation callbacks complete; a same-key replacement cannot be removed
by old cleanup. `CancelAsync` marks cancellation immediately but runs callbacks
off the transition caller. The first callback-completion task is retained and
observed, and its source is disposed only after those callbacks finish.

There is no hidden work queue. Capacity pressure throws `QueryCapacityException`;
workflow scheduling/retry/coalescing belongs to the later measured scheduler.
Provider factories must use real async I/O, forward their supplied cancellation,
and dispose resources before returning. They must not capture a single consumer's
token as the shared provider lifetime. Disposal rejects new jobs, cancels all
providers and awaits actual completion, including uncooperative providers. Callback
exceptions during cancellation do not mark a provider stopped or release its slot.
`CancellationCallbackFailures` counts observed callback errors without retaining
exception text. `AccessContractChecks.RunCancellationAsync(ct)` separately runs
the blocked-callback, retirement and disposal checks.
Faulted/canceled tasks are not cached, and abandoned failures remain observed.

## Generations and Publication

Use one `QueryLifetime` for one logical workflow owner, not a global lock for all
reads. `BeginRequest` supersedes/cancels the previous request. Advance its context
on scope, membership, alias, unloaded state, filter policy or mutable-head changes;
`ChangeContext` also handles reconnect. Context and request generations plus an
independent owner ID prevent A-B-A revival and cross-window publication.

Capture immutable WPF state before starting work. Keep `QueryRequest` alive until
the real operation exits and dispose it in `finally`. Cancel/close does not pretend
the job has released SQL resources. `TryPublish(stamp, shortSynchronousCallback)`
checks owner/epoch/scope/generations under the same gate as context changes; invoke
it on the dispatcher with no await, I/O or owner reentry inside its callback.
`IsCurrent` alone is advisory and is not an atomic check-then-publish operation.
WPF `Unloaded` during reparenting is not logical close.

These local generations are NOT substitutes for SQL rowversions, IndexRequestContext,
published revisions or provider cursor validation. Results must first be verified
by the existing store and the requested immutable revision before publication.
There is no automatic polling of database mutations or automatic UI invalidation.

## Diagnostic Contract

Diagnostics contain only a known operation enum, SHA-256 query-template fingerprint,
random connection epoch, duration, rows/bytes/query/cache-hit counts and outcome.
No SQL body, parameter, connection string, entity name/path, result body or exception
message can be supplied in a diagnostic record. `QueryFingerprint.FromTemplate`
hashes a reviewed parameterized template; `Parse` accepts only 64 hexadecimal digits.
Do not fingerprint interpolated parameter values as if they were stable templates.

Measurement completion is idempotent; disposal without completion is `Abandoned`.
Record provider completion after cleanup, not at cancellation request time. Query
counts/bytes are supplied by the workflow/provider; these primitives do not intercept
SqlClient or claim server logical-read/CPU measurements. Sinks must be bounded and
nonblocking; exceptions increment `SinkFailures` and never fail an authoritative
read. No internal diagnostic/event queue retains content or grows with corpus size.

## Verified and Deferred

The pure entrypoint checks epoch/domain identity, revision/source pinning, payload-free
contracts, byte/LRU/count pressure, pin retirement/replacement/disposal, supersession,
scope/reconnect A-B-A, logical close, diagnostic shape/outcomes/overflow/sink failure,
shared-consumer cancellation, retry, capacity, real teardown and same-key replacement.

Runtime composition, explorer/locator parsing, physical file fingerprint verification,
actual store calls, provider reader cancellation, a priority scheduler, result coverage,
profiling, large editor policy and WPF lifetimes remain parent-owned rollout gates.
No existing MainWindow or domain storage files were changed for this foundation.

The current-source isolated harness passed all 60 checks and repeated cancellation
checks ten times. No SQL or shared WPF build was executed by that harness.
