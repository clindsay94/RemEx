using System.Collections.Concurrent;
using System.Net.WebSockets;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// Offers a file to the phone and waits for its answer (RemEx-y7my).
/// </summary>
/// <remarks>
/// <para>
/// **THE SENDING HALF OF <c>file_push_offer</c>, WHICH DID NOT EXIST.** The phone has shipped the
/// receiving half for some time — the consent prompt, the id minting, the grant — and the host had
/// only the mirror-image code for when the PHONE pushes to it. Every transfer before this was
/// client-initiated: the phone asked and the host answered. This is the first thing the host offers.
/// </para>
/// <para>
/// It negotiates and stops there. Moving the bytes afterwards is the existing v3 transfer path, which
/// the phone drives once it has the ids.
/// </para>
/// </remarks>
public sealed class FilePushOriginator(ILogger<FilePushOriginator> logger)
{
    /// <summary>
    /// How long to wait for the phone's answer.
    /// </summary>
    /// <remarks>
    /// **LONGER THAN THE PROMPT ITSELF, WHICH IS THE ONLY THING THAT MAKES THIS NUMBER CORRECT.** The
    /// phone gives the user 60 seconds to decide (its own comment calls it "a 60s prompt"). Waiting
    /// any less would time out while somebody was still reading the dialog — every deliberate
    /// acceptance would arrive after the host had already given up, and the file would never be sent
    /// even though the user said yes. The margin covers the round trip and nothing more.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(70);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<FilePushResponse>> _pending = new();

    /// <summary>Offers one file and returns what the phone decided.</summary>
    /// <remarks>
    /// **REGISTERED BEFORE THE OFFER IS SENT.** The phone grants its consent before replying, on
    /// purpose, so the answer can arrive the instant the offer lands — registering afterwards would
    /// leave a window where a fast reply finds nothing waiting and is dropped as unknown.
    /// </remarks>
    public async Task<FilePushOutcome> OfferFileAsync(
        WebSocket controlWs, string fileName, long sizeBytes, CancellationToken ct)
    {
        var pushId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<FilePushResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[pushId] = completion;

        try
        {
            var offer = FilePushNegotiation.TryOfferOne(pushId, fileName, sizeBytes);
            if (offer is null)
            {
                return FilePushOutcome.Refused($"'{fileName}' is not a name the receiver could file");
            }

            await MessageSerializer.SendAsync(
                controlWs,
                new RemexMessage { Type = MessageTypes.FilePushOffer, FilePushOffer = offer },
                ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);

            FilePushResponse? reply = null;
            try
            {
                reply = await completion.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The deadline, not the caller. Falls through to Interpret(null), which refuses.
            }

            var outcome = FilePushNegotiation.Interpret(pushId, 1, reply);
            if (!outcome.Accepted)
            {
                logger.LogInformation(
                    "Push of {FileName} was not sent: {Reason}.", fileName, outcome.RefusedReason);
            }

            return outcome;
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            // The socket went away mid-offer. Nothing was sent, and saying so is the honest outcome -
            // this must not surface as a failure of whatever produced the file.
            logger.LogWarning(ex, "Could not offer {FileName} to the phone.", fileName);
            return FilePushOutcome.Refused("the connection dropped during the offer");
        }
        finally
        {
            _pending.TryRemove(pushId, out _);
        }
    }

    /// <summary>Hands an inbound <c>file_push_response</c> to whoever is waiting for it.</summary>
    /// <remarks>
    /// An unmatched id is dropped deliberately rather than logged as an error: it is what a reply that
    /// arrived after its deadline looks like, and that is an ordinary slow-user outcome rather than a
    /// fault. The negotiation has already refused by then, so completing anything here would be worse
    /// than dropping it.
    /// </remarks>
    public void Complete(FilePushResponse? response)
    {
        if (response is null || string.IsNullOrWhiteSpace(response.PushId))
        {
            return;
        }

        if (_pending.TryGetValue(response.PushId, out var completion))
        {
            completion.TrySetResult(response);
        }
        else
        {
            logger.LogDebug("A push response arrived for {PushId}, which is no longer waiting.", response.PushId);
        }
    }
}
