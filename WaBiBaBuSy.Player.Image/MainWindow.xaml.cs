using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
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

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Console.Error.WriteLine("[Player.Image] Window_Loaded event fired");

        // Send HWND to parent process
        var hwnd = new WindowInteropHelper(this).Handle;
        Console.Error.WriteLine($"[Player.Image] Got HWND: 0x{hwnd:X} ({hwnd.ToInt32()})");

        SendMessage(new PlayerMessageHwnd { Hwnd = hwnd.ToInt32() });
        Console.Error.WriteLine("[Player.Image] Sent HWND message to parent");

        // Start listening for commands from stdin
        _cancellationTokenSource = new CancellationTokenSource();
        _stdinListenerTask = Task.Run(() => ListenToStdIn(_cancellationTokenSource.Token));
        Console.Error.WriteLine("[Player.Image] Started stdin listener");
    }

    private async Task ListenToStdIn(CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var message = JsonConvert.DeserializeObject<PlayerMessageBase>(line);
                    if (message == null)
                        continue;

                    // Dispatch to UI thread
                    await Dispatcher.InvokeAsync(() => HandleCommand(message));
                }
                catch (JsonException ex)
                {
                    // Invalid JSON - log or ignore
                    Console.Error.WriteLine($"JSON deserialization error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"StdIn listener error: {ex.Message}");
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