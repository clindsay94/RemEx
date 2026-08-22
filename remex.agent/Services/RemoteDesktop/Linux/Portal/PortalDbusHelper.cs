using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Remex.Agent.Services.RemoteDesktop.Linux.Portal;

/// <summary>
/// Shared D-Bus request/response handshake helper for xdg-desktop-portal.
///
/// Portal methods are asynchronous: the method call returns a Request object
/// path immediately, and the actual result arrives via a Response signal on
/// that Request. This helper hides that pattern behind a single async call.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class PortalDbusHelper
{
    public delegate void ArgWriter(ref MessageWriter writer, string handleToken);

    /// <summary>
    /// Converts a unique bus name (e.g. <c>:1.291971</c>) into the sender segment
    /// used in Request object paths (e.g. <c>1_291971</c>).
    /// </summary>
    public static string NormalizeSender(string uniqueName)
        => uniqueName.TrimStart(':').Replace('.', '_');

    /// <summary>
    /// Invokes a portal method on the given interface, subscribing to its
    /// <c>org.freedesktop.portal.Request.Response</c> signal before the call so no
    /// response is missed. Returns the results dictionary on response code 0, or
    /// null if the user cancelled / response code is non-zero / timeout / error.
    /// </summary>
    public static async Task<Dictionary<string, VariantValue>?> CallPortalAsync(
        DBusConnection conn,
        string normalizedSender,
        string interfaceName,
        string method,
        string signature,
        TimeSpan requestTimeout,
        ArgWriter writeArgs,
        ILogger logger,
        CancellationToken ct)
    {
        var handleToken = $"remex_{Guid.NewGuid():N}";
        var requestPath =
            $"{PortalDbusNames.PortalPath}/request/{normalizedSender}/{handleToken}";

        var tcs = new TaskCompletionSource<Dictionary<string, VariantValue>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = PortalDbusNames.PortalService,
            Interface = PortalDbusNames.RequestInterface,
            Member = "Response",
            Path = requestPath,
        };

        IDisposable? subscription = null;
        try
        {
            subscription = await conn.AddMatchAsync(
                rule,
                static (Message msg, object? state) =>
                {
                    var reader = msg.GetBodyReader();
                    var response = reader.ReadUInt32();
                    var dict = reader.ReadDictionaryOfStringToVariantValue();
                    return (response, dict);
                },
                // Tmds.DBus.Protocol 0.94 folded the four loose callback arguments into one
                // Notification<T> (RemEx-jcma3). The exception and the value used to arrive as
                // separate parameters where "ex is null" implied a good value; now they are two
                // properties on one struct, and CRUCIALLY they are not exhaustive. A completion
                // notification can carry NEITHER - ObserverDisposed is a completion with no
                // exception and no value - so the HasValue guard below is load-bearing rather than
                // defensive. Reading .Value without it throws on observer teardown, inside a
                // callback, where the only visible symptom is a portal request that never resolves.
                (Notification<(uint Response, Dictionary<string, VariantValue> Results)> n) =>
                {
                    if (n.Exception is not null)
                    {
                        tcs.TrySetException(n.Exception);
                        return;
                    }
                    if (!n.HasValue)
                    {
                        return;
                    }
                    var data = n.Value;
                    if (data.Response != 0u)
                    {
                        // 1 = cancelled by user, 2 = other (e.g. compositor terminated session).
                        tcs.TrySetResult(null);
                        return;
                    }
                    tcs.TrySetResult(data.Results);
                },
                flags: ObserverFlags.None);

            MessageBuffer buf;
            {
                var writer = conn.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: PortalDbusNames.PortalService,
                    path: PortalDbusNames.PortalPath,
                    @interface: interfaceName,
                    member: method,
                    signature: signature);
                writeArgs(ref writer, handleToken);
                buf = writer.CreateMessage();
            }

            await conn.CallMethodAsync(buf);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(requestTimeout);

            using (timeoutCts.Token.Register(() => tcs.TrySetResult(null)))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            subscription?.Dispose();
        }
    }
}
