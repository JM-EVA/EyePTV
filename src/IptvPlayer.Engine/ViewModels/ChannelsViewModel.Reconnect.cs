namespace IptvPlayer.Engine.ViewModels;

/// <summary>
/// Automatisch opnieuw verbinden bij een zender (live): als de verbinding wegvalt, de uitzending stopt of het beeld vastloopt,
/// probeert de app dezelfde zender opnieuw, met steeds iets meer wachttijd ertussen.
/// </summary>
public sealed partial class ChannelsViewModel
{
    /// <summary>Zo lang zonder levensteken van de speler geldt een live zender als vastgelopen.</summary>
    internal static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(12);

    /// <summary>Zo lang mag het eerste beeld op zich laten wachten voordat we opnieuw proberen.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Na zo veel pogingen achter elkaar geven we het op (ongeveer een kwartier).</summary>
    public const int MaxReconnects = 30;

    private static readonly int[] BackoffSeconds = { 2, 4, 8, 15, 30 };

    private DateTime _liveSignalAt;
    private DateTime _liveStartedAt;
    private DateTime _lastReconnectAt;
    private DateTime? _reconnectDueAt;
    private int _reconnectAttempt;
    private bool _reconnecting;
    private bool _reconnectGaveUp;

    /// <summary>Er staat een nieuwe poging gepland of er is net een poging gedaan.</summary>
    public bool IsReconnecting => _reconnectDueAt is not null || _reconnectAttempt > 0;

    /// <summary>De speler is bij een live zender begonnen (een nieuwe keuze, of een nieuwe poging).</summary>
    private void OnLivePlayStarted()
    {
        var now = _utcNow();
        _liveStartedAt = now;
        _liveSignalAt = now;
        _reconnectDueAt = null;
        if (_reconnecting) return;

        _reconnectAttempt = 0;
        _reconnectGaveUp = false;
        Raise(nameof(IsReconnecting));
    }

    private void ResetReconnect()
    {
        _reconnectDueAt = null;
        _reconnectAttempt = 0;
        _reconnectGaveUp = false;
        Raise(nameof(IsReconnecting));
    }

    /// <summary>De speler meldt een levensteken (op de UI-thread); na een poging die blijft werken is de storing voorbij.</summary>
    private void LiveSignal()
    {
        var now = _utcNow();
        _liveSignalAt = now;
        if (_reconnectAttempt > 0 && now - _lastReconnectAt >= TimeSpan.FromSeconds(10) && Current is { } row)
        {
            _reconnectAttempt = 0;
            StatusText = row.Name;
            Raise(nameof(IsReconnecting));
        }
    }

    private void OnPlaybackError(string message)
    {
        if (_mode == PlayMode.Live && Current is not null)
        {
            if (!_reconnectGaveUp && !TryFailover("Afspelen mislukt")) ScheduleReconnect("Afspelen mislukt", immediate: true);
            return;
        }
        StatusText = message;
    }

    /// <param name="immediate">De eerste poging meteen doen (de stream is dan al weg, wachten helpt niet); anders bij de eerstvolgende controle.</param>
    private void ScheduleReconnect(string reason, bool immediate = false)
    {
        if (_reconnectDueAt is not null || _reconnectGaveUp) return;

        var delay = _reconnectAttempt == 0 ? 0 : BackoffSeconds[Math.Min(_reconnectAttempt - 1, BackoffSeconds.Length - 1)];
        _reconnectDueAt = _utcNow().AddSeconds(delay);
        StatusText = _reconnectAttempt == 0
            ? $"{reason}. Opnieuw verbinden…"
            : $"{reason}. Opnieuw proberen over {delay} s… (poging {_reconnectAttempt + 1})";
        Raise(nameof(IsReconnecting));

        if (immediate && delay == 0) Reconnect();
    }

    /// <summary>
    /// Elke paar seconden aanroepen: verbindt een live zender opnieuw als die vastloopt of als er een nieuwe poging gepland staat.
    /// </summary>
    public void CheckConnection()
    {
        if (_mode != PlayMode.Live || Current is null || _reconnectGaveUp) return;
        var now = _utcNow();

        if (_reconnectDueAt is { } due)
        {
            if (now >= due) Reconnect();
            return;
        }

        if (FailoverOnStartTimeout(now)) return;

        var quiet = _videoStarted ? now - _liveSignalAt > StallAfter : now - _liveStartedAt > StartTimeout;
        if (quiet) ScheduleReconnect(_videoStarted ? "De zender reageert niet meer" : "De zender start niet");
    }

    private void Reconnect()
    {
        _reconnectDueAt = null;
        if (Current is not { IsLive: true } row) return;

        if (_reconnectAttempt >= MaxReconnects)
        {
            _reconnectGaveUp = true;
            StatusText = $"Opnieuw verbinden met {row.Name} lukt niet. Kies de zender opnieuw als de storing voorbij is.";
            Raise(nameof(IsReconnecting));
            return;
        }

        _reconnectAttempt++;
        _lastReconnectAt = _utcNow();
        _reconnecting = true;
        try { Play(row); }
        finally { _reconnecting = false; }

        StatusText = $"Opnieuw verbinden met {row.Name}… (poging {_reconnectAttempt})";
        Raise(nameof(IsReconnecting));
    }
}
