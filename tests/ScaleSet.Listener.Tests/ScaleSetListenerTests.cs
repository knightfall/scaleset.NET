using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace ScaleSet.Listener.Tests;

public sealed class ScaleSetListenerTests
{
    [Fact]
    public async Task RunAsyncInitialStatisticsAreHandledWithoutAcknowledgment()
    {
        using var stop = new CancellationTokenSource();
        var client = new MessageSource();
        var handled = new List<RunnerScaleSetMessage?>();
        var handler = new MessageHandler((message, _) =>
        {
            handled.Add(message);
            stop.Cancel();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).RunAsync(handler, stop.Token));

        RunnerScaleSetMessage initial = Assert.IsType<RunnerScaleSetMessage>(Assert.Single(handled));
        Assert.Equal(-1, initial.MessageId);
        Assert.Same(client.Session.Statistics, initial.Statistics);
        Assert.Empty(client.Polls);
        Assert.Empty(client.Acknowledgments);
    }

    [Fact]
    public async Task RunAsyncEmptyPollStillCallsHandlerAndDoesNotAcknowledge()
    {
        using var stop = new CancellationTokenSource();
        var client = new MessageSource();
        var handled = new List<RunnerScaleSetMessage?>();
        var handler = new MessageHandler((message, _) =>
        {
            handled.Add(message);
            if (message is null)
            {
                stop.Cancel();
            }

            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).RunAsync(handler, stop.Token));

        Assert.Equal(2, handled.Count);
        Assert.Null(handled[1]);
        Assert.Equal((0L, 10), Assert.Single(client.Polls));
        Assert.Empty(client.Acknowledgments);
    }

    [Fact]
    public async Task RunAsyncHandlerFailureStopsWithoutAcknowledgment()
    {
        var client = new MessageSource();
        client.Messages.Enqueue(new RunnerScaleSetMessage { MessageId = 42 });
        var expected = new InvalidOperationException("processing failed");
        var handler = new MessageHandler((message, _) => message?.MessageId == 42
            ? Task.FromException(expected)
            : Task.CompletedTask);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RunAsync(handler, TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Single(client.Polls);
        Assert.Empty(client.Acknowledgments);
    }

    [Fact]
    public async Task RunAsyncSuccessfulHandlerCancelsShutdownAcknowledgmentOutlivesCancellation()
    {
        using var stop = new CancellationTokenSource();
        var client = new MessageSource();
        client.Messages.Enqueue(new RunnerScaleSetMessage { MessageId = 42 });
        bool processed = false;
        var handler = new MessageHandler((message, _) =>
        {
            if (message?.MessageId == 42)
            {
                processed = true;
                stop.Cancel();
            }

            return Task.CompletedTask;
        });
        client.OnAcknowledge = (id, token) =>
        {
            Assert.True(processed);
            Assert.Equal(42, id);
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).RunAsync(handler, stop.Token));

        Assert.Equal(42, Assert.Single(client.Acknowledgments));
    }

