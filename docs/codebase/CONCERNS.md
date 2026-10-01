# Codebase Concerns

Source-derived migration concerns at upstream commit
`e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5`. These are planning risks,
not a comprehensive security audit or verified production defect inventory.

## 1) Top Risks (Prioritized)

| Priority | Concern and evidence | Migration response |
|---|---|---|
| High | Example loses runner maps at restart; `runnerState` | Discover owned resources and reconcile before acquiring/provisioning |
| High | Example force-removes busy runners and deletes scale set on exit; `shutdown`, `run` | Confirmed D12: bounded drain, preserve unfinished runners and scale-set identity, reconcile on restart |
| High | Replay is possible; unknown runner events panic in example maps | Make lifecycle transitions and cleanup idempotent |
| High | Completion drops mapping before container removal succeeds | Retain pending cleanup state until absence is confirmed |
| High | Container creation can succeed before start fails; tracking happens after start | Persist operation intent and discover partial resources |
| Medium | Listener ownership comment differs from actual code | Assign session lifecycle to the host explicitly |
| Medium | Upstream mutable public preview and recent listener redesign | Pin baseline, preserve fixtures, review upgrades deliberately |
| Medium | Current Mac TLS baseline fails a string assertion | Record failure; assert trust rejection independently of OS wording |

## 2) Technical Debt

Core client combines auth, URL construction, APIs, parsing, and metadata in
1,184 lines. Split C# by responsibility while preserving tests/contracts.
README/devcontainer Go 1.25 guidance differs from `go.mod` 1.26.3.
Example README lists `--runner-image`, but `main.go` registers no such flag.
The scanner reports no container/performance configuration despite a
devcontainer and benchmark source; scan output is discovery evidence, not a
complete truth source. The production TODO scan found none; this does not
establish an absence of design gaps.

## 3) Security Concerns

| Source behavior | Implication | Planned handling |
|---|---|---|
| Error bodies may be included in errors | Credential-bearing responses need redaction | Bounded, redacted structured exceptions |
| Example credentials on command line; JIT in container env | Operational exposure to host/process/container inspectors | Mounted credential files; controlled JIT delivery |
| Disable-verification option | Can remove server identity checks | Deliberate explicit opt-in and diagnostic reporting |
| mTLS certificate offered to all connections | Enterprise deployments may need narrower scope | Host-aware certificate selection |

## 4) Performance and Scaling Concerns

The example provisions sequentially inside the listener callback, delaying
the next poll/ack during a large scale-up. Its idle poll does no convergence
work. It has no explicit idle scale-down path or shared Docker-host budget.
The C# product needs bounded provisioning parallelism, retained statistics,
owned-resource discovery, and observable lifecycle progress. No throughput
or startup-latency guarantee was measured.

## 5) Fragile/High-Churn Areas

The saved 90-day scan reports five changes to `client.go`, four to
`client_test.go`, and one each to listener source/tests and Docker main/scaler.
Recent commits changed listener callback responsibilities and added external
JWT signing. Freeze wire fixtures and concurrency/ack contracts before
restructuring; compare future upstream commits against the pinned baseline.

## 6) `[ASK USER]` Questions

All product decisions and remaining questions are centralized in
[DECISIONS.md](../migration/DECISIONS.md). An unresolved item there blocks
finalizing the affected part of the migration plan.

## 7) Evidence

- [Saved scan/history](../migration/upstream-scan.txt)
- [Example state and shutdown](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/examples/dockerscaleset/scaler.go)
- [Example startup](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/examples/dockerscaleset/main.go)
- [Listener contracts](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/listener/listener.go)
- [Validation](../migration/VALIDATION.md)
