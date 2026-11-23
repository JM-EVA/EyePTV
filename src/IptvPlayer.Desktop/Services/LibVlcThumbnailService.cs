using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IptvPlayer.Engine.ViewModels;
using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace IptvPlayer.Desktop.Services;

/// <summary>
/// Maakt een momentopname van een live-zender: opent de stream zonder geluid en zonder venster (LibVLC levert de beeldjes
/// aan in het geheugen), bewaart één beeld als jpg en sluit de stream weer. De bestanden staan in %APPDATA%\IptvPlayer\previews.
/// </summary>
public sealed class LibVlcThumbnailService : IThumbnailService
{
    private const int Width = 480;
    private const int Height = 270;

    private readonly LibVLC _libVlc;
    private readonly string _folder;

    public LibVlcThumbnailService(LibVLC libVlc, string folder)
    {
        _libVlc = libVlc;
        _folder = folder;
        Cleanup();
    }

    /// <summary>De map met de bewaarde beelden (de converter laadt alleen bestanden uit deze map).</summary>
    public static string DefaultFolder => Path.Combine(IptvPlayer.Engine.AppStore.DefaultRoot, "previews");

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();

    public async Task<string?> CaptureAsync(string key, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var pixels = await Task.Run(() => GrabFrame(url, timeout.Token), timeout.Token);
            if (pixels is null) return null;
            return await Task.Run(() => Save(key, pixels), CancellationToken.None);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private string Save(string key, byte[] pixels)
    {
        Directory.CreateDirectory(_folder);
        var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, pixels, Width * 4);
        source.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(source));

        var hash = Hash(key);
        var path = Path.Combine(_folder, $"{hash}-{DateTime.UtcNow.Ticks}.jpg");
        using (var fs = File.Create(path)) encoder.Save(fs);

        // Oudere beelden van dezelfde zender opruimen.
        foreach (var old in Directory.EnumerateFiles(_folder, hash + "-*.jpg"))
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>Beelden uit een eerdere sessie worden nooit hergebruikt: bij het starten gaat de map leeg.</summary>
    private void Cleanup()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (var f in new DirectoryInfo(_folder).EnumerateFiles("*.jpg")) f.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Speelt de stream stil af en geeft de pixels (BGRA) van een beeld terug nadat de stream is opgestart.</summary>
    private byte[]? GrabFrame(string url, CancellationToken ct)
    {
        var size = Width * Height * 4;
        var buffer = Marshal.AllocHGlobal(size);
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Stopwatch();
        var frames = 0;
        var done = 0;

        // De delegates moeten blijven leven zolang de speler draait.
        VlcPlayer.LibVLCVideoLockCb lockCb = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        VlcPlayer.LibVLCVideoUnlockCb unlockCb = (_, _, _) => { };
        VlcPlayer.LibVLCVideoDisplayCb displayCb = (_, _) =>
        {
            if (Volatile.Read(ref done) != 0) return;
            if (!clock.IsRunning) clock.Start();
            frames++;

            // De eerste beeldjes zijn vaak grijs of zwart; wacht even en neem dan het eerste beeld met inhoud.
            if (frames < 4 || clock.ElapsedMilliseconds < 500) return;
            var copy = new byte[size];
            Marshal.Copy(buffer, copy, 0, size);
            if (IsMostlyBlack(copy) && clock.ElapsedMilliseconds < 4000) return;

            if (Interlocked.Exchange(ref done, 1) == 0) result.TrySetResult(copy);
        };

        VlcPlayer? player = null;
        try
        {
            using var media = new Media(_libVlc, url, FromType.FromLocation);
            media.AddOption(":no-audio");
            media.AddOption(":network-caching=500");

            player = new VlcPlayer(_libVlc);
            player.SetVideoFormat("RV32", Width, Height, Width * 4);
            player.SetVideoCallbacks(lockCb, unlockCb, displayCb);
            player.EncounteredError += (_, _) => result.TrySetResult(null);

            using var registration = ct.Register(() => result.TrySetResult(null));
            player.Play(media);

            return result.Task.GetAwaiter().GetResult();
        }
        finally
        {
            Volatile.Write(ref done, 1);
            if (player is not null)
            {
                try { player.Stop(); } catch (Exception ex) when (ex is InvalidOperationException or AccessViolationException) { }
                player.Dispose();
            }

            GC.KeepAlive(lockCb);
            GC.KeepAlive(unlockCb);
            GC.KeepAlive(displayCb);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsMostlyBlack(byte[] bgra)
    {
        long sum = 0;
        var n = 0;
        for (var i = 0; i < bgra.Length; i += 4 * 97) // een steekproef is genoeg
        {
            sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
            n++;
        }

        return n == 0 || sum / (n * 3.0) < 8;
    }
}
