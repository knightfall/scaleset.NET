# Agent instructions

- For lifecycle and async changes, add or update deterministic tests for every affected ownership, contention, cancellation, and deadline boundary. Use explicit barriers to control ordering and `TimeProvider` or a controllable clock to cross deadlines. In deterministic tests, use wall-clock timeouts only as hang guards.
- Trace the changed paths through the caller, durable state, and external effects. Assert the outcome at each relevant boundary, including who can mutate, what is retained or released, what can be replayed, and when acknowledgement is safe.
- Before marking a PR ready, review the diff against these invariants, run the affected suites, and report the commands, results, and any unverified cases.
