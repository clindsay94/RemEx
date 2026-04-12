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
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct JavaVM
{
    public void** Functions;
}

public static unsafe class JniHelper
{
    public static string? ReadJString(IntPtr envPtr, IntPtr jstring)
    {
        if (envPtr == IntPtr.Zero || jstring == IntPtr.Zero) return null;

        var env = (JNIEnv*)envPtr;
        // GetStringUTFChars is at index 169
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

    public static int GetJavaVM(IntPtr envPtr, out IntPtr vmPtr)
    {
        var env = (JNIEnv*)envPtr;
        // GetJavaVM is at index 219
        var getJavaVm = (delegate* unmanaged<IntPtr, IntPtr*, int>)env->Functions[219];
        fixed (IntPtr* pVm = &vmPtr)
        {
            return getJavaVm(envPtr, pVm);
        }
    }

    public static IntPtr NewGlobalRef(IntPtr envPtr, IntPtr obj)
    {
        var env = (JNIEnv*)envPtr;
        // NewGlobalRef is at index 21
        var newGlobalRef = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)env->Functions[21];
        return newGlobalRef(envPtr, obj);
    }

    public static void DeleteGlobalRef(IntPtr envPtr, IntPtr obj)
    {
        var env = (JNIEnv*)envPtr;
        // DeleteGlobalRef is at index 22
        var deleteGlobalRef = (delegate* unmanaged<IntPtr, IntPtr, void>)env->Functions[22];
        deleteGlobalRef(envPtr, obj);
    }

    public static void DeleteLocalRef(IntPtr envPtr, IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        var env = (JNIEnv*)envPtr;
        // DeleteLocalRef is at index 23
        var deleteLocalRef = (delegate* unmanaged<IntPtr, IntPtr, void>)env->Functions[23];
        deleteLocalRef(envPtr, obj);
    }

    public static IntPtr GetObjectClass(IntPtr envPtr, IntPtr obj)
    {
        var env = (JNIEnv*)envPtr;
        // GetObjectClass is at index 31
        var getObjectClass = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)env->Functions[31];
        return getObjectClass(envPtr, obj);
    }

    public static IntPtr GetMethodID(IntPtr envPtr, IntPtr clazz, string name, string sig)
    {
        var env = (JNIEnv*)envPtr;
        // GetMethodID is at index 33
        var getMethodId = (delegate* unmanaged<IntPtr, IntPtr, byte*, byte*, IntPtr>)env->Functions[33];
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        fixed (byte* pSig = System.Text.Encoding.UTF8.GetBytes(sig + "\0"))
        {
            return getMethodId(envPtr, clazz, pName, pSig);
        }
    }

    public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, IntPtr arg)
    {
        var env = (JNIEnv*)envPtr;
        // CallVoidMethod is at index 61
        var callVoidMethod = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)env->Functions[61];
        callVoidMethod(envPtr, obj, methodId, arg);
    }

    public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, bool arg)
    {
        var env = (JNIEnv*)envPtr;
        // CallVoidMethod is at index 61
        var callVoidMethod = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, void>)env->Functions[61];
        callVoidMethod(envPtr, obj, methodId, arg ? (byte)1 : (byte)0);
    }

    public static IntPtr NewByteArray(IntPtr envPtr, int len)
    {
        var env = (JNIEnv*)envPtr;
        // NewByteArray is at index 176
        var newByteArray = (delegate* unmanaged<IntPtr, int, IntPtr>)env->Functions[176];
        return newByteArray(envPtr, len);
    }

    public static void SetByteArrayRegion(IntPtr envPtr, IntPtr array, int start, int len, byte[] buf)
    {
        var env = (JNIEnv*)envPtr;
        // SetByteArrayRegion is at index 208 (209 is SetCharArrayRegion).
        var setByteArrayRegion = (delegate* unmanaged<IntPtr, IntPtr, int, int, byte*, void>)env->Functions[208];
        fixed (byte* pBuf = buf)
        {
            setByteArrayRegion(envPtr, array, start, len, pBuf);
        }
    }

    public static int AttachCurrentThread(IntPtr vmPtr, out IntPtr envPtr, IntPtr args)
    {
        var vm = (JavaVM*)vmPtr;
        // AttachCurrentThread is at index 4
        var attachCurrentThread = (delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int>)vm->Functions[4];
        fixed (IntPtr* pEnv = &envPtr)
        {
            return attachCurrentThread(vmPtr, pEnv, args);
        }
    }

    public static int DetachCurrentThread(IntPtr vmPtr)
    {
        var vm = (JavaVM*)vmPtr;
        // DetachCurrentThread is at index 5
        var detachCurrentThread = (delegate* unmanaged<IntPtr, int>)vm->Functions[5];
        return detachCurrentThread(vmPtr);
    }

    [DllImport("log", EntryPoint = "__android_log_print", CallingConvention = CallingConvention.Cdecl)]
    private static extern int __android_log_print(int prio, string tag, string fmt, string msg);

    public static void AndroidLogE(string tag, string message)
    {
        __android_log_print(6, tag, "%s", message);
    }
}
