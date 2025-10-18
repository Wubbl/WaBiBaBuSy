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
        // Send HWND to parent process
        var hwnd = new WindowInteropHelper(this).Handle;
        SendMessage(new PlayerMessageHwnd { Hwnd = hwnd.ToInt32() });

        // Start listening for commands from stdin
        _cancellationTokenSource = new CancellationTokenSource();
        _stdinListenerTask = Task.Run(() => ListenToStdIn(_cancellationTokenSource.Token));
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
        switch (message.MessageType)
        {
            case "cmd_load":
                var loadCmd = JsonConvert.DeserializeObject<PlayerCommandLoad>(JsonConvert.SerializeObject(message));
                if (loadCmd != null)
                    LoadImage(loadCmd.FilePath);
                break;

            case "cmd_play":
                // For static images, "play" is a no-op (already visible)
                break;

            case "cmd_close":
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
            var json = JsonConvert.SerializeObject(message);
            Console.WriteLine(json);
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send message: {ex.Message}");
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