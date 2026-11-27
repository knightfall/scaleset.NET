using System.Buffers;
using ScaleSet.Providers.Docker;
using Xunit;

namespace ScaleSet.Docker.Tests;

public sealed class DockerEngineBufferTests
{
    private const int Chunk = 16 * 1024;
    private const int MaximumSize = (2 * Chunk) + 19;

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(2 * Chunk)]
    [InlineData(MaximumSize)]
    public async Task BoundedReadPreservesBytesAcrossChunksAndErasesScratch(int size)
    {
        byte[] payload = Payload(size);
        using var body = new StreamContent(new FragmentedStream(payload));
        var pool = new CheckingPool();

        byte[] actual = await DockerEngineClient.ReadBoundedBytesAsync(body, MaximumSize, TestContext.Current.CancellationToken, pool);

        Assert.Equal(payload, actual);
        Assert.Equal((size / Chunk) + 1, pool.Rented);
        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public async Task BoundedReadErasesEveryChunkOnOverflow()
    {
        using var body = new StreamContent(new FragmentedStream(Payload(MaximumSize + 1)));
        var pool = new CheckingPool();

        await Assert.ThrowsAsync<IOException>(() => DockerEngineClient.ReadBoundedBytesAsync(
            body, MaximumSize, TestContext.Current.CancellationToken, pool));

        Assert.Equal(3, pool.Rented);
        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public async Task BoundedReadErasesFullAndPartialChunksOnCancellation()
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new StreamContent(new FragmentedStream(Payload(Chunk + 23), paused));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pool = new CheckingPool();
        Task<byte[]> operation = DockerEngineClient.ReadBoundedBytesAsync(body, MaximumSize, caller.Token, pool);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(2, pool.Rented);
        Assert.Equal(pool.Rented, pool.Returned);
    }

    private static byte[] Payload(int size) =>
        Enumerable.Range(0, size).Select(static index => (byte)((index % 251) + 1)).ToArray();

    private sealed class FragmentedStream(byte[] source, TaskCompletionSource? paused = null) : MemoryStream(source)
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length && paused is not null)
            {
                paused.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1023)], cancellationToken);
        }
    }

    private sealed class CheckingPool : ArrayPool<byte>
    {
        public int Rented { get; private set; }
        public int Returned { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Rented++;
            // Include untouched pool capacity in the clearing assertion.
            var bytes = new byte[minimumLength + 7];
            Array.Fill<byte>(bytes, 0xA5);
            return bytes;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.DoesNotContain(array, static value => value != 0);
            Returned++;
        }
    }
}
