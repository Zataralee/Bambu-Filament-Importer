namespace BambuFilamentImporter.Services;

public sealed class BambuProcessMonitor : IDisposable
{
    private readonly Func<bool> _probe;
    private readonly TimeSpan _interval;
    private readonly object _stateLock = new();
    private Timer? _timer;
    private bool? _lastState;
    private int _polling;

    public event Action<bool>? StateChanged;

    public BambuProcessMonitor(Func<bool>? probe = null, TimeSpan? interval = null)
    {
        _probe = probe ?? BambuProcess.IsStudioRunning;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public void Start(bool? initialState = null)
    {
        lock (_stateLock)
        {
            if (_timer is not null)
            {
                return;
            }

            _lastState = initialState;
            _timer = new Timer(Poll, null, TimeSpan.Zero, _interval);
        }
    }

    private void Poll(object? state)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var isRunning = _probe();
            Action<bool>? handler = null;
            lock (_stateLock)
            {
                if (_lastState != isRunning)
                {
                    _lastState = isRunning;
                    handler = StateChanged;
                }
            }

            handler?.Invoke(isRunning);
        }
        catch
        {
            // A transient process-enumeration failure must not stop future checks.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_stateLock)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }
}
