using System.Security.Principal;

namespace TrafficLens;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private EventWaitHandle? restore;
    private RegisteredWaitHandle? listener;
    private bool owns;
    internal SingleInstance()
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        mutex = new Mutex(false, @"Local\TrafficLens.Clash." + user);
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (owns) restore = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TrafficLens.Restore." + user);
    }
    internal bool IsPrimary => owns;
    internal void SignalExisting(bool background)
    {
        if (background) return;
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        try { using var signal = EventWaitHandle.OpenExisting(@"Local\TrafficLens.Restore." + user); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { } // Primary may be finishing startup/shutdown.
    }
    internal void Attach(Form form)
    {
        if (restore == null || listener != null) return;
        listener = ThreadPool.RegisterWaitForSingleObject(restore, (_, _) =>
        {
            try
            {
                if (!form.IsDisposed && form.IsHandleCreated)
                    form.BeginInvoke((Action)(() => { if (!form.IsDisposed) TrayApplication.ShowWindow(form); }));
            }
            catch (InvalidOperationException) { }
        }, null, Timeout.Infinite, false);
    }
    public void Dispose()
    {
        listener?.Unregister(null); restore?.Dispose();
        if (owns) { mutex.ReleaseMutex(); owns = false; }
        mutex.Dispose();
    }
}
