using System.Net;

namespace PrivatePaste.Client.Core.Api;

public class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>401: the token was revoked or the password was wrong.</summary>
public sealed class UnauthorizedApiException(string m) : ApiException(HttpStatusCode.Unauthorized, m);

/// <summary>
/// 409 "Encryption was reset": the upload was sealed under a vault key the server no longer has. Retrying can't help;
/// the device needs the new passphrase.
/// </summary>
public sealed class KeyResetException(string m) : ApiException(HttpStatusCode.Conflict, m);

/// <summary>426: the server needs a newer app (protocol). Retrying can't help until the app is updated.</summary>
public sealed class UpdateRequiredException(string m) : ApiException(HttpStatusCode.UpgradeRequired, m);
