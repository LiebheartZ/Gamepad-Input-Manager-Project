namespace GDMInputManager.Interfaces;

public interface IDeviceChangeMonitor : IDisposable
{
    event EventHandler? DevicesChanged;
    void Start();
    void Stop();
}
