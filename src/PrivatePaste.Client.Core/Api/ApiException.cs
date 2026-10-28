using System.Net;

namespace PrivatePaste.Client.Core.Api;

public class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>401: the token was revoked or the password was wrong.</summary>
public sealed class UnauthorizedApiException(string m) : ApiException(HttpStatusCode.Unauthorized, m);
