# Knightfall.ScaleSet.Listener

Use `ScaleSetListener` in a .NET 10 worker to process the GitHub message queue
for one runner scale set. Open a session with `ScaleSetClient`, pass it to the
listener along with the scale-set ID and available runner capacity, and call
`RunAsync` with an `IScaleSetMessageHandler`.

```sh
dotnet add package Knightfall.ScaleSet.Listener --version 0.1.0-alpha.1
```

The listener delivers the session's initial statistics, then processes each
poll in order. It acknowledges a message after the handler succeeds. A handler
must tolerate redelivery if processing or acknowledgment fails. Use
`SetMaxRunners` to update the capacity the next poll advertises.

The handler decides whether to acquire offered jobs through the session.
Hosting provides `ScaleSetMessageHandler` for that purpose. The application
owns session creation, recovery after a listener failure, and closure when
polling stops. See the [worker example](https://github.com/knightfall/scaleset.NET/tree/main/examples/dotnet-worker).

## Checkpointed execution

Use `ScaleSetListenerPump.StepAsync` when your application owns durable
delivery state, execution authorization, and when to resume processing.
Supply an `IScaleSetListenerCheckpointStore`, an
`IScaleSetListenerDeliveryHandler`, and an
`IScaleSetListenerExecutionPolicy`. Bind the pump to a caller-owned session
and one fixed ownership-loss token. Each step resolves pending work before
polling. A successful real delivery costs four logical checkpoint writes.

The checkpoint store atomically checks revision and ownership on every write.
The handler recalculates admission after polling and journals replay-safe job
effects before returning `Completed`. Deferral keeps the checkpoint available
for a later step. A lost acknowledgement response leaves the delivery in
`Acknowledging` and requires a supervisor decision before that session can
poll again. Session replacement starts with fresh initial statistics and an
absent cursor. The concrete Client requires an already-open client for resume
on the same remote session.

Run the [checkpointed package example](https://github.com/knightfall/scaleset.NET/tree/28eb8a95e4e4d3f84f8876c3436c95a7714dc088/examples/checkpointed-listener).
The [usage guide](https://github.com/knightfall/scaleset.NET/blob/main/docs/usage.md)
covers composition, callback ownership, shutdown, and recovery.

## Tracing

Subscribe to `ScaleSetListener.ActivitySourceName` (`ScaleSet.Listener`).
The legacy loop emits `scaleset.message.poll` and `scaleset.message.process`.
The pump emits `scaleset.listener.step` and `scaleset.listener.operation`
with bounded operation and outcome tags. The autoscaler records queue
operation metrics in its host meter.

Licensed under [Apache-2.0](https://github.com/knightfall/scaleset.NET/blob/main/LICENSE).
