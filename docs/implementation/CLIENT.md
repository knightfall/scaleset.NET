# GitHub Client and Message-Session Libraries

The .NET client and listener implement the GitHub Runner Scale Set API and
message-session contracts used by the controller. The reviewed Go reference is
pinned at `716908e507993947ada4273aecf2361c02e5836c`.

## Public use and ownership

`ScaleSetClient` accepts a validated `GitHubTarget`, exactly one
`ScaleSetCredentials` descriptor, optional timeout/retry/metadata options, and
an optional caller-owned `HttpClient`, `ILogger<ScaleSetClient>` and
`TimeProvider`. Construction validates local settings without contacting GitHub.
PAT and App factories make mixed authentication modes unrepresentable. App
credentials support PKCS#1/PKCS#8 RSA PEM and a replaceable async JWT signer.

```csharp
using var client = new ScaleSetClient(
    GitHubTarget.Parse("https://github.com/example"),
    ScaleSetCredentials.FromPersonalAccessToken(token));

RunnerGroup group = await client.GetRunnerGroupByNameAsync("Default", cancellationToken);
RunnerScaleSet? scaleSet = await client.FindScaleSetAsync(group.Id, "linux", cancellationToken);
// The application resolves or creates the scale set before opening a session.
MessageSessionClient session = await client.CreateMessageSessionAsync(
    scaleSetId, "controller-identity", cancellationToken);
// Pass session to ScaleSetListener. The handler owns acquisition/provisioning.
// After stopping the listener, close with a separate bounded cleanup token.
await session.CloseAsync(cleanupToken);
```

Disposing the client releases its internally created HTTP transport (`SocketsHttpHandler`).
An injected client remains caller-owned. Its proxy, trust roots, client
certificates, connection lifetime, redirect policy and overall
`HttpClient.Timeout` remain the caller's responsibility. The default transport
uses normal platform TLS validation, enables HTTP/2 keep-alive pings
(`KeepAlivePingDelay = 30s`, `KeepAlivePingTimeout = 10s`,
`HttpKeepAlivePingPolicy.WithActiveRequests`), disables automatic redirects
(`AllowAutoRedirect = false`), and configures `DefaultRequestVersion = HttpVersion.Version20`
with `DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower` on the
owned `HttpClient` (propagated onto outgoing `HttpRequestMessage` instances so
HTTP/2 keep-alive pings activate, while allowing HTTP/1.1 fallback). Custom root
CA / mTLS transport factories and permissive TLS options remain out of scope for
now. Close the remote message session before disposing its parent client.

## Behavior

- GitHub.com (`github.com`/`www.github.com`) and GitHub Enterprise Cloud
  (`github.com/enterprises/{enterprise}` and `*.ghe.com`) repository,
  organization, and enterprise targets are supported. Repository and
  organization scopes accept App or PAT credentials. Enterprise scope uses a
  classic PAT with `manage_runners:enterprise`. Custom GHES hosts remain
  deferred and throw `NotSupportedException`.
  App JWTs use RS256, `iat = now - 1 minute`, and `exp = iat + 9 minutes`.
- Scoped registration precedes `RemoteAuth` administration discovery. A coherent
  URL/token snapshot is refreshed below 60 seconds of remaining lifetime;
  equality at 60 seconds remains cached, matching Go. Concurrent
  refresh is coordinated, and a failed refresh can be attempted later.
- Credentials are attached to each request. Administration, GitHub REST and
  queue credentials remain separate. Job acquisition uses the administration
  origin with the queue bearer token, as in Go.
- Administration operations cover the supported protocol inventory:
  scale sets, runner groups, registered runners, JIT configuration and sessions.
  Lookups preserve their null/cardinality contracts (`GetScaleSetAsync` and
  `GetRunnerAsync` preserve JSON `null`), while required admin endpoints and
  `GenerateJitConfigAsync` strictly reject `null`/invalid/trailing-garbage JSON,
  surfacing a `ScaleSetApiException` (with `StatusCode == 200` and redacted
  `BodyPreview` / `Response` snapshot). List methods expose empty C# collections for Go
  nil slices. Create defaults absent labels to the name; update only defaults
  supplied label types. Caller-owned records remain intact.
