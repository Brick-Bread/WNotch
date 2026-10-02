using Notch.Core.Shell;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Notch.Platform.Notifications;

/// <summary>
/// Reports new Windows notifications. Windows lets an app read them only when the user has allowed
/// it under Settings, Privacy and security, Notifications; <see cref="StartAsync"/> says whether that is so.
/// </summary>
public sealed class NotificationWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly NotificationDiff _diff = new();
    private CancellationTokenSource? _stop;

    /// <summary>Raised on a background thread for each new notification.</summary>
    public event Action<NotificationInfo>? Received;

    public bool IsRunning => _stop is not null;

    /// <summary>Starts watching. Returns null when it works, or what the user needs to know when Windows says no.</summary>
    public async Task<string?> StartAsync()
    {
        Stop();
        try
        {
            UserNotificationListener listener = UserNotificationListener.Current;
            UserNotificationListenerAccessStatus status = listener.GetAccessStatus();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                status = await listener.RequestAccessAsync();
            }

            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                return "Windows has not allowed Notch to read notifications. Turn it on under Settings > Privacy & security > Notifications.";
            }

            _diff.Reset();
            _stop = new CancellationTokenSource();
            CancellationToken token = _stop.Token;
            _ = Task.Run(() => PollAsync(listener, token), token);
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return "Windows would not let Notch read notifications: " + e.Message;
        }
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }

    public void Dispose() => Stop();

    private async Task PollAsync(UserNotificationListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<UserNotification> toasts = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                NotificationInfo[] current = [.. toasts.Select(Describe).OfType<NotificationInfo>()];
                foreach (NotificationInfo fresh in _diff.Fresh(current))
                {
                    Received?.Invoke(fresh);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // Windows may refuse for a moment (the session is locked, say); try again at the next tick.
            }

            try
            {
                await Task.Delay(PollInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static NotificationInfo? Describe(UserNotification notification)
    {
        try
        {
            string app = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
            NotificationBinding? binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            string[] texts = binding is null ? [] : [.. binding.GetTextElements().Select(t => t.Text)];
            return new NotificationInfo(notification.Id, app, texts.FirstOrDefault() ?? "", texts.Length > 1 ? string.Join(" ", texts.Skip(1)) : null);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
