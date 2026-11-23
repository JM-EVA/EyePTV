namespace IptvPlayer.Engine.ViewModels;

/// <summary>Casten naar een tv (Chromecast / Google TV): wat je kijkt gaat verder op de tv.</summary>
public sealed partial class ChannelsViewModel
{
    private void InitCast()
    {
        _player.CastTargetsChanged += () =>
        {
            Raise(nameof(CastTargets));
            RaiseCastState(); // ook als de cast door het apparaat of de speler is beëindigd
            CastTargetsChanged?.Invoke();
        };
    }

    /// <summary>De lijst met apparaten is veranderd (voor een menu dat open staat).</summary>
    public event Action? CastTargetsChanged;

    public bool CanCast => _player.SupportsCasting;

    /// <summary>De castknop is er zodra er iets speelt.</summary>
    public bool ShowCastButton => CanCast && _current is not null;

    public IReadOnlyList<CastTarget> CastTargets => _player.CastTargets;

    public string? CastingTo => _player.CastingTo;
    public bool IsCasting => CastingTo is not null;

    /// <summary>Op de pc staat een bericht in plaats van het beeld, want het beeld staat op de tv.</summary>
    public bool ShowCastCover => IsCasting && _current is not null && _videoStarted;

    public string CastCoverText => IsCasting ? $"Je kijkt op {CastingTo}" : "";

    public string CastTooltip => IsCasting ? $"Gecast naar {CastingTo}" : "Casten naar een tv";

    /// <summary>Begint met zoeken naar apparaten (alleen als de gebruiker de castknop gebruikt).</summary>
    public void StartCastDiscovery() => _player.StartCastDiscovery();

    /// <summary>Cast naar dit apparaat, of (null) kijk weer op de pc. Wat speelt, gaat verder op het andere scherm.</summary>
    public void CastTo(string? id)
    {
        var name = id is null ? null : CastTargets.FirstOrDefault(t => t.Id == id)?.Name;
        if (!_player.SetCastTarget(id))
        {
            StatusText = id is null
                ? "Terug naar deze pc lukt niet."
                : "Casten lukt niet. Controleer of de tv aan staat en op hetzelfde netwerk zit.";
            return;
        }

        // De speler start opnieuw op het andere scherm: het laadscherm tot het eerste beeld.
        if (_current is not null) SetVideoStarted(false);
        RaiseCastState();
        StatusText = id is null ? "Je kijkt weer op deze pc." : $"Casten naar {name ?? id}…";
        PreferHdWhenCasting();
    }

    private void RaiseCastState()
    {
        Raise(nameof(IsCasting));
        Raise(nameof(CastingTo));
        Raise(nameof(CastTooltip));
        Raise(nameof(CastCoverText));
        Raise(nameof(ShowCastCover));
        Raise(nameof(ShowVideo));
        Raise(nameof(ShowCover));
        Raise(nameof(ShowCastButton));
    }
}
