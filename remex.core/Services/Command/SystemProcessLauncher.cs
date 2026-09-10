namespace Remex.Core.Services.Command;

/// <summary>
/// Starts one external program on behalf of a power command.
/// </summary>
/// <remarks>
/// <para>
/// The single point every PROCESS-LAUNCHING power command passes through, so a test can assert the
/// exact program and argument list one would have run without shutting the machine down
/// (RemEx-msyn). Not every command launches a process: Windows <c>Lock</c> and <c>MonitorOff</c> go
/// straight to <c>LockWorkStation</c> and <c>SendMessage</c>, so they are outside this seam's reach
/// and absent from the tests that use it. Pinning those would need a different seam over the
/// P/Invokes, which nothing has needed yet.
/// </para>
/// <para>
/// IT EXISTS FOR THE FLAGS, NOT THE FORMATTING. <see cref="WindowsSystemCommandService"/> carries a
/// remark warning that switching <c>/r</c> to <c>/r /g</c> would arm Automatic Restart Sign-On and
/// silently falsify five localized confirmation strings, "and no test would fail" (RemEx-mkq1).
/// Asserting the argument builder alone could not have fixed that: the builder is handed the flags,
/// so it can only echo back whatever the call site chose. Only the call site is worth pinning.
/// </para>
/// <para>
/// That conclusion is not reached here for the first time. It is the same one the
/// <c>InputToolLauncher</c> delegate and the <c>IPortalInputSink</c> interface record in
/// <c>remex.agent</c>, in the words of the first: "the defect class is not 'the mapping is wrong',
/// it is 'the argv is wrong', and only the argv is worth pinning".
/// </para>
/// </remarks>
internal delegate void SystemProcessLauncher(string fileName, string arguments);
