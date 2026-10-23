using Microsoft.UI.Xaml;
namespace KClevoPanel.App;
public partial class App : Application
{
    private Window? _window;
    private SingleInstance? _instance;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        _instance = new SingleInstance(arguments.Contains("--smoke-test"));
        if (!_instance.IsPrimary)
        {
            if (!arguments.Contains("--startup")) _instance.Signal();
            _instance.Dispose(); _instance = null; Exit(); return;
        }
        var window = new MainWindow(); _window = window;
        _instance.Listen(() => window.DispatcherQueue.TryEnqueue(window.ActivateExisting));
        window.Closed += (_, _) => { _instance?.Dispose(); _instance = null; };
        _window.Activate();
    }
}
