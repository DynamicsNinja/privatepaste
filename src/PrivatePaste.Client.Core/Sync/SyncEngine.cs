using System.Net.WebSockets;
using System.Text.Json;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

public enum SyncState { SignedOut, Connecting, Connected, Offline, Paused }

/// <summary>A failure the engine recovered from (it keeps running). The host logs it.</summary>
public sealed record SyncError(string Message, Exception Exception);

/// <summary>
/// Keeps the local history in live sync with the server and applies clips from other devices to the clipboard.
/// Per connection: connect, catch up via <c>/api/sync</c> (applying at most the newest caught-up clip from another device),
/// flush the offline queue, then receive pushes, with an app-level ping every 25 s and a 10 s pong timeout.
/// A 1008 close or a 401 from any API call raises <see cref="Revoked"/> once and stops for good (State = SignedOut);
/// any other failure sets State = Offline and reconnects after <see cref="Backoff.Next"/>.
/// <para>
/// The engine does not own pausing; it polls <c>isPaused</c> every second, disconnecting while paused (State = Paused).
/// The host must sequence Resume as: <c>await coordinator.OnResumedAsync(ct)</c> while still paused, THEN flip the flag,
/// so nothing copied while paused is uploaded and the engine reconnects (and catches up) on its next poll.
/// </para>
/// <para>
/// Catch-up applies a clip only when something was applied before (<c>LastAppliedAt</c> set: the first sync after sign-in
/// is history only, which also keeps backfilled clips off the clipboard) and never on the first successful catch-up after
/// a pause (clips copied elsewhere while paused go to history only). Live clips are applied as usual.
/// </para>
/// All waiting uses the injected <see cref="TimeProvider"/>. The backoff is reset only after a connection stayed up for one
/// ping interval (25 s), so a server that accepts and then drops at once still sees growing delays.
/// <para>
/// Events are raised on the engine's loop. Handlers must be quick and must not block on (or synchronously wait for)
/// <see cref="StopAsync"/>; marshal to the UI thread instead. A throwing handler is caught (reported via <see cref="Error"/>)
/// and never stops the engine.
/// </para>
/// </summary>
public sealed class SyncEngine(
    PrivatePasteApi api, HistoryCache cache, ClipboardCoordinator coordinator,
    Func<CancellationToken, Task<WebSocket>> connect, Guid ownDeviceId,
    TimeProvider time, Backoff backoff, Func<bool> isPaused) : IAsyncDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);
    private const int MaxMessageBytes = 8 * 1024 * 1024;   // text clips are at most 1 MB; JSON escaping can grow that
    private static readonly byte[] PingBytes = JsonSerializer.SerializeToUtf8Bytes(new WsMessage(WsTypes.Ping), ProtocolJson.Options);

    private enum Outcome { Failed, Revoked, Paused, Stopped }
    private enum AbortReason { None, Paused, PongTimeout, SendFailed }

    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private volatile SyncState _state = SyncState.Offline;
    private int _signedOut;

    private readonly object _saveGate = new();
    private bool _dirty;
    private DateTimeOffset? _lastSaveAt;
    private (Guid Id, long Seq)? _lastApplied;
    /// <summary>Set when a pause is observed; the next successful catch-up is history only. Loop thread only.</summary>
    private bool _resumingFromPause;

    public SyncState State => _state;
    public event EventHandler<SyncState>? StateChanged;
    /// <summary>The cache changed (UI refresh).</summary>
    public event EventHandler? HistoryChanged;
    /// <summary>1008 close or 401: the host signs out. Raised at most once.</summary>
    public event EventHandler? Revoked;
    /// <summary>A recovered failure (connect error, clipboard write failure, failed image fetch, ...), for logging.</summary>
    public event EventHandler<SyncError>? Error;

    /// <summary>Runs the loop on a background task. No-op if already running or after a revoke.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || Volatile.Read(ref _signedOut) != 0) return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            (loop, stop) = (_loop, _stop);
            (_loop, _stop) = (null, null);
        }
        if (stop is null) return;
        await stop.CancelAsync();
        await loop!;   // never throws
        stop.Dispose();
        SaveIfDirty();
        if (_state != SyncState.SignedOut) SetState(SyncState.Offline);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (isPaused())
                {
                    _resumingFromPause = true;
                    var poll = Task.Delay(PollInterval, time, stop);   // timer exists before the state is visible
                    SetState(SyncState.Paused);
                    await poll;
                    continue;
                }

                SetState(SyncState.Connecting);
                switch (await RunConnectionAsync(stop))
                {
                    case Outcome.Revoked:
                        SignOut();
                        return;
                    case Outcome.Stopped:
                        return;
                    case Outcome.Paused:
                        _resumingFromPause = true;
                        continue;
                }

                var delay = Task.Delay(backoff.Next(), time, stop);
                SetState(SyncState.Offline);
                await delay;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e)
        {
            Report("Sync stopped unexpectedly.", e);
            if (_state != SyncState.SignedOut) SetState(SyncState.Offline);
        }
    }

    private async Task<Outcome> RunConnectionAsync(CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        WebSocket socket;
        try
        {
            socket = await connect(lifetime.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return Outcome.Stopped;
        }
        catch (Exception e)
        {
            Report("Could not connect to the server.", e);
            return await ProbeAuthAsync(stop);
        }

        var link = new Link(socket, lifetime);
        PeriodicTimer? timer = null;
        Task? monitor = null;
        DateTimeOffset? connectedAt = null;
        try
        {
            await CatchUpAsync(lifetime.Token);
            await coordinator.FlushQueueAsync(lifetime.Token);
            if (isPaused()) return Outcome.Paused;

            link.LastPingAt = time.GetUtcNow();
            timer = new PeriodicTimer(PollInterval, time);   // created before Connected is visible
            monitor = MonitorAsync(link, timer);
            connectedAt = time.GetUtcNow();
            SetState(SyncState.Connected);
            return await ReceiveLoopAsync(link);
        }
        catch (UnauthorizedApiException)
        {
            return Outcome.Revoked;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return Outcome.Stopped;
        }
        catch (Exception e)
        {
            switch (link.Reason)
            {
                case AbortReason.Paused:
                    return Outcome.Paused;
                case AbortReason.PongTimeout:
                    Report("No pong from the server; reconnecting.", e);
                    break;
                default:
                    Report("Connection to the server lost.", e);
                    break;
            }
            return Outcome.Failed;
        }
        finally
        {
            try { await lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
            socket.Abort();
            timer?.Dispose();
            if (monitor is not null) await monitor;   // never throws
            socket.Dispose();
            SaveIfDirty();
            // Only a connection that proved stable resets the delays (runs on the loop thread, after the monitor ended).
            if (connectedAt is { } since && time.GetUtcNow() - since >= PingInterval) backoff.Reset();
        }
    }

    /// <summary>After a failed connect: a 401 here means the device was revoked (the upgrade itself just failed).</summary>
    private async Task<Outcome> ProbeAuthAsync(CancellationToken stop)
    {
        try
        {
            await api.SyncAsync(cache.HighestSeq, limit: 1, ct: stop);
        }
        catch (UnauthorizedApiException)
        {
            return Outcome.Revoked;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return Outcome.Stopped;
        }
        catch (Exception) { }   // offline as well: plain backoff
        return Outcome.Failed;
    }

    private async Task CatchUpAsync(CancellationToken ct)
    {
        var caught = new List<ClipDto>();
        var changed = false;
        while (true)
        {
            var before = cache.HighestSeq;
            var page = await api.SyncAsync(before, ct: ct);
            foreach (var clip in page.Clips)
            {
                cache.Upsert(clip);
                caught.Add(clip);
            }
            // Truncated pages only report tombstones up to their last clip; the final page continues from latestSeq.
            var tombstoneSeq = page.HasMore && page.Clips.Count > 0 ? page.Clips[^1].Seq : page.LatestSeq;
            foreach (var id in page.DeletedIds) cache.Remove(id, tombstoneSeq);
            changed |= page.Clips.Count > 0 || page.DeletedIds.Count > 0;
            if (!page.HasMore || cache.HighestSeq <= before) break;
        }
        if (changed) OnHistoryChanged();

        var afterPause = _resumingFromPause;
        _resumingFromPause = false;   // cleared only once a catch-up completed
        if (afterPause || cache.LastAppliedAt is not { } lastApplied) return;   // history only

        // Apply at most the newest caught-up clip from another device that is still present, if newer than the last applied.
        var present = cache.Items.ToDictionary(i => i.Clip.Id, i => i.Clip.Seq);
        var newest = caught
            .Where(c => c.SourceDeviceId != ownDeviceId && present.TryGetValue(c.Id, out var seq) && seq == c.Seq)
            .MaxBy(c => (c.LastCopiedAt, c.Seq));
        if (newest is not null && !isPaused() && newest.LastCopiedAt > lastApplied)
            await ApplyAsync(newest, ct);
    }

    private async Task<Outcome> ReceiveLoopAsync(Link link)
    {
        var ct = link.Token;
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await link.Socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return result.CloseStatus == WebSocketCloseStatus.PolicyViolation ? Outcome.Revoked : Outcome.Failed;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) throw new InvalidDataException("WebSocket message too large.");
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            WsMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<WsMessage>(message.GetBuffer().AsSpan(0, (int)message.Length), ProtocolJson.Options);
            }
            catch (JsonException e)
            {
                Report("Ignored an unreadable message from the server.", e);
                continue;
            }
            if (msg is not null) await HandleAsync(msg, link, ct);
        }
    }

    private async Task HandleAsync(WsMessage msg, Link link, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case WsTypes.Pong:
                link.PongReceived();
                return;
            case WsTypes.ClipCreated when msg.Clip is { } clip:
                cache.Upsert(clip);
                if (clip.SourceDeviceId != ownDeviceId && msg.Live == true && !isPaused()
                    && _lastApplied != (clip.Id, clip.Seq)
                    && !(cache.LastAppliedAt > clip.LastCopiedAt))   // out-of-order broadcast of an older clip
                {
                    // A slow image fetch blocks this loop, and with it the pong: don't let that count as a dead link.
                    link.SetBusy(true);
                    try { await ApplyAsync(clip, ct); }
                    finally { link.SetBusy(false); }
                }
                OnHistoryChanged();
                return;
            case WsTypes.ClipDeleted when msg.Id is { } id:
                cache.Remove(id, msg.Seq ?? 0);
                OnHistoryChanged();
                return;
        }
    }

    /// <summary>Applies a remote clip. 401 and cancellation propagate; anything else is reported and skipped.</summary>
    private async Task ApplyAsync(ClipDto clip, CancellationToken ct)
    {
        try
        {
            await coordinator.ApplyRemoteAsync(clip, ct);
            _lastApplied = (clip.Id, clip.Seq);
        }
        catch (UnauthorizedApiException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Report("Could not put a clip from another device on the clipboard.", e);
        }
    }

    /// <summary>Every second while connected: pause check, ping/pong bookkeeping, throttled cache save.</summary>
    private async Task MonitorAsync(Link link, PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(link.Token))
            {
                if (isPaused())
                {
                    link.Abort(AbortReason.Paused);
                    return;
                }
                var now = time.GetUtcNow();
                switch (link.PingState(now))
                {
                    case PingDue.Timeout:
                        link.Abort(AbortReason.PongTimeout);
                        return;
                    case PingDue.Send:
                        await link.Socket.SendAsync(PingBytes, WebSocketMessageType.Text, true, link.Token);
                        break;
                }
                SaveIfDue();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            link.Abort(AbortReason.SendFailed);   // the receive loop reports the resulting failure
        }
    }

    private void OnHistoryChanged()
    {
        lock (_saveGate) _dirty = true;
        SaveIfDue();
        Raise(HistoryChanged, EventArgs.Empty);
    }

    private void SaveIfDue()
    {
        lock (_saveGate)
        {
            if (!_dirty) return;
            var now = time.GetUtcNow();
            if (_lastSaveAt is { } at && now - at < SaveInterval) return;
            SaveLocked(now);
        }
    }

    private void SaveIfDirty()
    {
        lock (_saveGate)
            if (_dirty) SaveLocked(time.GetUtcNow());
    }

    private void SaveLocked(DateTimeOffset now)
    {
        _lastSaveAt = now;
        try
        {
            cache.Save();
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report("Could not save local history.", e);   // stays dirty: retried on the next tick
        }
    }

    private void SignOut()
    {
        if (Interlocked.Exchange(ref _signedOut, 1) != 0) return;
        SetState(SyncState.SignedOut);
        Raise(Revoked, EventArgs.Empty);
    }

    private void SetState(SyncState state)
    {
        if (_state == state) return;
        _state = state;
        Raise(StateChanged, state);
    }

    private void Report(string message, Exception e)
    {
        try { Error?.Invoke(this, new SyncError(message, e)); }
        catch (Exception) { }   // the log sink itself failed: nowhere left to report it
    }

    /// <summary>Invokes each subscriber separately so one throwing handler neither skips the others nor faults the loop.</summary>
    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler<T>)d)(this, args); }
            catch (Exception e) { Report("A sync event handler failed.", e); }
        }
    }

    private void Raise(EventHandler? handler, EventArgs args)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler)d)(this, args); }
            catch (Exception e) { Report("A sync event handler failed.", e); }
        }
    }

    private enum PingDue { None, Send, Timeout }

    /// <summary>One live connection: its socket, its cancellation, and ping/pong state shared by the receive and monitor loops.</summary>
    private sealed class Link(WebSocket socket, CancellationTokenSource lifetime)
    {
        private readonly object _gate = new();
        private DateTimeOffset? _pingSentAt;
        private volatile AbortReason _reason;
        private bool _busy;

        public WebSocket Socket { get; } = socket;
        public CancellationToken Token { get; } = lifetime.Token;
        public AbortReason Reason => _reason;
        public DateTimeOffset LastPingAt { get; set; }

        public PingDue PingState(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_busy)
                {
                    if (_pingSentAt is not null) _pingSentAt = now;   // the pong timeout restarts once the loop reads again
                    return PingDue.None;
                }
                if (_pingSentAt is { } sent) return now - sent >= PongTimeout ? PingDue.Timeout : PingDue.None;
                if (now - LastPingAt < PingInterval) return PingDue.None;
                _pingSentAt = now;
                LastPingAt = now;
                return PingDue.Send;
            }
        }

        /// <summary>While busy (applying a clip), no pings are sent and the pong timeout is suspended.</summary>
        public void SetBusy(bool busy)
        {
            lock (_gate) _busy = busy;
        }

        public void PongReceived()
        {
            lock (_gate) _pingSentAt = null;
        }

        public void Abort(AbortReason reason)
        {
            if (_reason == AbortReason.None) _reason = reason;
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
            Socket.Abort();
        }
    }
}
