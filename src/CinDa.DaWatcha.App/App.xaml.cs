using System.Threading;
using System.Windows;
using System.Diagnostics.CodeAnalysis;

namespace CinDa.DaWatcha.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields " +
    "should be disposable", Justification =
    "WPF owns the application lifecycle; the mutex is disposed in OnExit.")]
public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\CinDa-DaWatcha",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "CinDa-DaWatcha is already running. Only one instance may " +
                "monitor and deliver handoffs.",
                "CinDa-DaWatcha already running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); }
            catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