- Polling preserves queue query parameters, sends capacity, adds positive
  cursors, returns null for HTTP 202 and parses the encoded body for HTTP 200.
  Queue 401 coordinates one refresh and one retry. Acknowledgment still occurs
  only after successful handling, with the listener's independent timeout.
- Requests have separate control and long-poll budgets. Transient read retries
  are bounded (`ScaleSetClientOptions` defaults, or customizable via
  `ScaleSetRetryOptions`, including `CreateUpstreamDefault()`, `Disabled`,
  `MaxElapsed`, `Deadline`, `OnRateLimit`/`ScaleSetRateLimitPolicies`, backoff
  hooks, and `ScaleSetRequestRetry` per-call scopes). Primary and secondary rate
  limits surface as `ScaleSetRateLimitException` (`Retry-After` and reset
  metadata). Expired `MaxElapsed` budgets raise
  `ScaleSetMaxElapsedExceededException` while expired `Deadline` budgets raise
  `OperationCanceledException` (both interrupt async `ShouldRetry` /
  `OnRateLimit` hooks and chunk timers beyond 49.7 days), and under explicit
  `ScaleSetRetryOptions`, registration-discovery auth retries share the
  configured `MaxRetries` allowance. `ScaleSetApiException.Response`
  carries a bounded inspectable `ScaleSetResponseSnapshot` (up to 65,536 body
  bytes, headers, trailing headers, and truncation/incompleteness flags).
  Long-poll `queue` receives do not retry transient failures by default;
  queue-token 401 refresh and registration-discovery auth retries remain
  operation-specific.
- The library emits `ScaleSet.Client` logical attempt activities and metrics and
  accepts Microsoft logging abstractions. Logical activities use Internal kind;
  application HTTP instrumentation supplies the network Client spans.
  Application composition owns SDK/exporter configuration.
  Diagnostics redact credentials and omit queue URL credentials; HTTP failures
  retain independent status/domain classifications and correlation metadata.

## Client verification

Independent local validation on macOS arm64:

```text
dotnet build ScaleSet.slnx --configuration Release --no-restore -m:1 -nr:false
Build succeeded. 0 Warning(s), 0 Error(s); exit: 0

dotnet test --solution ScaleSet.slnx --configuration Release --no-build
total: 424, failed: 0, succeeded: 422, skipped: 2; exit: 0
```

The solution test suites include 194 client, 20 listener, 161 hosting, and 47
Docker unit tests (plus 2 opt-in `ScaleSet.Docker.IntegrationTests` skipped on
macOS without a Linux Docker daemon / `SCALESET_DOCKER_INTEGRATION=1`).
Compiler, Roslynator, .NET and xUnit analyzer diagnostics remain build errors
(with narrow `RCS1194` suppressions on `ScaleSetRateLimitException` and
`ScaleSetMaxElapsedExceededException` over unsealed `ScaleSetApiException`).
Tests use fake HTTP handlers and injected clocks/time providers, with no live
GitHub credentials or remote API effects. The existing hosting tests also
verify OTLP export to a loopback receiver. The Linux release candidate ran
all 424 tests and exercised `Knightfall.ScaleSet.Client` and
`Knightfall.ScaleSet.Listener` in a fresh consumer. See the
[verification record](../product/VERIFICATION.md).

The table maps requirements to exact tests. Paths are beneath `tests/`.

