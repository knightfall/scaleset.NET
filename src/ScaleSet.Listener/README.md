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

Licensed under [Apache-2.0](https://github.com/knightfall/scaleset.NET/blob/main/LICENSE).
