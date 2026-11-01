using System.Net.WebSockets;
using System.Text.Json;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

public enum SyncState { SignedOut, Connecting, Connected, Offline, Paused }

/// <summary>A failure the engine recovered from (it keeps running). The host logs it.</summary>
public sealed record SyncError(string Message, Exception Exception);

/// <summary>
/// Keeps the local history in live sync with the server and applies clips from other devices to the clipboard.
/// Per connection: connect, catch up via <c>/api/sync</c> (applying at most the newest caught-up clip from another device),
/// flush the offline queue, then receive pushes, with an app-level ping every 25 s and a 10 s pong timeout. While connected,
/// a non-empty offline queue is flushed again every 30 s (a live upload that failed transiently waits there).
/// A 1008 close or a 401 from any API call raises <see cref="Revoked"/> once and stops for good (State = SignedOut);
/// any other failure sets State = Offline and reconnects after <see cref="Backoff.Next"/>.
/// <para>
/// Every clip from the WebSocket and the catch-up is opened with the current <c>codec</c> (<see cref="ClipCodec.Decode"/>)
/// before it reaches the cache, so the cache holds plaintext and <see cref="ClipKind.Unknown"/> clips are never applied.
/// Without a codec (locked) the engine stays disconnected (State = Offline) and checks again every second; each connection
/// keeps the codec it started with, and a key disposed under it ends that connection like a dropped link.
/// A <c>vault.reset</c> message, a clip sealed under another key id, or the coordinator's <see cref="ClipboardCoordinator.KeyReset"/>
/// raises <see cref="KeyReset"/>; a 426 raises <see cref="UpdateRequired"/>. Either is raised once and stops the engine for
/// good (State = Offline): the host builds a new session once it has the new key or the app was updated.
/// </para>
/// <para>
/// The engine does not own pausing; it polls <c>isPaused</c> every second, disconnecting while paused (State = Paused).
/// The host must sequence Resume as: <c>await coordinator.OnResumedAsync(ct)</c> while still paused, THEN flip the flag,
/// so nothing copied while paused is uploaded and the engine reconnects (and catches up) on its next poll.
/// </para>
/// <para>
/// Catch-up applies a clip only when something was applied before (<c>LastAppliedAt</c> set: the first sync after sign-in
/// is history only, which also keeps backfilled clips off the clipboard) and never on the first successful catch-up after
/// a pause (clips copied elsewhere while paused go to history only). It never overwrites a newer local copy: the candidate
/// must be the top of the history, newer than <c>LastAppliedAt</c> and the coordinator's <c>LastLocalCaptureAt</c>, with
/// the offline queue empty, and not a clip already held with the same copy time (a pin change). Live clips are applied as
/// usual (a pin change arrives with <c>live: false</c>).
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
    PrivatePasteApi api, HistoryCache cache, ClipboardCoordinator coordinator, Func<ClipCodec?> codec,
    Func<CancellationToken, Task<WebSocket>> connect, Guid ownDeviceId,
    TimeProvider time, Backoff backoff, Func<bool> isPaused) : IAsyncDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);
    /// <summary>While connected, queued clips (a transient upload failure) are retried this often.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private const int MaxMessageBytes = 8 * 1024 * 1024;   // text clips are at most 1 MB; JSON escaping can grow that
    private static readonly byte[] PingBytes = JsonSerializer.SerializeToUtf8Bytes(new WsMessage(WsTypes.Ping), ProtocolJson.Options);

    private enum Outcome { Failed, Revoked, Paused, Stopped, KeyReset, UpdateRequired }
    private enum AbortReason { None, Paused, PongTimeout, SendFailed, Revoked, Nudged, Halted }

    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private volatile SyncState _state = SyncState.Offline;
    private int _signedOut;
    /// <summary>0 while running; otherwise the <see cref="Outcome"/> (KeyReset or UpdateRequired) that stopped the engine for good.</summary>
    private int _halted;

    private readonly object _saveGate = new();
    private bool _dirty;
    private DateTimeOffset? _lastSaveAt;
    private (Guid Id, long Seq)? _lastApplied;
    /// <summary>Set when a pause is observed; the next successful catch-up is history only. Loop thread only.</summary>
    private bool _resumingFromPause;
    private bool _thumbnailsHooked;
    // Nudge support: what to cancel right now (connection attempt, live link, backoff wait) and whether a nudge is pending.
    private volatile CancellationTokenSource? _attempt;
    private volatile Link? _link;
    private volatile CancellationTokenSource? _wait;
    private int _nudged;

    public SyncState State => _state;
    public event EventHandler<SyncState>? StateChanged;
    /// <summary>The cache changed (UI refresh).</summary>
    public event EventHandler? HistoryChanged;
    /// <summary>1008 close or 401: the host signs out. Raised at most once.</summary>
    public event EventHandler? Revoked;
    /// <summary>
    /// The server's vault key is no longer this device's (reset by the owner): the engine has stopped, and the host asks for
    /// the new passphrase. Raised at most once, and never together with <see cref="UpdateRequired"/>. May be raised on the
    /// thread that ran the coordinator's upload.
    /// </summary>
    public event EventHandler? KeyReset;
    /// <summary>The server answered 426 (this app is too old to sync): the engine has stopped. Raised at most once.</summary>
    public event EventHandler? UpdateRequired;
    /// <summary>A recovered failure (connect error, clipboard write failure, failed image fetch, ...), for logging.</summary>
    public event EventHandler<SyncError>? Error;
    /// <summary>
    /// A clip from another device (live, or the single catch-up clip) was written to the clipboard. Raised only after the
    /// write succeeded; never for this device's own clips, backfill or history-only clips, or while paused.
    /// </summary>
    public event EventHandler<ClipDto>? ClipApplied;

    /// <summary>Runs the loop on a background task. No-op if already running, after a revoke, or after a key reset or 426.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || Volatile.Read(ref _signedOut) != 0 || Volatile.Read(ref _halted) != 0) return;
            if (!_thumbnailsHooked)
            {
                _thumbnailsHooked = true;
                coordinator.Thumbnails.Stored += (_, _) => { lock (_saveGate) _dirty = true; };   // saved on the next tick or stop
                coordinator.KeyReset += (_, _) => Halt(Outcome.KeyReset);
            }
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>
    /// The network or the machine's power state changed (resume from sleep, network up, new address): the current link is
    /// likely dead without knowing it. Drops the current connection attempt or link, resets the backoff and reconnects at
    /// once. Thread-safe; a no-op while paused, stopped or signed out.
    /// </summary>
    public void Nudge()
    {
        Interlocked.Exchange(ref _nudged, 1);
        if (_link is { } link) link.Abort(AbortReason.Nudged);
        else Cancel(_attempt);
        Cancel(_wait);
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
        await stop.CancelAsync().ConfigureAwait(false);
        await loop!.ConfigureAwait(false);   // never throws
        stop.Dispose();
        SaveIfDirty();
        if (_state != SyncState.SignedOut) SetState(SyncState.Offline);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (Volatile.Read(ref _halted) != 0)
                {
                    SetState(SyncState.Offline);
                    return;
                }
                if (isPaused())
                {
                    _resumingFromPause = true;
                    var poll = Task.Delay(PollInterval, time, stop);   // timer exists before the state is visible
                    SetState(SyncState.Paused);
                    await poll.ConfigureAwait(false);
                    continue;
                }
                if (codec() is not { } current)
                {
                    // Locked: nothing could be opened, so nothing is fetched until there is a key.
                    var poll = Task.Delay(PollInterval, time, stop);
                    SetState(SyncState.Offline);
                    await poll.ConfigureAwait(false);
                    continue;
                }

                SetState(SyncState.Connecting);
                var outcome = await RunConnectionAsync(current, stop).ConfigureAwait(false);
                switch (outcome)
                {
                    case Outcome.Revoked:
                        SignOut();
                        return;
                    case Outcome.Stopped:
                        return;
                    case Outcome.Paused:
                        _resumingFromPause = true;
                        continue;
                    case Outcome.KeyReset or Outcome.UpdateRequired:
                        Halt(outcome);
                        continue;   // the check at the top ends the loop
                }
                if (Volatile.Read(ref _halted) != 0) continue;   // halted from elsewhere (the coordinator) meanwhile

                if (Interlocked.Exchange(ref _nudged, 0) == 1)
                {
                    backoff.Reset();   // the network just changed: retry at once, with fresh delays
                    continue;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                _wait = wait;
                if (Volatile.Read(ref _nudged) == 1) wait.Cancel();   // nudged before _wait was visible
                var delay = Task.Delay(backoff.Next(), time, wait.Token);
                SetState(SyncState.Offline);
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _nudged, 0);
                    backoff.Reset();
                }
                finally { _wait = null; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e)
        {
            Report("Sync stopped unexpectedly.", e);
            if (_state != SyncState.SignedOut) SetState(SyncState.Offline);
        }
    }

    private async Task<Outcome> RunConnectionAsync(ClipCodec c, CancellationToken stop)
    {
        Interlocked.Exchange(ref _nudged, 0);   // this attempt is the fresh start a nudge asks for
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _attempt = lifetime;
        WebSocket socket;
        try
        {
            socket = await connect(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _attempt = null;
            return Outcome.Stopped;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            _attempt = null;
            return Outcome.Failed;   // nudged: reconnect at once
        }
        catch (Exception e)
        {
            _attempt = null;
            Report("Could not connect to the server.", e);
            return await ProbeAuthAsync(stop).ConfigureAwait(false);   // a 426 or 401 also fails the upgrade itself
        }

        var link = new Link(socket, lifetime);
        _link = link;
        _attempt = null;
        if (Volatile.Read(ref _nudged) == 1) link.Abort(AbortReason.Nudged);   // nudged while the connect completed
        PeriodicTimer? timer = null;
        Task? monitor = null;
        DateTimeOffset? connectedAt = null;
        try
        {
            cache.KeyId = c.KeyId;
            await CatchUpAsync(c, lifetime.Token).ConfigureAwait(false);
            await coordinator.FlushQueueAsync(lifetime.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _halted) != 0) return Outcome.Failed;   // the flush hit a key reset
            if (isPaused()) return Outcome.Paused;

            link.LastPingAt = time.GetUtcNow();
            timer = new PeriodicTimer(PollInterval, time);   // created before Connected is visible
            monitor = MonitorAsync(link, timer);
            connectedAt = time.GetUtcNow();
            SetState(SyncState.Connected);
            return await ReceiveLoopAsync(link, c).ConfigureAwait(false);
        }
        catch (UnauthorizedApiException)
        {
            return Outcome.Revoked;
        }
        catch (UpdateRequiredException)
        {
            return Outcome.UpdateRequired;
        }
        catch (VaultResetSignal)
        {
            return Outcome.KeyReset;
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
                case AbortReason.Revoked:
                    return Outcome.Revoked;
                case AbortReason.Nudged:
                    Report("Network or power change; reconnecting.", e);
                    break;
                case AbortReason.PongTimeout:
                    Report("No pong from the server; reconnecting.", e);
                    break;
                case AbortReason.Halted:
                    break;
                case AbortReason.None when e is VaultKeyClosedException:
                    Report("The vault key was closed; reconnecting.", e);
                    break;
                default:
                    Report("Connection to the server lost.", e);
                    break;
            }
            return Outcome.Failed;
        }
        finally
        {
            _link = null;
            try { await lifetime.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
            socket.Abort();
            timer?.Dispose();
            if (monitor is not null) await monitor.ConfigureAwait(false);   // never throws
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
            await api.SyncAsync(cache.HighestSeq, limit: 1, ct: stop).ConfigureAwait(false);
        }
        catch (UnauthorizedApiException)
        {
            return Outcome.Revoked;
        }
        catch (UpdateRequiredException)
        {
            return Outcome.UpdateRequired;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return Outcome.Stopped;
        }
        catch (Exception) { }   // offline as well: plain backoff
        return Outcome.Failed;
    }

    private async Task CatchUpAsync(ClipCodec c, CancellationToken ct)
    {
        var caught = new List<ClipDto>();
        var changed = false;
        // A clip already held with the same copy time came back for another reason (a pin or unpin): it is not a copy, so it
        // never goes to the clipboard, even when it is the newest clip and was skipped before (e.g. after a pause).
        var heldCopiedAt = cache.Items.ToDictionary(i => i.Clip.Id, i => i.Clip.LastCopiedAt);
        while (true)
        {
            var before = cache.HighestSeq;
            var page = await api.SyncAsync(before, ct: ct).ConfigureAwait(false);
            foreach (var wire in page.Clips)
            {
                var clip = Decode(c, wire);
                changed |= cache.Upsert(clip);
                caught.Add(clip);
                coordinator.Thumbnails.Request(clip);
            }
            // Truncated pages only report tombstones up to their last clip; the final page continues from latestSeq.
            var tombstoneSeq = page.HasMore && page.Clips.Count > 0 ? page.Clips[^1].Seq : page.LatestSeq;
            foreach (var id in page.DeletedIds) changed |= cache.Remove(id, tombstoneSeq);
            if (!page.HasMore || cache.HighestSeq <= before) break;
        }
        if (changed) OnHistoryChanged();

        var afterPause = _resumingFromPause;
        _resumingFromPause = false;   // cleared only once a catch-up completed
        if (afterPause || cache.LastAppliedAt is not { } lastApplied) return;   // history only

        // Apply at most the newest caught-up clip from another device, and only if it is the newest thing there is: the top of
        // the history, newer than the last applied clip and than the last local copy, with no local copy still queued.
        var items = cache.Items;
        var present = items.ToDictionary(i => i.Clip.Id, i => i.Clip.Seq);
        var newest = caught
            .Where(c => c.SourceDeviceId != ownDeviceId && c.Kind != ClipKind.Unknown
                        && present.TryGetValue(c.Id, out var seq) && seq == c.Seq
                        && !(heldCopiedAt.TryGetValue(c.Id, out var heldAt) && c.LastCopiedAt <= heldAt))
            .MaxBy(c => (c.LastCopiedAt, c.Seq));
        if (newest is null || isPaused()) return;
        if (coordinator.HasQueuedClips) return;
        if (items[0].Clip.Id != newest.Id || items[0].Clip.Seq != newest.Seq) return;
        if (newest.LastCopiedAt <= lastApplied) return;
        if (coordinator.LastLocalCaptureAt is { } localCopy && newest.LastCopiedAt <= localCopy) return;
        await ApplyAsync(newest, ct).ConfigureAwait(false);
    }

    private async Task<Outcome> ReceiveLoopAsync(Link link, ClipCodec c)
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
                result = await link.Socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
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
            if (msg is not null) await HandleAsync(msg, link, c, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(WsMessage msg, Link link, ClipCodec c, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case WsTypes.Pong:
                link.PongReceived();
                return;
            case WsTypes.VaultReset:
                throw new VaultResetSignal();
            case WsTypes.ClipCreated when msg.Clip is { } wire:
                var clip = Decode(c, wire);
                // Recorded (and saved when due) before the apply, which a stop can cancel midway.
                if (cache.Upsert(clip)) OnHistoryChanged();
                coordinator.Thumbnails.Request(clip);
                if (clip.SourceDeviceId != ownDeviceId && clip.Kind != ClipKind.Unknown && msg.Live == true && !isPaused()
                    && _lastApplied != (clip.Id, clip.Seq)
                    && !(cache.LastAppliedAt > clip.LastCopiedAt))   // out-of-order broadcast of an older clip
                {
                    // A slow image fetch blocks this loop, and with it the pong: don't let that count as a dead link.
                    link.SetBusy(true);
                    try { await ApplyAsync(clip, ct).ConfigureAwait(false); }
                    finally { link.SetBusy(false); }
                }
                return;
            case WsTypes.ClipDeleted when msg.Id is { } id:
                if (cache.Remove(id, msg.Seq ?? 0)) OnHistoryChanged();
                return;
        }
    }

    /// <summary>Applies a remote clip. 401 and cancellation propagate; anything else is reported and skipped.</summary>
    private async Task ApplyAsync(ClipDto clip, CancellationToken ct)
    {
        try
        {
            var written = await coordinator.ApplyRemoteAsync(clip, ct).ConfigureAwait(false);
            _lastApplied = (clip.Id, clip.Seq);
            if (written) Raise(ClipApplied, clip);
        }
        catch (UnauthorizedApiException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Report("Could not put a clip from another device on the clipboard.", e);
        }
    }

    /// <summary>Every second while connected: pause check, ping/pong bookkeeping, queue retry, throttled cache save.</summary>
    private async Task MonitorAsync(Link link, PeriodicTimer timer)
    {
        var lastFlushAt = time.GetUtcNow();   // the connect itself just flushed
        Task? flush = null;
        try
        {
            while (await timer.WaitForNextTickAsync(link.Token).ConfigureAwait(false))
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
                        await link.Socket.SendAsync(PingBytes, WebSocketMessageType.Text, true, link.Token).ConfigureAwait(false);
                        break;
                }
                if (now - lastFlushAt >= FlushInterval)
                {
                    lastFlushAt = now;
                    // In the background: a slow upload must not hold up pings or the pause check.
                    if (flush is null or { IsCompleted: true } && coordinator.HasQueuedClips) flush = FlushAsync(link);
                }
                SaveIfDue();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            link.Abort(AbortReason.SendFailed);   // the receive loop reports the resulting failure
        }
        finally
        {
            if (flush is not null) await flush.ConfigureAwait(false);   // never throws; the link token is cancelled by now
        }
    }

    /// <summary>A periodic retry of the offline queue. Never throws: a 401 ends the link as revoked.</summary>
    private async Task FlushAsync(Link link)
    {
        try
        {
            await Task.Yield();   // off the monitor's tick
            await coordinator.FlushQueueAsync(link.Token).ConfigureAwait(false);
        }
        catch (UnauthorizedApiException)
        {
            link.Abort(AbortReason.Revoked);
        }
        catch (OperationCanceledException) when (link.Token.IsCancellationRequested) { }
        catch (Exception e)
        {
            Report("Could not send the queued clips.", e);
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

    /// <summary>
    /// Opens a clip from the server. One sealed under another key id means the owner reset encryption: this device's key
    /// is stale (<see cref="VaultResetSignal"/>), and nothing more is fetched with it.
    /// </summary>
    private static ClipDto Decode(ClipCodec c, ClipDto wire) =>
        wire.KeyId is { } keyId && keyId != c.KeyId ? throw new VaultResetSignal() : c.Decode(wire);

    /// <summary>
    /// Stops the engine for good on a key reset or a 426, raising the matching event once. Thread-safe: the coordinator
    /// calls it from whichever thread hit the reset.
    /// </summary>
    private void Halt(Outcome why)
    {
        if (Interlocked.CompareExchange(ref _halted, (int)why, 0) != 0) return;
        Raise(why == Outcome.KeyReset ? KeyReset : UpdateRequired, EventArgs.Empty);
        if (_link is { } link) link.Abort(AbortReason.Halted);
        else Cancel(_attempt);
        Cancel(_wait);
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>The server's vault key is not this device's any more (a <c>vault.reset</c> message, or a foreign key id).</summary>
    private sealed class VaultResetSignal() : Exception("Encryption was reset.");

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