| Requirement | Evidence |
|---|---|
| Repository, org, GHEC enterprise/`*.ghe.com` parsing and deferred GHES rejection | `GitHubTargetTests.ParseRepositoryTargetResolvesGitHubApiAndRegistrationPath`, `ParseEnterpriseTargetOnGitHubComResolvesEnterpriseScopeAndPath`, `ParseHostedGheOrganizationRepositoryAndEnterpriseTargetsResolveTenantApiBase`, `ParseRejectsDeferredGhesTargetsClearly`, `AdminTokenRefreshTests.PatAuthenticationSupportsEnterpriseAndHostedGheTargets` |
| RS256, both PEM formats, issuer and backdated lifetime | `GitHubAppSigningTests.FromGitHubAppPemFormatsProduceVerifiableRs256JwtWithExpectedClaims` verifies the emitted signature with an independent public key |
| Async signing seam, cancellation and local credential validation | `FromGitHubAppUsesAsyncSignerArgumentsAndReturnedTokenInInstallationRequest`, `FromGitHubAppCanceledAsyncSigningStopsBeforeAnyHttpRequest`, `FromPersonalAccessTokenValidatesInputAndRedactsDiagnosticString`, `FromGitHubAppRejectsPublicOnlyPemAndNullCustomSigner` |
| Coherent admin discovery/cache and exact refresh boundary | `AdminTokenRefreshTests.AdminSnapshotRefreshesAtSixtySecondBoundaryAndPublishesUrlWithToken`, `ConcurrentAdminRefreshCoalescesAndSharesOneSnapshot`, `FailedAdminRefreshCanBeRetriedOnTheNextCall`, `NearExpiryFreshTokenServesCurrentCallAndRefreshesOnNextCall` |
| Encoded URLs, base path/query and API version | `ScaleSetClientTransportTests.ActionsRequestMergesBasePathExistingQueriesAndApiVersion`, `RootRelativeActionsRouteIsJoinedToTheDiscoveredServicePath` |
| Bounded retries, rate limits, `Retry-After`, budgets and per-call scopes | `ReadOnlyRetriesExhaustAtConfiguredLimitPlusOneAttempts`, `MutationIsNotRetriedAfterTransientFailure`, `DiscoveryAuthenticationRetriesExhaustAtConfiguredLimitPlusOneAttempts`, `QueueTransientFailureIsNotRetried`, `RateLimitTests.*` (`Retry-After`, reset, 403/429 classification, `OnRateLimit`), `RetryPolicyTests.*` (`MaxElapsed`, `Deadline`, `ScaleSetRequestRetry`, 501/cert non-retry, upstream-default policy) |
| Scale-set find/list/get and strict JSON/null contracts | `ScaleSetClientAdministrationTests.FindScaleSetAsyncRejectsMultipleOrInconsistentCountedValues`, `ListScaleSetsAsyncUsesOnlyTheRunnerGroupQueryAndNormalizesNullValueToEmpty`, `GetScaleSetAsyncPreservesJsonNullResult`, `RequiredAdministrationEndpointsRejectJsonNullBodies`, `AdministrationDecodeRejectsTrailingJsonGarbage` |
| Scale-set create/update/delete, defaults and caller ownership | `CreateScaleSetAsyncAddsNameLabelAndSystemTypeWithoutMutatingInput`, `UpdateScaleSetAsyncDefaultsOnlySuppliedLabelTypesAndDoesNotMutateInput`, `UpdateScaleSetAsyncDoesNotAddCreateOnlyNameLabel`, `DeleteScaleSetAsyncRequiresExactNoContentStatus` |
| Runner groups, JIT and registered-runner operations | `GetRunnerGroupByNameAsyncRejectsZeroAndMultipleMatches`, `GenerateJitConfigAsyncSendsNameAndWorkFolderAndRejectsNullResponse`, `GenerateJitConfigAsyncDeserializesRunnerAndEncodedConfiguration`, `GetRunnerAsyncDeserializesRunnerReference`, `FindRunnerByNameAsyncReturnsOneRunnerAndRejectsMultipleMatches`, `RemoveRunnerAsyncRequiresExactNoContentAndPreservesDomainClassification` |
| 64-bit resource paths and job/message IDs | `AdministrationIdsRemain64BitInAllResourcePaths`, `MessageSessionClientTests.DeleteMessageAsync_AppendsLargeIdPreservesQueryAndUsesQueueToken`, `AcquireJobsAsync_UsesQueueBearerAndPreservesInt64Identifiers` |
| Session creation, strict null checks, queue capacity/cursor/202 and jobs | `CreateMessageSessionAsync_PostsOwnerAndPublishesValidatedSnapshot`, `CreateMessageSessionAsync_RejectsJsonNull`, `RefreshSession_RejectsJsonNullWithoutReplacingCurrentSession`, `GetMessageAsync_PreservesQueueQueryAndSendsCursorCapacityAndAccept`, `GetMessageAsync_MapsAcceptedToEmptyAndDoesNotAddZeroCursor`, `AcquireJobsAsync_EncodesAnEmptyRequestList` |
| Queue refresh once, snapshot generation and later failure recovery | `QueueUnauthorizedRefreshesAndRetriesExactlyOnce`, `QueueUnauthorizedAfterOneRefreshIsReturnedWithoutAnotherRetry`, `ConcurrentUnauthorizedPollsCoalesceWhenRefreshKeepsSameIdAndToken`, `FailedRefreshDoesNotPreventALaterOperationFromRefreshing` |
| Independent acknowledgment survives a canceled refresh owner | `CanceledRefreshOwnerDoesNotCancelIndependentAcknowledgment` verifies two refresh attempts and a successful acknowledgment using its own live token |
| Close lifecycle, retryable cleanup and refresh ordering | `CloseAsyncUsesAdminCredentialIsIdempotentAndRejectsLaterOperations`, `CloseFailureCanBeRetried`, `CancelDuringRefreshThenCloseWaitsUntilRefreshCompletes`, `CancellationTokenReachesEverySessionRequest` |
| Status/domain errors, inspectable response snapshots, redaction and correlation | `ResponsePreservesHttpStatusAndRecognizedDomainError`, `ErrorIncludesCorrelationAndSafeUriWithBoundedSecretFreePreview`, `MalformedTruncatedQuotedTokenFieldIsRedacted`, `ErrorPreviewRedactsUnknownQueueUriUserInfoAndSignedQuery`, `ResponseSnapshotTests.*` (headers/bounded bodies/BOM/partial-read snapshots), `ScaleSetApiExceptionTests.RateLimitAndMaxElapsedExceptionsSupportStandardConstructorsAndValidateInputs`, `UnsafeRegistrationTokenIsRejectedWithSafeResponseMetadata` |
| Timeouts, `SocketsHttpHandler`/redirect policy, HTTP/2 negotiation, transport ownership and disposal | `ControlRequestTimeoutUsesConfiguredTimerBudget`, `OwnedClientHandlerDisablesRedirects`, `SendRequestAppliesHttpClientDefaultVersionAndPolicy`, `CallerHttpClientRemainsUsableAfterClientDisposal`, `RequestAndResponseContentAreDisposedAfterSuccess`, `ResponseReadFailuresAreWrappedAndResponseStreamIsDisposed` |
| Correlated logical activities, error status and bounded metric tags/seconds | `ClientTelemetryTests.ClientEmitsCorrelatedActivitiesAndBoundedMetricsWithSecondsDuration`, `ClientTelemetryTests.ClientEmitsRateLimitRetryMetricWithBoundedKind` |
| Concrete listener callback-before-ack and safe cancellation/replay | `ListenerTransportIntegrationTests.SuccessfulAcquisitionThenHostCancellationStillAcknowledgesBeforeExplicitClose` (with/without a queue-token refresh), `HandlerFailureLeavesConcreteSessionMessageUnacknowledged` |

Tests reside in [client tests](../../tests/ScaleSet.Client.Tests),
[listener integration](../../tests/ScaleSet.Listener.Tests/ListenerTransportIntegrationTests.cs)
and the existing [hosting tests](../../tests/ScaleSet.Hosting.Tests).
Static source pairing informed test placement; it is a parse-only heuristic,
not line or branch coverage. The review corrected actual URI/202/retry,
cancellation-sharing and credential-redaction defects and strengthened boundary
and bookkeeping assertions. No mutation-testing or coverage-percentage result
is claimed.

The client and listener libraries are composed into the Docker controller and
the standalone .NET worker. Both completed a GitHub.com repository canary.
AWS runner providers and controller hosting remain planned. See the
[implementation status](WORK-STATE.md).
