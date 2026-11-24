using IloWidget.Models;

namespace IloWidget.Services;

/// <summary>
/// Lifetime of the iLO web session behind the remote console. A login that is still running when the
/// console closes is not cut off: when it returns, the session it created is logged out right away.
/// </summary>
public sealed class ConsoleSession
{
    private readonly Func<IIloWebSession> _factory;
    private IIloWebSession? _session;
    private string? _key;
    private bool _opening;
    private bool _ended;
    private Task? _release;
    private readonly TimeSpan _closeTimeout;

    /// <summary>Completed when the login in flight has returned and, if the console was closed meanwhile, been logged out.</summary>
    private TaskCompletionSource? _loginDone;

    /// <param name="closeTimeout">How long <see cref="CloseAsync"/> waits for a login in flight (default 5 s).</param>
    public ConsoleSession(Func<IIloWebSession> factory, TimeSpan? closeTimeout = null)
    {
        _factory = factory;
        _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Logs in. Returns the session key, or null when the console was closed while the login ran.</summary>
    public async Task<string?> OpenAsync(CancellationToken ct = default)
    {
        if (_ended)
            return null;
        await ReleaseAsync(); // on reconnect: release the previous iLO session first
        if (_ended)
            return null; // the console was closed while that logout ran: do not start a new session

        var session = _factory();
        _session = session;
        _opening = true;
        var done = _loginDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string key;
            try
            {
                key = await session.OpenWebSessionAsync(ct);
            }
            catch
            {
                _opening = false;
                _session = null;
                session.Dispose();
                throw;
            }
            _opening = false;

            if (_ended)
            {
                _session = null;
                await LogoutAsync(session, key);
                return null;
            }
            _key = key;
            return key;
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>Logs out the open session, if any. A login still in flight is left to <see cref="OpenAsync"/>.</summary>
    public async Task ReleaseAsync()
    {
        if (_opening)
            return;
        var session = _session;
        var key = _key;
        _session = null;
        _key = null;
        if (session is not null)
        {
            var logout = LogoutAsync(session, key);
            _release = logout;
            await logout;
        }
    }

    /// <summary>
    /// Ends the console for good: releases the session now. A login still in flight is awaited (up to the
    /// close timeout) so its session is logged out before the caller, e.g. an exiting app, goes on.
    /// </summary>
    public async Task CloseAsync()
    {
        _ended = true;
        await ReleaseAsync();
        if (_release is { } release)
            await release; // a logout started earlier by ReleaseAsync (e.g. a reconnect) must finish too
        if (_opening && _loginDone is { } done)
            await Task.WhenAny(done.Task, Task.Delay(_closeTimeout));
    }

    private static async Task LogoutAsync(IIloWebSession session, string? key)
    {
        try
        {
            if (key is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await session.CloseWebSessionAsync(key, cts.Token);
            }
        }
        catch (Exception e) when (e is IloException or OperationCanceledException)
        {
            // the iLO will expire the session on its own
        }
        finally
        {
            session.Dispose();
        }
    }
}
