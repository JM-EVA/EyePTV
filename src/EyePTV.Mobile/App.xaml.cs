using EyePTV.Mobile.Services;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace EyePTV.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;

        try
        {
            var http = HttpFactory.Create();
            // Wachtwoorden en inlog-links staan versleuteld; de sleutel zit in de iOS-keychain (SecureStorage).
            var root = FileSystem.AppDataDirectory;
            var store = new AppStore(root, FileCipher.Open(root, new KeychainKeyProtector(root)));
            var library = new Library(store);
            var epg = new EpgService(http, store);

            var player = new LibVlcPlayerService();
            var dialogs = new MobileDialogService();
            var vm = new MainViewModel(library, new CatalogService(http), epg, player, dialogs);
            MainPage = new MainPage(vm, player, dialogs);
        }
        catch (Exception ex)
        {
            // Liever een leesbare melding dan een app die direct sluit.
            MainPage = new ContentPage
            {
                BackgroundColor = Color.FromArgb("#0E0E10"),
                Content = new ScrollView
                {
                    Padding = 24,
                    Content = new Label { Text = "EyePTV kon niet starten:\n\n" + ex, TextColor = Colors.White }
                }
            };
        }
    }
}
