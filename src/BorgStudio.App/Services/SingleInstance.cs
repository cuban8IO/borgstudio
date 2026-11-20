using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace BorgStudio.App.Services;

/// <summary>
/// One BorgStudio per user: the first start holds a lock file in the data folder and listens on a named pipe; a second
/// start asks it to show its window and ends. Runs without window (scheduled backups, updates) are not counted.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowCommand = "show";

    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream @lock, string pipeName)
    {
        _lock = @lock;
        _pipeName = pipeName;
        Listening = Task.Run(ListenAsync);
    }

    /// <summary>The instance of this process, if it is the first.</summary>
    public static SingleInstance? Current { get; private set; }

    /// <summary>Another start asked for the window (raised on a background thread).</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Ends when the instance is disposed (tests).</summary>
    internal Task Listening { get; }

    /// <summary>
    /// Claims <paramref name="dataDirectory"/> for this process; <c>null</c> if another BorgStudio holds it already.
    /// The lock goes with the process, also when it crashes.
    /// </summary>
    public static SingleInstance? TryClaim(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Current = new SingleInstance(stream, PipeName(dataDirectory));
    }

    /// <summary>Asks the BorgStudio holding <paramref name="dataDirectory"/> to show its window; <c>false</c> if it did not answer.</summary>
    public static bool ShowExisting(string dataDirectory, TimeSpan? timeout = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(dataDirectory), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout ?? TimeSpan.FromSeconds(3));
            using var writer = new StreamWriter(client);
            writer.WriteLine(ShowCommand);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stops listening and lets go of the lock (before the app starts itself again).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            Listening.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _lock.Dispose();
        if (Current == this)
            Current = null;
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_stop.Token) == ShowCommand)
                    ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The pipe is still held by the instance before a restart, or a caller went away: try again shortly.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One pipe per user and data folder (a test's data folder gets its own).</summary>
    private static string PipeName(string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}|{Path.GetFullPath(dataDirectory)}"));
        return "BorgStudio-" + Convert.ToHexString(hash)[..16];
    }
}
