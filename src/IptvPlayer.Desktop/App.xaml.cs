using System.IO;
using System.Windows;
using IptvPlayer.Desktop.Services;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop;

public partial class App : Application
{
    public LibVlcPlayerService? Player { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Een onverwachte fout mag de app niet stilletjes laten verdwijnen.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            MessageBox.Show(args.Exception.Message, "EyePTV: onverwachte fout", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var http = HttpFactory.Create();
            // Wachtwoorden en inlog-links worden versleuteld opgeslagen; de sleutel is beschermd met Windows DPAPI.
            var store = new AppStore(cipher: FileCipher.Open(AppStore.DefaultRoot, new DpapiKeyProtector()));
            var library = new Library(store);
            var epg = new EpgService(http, store);

            Player = new LibVlcPlayerService();
            var thumbnails = new LibVlcThumbnailService(Player.LibVlc, LibVlcThumbnailService.DefaultFolder);
            var vm = new MainViewModel(library, new CatalogService(http), epg, Player, new WpfDialogService(), thumbnails: thumbnails);

            var window = new Desktop.MainWindow(vm);
            MainWindow = window;
            Player.LocalFileAllowed = url => vm.Recordings.OwnsUrl(url) || vm.Downloads.OwnsUrl(url);
            vm.Recordings.Converter = new LibVlcRecordingConverter(Player.LibVlc);
            window.Show();

            _ = StartAsync(vm);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "De app kon niet starten:\n\n" + ex.Message +
                "\n\nControleer of de LibVLC-bestanden naast de app staan (map 'libvlc').",
                "EyePTV", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Bewaart de foutmelding en de plek waar het misging in error.log (handig bij het zoeken naar de oorzaak).</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var path = Path.Combine(AppStore.DefaultRoot, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task StartAsync(MainViewModel vm)
    {
        try { await vm.StartupAsync(); }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(ex.Message, "EyePTV", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Player?.Dispose();
        base.OnExit(e);
    }
}
