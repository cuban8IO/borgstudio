using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Providers.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Pairing with a BorgStudio Server: shows the code, opens the server's confirmation page in the browser and waits until
/// the user confirmed (or denied) it there.
/// </summary>
public sealed partial class ServerPairingViewModel : ViewModelBase
{
    private readonly ServerClient _client;
    private readonly PairingStart _start;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cancellation = new();

    public ServerPairingViewModel(ServerClient client, PairingStart start, IDialogService dialogs, TimeProvider? time = null)
    {
        _client = client;
        _start = start;
        _time = time ?? TimeProvider.System;
        Dialogs = dialogs;
        Status = Strings.PairingWaiting;
    }

    /// <summary>Where the browser and the clipboard are reached; set by the window.</summary>
    public IDialogService Dialogs { get; set; }

    public string ServerText => BorgTexts.Format(Strings.PairingServer, ServerAddress.Display(_client.Url));

    /// <summary>The code to compare on the server's page.</summary>
    public string UserCode => _start.UserCode;

    public string VerificationUrl => _start.VerificationUrlComplete;

    [ObservableProperty]
    public partial string Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    public partial bool IsWaiting { get; private set; } = true;

    public bool IsFinished => !IsWaiting;

    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    /// <summary>The approved pairing (with the token); <c>null</c> if it was not approved.</summary>
    public PairingState? Result { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private Task OpenBrowserAsync() => Dialogs.OpenUrlAsync(VerificationUrl);

    [RelayCommand]
    private Task CopyCodeAsync() => Dialogs.CopyToClipboardAsync(UserCode);

    [RelayCommand]
    private void Cancel()
    {
        Stop();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops asking the server (the window was closed).</summary>
    public void Stop() => _cancellation.Cancel();

    /// <summary>Asks the server until the pairing is decided or expired (or <see cref="Cancel"/>). Started by the window.</summary>
    public async Task RunAsync()
    {
        var token = _cancellation.Token;
        var interval = TimeSpan.FromSeconds(Math.Clamp(_start.Interval, 0, 30));
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_time.GetUtcNow() >= _start.ExpiresAt)
                {
                    Finish(Strings.PairingExpired);
                    return;
                }
                await Task.Delay(interval, _time, token);
                PairingState state;
                try
                {
                    state = await _client.CompletePairingAsync(_start.PairingCode, token);
                }
                catch (ServerException exception) when (exception.Problem == ServerProblem.Unreachable)
                {
                    // A short outage: keep asking until the code expires.
                    Status = exception.Message;
                    HasProblem = true;
                    continue;
                }

                switch (state.State)
                {
                    case "approved" when state.Token is { Length: > 0 }:
                        Result = state;
                        IsWaiting = false;
                        CloseRequested?.Invoke(this, EventArgs.Empty);
                        return;
                    case "denied":
                        Finish(Strings.PairingDenied);
                        return;
                    case "expired":
                        Finish(Strings.PairingExpired);
                        return;
                    default:
                        Status = Strings.PairingWaiting;
                        HasProblem = false;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user.
        }
        catch (ServerException exception)
        {
            Finish(exception.Message);
        }
    }

    private void Finish(string problem)
    {
        Status = problem;
        HasProblem = true;
        IsWaiting = false;
    }
}
