using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using WaBiBaBuSy.Player.Common.Messages;
using Debug = System.Diagnostics.Debug;

namespace WaBiBaBuSy.Player.Common;

/// <summary>
/// Manages communication with a player process via stdin/stdout JSON messages.
/// Used by the parent process to control player instances.
/// </summary>
public class ProcessCommunicator : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _stdoutListenerTask;
    private bool _disposed;

    public event EventHandler<PlayerMessageBase>? MessageReceived;
    public event EventHandler<string>? ErrorReceived;

    public IntPtr WindowHandle { get; private set; } = IntPtr.Zero;
    public bool IsRunning => !_process.HasExited;

    public ProcessCommunicator(string executablePath)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8
            }
        };

        _process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                ErrorReceived?.Invoke(this, e.Data);
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _cancellationTokenSource = new CancellationTokenSource();
        _stdoutListenerTask = Task.Run(() => ListenToStdOut(_cancellationTokenSource.Token));
    }

    private async Task ListenToStdOut(CancellationToken cancellationToken)
    {
        try
        {
            Debug.WriteLine("[ProcessCommunicator] Started listening to player stdout");
            while (!cancellationToken.IsCancellationRequested && !_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Debug.WriteLine($"[ProcessCommunicator] Received from player: {line}");

                try
                {
                    // First, peek at the MessageType to determine which concrete type to deserialize
                    var wrapper = JsonConvert.DeserializeObject<MessageTypeWrapper>(line);
                    if (wrapper == null || string.IsNullOrEmpty(wrapper.MessageType))
                    {
                        Debug.WriteLine("[ProcessCommunicator] Could not determine message type");
                        continue;
                    }

                    Debug.WriteLine($"[ProcessCommunicator] MessageType: {wrapper.MessageType}");

                    // Deserialize to the correct concrete type based on MessageType
                    PlayerMessageBase? message = wrapper.MessageType switch
                    {
                        "hwnd" => JsonConvert.DeserializeObject<PlayerMessageHwnd>(line),
                        "loaded" => JsonConvert.DeserializeObject<PlayerMessageLoaded>(line),
                        "cmd_load" => JsonConvert.DeserializeObject<PlayerCommandLoad>(line),
                        "cmd_play" => JsonConvert.DeserializeObject<PlayerCommandPlay>(line),
                        "cmd_close" => JsonConvert.DeserializeObject<PlayerCommandClose>(line),
                        _ => null
                    };

                    if (message == null)
                    {
                        Debug.WriteLine($"[ProcessCommunicator] Unknown message type: {wrapper.MessageType}");
                        continue;
                    }

                    Debug.WriteLine($"[ProcessCommunicator] Deserialized to: {message.GetType().Name}");

                    // Special handling for HWND message
                    if (message is PlayerMessageHwnd hwndMsg)
                    {
                        WindowHandle = new IntPtr(hwndMsg.Hwnd);
                        Debug.WriteLine($"[ProcessCommunicator] Received HWND: 0x{hwndMsg.Hwnd:X} ({hwndMsg.Hwnd})");
                    }

                    MessageReceived?.Invoke(this, message);
                }
                catch (JsonException ex)
                {
                    Debug.WriteLine($"[ProcessCommunicator] JSON error: {ex.Message}");
                    ErrorReceived?.Invoke(this, $"JSON deserialization error: {ex.Message}");
                }
            }
            Debug.WriteLine("[ProcessCommunicator] Stopped listening to player stdout");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessCommunicator] StdOut listener error: {ex.Message}");
            ErrorReceived?.Invoke(this, $"StdOut listener error: {ex.Message}");
        }
    }

    public async Task SendCommandAsync(PlayerMessageBase command)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProcessCommunicator));

        if (_process.HasExited)
            throw new InvalidOperationException("Process has exited");

        try
        {
            // Serialize the concrete type to avoid JsonConverter issues
            var json = JsonConvert.SerializeObject(command, command.GetType(), new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                TypeNameHandling = TypeNameHandling.None
            });
            await _process.StandardInput.WriteLineAsync(json);
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(this, $"Failed to send command: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> WaitForWindowHandleAsync(TimeSpan timeout)
    {
        var startTime = DateTime.UtcNow;
        while (WindowHandle == IntPtr.Zero && DateTime.UtcNow - startTime < timeout)
        {
            await Task.Delay(50);
        }
        return WindowHandle != IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            // Try graceful shutdown first
            SendCommandAsync(new PlayerCommandClose()).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore errors during shutdown
        }

        _cancellationTokenSource.Cancel();

        if (!_process.WaitForExit(2000))
        {
            try
            {
                _process.Kill();
            }
            catch
            {
                // Process may have already exited
            }
        }

        _stdoutListenerTask.Wait(TimeSpan.FromSeconds(1));
        _cancellationTokenSource.Dispose();
        _process.Dispose();
    }
}
