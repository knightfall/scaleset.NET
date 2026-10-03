# GitHub Client and Message-Session Libraries

The .NET client and listener implement the GitHub Runner Scale Set API and
message-session contracts used by the controller. The reviewed Go reference is
pinned at `e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5`.

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

Disposing the client releases its internally created HTTP transport. An injected
client remains caller-owned. Its proxy, trust roots, client certificates,
connection lifetime, redirect policy and overall `HttpClient.Timeout` remain
the caller's responsibility. The default transport uses normal platform TLS
validation and disables automatic redirects. No permissive TLS option is added.
Close the remote message session before disposing its parent client.

## Behavior

- GitHub.com organization/repository targets and App/PAT authentication are
  supported. App JWTs use RS256, `iat = now - 1 minute`, and `exp = iat + 9 minutes`.
- Scoped registration precedes `RemoteAuth` administration discovery. A coherent
  URL/token snapshot is refreshed below 60 seconds of remaining lifetime;
  equality at 60 seconds remains cached, matching Go. Concurrent
  refresh is coordinated, and a failed refresh can be attempted later.
- Credentials are attached to each request. Administration, GitHub REST and
  queue credentials remain separate. Job acquisition uses the administration
  origin with the queue bearer token, as in Go.
- Administration operations cover the supported protocol inventory:
  scale sets, runner groups, registered runners, JIT configuration and sessions.
  Lookups preserve their null/cardinality contracts. List methods expose empty
  C# collections for Go nil slices. Create defaults absent labels to the name;
  update only defaults supplied label types. Caller-owned records remain intact.
- Polling preserves queue query parameters, sends capacity, adds positive
  cursors, returns null for HTTP 202 and parses the encoded body for HTTP 200.
  Queue 401 coordinates one refresh and one retry. Acknowledgment still occurs
  only after successful handling, with the listener's independent timeout.
- Requests have separate control and long-poll budgets. Transient read retries
  are bounded. General mutation replay and poll hedging are absent; registration
  discovery has its scoped authorization retry policy. This follows the
  product's safer replay policy.
- The library emits `ScaleSet.Client` logical attempt activities and metrics and
  accepts Microsoft logging abstractions. Logical activities use Internal kind;
  application HTTP instrumentation supplies the network Client spans.
  Application composition owns SDK/exporter configuration.
  Diagnostics redact credentials and omit queue URL credentials; HTTP failures
  retain independent status/domain classifications and correlation metadata.

## Client verification

Independent local validation on macOS arm64:

```text
dotnet test --solution ScaleSet.slnx --configuration Release --no-restore
Passed: 220; failed: 0; skipped: 0; exit: 0

dotnet pack ScaleSet.slnx --configuration Release --no-build --output artifacts/packages
Packed ScaleSet.Client, ScaleSet.Listener and ScaleSet.Hosting; exit: 0
```

The solution includes 141 client, 20 listener and 59 hosting test cases. Compiler,
Roslynator, .NET and xUnit analyzer diagnostics remain build errors; no new
diagnostic suppressions were introduced. Tests use fake HTTP handlers and
injected clocks, with no live GitHub credentials or remote API effects. The
existing hosting tests also verify actual OTLP export to a loopback receiver.
These results do not establish a real runner workflow, Docker lifecycle, or
platform trust-store behavior. Packages were built locally and were not published.

The table maps requirements to exact tests. Paths are beneath `tests/`.

