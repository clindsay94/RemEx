using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Remex.Agent.Services.Session;

/// <summary>
/// <see cref="IKeepAwakeBackend"/> over the handle-based Windows power request API
/// (<c>PowerCreateRequest</c> / <c>PowerSetRequest</c> / <c>PowerClearRequest</c>).
/// </summary>
/// <remarks>
/// WHY NOT <c>SetThreadExecutionState</c>: its requirement is tracked per CALLING THREAD. The guard's
/// engage and disengage run on different thread-pool workers (RemoteDesktopHandler awaits the whole
/// stream in between), so clearing it from the second thread left the first thread's hold in place
/// until that pool thread happened to call it again or exit - a keep-awake that outlived the session
/// (PERF-TRACKER P1-10). A power request is an object: it is set and cleared through its handle from
/// any thread, and closing the handle drops every request on it, so it cannot leak past the hold.
///
/// SAME EFFECT as the old <c>ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED</c>: one handle
/// carrying both <c>PowerRequestSystemRequired</c> and <c>PowerRequestDisplayRequired</c>. The docs
/// require the system request alongside the display one for the display to stay on and the system to
/// stay out of sleep. Like the old flags, it does not block user-initiated sleep (lid, power button).
///
/// Visible to an admin via <c>powercfg /requests</c> (listed under DISPLAY and SYSTEM for the host
/// process, with the reason string below) - which is also how to confirm by hand that it is released.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PowerRequestKeepAwakeBackend : IKeepAwakeBackend
{
    private const string Reason = "RemEx: a remote-control client is connected";

    public IDisposable Acquire()
    {
        IntPtr reason = Marshal.StringToHGlobalUni(Reason);
        PowerRequestSafeHandle handle;
        try
        {
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = reason,
            };
            handle = PowerCreateRequest(ref context);
        }
        finally
        {
            // PowerCreateRequest copies the reason string; it is not referenced after the call.
            Marshal.FreeHGlobal(reason);
        }

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "PowerCreateRequest failed.");
        }

        try
        {
            SetOrThrow(handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
            SetOrThrow(handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
        }
        catch
        {
            // Closing the handle drops whichever request did get set.
            handle.Dispose();
            throw;
        }

        return new Hold(handle);
    }

    private static void SetOrThrow(PowerRequestSafeHandle handle, POWER_REQUEST_TYPE type)
    {
        if (!PowerSetRequest(handle, type))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"PowerSetRequest({type}) failed.");
        }
    }

    /// <summary>One acquired hold. Releasing it is idempotent and thread-agnostic.</summary>
    private sealed class Hold : IDisposable
    {
        private PowerRequestSafeHandle? _handle;

        public Hold(PowerRequestSafeHandle handle) => _handle = handle;

        public void Dispose()
        {
            PowerRequestSafeHandle? handle = Interlocked.Exchange(ref _handle, null);
            if (handle is null)
            {
                return;
            }

            int clearError = 0;
            try
            {
                // Balance each PowerSetRequest, as the docs ask. Closing the handle below would drop
                // them anyway, so a failed clear is reported but cannot leave the hold behind.
                if (!PowerClearRequest(handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired))
                {
                    clearError = Marshal.GetLastPInvokeError();
                }

                if (!PowerClearRequest(handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired) && clearError == 0)
                {
                    clearError = Marshal.GetLastPInvokeError();
                }
            }
            finally
            {
                handle.Dispose();
            }

            if (clearError != 0)
            {
                throw new Win32Exception(clearError, "PowerClearRequest failed; the request handle was closed, which releases it.");
            }
        }
    }

    /// <summary>A power request object handle, released with <c>CloseHandle</c>.</summary>
    private sealed class PowerRequestSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        // P/Invoke instantiates the returned SafeHandle through this constructor (CA1419).
        public PowerRequestSafeHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    // POWER_REQUEST_TYPE (winnt.h). Only the two used here are named; the values are fixed by the ABI.
    private enum POWER_REQUEST_TYPE
    {
        PowerRequestDisplayRequired = 0,
        PowerRequestSystemRequired = 1,
    }

    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x00000001;

    // REASON_CONTEXT (minwinbase.h) in its SIMPLE_STRING form: ULONG Version; DWORD Flags; then the
    // Reason union, whose SimpleReasonString member sits at offset 8 on both x86 and x64 (the union's
    // pointer alignment puts it there either way). With the SIMPLE_STRING flag only that member is read.
    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern PowerRequestSafeHandle PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(PowerRequestSafeHandle powerRequest, POWER_REQUEST_TYPE requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(PowerRequestSafeHandle powerRequest, POWER_REQUEST_TYPE requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
