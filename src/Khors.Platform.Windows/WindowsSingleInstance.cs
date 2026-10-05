namespace Khors.Platform.Windows;

/// <summary>
/// Именованные Mutex и событие в пространстве сессии пользователя (<c>Local\</c>). Mutex не захватывается:
/// «первый» — тот, кто его создал; объект исчезает, когда закрыт последний дескриптор. Так нет привязки
/// к потоку и брошенных (abandoned) Mutex после аварийного завершения.
/// </summary>
public sealed class WindowsSingleInstance : ISingleInstance
{
    private const string MutexName = @"Local\KHORS-Desktop";
    private const string ActivateEventName = @"Local\KHORS-Desktop-Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly ManualResetEvent _stop = new(initialState: false);
    private readonly Thread? _listener;

    public WindowsSingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        IsFirst = createdNew;

        if (IsFirst)
        {
            _activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);
            _listener = new Thread(Listen) { IsBackground = true, Name = "KHORS single instance" };
            _listener.Start();
        }
    }

    public event EventHandler? ActivationRequested;

    public bool IsFirst { get; }

    public void SignalFirstInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
        {
            using (activate)
            {
                activate.Set();
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _activate?.Dispose();
        _mutex.Dispose();
        _stop.Dispose();
    }

    private void Listen()
    {
        WaitHandle[] handles = [_activate!, _stop];
        while (WaitHandle.WaitAny(handles) == 0)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