    [Fact]
    public async Task RunAsyncNextPollUsesLastMessageAndUpdatedCapacity()
    {
        using var stop = new CancellationTokenSource();
        var client = new MessageSource();
        client.Messages.Enqueue(new RunnerScaleSetMessage { MessageId = 5 });
        ScaleSetListener listener = Create(client);
        var handler = new MessageHandler((message, _) =>
        {
            if (message?.MessageId == 5)
            {
                listener.SetMaxRunners(7);
            }
            else if (message is null)
            {
                stop.Cancel();
            }

            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(handler, stop.Token));

        Assert.Equal([(0L, 10), (5L, 7)], client.Polls);
        Assert.Equal(5, Assert.Single(client.Acknowledgments));
    }

    [Fact]
    public async Task RunAsyncAcknowledgmentFailureDoesNotPollAgain()
    {
        var client = new MessageSource();
        client.Messages.Enqueue(new RunnerScaleSetMessage { MessageId = 42 });
        var expected = new InvalidOperationException("ack failed");
        client.OnAcknowledge = (_, _) => Task.FromException(expected);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(client).RunAsync(new MessageHandler((_, _) => Task.CompletedTask), TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Single(client.Polls);
        Assert.Equal(42, Assert.Single(client.Acknowledgments));
    }

    [Fact]
    public async Task RunAsyncAcknowledgmentTimeoutCancelsIndependentBudget()
    {
        var time = new ManualTimeProvider();
        var client = new MessageSource();
        client.Messages.Enqueue(new RunnerScaleSetMessage { MessageId = 42 });
        var acknowledging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnAcknowledge = async (_, token) =>
        {
            acknowledging.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var listener = new ScaleSetListener(client, new ScaleSetListenerOptions { ScaleSetId = 1 }, timeProvider: time);
        Task run = listener.RunAsync(new MessageHandler((_, _) => Task.CompletedTask), TestContext.Current.CancellationToken);
        await acknowledging.Task;

        Assert.Equal(TimeSpan.FromSeconds(60), time.DueTime);
        time.Expire();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Single(client.Polls);
        Assert.Equal(42, Assert.Single(client.Acknowledgments));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsyncInvalidInitialSessionRejectsBeforeHandling(bool emptyId)
    {
        var client = new MessageSource
        {
            Session = new RunnerScaleSetSession
            {
                SessionId = emptyId ? Guid.Empty : Guid.NewGuid(),
                Statistics = emptyId ? new RunnerScaleSetStatistic() : null,
            },
        };
        bool handled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RunAsync(new MessageHandler((_, _) =>
        {
            handled = true;
            return Task.CompletedTask;
        }), TestContext.Current.CancellationToken));

        Assert.False(handled);
        Assert.Empty(client.Polls);
    }

    [Fact]
    public void SetMaxRunnersNegativeValueIsRejectedWithoutChangingCapacity()
    {
        ScaleSetListener listener = Create(new MessageSource());

        Assert.Throws<ArgumentOutOfRangeException>(() => listener.SetMaxRunners(-1));
        Assert.Equal(10, listener.MaxRunners);
        listener.SetMaxRunners(0);
        Assert.Equal(0, listener.MaxRunners);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsInvalidScaleSetId(long scaleSetId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleSetListener(
            new MessageSource(), new ScaleSetListenerOptions { ScaleSetId = scaleSetId }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void ConstructorRejectsNonPositiveAcknowledgmentBudget(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleSetListener(
            new MessageSource(), new ScaleSetListenerOptions
            {
                ScaleSetId = 1,
                AcknowledgmentTimeout = TimeSpan.FromMilliseconds(milliseconds),
            }));
    }

    [Fact]
    public void ConstructorRejectsNegativeInitialCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleSetListener(
            new MessageSource(), new ScaleSetListenerOptions { ScaleSetId = 1, MaxRunners = -1 }));
    }

    [Fact(Timeout = 10000)]
    public async Task ConcurrentRunsAreRejectedWithoutInterruptingTheActiveRun()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<RunnerScaleSetMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MessageSource
        {
            OnGetMessage = (_, _, token) =>
            {
                entered.SetResult();
                return pending.Task.WaitAsync(token);
            },
        };
        ScaleSetListener listener = Create(client);
        var handler = new MessageHandler((_, _) => Task.CompletedTask);
        Task active = listener.RunAsync(handler, stop.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.RunAsync(handler, TestContext.Current.CancellationToken));
        Assert.False(active.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);

        Assert.Single(client.Polls);
        Assert.Empty(client.Acknowledgments);
    }

    private static ScaleSetListener Create(MessageSource client) => new(client, new ScaleSetListenerOptions { ScaleSetId = 1 });

    private sealed class MessageHandler(Func<RunnerScaleSetMessage?, CancellationToken, Task> handle) : IScaleSetMessageHandler
    {
        public Task HandleAsync(RunnerScaleSetMessage? message, CancellationToken cancellationToken) => handle(message, cancellationToken);
    }

    private sealed class MessageSource : IMessageSessionClient
    {
        public RunnerScaleSetSession Session { get; init; } = new()
        {
            SessionId = Guid.NewGuid(),
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 7 },
        };

        public Queue<RunnerScaleSetMessage?> Messages { get; } = new();

        public List<(long Cursor, int Capacity)> Polls { get; } = [];

        public List<long> Acknowledgments { get; } = [];

        public Func<long, CancellationToken, Task>? OnAcknowledge { get; set; }

        public Func<long, int, CancellationToken, Task<RunnerScaleSetMessage?>>? OnGetMessage { get; init; }

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
        {
            Polls.Add((lastMessageId, maxCapacity));
            if (OnGetMessage is not null)
            {
                return OnGetMessage(lastMessageId, maxCapacity, cancellationToken);
            }

            return Task.FromResult(Messages.Count > 0 ? Messages.Dequeue() : null);
        }

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
        {
            Acknowledgments.Add(messageId);
            return OnAcknowledge?.Invoke(messageId, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new ManualTimer();
        }

        public void Expire() => _callback!(_state);

        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
