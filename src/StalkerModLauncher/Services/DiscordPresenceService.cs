using DiscordRPC;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class DiscordPresenceService : IDisposable
{
    private DiscordRpcClient? _client;
    private readonly string _clientId;
    private readonly Action<string>? _diagnostic;
    private bool _failureReported;

    public DiscordPresenceService(string clientId, Action<string>? diagnostic = null)
    {
        _clientId = clientId;
        _diagnostic = diagnostic;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_clientId);
    public string ClientId => _clientId;

    public void Initialize()
    {
        if (!IsEnabled || _client is not null)
        {
            return;
        }

        try
        {
            _client = new DiscordRpcClient(_clientId);
            _client.Initialize();
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
    }

    public void SetPlaying(string profileName)
    {
        if (_client is null || !_client.IsInitialized)
        {
            return;
        }

        try
        {
            _client.SetPresence(new RichPresence
            {
                Details = profileName,
                State = null,
                Assets = new Assets
                {
                    LargeImageText = "CORDON — S.T.A.L.K.E.R. Mod Launcher"
                }
            });
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
    }

    public void Clear()
    {
        if (_client is null || !_client.IsInitialized)
        {
            return;
        }

        try
        {
            _client.ClearPresence();
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
    }

    public void Dispose()
    {
        try
        {
            Clear();
            _client?.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void HandleFailure(Exception ex)
    {
        try
        {
            _client?.Dispose();
        }
        catch
        {
            // The original Discord error is more useful than a disposal error.
        }

        _client = null;
        if (_failureReported)
        {
            return;
        }

        _failureReported = true;
        _diagnostic?.Invoke(LocalizedText.Format(Strings.Discord_UnavailableFormat, ex.Message));
    }
}
