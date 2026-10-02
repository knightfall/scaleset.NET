# Phase-one foundation

The foundation was merged in PR #2. Subsequent GitHub transport/authentication
and session work is recorded in [CLIENT.md](CLIENT.md); the evidence below
records the original foundation milestone.

Started after planning PR #1 merged into main at
`30017791ad8a898ca06d5d7fd27a4405dc8a01f0`. Implementation branch:
`codex/phase-one-foundation`.

The first work packages use GPT-6 Luna with max reasoning. Parent owns shared
build/solution/CI, integration and verification; agents own client contracts,
hosting configuration/scaling and telemetry. No AWS infrastructure work is
performed.

## Implemented scope

- Eight net10.0 project manifests and one solution, centralized dependencies,
  nullable references, deterministic builds and analyzer warnings as errors.
- Exact wire DTOs, encoded message parsing, GitHub.com repo/org target parsing,
  status/domain exceptions and session-consumer interface.
- Serial listener: initial statistics, empty polls, cursor/capacity tracking,
  success-before-ack, cancellation-independent bounded acknowledgment.
- Environment-bound configuration, safe validation, distinct scale-set ownership
  identities and both overflow-safe scaling formulas.
- ILogger/Serilog OTLP logs and separate SDK trace/metric providers, shared
  resources, redaction and validate-config/telemetry-smoke commands.
- xUnit v3/native MTP wiring, local OTLP receiver tests and cross-platform CI.

## Scope still pending

HTTP authentication/administration/session transport (M2), provider discovery/
state/reconciliation and Docker runtime (M4), full host/health/drain/deployment
packaging/live canaries (M5). The Docker provider project is a reserved scaffold,
not an implemented provider. Library protocol tests do not establish live
GitHub/Docker compatibility. Phase-two AWS design remains deferred.

## Validation evidence

Parent verification on macOS arm64 with .NET SDK 10.0.400:

- `dotnet test --solution ScaleSet.slnx --configuration Release --no-restore`:
  Passed: 108, Failed: 0, Skipped: 0 (Client 32, Listener 17, Hosting 59).
- `dotnet pack ScaleSet.slnx --configuration Release --no-build --output artifacts/packages`:
  three alpha library packages created; readme/notices included; Roslynator
  excluded from consumer dependencies. The empty Docker scaffold is not packable.
- `validate-config` executed with synthetic environment settings: exit 0,
  Configuration is valid. No GitHub/Docker calls were made.
- Roslynator rejected a temporary bad example with RCS1104; the corrected
  example built with zero warnings/errors. Probe source was removed.
- Runtime tests and builds pass compiler/analyzer warnings-as-errors. Named-pipe
  sandbox restrictions required local verification outside the sandbox; no
  analyzer or test gate was disabled to bypass those restrictions.

| Requirement | Concrete evidence |
|---|---|
| Wire names/omission/encoded messages | RunnerScaleSetJsonUsesExactNamesAndOmissionRules; ParseDeserializesEncodedKnownMessages |
| Hosted targets and safe diagnostics | ParseRepositoryTargetResolvesGitHubApiAndRegistrationPath; DiagnosticToStringRedactsQueueTokenAndEncodedJitConfig |
| Serial listener and ack safety | RunAsyncSuccessfulHandlerCancelsShutdownAcknowledgmentOutlivesCancellation; RunAsyncAcknowledgmentTimeoutCancelsIndependentBudget; ConcurrentRunsAreRejectedWithoutInterruptingTheActiveRun |
| Environment/options and shared-set identity | Bind_UsesClrPropertyNamesAndBindsNestedListsAndCredentialsCaseInsensitively; Validate_RejectsDuplicateTargetGroupAndNameCaseInsensitively; Validate_AllowsMultipleScaleSetNamesForOneOrganization |
| Scaling boundaries and overflow | SpareCapacity_MaximumAssignedJobsAndMinimum_DoesNotOverflow; AnyPolicy_MaximumZeroPausesProvisioning |
| Actual OTLP signals/correlation/redaction | SerilogAndSdkExportCorrelatedLogsCompletedSpansAndMetricsOnce |
| Numeric metrics and bounded dimensions | OperationsRecordCountDurationSecondsAndBoundedDimensions; RunnerGaugeNormalizesUnknownEnumsAndRemovesZeroCounts |
| Quality and package boundaries | Global Roslynator reference, RCS1104 probe, three package nuspec inspections |

The three implementation work packages ran with GPT-6 Luna/max. The telemetry
agent reached its usage limit during verification; parent completed integration
and validation. Further delegated milestones need agent capacity to resume.
No live GitHub/Docker compatibility or Linux/Windows runtime result is claimed
from this local test run; CI is configured for those operating systems.
