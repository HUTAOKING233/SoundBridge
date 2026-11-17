using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SoundBridge.Core;

/// <summary>Manages the Windows A2DP sink connection for each audio source.</summary>
public sealed class AudioPlaybackReceiver(Action<string>? log = null, Func<bool>? autoWakeEnabled = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<ConnectionUpdate>? ConnectionChanged;

    public async Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);

        DeviceInformationCollection infos =
            await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector()).AsTask(cancellationToken);

        var devices = new List<AudioDevice>(infos.Count);
        foreach (DeviceInformation info in infos)
        {
            string name = string.IsNullOrWhiteSpace(info.Name) ? info.Id : info.Name;
            devices.Add(new AudioDevice(info.Id, name));
            log?.Invoke($"A2DP [{DeviceTag(info.Id)}]: discovered {info.Name}");
        }

        return devices;
    }

    public ConnectionState GetConnectionState(string deviceId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(deviceId, out Session? session)
                ? session.State
                : ConnectionState.Disconnected;
        }
    }

    public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        string tag = DeviceTag(deviceId);
        Session? session = null;
        string step = "创建蓝牙音频连接";

        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sessions.ContainsKey(deviceId)) return;

                AudioPlaybackConnection connection = AudioPlaybackConnection.TryCreateFromId(deviceId)
                    ?? throw new InvalidOperationException("系统没有为该设备提供音频接收通道（AudioPlaybackConnection）。");
                session = new Session(this, deviceId, connection);
                _sessions.Add(deviceId, session);
                Raise(new ConnectionUpdate(deviceId, ConnectionState.Connecting, null));
            }

            log?.Invoke($"A2DP [{tag}]: opening connection");
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.ClosingToken);
            pending.Token.ThrowIfCancellationRequested();
            step = "启用蓝牙音频接收";
            await session.Connection.StartAsync().AsTask(pending.Token);
            log?.Invoke($"A2DP [{tag}]: StartAsync completed");

            pending.Token.ThrowIfCancellationRequested();
            step = "打开蓝牙音频连接";
            AudioPlaybackConnectionOpenResult result = await session.Connection.OpenAsync().AsTask(pending.Token);
            log?.Invoke($"A2DP [{tag}]: OpenAsync returned {result.Status}");
            if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                string detail = result.ExtendedError is { } error ? $"：{error.Message}" : "。";
                throw new InvalidOperationException($"连接失败（{result.Status}）{detail}");
            }

            bool recoverSound = autoWakeEnabled?.Invoke() == true;
            lock (_gate)
            {
                if (session.State != ConnectionState.Connecting || session.CloseObserved || _disposed)
                    return;

                session.State = ConnectionState.Connected;
                log?.Invoke($"A2DP [{tag}]: connected; automatic audio recovery {(recoverSound ? "on" : "off")}");
                session.EndpointSetup = Task.Run(() => A2dpCaptureEndpoint.ConfigureAsync(
                    deviceId, () => session.State == ConnectionState.Connected && !session.CloseObserved,
                    recoverSound, message => log?.Invoke($"A2DP [{tag}]: {message}"), session.ClosingToken));
                Raise(new ConnectionUpdate(deviceId, ConnectionState.Connected, null));
            }
        }
        catch (OperationCanceledException)
        {
            if (session is not null)
                await CloseSessionAsync(deviceId, session, ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            var failure = new InvalidOperationException($"{step}失败：{ex.Message}（0x{ex.HResult:X8}）", ex);
            log?.Invoke($"A2DP [{tag}]: {failure.Message}");
            if (session is not null)
            {
                await CloseSessionAsync(deviceId, session, ConnectionState.Failed, failure.Message);
            }
            else
            {
                lock (_gate)
                {
                    if (!_disposed && !_sessions.ContainsKey(deviceId))
                        Raise(new ConnectionUpdate(deviceId, ConnectionState.Failed, failure.Message));
                }
            }
            throw failure;
        }
    }

    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        cancellationToken.ThrowIfCancellationRequested();

        Session? session;
        lock (_gate) _sessions.TryGetValue(deviceId, out session);

        if (session is null) return Task.CompletedTask;

        log?.Invoke($"A2DP [{DeviceTag(deviceId)}]: user requested disconnect");
        return CloseSessionAsync(deviceId, session, ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        KeyValuePair<string, Session>[] sessions;
        lock (_gate)
        {
            _disposed = true;
            sessions = _sessions.ToArray();
        }

        await Task.WhenAll(sessions.Select(pair =>
            CloseSessionAsync(pair.Key, pair.Value, ConnectionState.Disconnected))).ConfigureAwait(false);
    }

    private Task CloseSessionAsync(string deviceId, Session session, ConnectionState finalState, string? error = null)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out Session? current) || !ReferenceEquals(current, session)
                || session.State == ConnectionState.Disconnecting)
            {
                return session.Closed.Task;
            }

            session.State = ConnectionState.Disconnecting;
            Raise(new ConnectionUpdate(deviceId, ConnectionState.Disconnecting, null));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                try
                {
                    session.CancelPendingOperations();
                    await session.EndpointSetup.ConfigureAwait(false);
                }
                finally
                {
                    session.Dispose();
                }
                log?.Invoke($"A2DP [{DeviceTag(deviceId)}]: connection released");
            }
            catch (Exception ex)
            {
                log?.Invoke($"A2DP [{DeviceTag(deviceId)}]: connection release failed: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _sessions.Remove(deviceId);
                    Raise(new ConnectionUpdate(deviceId, finalState, error));
                }
                session.Closed.TrySetResult();
            }
        });

        return session.Closed.Task;
    }

    private static string DeviceTag(string deviceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId.ToUpperInvariant())).AsSpan(0, 4));

    private void OnSessionClosed(string deviceId, Session session)
    {
        log?.Invoke($"A2DP [{DeviceTag(deviceId)}]: Windows reported Closed");
        _ = CloseSessionAsync(deviceId, session, ConnectionState.Disconnected);
    }

    // Published in the same order as session transitions. Subscribers must
    // enqueue UI updates instead of waiting on the UI thread.
    private void Raise(ConnectionUpdate update)
    {
        try
        {
            ConnectionChanged?.Invoke(update);
        }
        catch
        {
            // A subscriber cannot interrupt cleanup of the Windows connection.
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly AudioPlaybackReceiver _owner;
        private readonly string _deviceId;
        private readonly CancellationTokenSource _closing = new();
        private int _closeObserved;
        public volatile ConnectionState State = ConnectionState.Connecting;
        public AudioPlaybackConnection Connection { get; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EndpointSetup { get; set; } = Task.CompletedTask;
        public CancellationToken ClosingToken { get; }
        public bool CloseObserved => Volatile.Read(ref _closeObserved) != 0;

        public Session(AudioPlaybackReceiver owner, string deviceId, AudioPlaybackConnection connection)
        {
            _owner = owner;
            _deviceId = deviceId;
            Connection = connection;
            ClosingToken = _closing.Token;
            Connection.StateChanged += OnStateChanged;
        }

        private void OnStateChanged(AudioPlaybackConnection sender, object args)
        {
            if (State == ConnectionState.Disconnecting || CloseObserved) return;
            if (sender.State != AudioPlaybackConnectionState.Closed) return;
            if (Interlocked.Exchange(ref _closeObserved, 1) != 0) return;

            _owner.OnSessionClosed(_deviceId, this);
        }

        public void CancelPendingOperations() => _closing.Cancel();

        public void Dispose()
        {
            try
            {
                Connection.StateChanged -= OnStateChanged;
            }
            finally
            {
                try { Connection.Dispose(); }
                finally { _closing.Dispose(); }
            }
        }
    }
}