| Requirement | Evidence |
|---|---|
| RS256, both PEM formats, issuer and backdated lifetime | `GitHubAppSigningTests.FromGitHubAppPemFormatsProduceVerifiableRs256JwtWithExpectedClaims` verifies the emitted signature with an independent public key |
| Async signing seam, cancellation and local credential validation | `FromGitHubAppUsesAsyncSignerArgumentsAndReturnedTokenInInstallationRequest`, `FromGitHubAppCanceledAsyncSigningStopsBeforeAnyHttpRequest`, `FromPersonalAccessTokenValidatesInputAndRedactsDiagnosticString`, `FromGitHubAppRejectsPublicOnlyPemAndNullCustomSigner` |
| Coherent admin discovery/cache and exact refresh boundary | `AdminTokenRefreshTests.AdminSnapshotRefreshesAtSixtySecondBoundaryAndPublishesUrlWithToken`, `ConcurrentAdminRefreshCoalescesAndSharesOneSnapshot`, `FailedAdminRefreshCanBeRetriedOnTheNextCall`, `NearExpiryFreshTokenServesCurrentCallAndRefreshesOnNextCall` |
| Encoded URLs, base path/query and API version | `ScaleSetClientTransportTests.ActionsRequestMergesBasePathExistingQueriesAndApiVersion`, `RootRelativeActionsRouteIsJoinedToTheDiscoveredServicePath` |
| Bounded retries and safe mutation/poll policies | `ReadOnlyRetriesExhaustAtConfiguredLimitPlusOneAttempts`, `MutationIsNotRetriedAfterTransientFailure`, `DiscoveryAuthenticationRetriesExhaustAtConfiguredLimitPlusOneAttempts`, `DiscoveryDoesNotReplayServerFailure`, `QueueTransientFailureIsNotRetried` |
| Scale-set find/list/get and count/null contracts | `ScaleSetClientAdministrationTests.FindScaleSetAsyncRejectsMultipleOrInconsistentCountedValues`, `ListScaleSetsAsyncUsesOnlyTheRunnerGroupQueryAndNormalizesNullValueToEmpty`, `GetScaleSetAsyncPreservesJsonNullResult` |
| Scale-set create/update/delete, defaults and caller ownership | `CreateScaleSetAsyncAddsNameLabelAndSystemTypeWithoutMutatingInput`, `UpdateScaleSetAsyncDefaultsOnlySuppliedLabelTypesAndDoesNotMutateInput`, `UpdateScaleSetAsyncDoesNotAddCreateOnlyNameLabel`, `DeleteScaleSetAsyncRequiresExactNoContentStatus` |
| Runner groups, JIT and registered-runner operations | `GetRunnerGroupByNameAsyncRejectsZeroAndMultipleMatches`, `GenerateJitConfigAsyncDeserializesRunnerAndEncodedConfiguration`, `GetRunnerAsyncDeserializesRunnerReference`, `FindRunnerByNameAsyncReturnsOneRunnerAndRejectsMultipleMatches`, `RemoveRunnerAsyncRequiresExactNoContentAndPreservesDomainClassification` |
| 64-bit resource paths and job/message IDs | `AdministrationIdsRemain64BitInAllResourcePaths`, `MessageSessionClientTests.DeleteMessageAsync_AppendsLargeIdPreservesQueryAndUsesQueueToken`, `AcquireJobsAsync_UsesQueueBearerAndPreservesInt64Identifiers` |
| Session creation, queue capacity/cursor/202 and acquired-job body | `CreateMessageSessionAsync_PostsOwnerAndPublishesValidatedSnapshot`, `GetMessageAsync_PreservesQueueQueryAndSendsCursorCapacityAndAccept`, `GetMessageAsync_MapsAcceptedToEmptyAndDoesNotAddZeroCursor`, `AcquireJobsAsync_EncodesAnEmptyRequestList` |
| Queue refresh once, snapshot generation and later failure recovery | `QueueUnauthorizedRefreshesAndRetriesExactlyOnce`, `QueueUnauthorizedAfterOneRefreshIsReturnedWithoutAnotherRetry`, `ConcurrentUnauthorizedPollsCoalesceWhenRefreshKeepsSameIdAndToken`, `FailedRefreshDoesNotPreventALaterOperationFromRefreshing` |
| Independent acknowledgment survives a canceled refresh owner | `CanceledRefreshOwnerDoesNotCancelIndependentAcknowledgment` verifies two refresh attempts and a successful acknowledgment using its own live token |
| Close lifecycle, retryable cleanup and refresh ordering | `CloseAsyncUsesAdminCredentialIsIdempotentAndRejectsLaterOperations`, `CloseFailureCanBeRetried`, `CancelDuringRefreshThenCloseWaitsUntilRefreshCompletes`, `CancellationTokenReachesEverySessionRequest` |
| Status/domain errors, bounded redaction and correlation | `ResponsePreservesHttpStatusAndRecognizedDomainError`, `ErrorIncludesCorrelationAndSafeUriWithBoundedSecretFreePreview`, `MalformedTruncatedQuotedTokenFieldIsRedacted`, `ErrorPreviewRedactsUnknownQueueUriUserInfoAndSignedQuery`, `MalformedSuccessResponseRetainsCorrelationAndRedactsBodyPreview`, `UnsafeRegistrationTokenIsRejectedWithSafeResponseMetadata` |
| Timeouts, transport ownership and disposal | `ControlRequestTimeoutUsesConfiguredTimerBudget`, `OwnedClientHandlerDisablesRedirects`, `CallerHttpClientRemainsUsableAfterClientDisposal`, `RequestAndResponseContentAreDisposedAfterSuccess`, `ResponseReadFailuresAreWrappedAndResponseStreamIsDisposed` |
| Correlated logical activities, error status and bounded metric tags/seconds | `ClientTelemetryTests.ClientEmitsCorrelatedActivitiesAndBoundedMetricsWithSecondsDuration` |
| Concrete listener callback-before-ack and safe cancellation/replay | `ListenerTransportIntegrationTests.SuccessfulAcquisitionThenHostCancellationStillAcknowledgesBeforeExplicitClose` (with/without a queue-token refresh), `HandlerFailureLeavesConcreteSessionMessageUnacknowledged` |

Tests reside in [client tests](../../tests/ScaleSet.Client.Tests),
[listener integration](../../tests/ScaleSet.Listener.Tests/ListenerTransportIntegrationTests.cs)
and the existing [hosting tests](../../tests/ScaleSet.Hosting.Tests).
Static source pairing informed test placement; it is a parse-only heuristic,
not line or branch coverage. The review corrected actual URI/202/retry,
cancellation-sharing and credential-redaction defects and strengthened boundary
and bookkeeping assertions. No mutation-testing or coverage-percentage result
is claimed.

The client and listener libraries are now composed into the Docker controller.
AWS runner providers, AWS controller hosting, and live GitHub canaries remain
planned work; see [Implementation Status](WORK-STATE.md).
