using ScaleSet.CheckpointedListener.Example;
using ScaleSet.Listener;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var stoppingToken = deadline.Token;
using var ownership = new DemoOwnership();
var session = new DemoSession();
var key = new ScaleSetListenerSessionKey("github.com/example/demo", 42, session.Session.SessionId);
var store = new DemoCheckpointStore(ownership);
var policy = new DemoPolicy(ownership) { Capacity = 2 };
var handler = new DemoDeliveryHandler(session, store, ownership, policy);
ScaleSetListenerPump OpenPump() => new(session, new(key, ownership.Token), store, handler, policy);

var pump = OpenPump();
Require((await pump.StepAsync(stoppingToken)).Status == ScaleSetListenerStepStatus.Initialized && session.Polls == 0,
    "Initialization commits before polling.");

policy.CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(5);
Require((await pump.StepAsync(stoppingToken)).Status == ScaleSetListenerStepStatus.Deferred && session.Polls == 0,
    "The caller's cooldown deferred polling.");
policy.CooldownUntil = null;

const long messageId = 5_000_000_001;
session.Offer(messageId, assigned: 1, requestId: 99);
var poll = session.HoldNextPoll();
var polling = pump.StepAsync(stoppingToken);
await poll.Entered.Task.WaitAsync(stoppingToken);
policy.Capacity = 0;
poll.Release.SetResult();
Require((await polling).Status == ScaleSetListenerStepStatus.Deferred &&
    store.Latest(key)?.Pending?.Phase == ScaleSetListenerPendingPhase.Received &&
    session.AcknowledgeAttempts == 0 && session.Acquisitions == 0 && session.AdvertisedCapacity == 2,
    "Capacity changed after polling began. The receipt survived and no job was acquired or acknowledged.");

var observedAt = store.Latest(key)!.Pending!.Delivery.ObservedAt;
policy.Capacity = 1;
policy.PauseAcknowledgement = true;
pump = OpenPump();
Require((await pump.StepAsync(stoppingToken)).Status == ScaleSetListenerStepStatus.Deferred &&
    store.Latest(key)?.Pending?.Phase == ScaleSetListenerPendingPhase.Processed &&
    handler.LastObservation == observedAt && handler.LastWasReplay && session.Acquisitions == 1,
    "A new pump for the existing session replayed the receipt with its original time and recorded completed handling.");

policy.PauseAcknowledgement = false;
pump = OpenPump();
Require((await pump.StepAsync(stoppingToken)).Status == ScaleSetListenerStepStatus.MessageAcknowledged &&
    session.Polls == 1 && session.AcknowledgeAttempts == 1 && handler.RealDeliveries == 2,
    "Restored processed work acknowledged without polling or handling again.");

session.Offer(messageId, assigned: 5, requestId: 99);
Require((await pump.StepAsync(stoppingToken)).Status == ScaleSetListenerStepStatus.MessageAcknowledged &&
    session.Acquisitions == 1 && handler.Assigned == 5,
    "A duplicate supplied new statistics without repeating the ledgered job effect.");

session.Offer(messageId + 1, assigned: 6, requestId: 99);
session.LoseAcknowledgement = true;
try
{
    _ = await pump.StepAsync(stoppingToken);
    throw new InvalidOperationException("The scripted acknowledgement should have failed.");
}
catch (IOException)
{
    Require(store.Latest(key)?.Pending?.Phase == ScaleSetListenerPendingPhase.Acknowledging,
        "The lost response retained acknowledgement uncertainty.");
}

int pollsBeforeRecovery = session.Polls;
int deletesBeforeRecovery = session.AcknowledgeAttempts;
Require((await OpenPump().StepAsync(stoppingToken)).Status == ScaleSetListenerStepStatus.RecoveryRequired &&
    session.Polls == pollsBeforeRecovery && session.AcknowledgeAttempts == deletesBeforeRecovery &&
    store.Latest(key)?.LastAcknowledgedMessageId == messageId,
    "Recovery yielded without polling, retrying DELETE, or advancing the confirmed cursor.");

Require(!session.Closed, "The pump left session lifetime with the caller.");
await session.CloseAsync(stoppingToken);
using var replacementOwnership = new DemoOwnership();
var replacement = new DemoSession();
var replacementKey = key with { SessionId = replacement.Session.SessionId };
var replacementStore = new DemoCheckpointStore(replacementOwnership);
var replacementPolicy = new DemoPolicy(replacementOwnership) { Capacity = 1 };
var replacementHandler = new DemoDeliveryHandler(replacement, replacementStore, replacementOwnership, replacementPolicy);
var replacementPump = new ScaleSetListenerPump(replacement, new(replacementKey, replacementOwnership.Token),
    replacementStore, replacementHandler, replacementPolicy);
Require((await replacementPump.StepAsync(stoppingToken)).Status == ScaleSetListenerStepStatus.Initialized &&
    replacementStore.Latest(replacementKey)?.LastAcknowledgedMessageId is null &&
    store.Latest(key)?.Pending?.Phase == ScaleSetListenerPendingPhase.Acknowledging,
    "The caller opened a separate session and retained the old unresolved checkpoint.");

replacement.Offer(0, assigned: 1, requestId: 100);
var replacementPoll = replacement.HoldNextPoll();
var inFlight = replacementPump.StepAsync(stoppingToken);
await replacementPoll.Entered.Task.WaitAsync(stoppingToken);
replacementOwnership.Revoke();
try
{
    _ = await inFlight;
    throw new InvalidOperationException("The scripted poll should have been canceled by ownership loss.");
}
catch (OperationCanceledException) when (replacementOwnership.Token.IsCancellationRequested)
{
    Require(replacement.AcknowledgeAttempts == 0 && replacement.Acquisitions == 0 &&
        replacement.LastCursor == 0 && replacementStore.Latest(replacementKey)?.Pending is null,
        "Revocation canceled polling before receipt, acquisition, or acknowledgement.");
}

await replacement.CloseAsync(stoppingToken);
Console.WriteLine("Verified checkpointed listener package scenarios.");

static void Require(bool condition, string scenario)
{
    if (!condition)
    {
        throw new InvalidOperationException(scenario);
    }

    Console.WriteLine(scenario);
}
