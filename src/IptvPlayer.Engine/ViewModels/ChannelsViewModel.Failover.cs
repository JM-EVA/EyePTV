namespace IptvPlayer.Engine.ViewModels;

/// <summary>
/// Reservezenders: valt een live zender uit of start hij niet binnen <see cref="FailoverWait"/>, dan schakelt de app naar de
/// eerstvolgende reservezender die in deze afspeelpoging nog niet is geprobeerd. Een zelf gekozen zender begint weer bij de hoofdzender.
/// </summary>
public sealed partial class ChannelsViewModel
{
    private PersonalChannel? _failoverMain;
    private readonly HashSet<string> _failoverTried = new();
    private bool _failingOver;

    /// <summary>Zo lang wachten op beeld voordat we naar een reservezender gaan (uit de voorkeuren, standaard 10 s).</summary>
    internal TimeSpan FailoverWait => TimeSpan.FromSeconds(Math.Max(1, _settings?.Current.FailoverSeconds ?? 10));

    /// <summary>Een nieuwe afspeelpoging door de gebruiker begint weer bij de hoofdzender (herverbinden en overschakelen niet).</summary>
    private void ResetFailover(PersonalChannel main)
    {
        if (_failingOver || _reconnecting) return;
        _failoverMain = main;
        _failoverTried.Clear();
        _failoverTried.Add(main.Id);
    }

    /// <summary>Probeert de volgende reservezender. False: deze zender heeft geen reservezenders, laat de gewone herverbinding het doen.</summary>
    private bool TryFailover(string reason)
    {
        if (_failoverMain is not { } main || _mode != PlayMode.Live) return false;
        var backups = _library.BackupsOf(main);
        if (backups.Count == 0) return false;

        var next = backups.FirstOrDefault(b => !_failoverTried.Contains(b.Id) && PlayableUrl.IsAllowed(b.Url));
        if (next is null)
        {
            _reconnectGaveUp = true;
            _reconnectDueAt = null;
            StatusText = $"{main.DisplayName} en {(backups.Count == 1 ? "de reservezender" : "alle reservezenders")} werken niet. Kies de zender opnieuw om het nog eens te proberen.";
            Raise(nameof(IsReconnecting));
            return true;
        }

        _failoverTried.Add(next.Id);
        _failingOver = true;
        try { Play(new ChannelRow(this, next)); }
        finally { _failingOver = false; }
        StatusText = $"{reason}. Overgeschakeld naar {next.DisplayName}";
        return true;
    }

    /// <summary>De live zender heeft nog geen beeld en de wachttijd is om.</summary>
    private bool FailoverOnStartTimeout(DateTime now) =>
        !_videoStarted && now - _liveStartedAt > FailoverWait && TryFailover("De zender start niet");

    internal void SetBackups(ChannelRow row)
    {
        var key = ChannelKey(row.Model.Name);
        var options = _library.Ordered()
            .Where(p => p.Kind == ContentKind.Live && p.Id != row.Model.Id && !_lock.Hides(p))
            .Select(p => new ChannelChoice(p.Id, p.DisplayName, p.Group, key.Length > 0 && ChannelKey(p.Name) == key))
            .OrderByDescending(o => o.Suggested)
            .ToList();
        if (options.Count == 0)
        {
            _dialogs.ShowInfo("Reservezender instellen", "Er staan geen andere zenders in je lijst om als reserve te gebruiken.");
            return;
        }

        var chosen = _dialogs.PickChannels("Reservezender instellen",
            $"Welke zenders neemt {row.Name} over als deze niet werkt? Ze worden in de volgorde van aanvinken geprobeerd. Zenders met dezelfde naam staan bovenaan.",
            options, row.Model.BackupIds);
        if (chosen is null) return;
        _library.SetBackups(row.Model, chosen);
        row.BackupsChanged();
    }

    internal void ClearBackups(ChannelRow row)
    {
        _library.SetBackups(row.Model, Array.Empty<string>());
        row.BackupsChanged();
    }
}
