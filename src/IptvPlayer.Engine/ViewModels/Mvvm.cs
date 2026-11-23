using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Command voor async acties: voorkomt dubbel starten en geeft fouten door aan een handler in plaats van de app te laten crashen.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception> _onError;
    private bool _running;

    public AsyncCommand(Func<object?, Task> execute, Action<Exception> onError, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    public AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
        : this(_ => execute(), onError, canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    /// <summary>Voor tests: wacht tot de actie klaar is.</summary>
    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await _execute(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _onError(ex); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Een audio- of ondertitelspoor zoals de speler het meldt.</summary>
public sealed record TrackInfo(int Id, string Name);

/// <summary>Maakt kleine momentopnames van live-zenders (voor de kaarten op het beginscherm).</summary>
public interface IThumbnailService
{
    /// <summary>Opent de stream even, bewaart één beeld en geeft het pad terug (null als het niet lukte).</summary>
    Task<string?> CaptureAsync(string key, string url, CancellationToken ct);
}

/// <summary>De videospeler (in de app: LibVLC). Als interface zodat de logica zonder speler getest kan worden.</summary>
public interface IPlayerService
{
    /// <param name="startMs">Begin op dit punt (voor "verder kijken"); 0 = vanaf het begin.</param>
    void Play(string url, string title, long startMs = 0);
    void Stop();
    int Volume { get; set; }
    bool IsMuted { get; set; }

    bool IsPaused { get; }
    void TogglePause();

    /// <summary>Huidige positie en lengte in milliseconden (lengte is 0 bij live-tv).</summary>
    long TimeMs { get; }
    long LengthMs { get; }
    bool IsSeekable { get; }
    void SeekTo(long ms);

    // ---- Audio- en ondertitelsporen (alleen betekenisvol bij films en afleveringen) ----

    /// <summary>Audiosporen in de video; een spoor met Id &lt; 0 is "uit" en telt niet mee.</summary>
    IReadOnlyList<TrackInfo> AudioTracks => Array.Empty<TrackInfo>();

    /// <summary>Ondertitelsporen die in de video zitten of er later bij zijn gedaan.</summary>
    IReadOnlyList<TrackInfo> SubtitleTracks => Array.Empty<TrackInfo>();

    int CurrentAudioTrack => -1;

    /// <summary>-1 = ondertiteling uit.</summary>
    int CurrentSubtitleTrack => -1;

    void SetAudioTrack(int id) { }

    /// <param name="id">Spoor-id, of -1 om de ondertiteling uit te zetten.</param>
    void SetSubtitleTrack(int id) { }

    /// <summary>De lijst met audio- of ondertitelsporen is veranderd (op de UI-thread).</summary>
    event Action? TracksChanged;

    /// <summary>Wordt een paar keer per seconde op de UI-thread aangeroepen terwijl er iets speelt.</summary>
    event Action? PositionChanged;

    /// <summary>Het item is uitgespeeld (op de UI-thread).</summary>
    event Action? PlaybackEnded;

    /// <summary>Wordt op de UI-thread aangeroepen als afspelen mislukt.</summary>
    event Action<string>? PlaybackError;

    // ---- Casten naar een tv ----

    /// <summary>De speler kan naar een apparaat in het netwerk casten (Chromecast / Google TV).</summary>
    bool SupportsCasting => false;

    /// <summary>Begint met zoeken naar apparaten (gebeurt pas als je de castknop gebruikt, om niets onnodig op het netwerk te doen).</summary>
    void StartCastDiscovery() { }

    /// <summary>Apparaten die beeld kunnen ontvangen.</summary>
    IReadOnlyList<CastTarget> CastTargets => Array.Empty<CastTarget>();

    /// <summary>De lijst met apparaten is veranderd (op de UI-thread).</summary>
    event Action? CastTargetsChanged { add { } remove { } }

    /// <summary>De naam van het apparaat waar nu naartoe gecast wordt, of null.</summary>
    string? CastingTo => null;

    /// <summary>Cast naar dit apparaat (of null: weer op deze pc kijken). Wat speelt, gaat verder op het nieuwe scherm.</summary>
    bool SetCastTarget(string? id) => false;
}

/// <summary>Een apparaat waar naartoe gecast kan worden. <paramref name="Kind"/> is bijvoorbeeld "Chromecast".</summary>
public sealed record CastTarget(string Id, string Name, string Kind = "");

/// <summary>Wat de gebruiker kiest als er bij het afsluiten nog opnames lopen of gepland staan.</summary>
public enum ExitChoice
{
    /// <summary>Echt afsluiten.</summary>
    Quit,
    /// <summary>Alleen het venster minimaliseren; de opnames lopen door.</summary>
    Minimize,
    /// <summary>Niet afsluiten, terug naar de app.</summary>
    Stay
}

public interface IDialogService
{
    /// <summary>Waarschuwt dat er opnames lopen of gepland staan en vraagt wat er moet gebeuren.</summary>
    ExitChoice AskExit(string message);

    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    bool Confirm(string title, string message);

    /// <summary>Vraagt om een stukje tekst. Geeft null terug als de gebruiker annuleert.</summary>
    string? AskText(string title, string message, string initial);

    /// <summary>Laat zenders aanvinken. Geeft de gekozen id's in volgorde van aanvinken terug, of null bij annuleren.</summary>
    IReadOnlyList<string>? PickChannels(string title, string message, IReadOnlyList<ChannelChoice> options, IReadOnlyList<string> selected);
}

/// <summary>Een zender in een keuzelijst. <paramref name="Suggested"/>: heeft (bijna) dezelfde naam als de zender waar het om gaat.</summary>
public sealed record ChannelChoice(string Id, string Name, string Detail, bool Suggested);

public interface INavigator
{
    void ShowChannels();
    void ShowSources();
    void ShowSelection(SelectionViewModel selection);
}
