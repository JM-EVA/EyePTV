using IptvPlayer.Engine.ViewModels;

namespace EyePTV.Mobile.Services;

/// <summary>
/// De dialogen van de Engine zijn synchroon, die van iOS niet. Meldingen worden daarom los getoond. Een bevestiging vraagt de
/// pagina zelf eerst aan de gebruiker (DisplayAlert) en zet dan <see cref="PreConfirmed"/> aan voor de opdracht die erop volgt.
/// </summary>
public sealed class MobileDialogService : IDialogService
{
    /// <summary>De gebruiker heeft de bevestiging al gegeven (door de pagina gevraagd).</summary>
    public bool PreConfirmed { get; set; }

    public ExitChoice AskExit(string message) => ExitChoice.Quit;

    public void ShowInfo(string title, string message) => Show(title, message);

    public void ShowError(string title, string message) => Show(title, message);

    public bool Confirm(string title, string message) => PreConfirmed;

    // Naam wijzigen en reservezenders zitten niet in deze versie.
    public string? AskText(string title, string message, string initial) => null;

    public IReadOnlyList<string>? PickChannels(string title, string message, IReadOnlyList<ChannelChoice> options, IReadOnlyList<string> selected) => null;

    private static void Show(string title, string message) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = Application.Current?.MainPage;
            if (page is not null) await page.DisplayAlert(title, message, "OK");
        });
}
