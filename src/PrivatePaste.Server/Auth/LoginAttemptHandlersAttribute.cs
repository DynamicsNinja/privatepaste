namespace PrivatePaste.Server.Auth;

/// <summary>
/// On a Razor page under the "login" rate-limit policy: only posts to these handlers are attempts. Rate-limit
/// attributes can't go on page handlers, so the page carries the policy and this narrows it.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LoginAttemptHandlersAttribute(params string[] handlers) : Attribute
{
    public IReadOnlyList<string> Handlers { get; } = handlers;

    /// <summary>
    /// Whether the request goes to one of <see cref="Handlers"/>, picking the handler name the way Razor Pages does:
    /// the route value first, then the first query value, ignoring case.
    /// </summary>
    public bool Matches(HttpRequest request)
    {
        var handler = request.RouteValues["handler"]?.ToString();
        if (string.IsNullOrEmpty(handler) && request.Query.TryGetValue("handler", out var values))
            handler = values[0];
        return handler is not null && Handlers.Contains(handler, StringComparer.OrdinalIgnoreCase);
    }
}
