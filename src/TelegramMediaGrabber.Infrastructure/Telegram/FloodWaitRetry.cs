namespace TelegramMediaGrabber.Infrastructure.Telegram;

/// <summary>
/// The FloodWait retry shape mandated by PROJECT_STATE.md §5 / AGENTS.md
/// §5.6: on a flood-wait signal, sleep for exactly the server-requested
/// duration plus a small fixed safety buffer, capped at a fixed number of
/// attempts — never a growing/exponential multiple, never a tight loop.
/// </summary>
/// <remarks>
/// Deliberately generic over the "what counts as a flood-wait exception"
/// question (<paramref name="floodWaitSecondsSelector"/> in
/// <see cref="ExecuteAsync{T}"/>) and the "how do we wait"
/// question (<c>delay</c>), so this policy can be unit tested with a fake
/// exception-throwing delegate and an instrumented delay function —
/// entirely independent of a live WTelegramClient connection.
/// </remarks>
public static class FloodWaitRetry
{
    /// <summary>Fixed safety buffer added on top of the server-requested wait, in seconds.</summary>
    public const double BufferSeconds = 2.0;

    /// <summary>Maximum number of attempts (including the first) before the final exception propagates.</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying on flood-wait per the policy above.
    /// </summary>
    /// <param name="operation">The Telegram call to attempt.</param>
    /// <param name="floodWaitSecondsSelector">
    /// Given a caught exception, returns the server-requested wait in whole seconds if it
    /// represents a flood-wait condition, or <see langword="null"/> if the exception should
    /// propagate immediately unretried.
    /// </param>
    /// <param name="delay">Performs the actual wait — injected so tests can replace real sleeping with instrumentation.</param>
    /// <param name="cancellationToken">Cancels before the next attempt or during the delay.</param>
    /// <returns>The result of the first successful attempt.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was triggered.</exception>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        Func<Exception, int?> floodWaitSecondsSelector,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(floodWaitSecondsSelector);
        ArgumentNullException.ThrowIfNull(delay);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (floodWaitSecondsSelector(ex) is int seconds && attempt < MaxAttempts)
            {
                var wait = TimeSpan.FromSeconds(seconds + BufferSeconds);
                await delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
