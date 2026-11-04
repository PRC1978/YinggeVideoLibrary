using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Yingge.Cloud.Client;

namespace YinggeVideoLibrary;

// Bridges the client library to the Windows credential manager. Tokens are
// never written to a JSON file, the registry or the video library directory.
internal sealed class WindowsCredentialSessionStore(string credentialName) : ISessionStore
{
    public ValueTask<string?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var value = WindowsCredentialStore.Read(credentialName);
            return ValueTask.FromResult(string.IsNullOrEmpty(value) ? null : value);
        }
        catch (Win32Exception)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }

    public ValueTask WriteAsync(string token, CancellationToken ct = default)
    {
        WindowsCredentialStore.Write(credentialName, token);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        try
        {
            WindowsCredentialStore.Write(credentialName, "");
        }
        catch (Win32Exception)
        {
            // Already absent.
        }
        return ValueTask.CompletedTask;
    }
}
