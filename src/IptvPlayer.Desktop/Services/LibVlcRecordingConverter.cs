using System.IO;
using IptvPlayer.Engine;
using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace IptvPlayer.Desktop.Services;

/// <summary>Verpakt een opname (.ts) als .mp4 met LibVLC, zonder beeld of geluid opnieuw te coderen.</summary>
public sealed class LibVlcRecordingConverter : IRecordingConverter
{
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    private readonly LibVLC _libVlc;

    public LibVlcRecordingConverter(LibVLC libVlc) => _libVlc = libVlc;

    public async Task<bool> ConvertAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        // Voorwaartse streepjes: binnen de aanhalingstekens van de sout-keten zijn backslashes een ontsnappingsteken.
        var dst = Path.GetFullPath(targetPath).Replace('\\', '/');

        using var media = new Media(_libVlc, new Uri(Path.GetFullPath(sourcePath)));
        media.AddOption($":sout=#std{{access=file,mux=mp4,dst=\"{dst}\"}}");
        media.AddOption(":no-sout-all");
        media.AddOption(":sout-keep");

        using var player = new VlcPlayer(media);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => ended.TrySetResult(true);
        player.EncounteredError += (_, _) => ended.TrySetResult(false);

        if (!player.Play()) return false;

        var ok = false;
        long lastTime = -1;
        var lastMove = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var done = await Task.WhenAny(ended.Task, Task.Delay(500, ct));
            if (done == ended.Task) { ok = ended.Task.Result; break; }

            // Staat het stil (bijvoorbeeld een kapot bestand), dan geven we het op.
            var time = player.Time;
            if (time != lastTime) { lastTime = time; lastMove = DateTime.UtcNow; }
            else if (DateTime.UtcNow - lastMove > StallLimit) break;
        }

        // Stoppen sluit het .mp4-bestand af (daar komt de index achteraan); pas daarna is het afspeelbaar.
        await Task.Run(player.Stop);
        return ok;
    }
}
