using EyePTV.Mobile.Services;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace EyePTV.Mobile;

/// <summary>
/// Eén pagina met vier lagen (lijst, bronnen, keuzescherm, speler). Welke laag zichtbaar is volgt de bestaande viewmodels:
/// MainViewModel.Current (de INavigator) en ChannelsViewModel.Current (er speelt iets).
/// </summary>
public partial class MainPage : ContentPage
{
    private static readonly Color Active = Color.FromArgb("#E5132D");
    private static readonly Color Inactive = Color.FromArgb("#9A9AA5");

    private readonly MainViewModel _vm;
    private readonly MobileDialogService _dialogs;
    private SelectionViewModel? _shownSelection;
    private bool _controlsVisible = true;
    private IDispatcherTimer? _connectionTimer;
    private IDispatcherTimer? _tickTimer;

    public MainPage(MainViewModel vm, LibVlcPlayerService player, MobileDialogService dialogs)
    {
        InitializeComponent();
        _vm = vm;
        _dialogs = dialogs;

        Video.MediaPlayer = player.MediaPlayer;
        LibraryLayer.BindingContext = vm.Channels;
        SourcesLayer.BindingContext = vm.Sources;
        PlayerLayer.BindingContext = vm.Channels;

        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Current)) UpdateLayers(); };
        vm.Channels.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChannelsViewModel.Current) or nameof(ChannelsViewModel.SelectedKind)) UpdateLayers();
        };
        SizeChanged += (_, _) => UpdatePosterColumns();

        // Zoals op de pc: elke 5 s de verbinding van een live zender controleren, elke 30 s "nu bezig" bijwerken.
        _connectionTimer = Dispatcher.CreateTimer();
        _connectionTimer.Interval = TimeSpan.FromSeconds(5);
        _connectionTimer.Tick += (_, _) => vm.Channels.CheckConnection();
        _connectionTimer.Start();
        _tickTimer = Dispatcher.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(30);
        _tickTimer.Tick += (_, _) => vm.Tick();
        _tickTimer.Start();

        UpdateLayers();
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _vm.StartupAsync();
            // Het beginscherm van de pc bestaat hier niet: ga naar de lijst, of naar Bronnen als die nog leeg is.
            if (_vm.Current == _vm.Home) _vm.ShowKind(ContentKind.Live);
        }
        catch (Exception ex)
        {
            await DisplayAlert("EyePTV", ex.Message, "OK");
        }
        UpdateLayers();
    }

    // ---- Welke laag staat aan ----

    private void UpdateLayers()
    {
        if (!MainThread.IsMainThread) { MainThread.BeginInvokeOnMainThread(UpdateLayers); return; }

        var playing = _vm.Channels.Current is not null;
        var selection = _vm.Current as SelectionViewModel;
        var sources = _vm.Current == _vm.Beheer;

        if (!ReferenceEquals(selection, _shownSelection))
        {
            _shownSelection = selection;
            SelectionLayer.BindingContext = selection;
        }

        PlayerLayer.IsVisible = playing;
        NavBar.IsVisible = !playing;
        SelectionLayer.IsVisible = !playing && selection is not null;
        SourcesLayer.IsVisible = !playing && sources;
        LibraryLayer.IsVisible = !playing && selection is null && !sources;

        var kind = _vm.Channels.SelectedKind;
        LiveList.IsVisible = kind == ContentKind.Live;
        PosterList.IsVisible = kind != ContentKind.Live;

        var inLibrary = selection is null && !sources;
        NavLive.TextColor = inLibrary && kind == ContentKind.Live ? Active : Inactive;
        NavMovies.TextColor = inLibrary && kind == ContentKind.Movie ? Active : Inactive;
        NavSeries.TextColor = inLibrary && kind == ContentKind.Series ? Active : Inactive;
        NavSources.TextColor = !inLibrary ? Active : Inactive;

        if (playing) SetControls(true);
        UpdatePosterColumns();
    }

    private void UpdatePosterColumns()
    {
        if (Width <= 0 || PosterList.ItemsLayout is not GridItemsLayout grid) return;
        var span = Math.Max(2, (int)(Width / 120));
        if (grid.Span != span) grid.Span = span;
    }

    // ---- Menu ----

    private void OnNavLive(object? sender, EventArgs e) => _vm.ShowKind(ContentKind.Live);
    private void OnNavMovies(object? sender, EventArgs e) => _vm.ShowKind(ContentKind.Movie);
    private void OnNavSeries(object? sender, EventArgs e) => _vm.ShowKind(ContentKind.Series);
    private void OnNavSources(object? sender, EventArgs e) => _vm.ShowSources();

    // ---- Lijst ----

    private static void OnFavoriteTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: ChannelRow row }) row.IsFavorite = !row.IsFavorite;
    }

    // ---- Bronnen ----

    private void OnSourcePrimary(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: SourceRow row }) _vm.Sources.PrimaryActionCommand.Execute(row);
    }

    private void OnSourceSync(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: SourceRow row }) _vm.Sources.SyncOneCommand.Execute(row);
    }

    private async void OnSourceRemove(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: SourceRow row }) return;
        if (!await DisplayAlert("Bron verwijderen", $"\"{row.Name}\" en wat je ervan in je lijst hebt gekozen verwijderen?", "Verwijderen", "Annuleren"))
            return;

        // De Engine vraagt zelf ook om bevestiging (synchroon); die hebben we hierboven al gekregen.
        _dialogs.PreConfirmed = true;
        try { _vm.Sources.RemoveCommand.Execute(row); }
        finally { _dialogs.PreConfirmed = false; }
    }

    // ---- Speler ----

    private void OnPlayerTapped(object? sender, TappedEventArgs e) => SetControls(!_controlsVisible);

    private void SetControls(bool visible)
    {
        _controlsVisible = visible;
        PlayerControls.IsVisible = visible;
    }

    private void OnSeekBack(object? sender, EventArgs e) => _vm.Channels.SeekRelative(-10_000);

    private void OnSeekForward(object? sender, EventArgs e) => _vm.Channels.SeekRelative(30_000);

    private void OnSeekStarted(object? sender, EventArgs e) => _vm.Channels.BeginSeek();

    private void OnSeekCompleted(object? sender, EventArgs e) => _vm.Channels.EndSeek(SeekBar.Value);
}
