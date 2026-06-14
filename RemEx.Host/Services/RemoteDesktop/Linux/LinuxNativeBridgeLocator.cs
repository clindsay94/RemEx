using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Reflection;
using System.Threading;

namespace Remex.Host.Services.RemoteDesktop.Linux;

[SupportedOSPlatform("linux")]
internal static class LinuxNativeBridgeLocator
{
    private const string NativeBridgeLibraryName = "remex_linux_bridge";
    private static int _resolverRegistered;

    internal const string NativeBridgeFileName = "libremex_linux_bridge.so";

    public static string GetExpectedPath(string? baseDirectory = null)
        => Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "runtimes", "linux-x64", "native", NativeBridgeFileName);

    public static string GetLegacyRootPath(string? baseDirectory = null)
        => Path.Combine(baseDirectory ?? AppContext.BaseDirectory, NativeBridgeFileName);

    public static void EnsureDllImportResolverRegistered()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(LinuxNativeBridgeLocator).Assembly, ResolveNativeBridge);
    }

    public static bool TryLoadExpectedPath(out IntPtr handle, string? baseDirectory = null)
        => NativeLibrary.TryLoad(GetExpectedPath(baseDirectory), out handle);

    private static IntPtr ResolveNativeBridge(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeBridgeLibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        string expectedPath = GetExpectedPath();
        if (File.Exists(expectedPath) && NativeLibrary.TryLoad(expectedPath, out var handle))
        {
            return handle;
        }

        string legacyRootPath = GetLegacyRootPath();
        if (!File.Exists(expectedPath) && File.Exists(legacyRootPath) && NativeLibrary.TryLoad(legacyRootPath, out handle))
        {
            return handle;
        }

        return IntPtr.Zero;
    }
}
