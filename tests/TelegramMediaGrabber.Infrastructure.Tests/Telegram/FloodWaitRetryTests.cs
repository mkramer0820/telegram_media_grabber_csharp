using TelegramMediaGrabber.Infrastructure.Telegram;

namespace TelegramMediaGrabber.Infrastructure.Tests.Telegram;

/// <summary>
/// Verifies the FloodWait retry shape (PROJECT_STATE.md §5) in isolation,
/// with a fake exception-throwing operation and an instrumented delay —
/// no live WTelegramClient connection involved.
/// </summary>
public sealed class FloodWaitRetryTests
{
    private sealed class FakeFloodWaitException(int seconds) : Exception
    {
        public int Seconds { get; } = seconds;
    }

    private static int? SelectSeconds(Exception ex) => ex is FakeFloodWaitException f ? f.Seconds : null;

    [Fact]
    public async Task ExecuteAsync_returns_result_on_first_success_without_delay()
    {
        var delays = new List<TimeSpan>();
        var result = await FloodWaitRetry.ExecuteAsync(
            () => Task.FromResult(42),
            SelectSeconds,
            (span, _) => { delays.Add(span); return Task.CompletedTask; });

        Assert.Equal(42, result);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task ExecuteAsync_retries_after_flood_wait_and_sleeps_server_seconds_plus_buffer()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;

        var result = await FloodWaitRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new FakeFloodWaitException(7);
                }

                return Task.FromResult("ok");
            },
            SelectSeconds,
            (span, _) => { delays.Add(span); return Task.CompletedTask; });

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
        var delay = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(7 + FloodWaitRetry.BufferSeconds), delay);
    }

    [Fact]
    public async Task ExecuteAsync_never_grows_the_wait_across_repeated_flood_waits()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;

        await FloodWaitRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts < 4)
                {
                    throw new FakeFloodWaitException(3);
                }

                return Task.FromResult(true);
            },
            SelectSeconds,
            (span, _) => { delays.Add(span); return Task.CompletedTask; });

        Assert.Equal(3, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(3 + FloodWaitRetry.BufferSeconds), d));
    }

    [Fact]
    public async Task ExecuteAsync_stops_after_MaxAttempts_and_propagates_the_final_exception()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<FakeFloodWaitException>(() => FloodWaitRetry.ExecuteAsync<int>(
            () =>
            {
                attempts++;
                throw new FakeFloodWaitException(1);
            },
            SelectSeconds,
            (_, _) => Task.CompletedTask));

        Assert.Equal(1, ex.Seconds);
        Assert.Equal(FloodWaitRetry.MaxAttempts, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_propagates_non_flood_wait_exceptions_immediately_without_retry()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => FloodWaitRetry.ExecuteAsync<int>(
            () =>
            {
                attempts++;
                throw new InvalidOperationException("boom");
            },
            SelectSeconds,
            (_, _) => Task.CompletedTask));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_honors_cancellation_before_the_next_attempt()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => FloodWaitRetry.ExecuteAsync<int>(
            () =>
            {
                attempts++;
                cts.Cancel();
                throw new FakeFloodWaitException(1);
            },
            SelectSeconds,
            (_, ct) => Task.CompletedTask,
            cts.Token));

        Assert.Equal(1, attempts);
    }
}
