using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace IptvPlayer.Desktop.Services;

/// <summary>
/// Laat een zender zonder geluid live meekijken in een klein plaatje (de kaart op het beginscherm als je hem aanwijst).
/// LibVLC levert de beeldjes aan in het geheugen, zodat er geen apart videovenster nodig is.
/// </summary>
public sealed class LiveHoverStream : IDisposable
{
    private const int Width = 480;
    private const int Height = 270;
    private const int Size = Width * Height * 4;

    private readonly Dispatcher _dispatcher;
    private readonly WriteableBitmap _bitmap = new(Width, Height, 96, 96, PixelFormats.Bgr32, null);
    private readonly IntPtr _buffer = Marshal.AllocHGlobal(Size);
    private readonly VlcPlayer _player;
    private readonly Media _media;
    private readonly VlcPlayer.LibVLCVideoLockCb _lock;
    private readonly VlcPlayer.LibVLCVideoUnlockCb _unlock;
    private readonly VlcPlayer.LibVLCVideoDisplayCb _display;
    private int _pending;
    private int _disposed;

    /// <summary>Het plaatje dat steeds ververst wordt; zet dit als bron van een Image.</summary>
    public ImageSource Image => _bitmap;

    /// <summary>Er is een eerste beeld binnen.</summary>
    public event Action? FirstFrame;

    public LiveHoverStream(LibVLC libVlc, string url)
    {
        _dispatcher = Application.Current.Dispatcher;
        _lock = (_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; };
        _unlock = (_, _, _) => { };
        _display = (_, _) =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.Exchange(ref _pending, 1) != 0) return; // het vorige beeld wordt nog getekend: overslaan
            var copy = new byte[Size];
            Marshal.Copy(_buffer, copy, 0, Size);
            _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    _bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), copy, Width * 4, 0);
                    FirstFrame?.Invoke();
                }
                finally { Volatile.Write(ref _pending, 0); }
            });
        };

        _media = new Media(libVlc, url, FromType.FromLocation);
        _media.AddOption(":no-audio");
        _media.AddOption(":network-caching=500");

        _player = new VlcPlayer(libVlc);
        _player.SetVideoFormat("RV32", Width, Height, Width * 4);
        _player.SetVideoCallbacks(_lock, _unlock, _display);
        _player.Play(_media);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stoppen kan even duren; dat gebeurt buiten de UI-thread. Pas daarna mag het geheugen vrij.
        var player = _player;
        var media = _media;
        var buffer = _buffer;
        Task.Run(() =>
        {
            try { player.Stop(); } catch (Exception ex) when (ex is InvalidOperationException) { }
            player.Dispose();
            media.Dispose();
            GC.KeepAlive(_lock);
            GC.KeepAlive(_unlock);
            GC.KeepAlive(_display);
            Marshal.FreeHGlobal(buffer);
        });
    }
}
