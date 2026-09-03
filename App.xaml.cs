using System.Windows;
using System.Windows.Threading;
namespace BlenderRenderHeadless;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += HandleUiException;
        base.OnStartup(e);
    }

    private static void HandleUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"The interface encountered an error but the app can remain open.\n\n{e.Exception.Message}",
            "Blender Render", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
