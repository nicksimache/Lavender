using System.Configuration;
using System.Data;
using System.Windows;

namespace Lavender.App
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private ExternalOpenChannel? _navigation;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                var request = ExternalOpenRequest.Parse(e.Args);
                _navigation = new ExternalOpenChannel();
                if (!_navigation.TryOwn())
                {
                    await ExternalOpenChannel.SendAsync(e.Args);
                    Shutdown();
                    return;
                }
                var window = new MainWindow();
                MainWindow = window;
                window.QueueExternalOpen(request);
                _ = _navigation.ListenAsync(open => Dispatcher.BeginInvoke(new Action(() => window.QueueExternalOpen(open))));
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Cannot open Lavender", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _navigation?.Dispose();
            base.OnExit(e);
        }
    }

}
