namespace PrivatePaste.Client.Core.Platform;

/// <summary>Short, user-facing, one-line notifications.</summary>
public interface INotifier
{
    void Notify(string message);
}
