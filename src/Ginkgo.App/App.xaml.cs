using System.Runtime;
using System.Windows;
namespace Ginkgo.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // avoid long, screen-stopping full collections while the app is in use
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        base.OnStartup(e);
    }
}
