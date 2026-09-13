# Event processing contracts

One session represents one match and freezes its objective subscriptions. Sources
publish observations with game timestamps, receipt timestamps and clock uncertainty.
The existing Live Client collector implements `IObservationSource` and coalesces
optional sampling with its baseline reads. New adapters must honor `SamplingPlan`
instead of starting a polling loop for each objective.

To add a detector:

1. Define immutable `IObservationPayload` / `IEventPayload` records and register their
   kinds and versions with `PayloadRegistry`. Unknown contracts fail closed.
2. Implement a per-match `IEventDetector`; declare its input cadence/clock bounds,
   output tokens, history and execution budget in `DetectorDefinition`.
3. Register pure reusable computations in `SharedFactRegistry` when multiple
   detectors need the same fact. Declare their source inputs in consuming detectors.
   `history.Fact<T>(id, version)` memoizes once per frozen observation window.
4. Register source capabilities and detector factories in `LiveProcessingCatalog`.
   Add objective vocabulary entries only when the event is ready to be tracked.
5. Emit supported candidates with stable keys and references covering every required
   input kind. `TrackedTokens` may express multiple associations for one stored event.
6. Validate captured current-patch encounters and resource use in shadow mode before
   enabling production output. A unit test is not evidence of gameplay accuracy.

Consumer eligibility is independent of objective relevance. Unknown/legacy inferred
events and shadow candidates cannot become timeline events. Raw reads and correction
identities remain available. `event_processing_reports` persists coverage,
subscriptions and shadow evidence; accepted events use the existing event repository.

Bounded queues/history, payload and evidence byte limits, and callback/finalization
deadlines protect the baseline collector. Callback timeout suppresses late output;
it cannot forcibly terminate arbitrary managed code, so only reviewed application
code is registered. Payloads, shared facts, and registrations are immutable during
a session. No user executable scripts are supported.

The shipped advanced trade/fight definitions are intentionally unavailable: the
existing sources do not substantiate attributed exchanges or exact changing
membership. Future recorder changes and UI changes are outside this subsystem.

## Deterministic post-game recovery (shadow)

`DeathRecapRecovery` replays Match-V5 death damage records through `ProcessingSession`.
It runs in the existing post-game map-state job using the responses already fetched,
with the original report's objective subscriptions. It never substitutes today's
objectives for a missing match-start snapshot. The live report is preserved and its
`Recovery` field is replaced on retry; no shadow candidate is appended to game events.
`POST /api/events/reprocess/{gameId}` retries one saved match, including an already
processed match. There is no UI addition or recorder dependency.

The registered `RECORDED_EXCHANGE` candidate is a pair of positive, attributed damage
totals from one death record involving the player and an opposing champion. Its
experimental TRADE association is for validation, not authorization to display an
ordinary trade marker. The timestamp is the death record's clock, never an inferred
trade start; counts, duration, short/extended/all-in classification and nonfatal
coverage are not inferred. Incoming records identify the dealer by ID/name; outgoing
records identify the target by ID but name the victim/dealer. Current-patch champion
entries use `type: OTHER`. IDs must resolve against the roster, names must agree,
environmental/zero damage cannot support a candidate. Ambiguous recaps are rejected
independently and counted in coverage; schema failures and budget overflow fail closed.

The source is sparse/event-driven (`TimeSpan.MaxValue` sampling interval), has a
256-observation ceiling, and uses point evidence rather than full-match history.
Compact provenance includes the timeline SHA-256, record path, source/detector
versions, patch, identities and normalized totals. This detects repeated source
records deterministically; separate death records are not claimed to be unique fights.

An actual current-patch match produced 15 shadow pair records; six ambiguous recaps
were rejected and six lacked damage arrays. This verifies parsing and execution,
not labeled-match accuracy or gameplay performance. Release gates remain unmet.
The existing LCU timeline audit must not be generalized to Match-V5: the latter's
death recaps contain attributed damage, but do not provide per-hit timestamps.

Use `scripts/Inspect-EventSources.ps1` for schema-only source observations and
`scripts/Measure-EventProcessing.ps1` for matched process-resource samples. Gameplay
frame times require an external frame-time capture. Do not claim the end-to-end
15-second review SLA from the event finalizer's one-second deadline alone: source
availability, game-phase detection and the rest of the save workflow also matter.
