using System.Security.Principal;

namespace KClevoPanel.App;

// Kernel object lifetime follows the process. Never kill another process by its file name.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _wait;
    public bool IsPrimary { get; }
    public SingleInstance(bool isolated)
    {
        string name = @"Local\KClevoPanel." + WindowsIdentity.GetCurrent().User!.Value + (isolated ? ".Smoke" : "");
        _activate = new(false, EventResetMode.AutoReset, name + ".Activate");
        _mutex = new(false, name + ".Instance");
        try { IsPrimary = _mutex.WaitOne(0); } catch (AbandonedMutexException) { IsPrimary = true; }
    }
    public void Signal() => _activate.Set();
    public void Listen(Action action) => _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => action(), null, Timeout.Infinite, false);
    public void Dispose() { _wait?.Unregister(null); if (IsPrimary) _mutex.ReleaseMutex(); _mutex.Dispose(); _activate.Dispose(); }
}
