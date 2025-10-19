using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.Player.Image;

/// <summary>
/// WPF Image Player that communicates with parent process via stdin/stdout JSON messages.
/// This player runs as a separate .exe process and can be parented to the desktop.
/// </summary>
public partial class MainWindow : Window
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _stdinListenerTask;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Console.Error.WriteLine("[Player.Image] Window_Loaded event fired");

        // Get HWND and HwndSource
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        Console.Error.WriteLine($"[Player.Image] Got HWND: 0x{hwnd:X} ({hwnd.ToInt32()})");
        Console.Error.WriteLine($"[Player.Image] Got HwndSource: {_hwndSource != null}");

        // Fix for Windows 10 Taskview crash (from Lively Wallpaper)
        // ShowInTaskbar = false causes issue with Windows 10 Taskview
        // This hides window from taskbar and fixes crash when taskview is launched
        ShowInTaskbar = false;
        ShowInTaskbar = true;

        // IMPORTANT: Just send HWND - parent will handle SetParent
        // We do NOT parent ourselves here
        SendMessage(new PlayerMessageHwnd { Hwnd = hwnd.ToInt32() });
        Console.Error.WriteLine("[Player.Image] Sent HWND message to parent");

        // Start listening for commands from stdin
        _cancellationTokenSource = new CancellationTokenSource();
        _stdinListenerTask = Task.Run(() => ListenToStdIn(_cancellationTokenSource.Token));
        Console.Error.WriteLine("[Player.Image] Started stdin listener");
    }

    /// <summary>
    /// Called AFTER parent process has completed SetParent.
    /// Forces WPF to refresh its composition rendering.
    /// </summary>
    public void OnParentChanged()
    {
        Console.Error.WriteLine("[Player.Image] OnParentChanged called - forcing WPF composition refresh");

        try
        {
            // Force WPF to re-render the composition
            if (_hwndSource != null)
            {
                // Invalidate the visual tree
                InvalidateVisual();

                // Force composition update
                CompositionTarget.Rendering += OnCompositionTargetRendering;

                Console.Error.WriteLine("[Player.Image] Attached to CompositionTarget.Rendering");
            }

            // Force layout update
            UpdateLayout();
            Console.Error.WriteLine("[Player.Image] UpdateLayout called");

            // Force redraw of image
            if (WallpaperImage.Source != null)
            {
                var source = WallpaperImage.Source;
                WallpaperImage.Source = null;
                WallpaperImage.Source = source;
                Console.Error.WriteLine("[Player.Image] Forced image source refresh");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player.Image] OnParentChanged error: {ex.Message}");
        }
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        // One-time handler to force initial render
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        Console.Error.WriteLine("[Player.Image] CompositionTarget.Rendering fired - WPF should be rendering now");
    }

    private async Task ListenToStdIn(CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);

                // If ReadLineAsync returns null, stdin has been closed - exit loop
                if (line == null)
                {
                    Console.Error.WriteLine("[Player.Image] Stdin closed, exiting listener");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    Console.Error.WriteLine($"[Player.Image] Received command: {line}");

                    // First, peek at the MessageType to determine which concrete type to deserialize
                    var wrapper = JsonConvert.DeserializeObject<MessageTypeWrapper>(line);
                    if (wrapper == null || string.IsNullOrEmpty(wrapper.MessageType))
                    {
                        Console.Error.WriteLine("[Player.Image] Could not determine message type");
                        continue;
                    }

                    Console.Error.WriteLine($"[Player.Image] MessageType: {wrapper.MessageType}");

                    // Deserialize to the correct concrete type based on MessageType
                    PlayerMessageBase? message = wrapper.MessageType switch
                    {
                        "cmd_load" => JsonConvert.DeserializeObject<PlayerCommandLoad>(line),
                        "cmd_play" => JsonConvert.DeserializeObject<PlayerCommandPlay>(line),
                        "cmd_close" => JsonConvert.DeserializeObject<PlayerCommandClose>(line),
                        "cmd_refresh" => JsonConvert.DeserializeObject<PlayerCommandRefresh>(line),
                        _ => null
                    };

                    if (message == null)
                    {
                        Console.Error.WriteLine($"[Player.Image] Unknown message type: {wrapper.MessageType}");
                        continue;
                    }

                    Console.Error.WriteLine($"[Player.Image] Deserialized to: {message.GetType().Name}");

                    // Dispatch to UI thread
                    await Dispatcher.InvokeAsync(() => HandleCommand(message));
                }
                catch (JsonException ex)
                {
                    // Invalid JSON - log or ignore
                    Console.Error.WriteLine($"[Player.Image] JSON deserialization error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player.Image] StdIn listener error: {ex.Message}");
        }
    }

    private void HandleCommand(PlayerMessageBase message)
    {
        // The JsonConverter already deserialized to the correct concrete type
        switch (message)
        {
            case PlayerCommandLoad loadCmd:
                LoadImage(loadCmd.FilePath);
                break;

            case PlayerCommandPlay:
                // For static images, "play" is a no-op (already visible)
                break;

            case PlayerCommandRefresh:
                // Called after SetParent to force WPF composition refresh
                OnParentChanged();
                break;

            case PlayerCommandClose:
                Close();
                break;

            default:
                Console.Error.WriteLine($"Unknown command: {message.MessageType}");
                break;
        }
    }

    private void LoadImage(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                SendMessage(new PlayerMessageLoaded
                {
                    Success = false,
                    ErrorMessage = $"File not found: {filePath}"
                });
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze(); // Allow use on other threads

            WallpaperImage.Source = bitmap;

            SendMessage(new PlayerMessageLoaded { Success = true });
        }
        catch (Exception ex)
        {
            SendMessage(new PlayerMessageLoaded
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    private void SendMessage(PlayerMessageBase message)
    {
        try
        {
            Console.Error.WriteLine($"[Player.Image] SendMessage called for {message.MessageType}");

            // Serialize the concrete type directly, not the base class
            // This avoids issues with the JsonConverter on PlayerMessageBase
            var json = JsonConvert.SerializeObject(message, message.GetType(), new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                TypeNameHandling = TypeNameHandling.None
            });

            Console.Error.WriteLine($"[Player.Image] Serialized JSON: {json}");
            Console.WriteLine(json);  // This goes to stdout for parent to read
            Console.Out.Flush();
            Console.Error.WriteLine("[Player.Image] JSON written to stdout and flushed");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player.Image] Failed to send message: {ex.Message}");
            Console.Error.WriteLine($"[Player.Image] Exception: {ex}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Stop stdin listener
        _cancellationTokenSource?.Cancel();
        _stdinListenerTask?.Wait(TimeSpan.FromSeconds(1));
        _cancellationTokenSource?.Dispose();
    }
}