using System.Collections.Generic;
using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Core.Services;

public interface IProcessMonitorService
{
    Task<List<ProcessInfo>> GetProcessesAsync();

    /// <summary>
    /// Ends the process with <paramref name="processId"/>, refusing if it is no longer the program
    /// the caller meant.
    /// </summary>
    /// <param name="expectedName">
    /// The <see cref="ProcessInfo.Name"/> the caller believes owns <paramref name="processId"/>. When
    /// supplied, the live process must still carry this name or nothing is killed. Null skips the
    /// check, which is what a client too old to send it gets.
    /// </param>
    /// <remarks>
    /// A PID alone is not an identity. It is recycled, often quickly, so between the moment a client
    /// renders a process list and the moment the user confirms, that number can belong to something
    /// else entirely — and ending the wrong program discards whatever it had not saved, with no undo.
    /// <para>
    /// A client cannot close this on its own. RemEx-2s91 had the PC re-check its own last-polled list
    /// before sending, which removes the grossest case but is still only as fresh as the last poll,
    /// and Android sent a bare PID with no check at all. Only the host can read identity and kill in
    /// one step, and doing it here covers both clients at once rather than duplicating a heuristic in
    /// Kotlin. (RemEx-druh.)
    /// </para>
    /// </remarks>
    ProcessKillResult KillProcess(int processId, string? expectedName = null);
}
