using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using WaBiBaBuSy.Player.Common.Messages;

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
            while (!cancellationToken.IsCancellationRequested && !_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var baseMessage = JsonConvert.DeserializeObject<PlayerMessageBase>(line);
                    if (baseMessage == null)
                        continue;

                    // Deserialize specific message type
                    PlayerMessageBase? typedMessage = baseMessage.MessageType switch
                    {
                        "hwnd" => JsonConvert.DeserializeObject<PlayerMessageHwnd>(line),
                        "loaded" => JsonConvert.DeserializeObject<PlayerMessageLoaded>(line),
                        _ => baseMessage
                    };

                    if (typedMessage != null)
                    {
                        // Special handling for HWND message
                        if (typedMessage is PlayerMessageHwnd hwndMsg)
                        {
                            WindowHandle = new IntPtr(hwndMsg.Hwnd);
                        }

                        MessageReceived?.Invoke(this, typedMessage);
                    }
                }
                catch (JsonException ex)
                {
                    ErrorReceived?.Invoke(this, $"JSON deserialization error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
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
            var json = JsonConvert.SerializeObject(command);
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
