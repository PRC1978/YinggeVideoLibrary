using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace YinggeVideoLibrary;

/// <summary>
/// Wakes a library window hidden in the notification area. A hidden WPF window
/// may have no HWND, so Windows messages cannot be used as the only signal.
/// The library/database leases still prevent concurrent writes.
/// </summary>
internal sealed class SingleInstanceActivation : IDisposable
{
    readonly CancellationTokenSource cancellation = new();
    readonly string pipeName;
    readonly MainWindow window;
    readonly Task server;

    SingleInstanceActivation(MainWindow owner, string libraryDirectory)
    {
        window = owner;
        pipeName = PipeNameFor(libraryDirectory);
        server = Task.Run(ListenAsync);
    }

    static string PipeNameFor(string libraryDirectory)
    {
        var normalized = Path.GetFullPath(libraryDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return "YinggeVideoLibrary.Activate." + hash;
    }

    internal static SingleInstanceActivation Start(MainWindow window, string libraryDirectory) =>
        new(window, libraryDirectory);

    async Task ListenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                var request = new byte[1];
                if (await pipe.ReadAsync(request, cancellation.Token) != 1 || request[0] != 1)
                    continue;
                await pipe.WriteAsync(new byte[] { 1 }, cancellation.Token);
                _ = window.Dispatcher.BeginInvoke(new Action(window.ActivateFromSecondLaunch));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { break; }
            catch (IOException)
            {
                // A simultaneous launch may briefly own the pipe. The library
                // lease remains authoritative; retry after its owner exits.
                try { await Task.Delay(200, cancellation.Token); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception)
            {
                try { await Task.Delay(200, cancellation.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal static bool TryActivateExisting(string libraryDirectory, int connectTimeoutMilliseconds = 350)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeNameFor(libraryDirectory),
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(connectTimeoutMilliseconds);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            pipe.WriteAsync(new byte[] { 1 }, 0, 1, timeout.Token).GetAwaiter().GetResult();
            var response = new byte[1];
            return pipe.ReadAsync(response, 0, 1, timeout.Token).GetAwaiter().GetResult() == 1 &&
                response[0] == 1;
        }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        _ = server.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
    }
}
