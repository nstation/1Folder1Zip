using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace OneFolderOneZip.App;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    public bool IsPrimary { get; }
    public SingleInstance()
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        _name = $"1Folder1Zip-v1-{process.SessionId}-" + user;
        _mutex = new Mutex(true, @"Local\" + _name, out bool created);
        IsPrimary = created;
    }

    public async Task ForwardAsync(string[] paths)
    {
        using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(paths).AsMemory(), timeout.Token);
        string? result = await reader.ReadLineAsync(timeout.Token);
        if (result != "OK") throw new IOException("The running instance did not acknowledge the folders.");
    }

    public void Listen(Action<string[]> receive) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? payload = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                var paths = payload == null ? null : JsonSerializer.Deserialize<string[]>(payload);
                if (paths == null) continue;
                receive(paths);
                await writer.WriteLineAsync("OK".AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or JsonException or OperationCanceledException) { }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
