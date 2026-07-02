using System.Threading;
using System.Windows;

namespace AudioVisualizer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\AudioVisualizer.SingleInstance";

    private static Mutex? _singleInstanceMutex;
    private static bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }

    public static void ReleaseSingleInstanceMutex()
    {
        if (!_ownsSingleInstanceMutex || _singleInstanceMutex is null)
        {
            return;
        }

        _singleInstanceMutex.ReleaseMutex();
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
    }
}
