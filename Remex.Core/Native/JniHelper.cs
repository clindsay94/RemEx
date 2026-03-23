using System;
using System.Runtime.InteropServices;

namespace Remex.Core.Native;

/// <summary>
/// Minimal JNI mapping for .NET NativeAOT.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct JNIEnv
{
    public void** Functions;

    public static string? ReadJString(IntPtr envPtr, IntPtr jstring)
    {
        if (envPtr == IntPtr.Zero || jstring == IntPtr.Zero) return null;

        var env = (JNIEnv*)envPtr;
        // GetStringUTFChars is at index 169 in the JNIEnv function table
        var getStringUtfChars = (delegate* unmanaged<IntPtr, IntPtr, byte*, byte*>)env->Functions[169];
        // ReleaseStringUTFChars is at index 170
        var releaseStringUtfChars = (delegate* unmanaged<IntPtr, IntPtr, byte*, void>)env->Functions[170];

        byte* utf8Ptr = getStringUtfChars(envPtr, jstring, null);
        if (utf8Ptr == null) return null;

        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)utf8Ptr);
        }
        finally
        {
            releaseStringUtfChars(envPtr, jstring, utf8Ptr);
        }
    }

    public static IntPtr CreateJString(IntPtr envPtr, string? value)
    {
        if (envPtr == IntPtr.Zero || value == null) return IntPtr.Zero;

        var env = (JNIEnv*)envPtr;
        // NewStringUTF is at index 167
        var newStringUtf = (delegate* unmanaged<IntPtr, byte*, IntPtr>)env->Functions[167];

        fixed (byte* pValue = System.Text.Encoding.UTF8.GetBytes(value + "\0"))
        {
            return newStringUtf(envPtr, pValue);
        }
    }
}
