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

[StructLayout(LayoutKind.Explicit)]
public struct JValue
{
    [FieldOffset(0)] public byte Z;      // jboolean
    [FieldOffset(0)] public int I;       // jint
    [FieldOffset(0)] public long J;      // jlong
    [FieldOffset(0)] public IntPtr L;    // jobject
}

public static unsafe class JniHelper
{
    public static string? ReadJString(IntPtr envPtr, IntPtr jstring)
    {
        if (envPtr == IntPtr.Zero || jstring == IntPtr.Zero) return null;

        var env = (JNIEnv*)envPtr;
        // GetStringLength is at index 164
        var getStringLength = (delegate* unmanaged<IntPtr, IntPtr, int>)env->Functions[164];
        // GetStringChars is at index 165
        var getStringChars = (delegate* unmanaged<IntPtr, IntPtr, byte*, char*>)env->Functions[165];
        // ReleaseStringChars is at index 166
        var releaseStringChars = (delegate* unmanaged<IntPtr, IntPtr, char*, void>)env->Functions[166];

        int length = getStringLength(envPtr, jstring);
        // No JNI call may run while a pending Java exception is set, or the runtime
        // aborts the process (SIGABRT). Clear and bail out to a managed null instead.
        if (ExceptionCheck(envPtr)) { ExceptionClear(envPtr); return null; }
        if (length < 0) return null;
        if (length == 0) return string.Empty;

        char* chars = getStringChars(envPtr, jstring, null);
        if (ExceptionCheck(envPtr)) { ExceptionClear(envPtr); return null; }
        if (chars == null) return null;

        try
        {
            return new string(chars, 0, length);
        }
        finally
        {
            releaseStringChars(envPtr, jstring, chars);
        }
    }

    public static IntPtr CreateJString(IntPtr envPtr, string? value)
    {
        if (envPtr == IntPtr.Zero || value == null) return IntPtr.Zero;

        var env = (JNIEnv*)envPtr;
        // NewString (UTF-16) is at index 163
        var newString = (delegate* unmanaged<IntPtr, char*, int, IntPtr>)env->Functions[163];

        fixed (char* pValue = value)
        {
            return newString(envPtr, pValue, value.Length);
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

    public static bool ExceptionCheck(IntPtr envPtr)
    {
        var env = (JNIEnv*)envPtr;
        // ExceptionCheck is at index 228
        var exceptionCheck = (delegate* unmanaged<IntPtr, byte>)env->Functions[228];
        return exceptionCheck(envPtr) != 0;
    }

    public static void ExceptionClear(IntPtr envPtr)
    {
        var env = (JNIEnv*)envPtr;
        // ExceptionClear is at index 17
        var exceptionClear = (delegate* unmanaged<IntPtr, void>)env->Functions[17];
        exceptionClear(envPtr);
    }

    public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, IntPtr arg)
    {
        var env = (JNIEnv*)envPtr;
        // CallVoidMethodA is at index 63 (fixed-signature jvalue[] variant of CallVoidMethod)
        var callVoidMethodA = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, JValue*, void>)env->Functions[63];
        var args = stackalloc JValue[1];
        args[0].L = arg;
        callVoidMethodA(envPtr, obj, methodId, args);
    }

    public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, bool arg)
    {
        var env = (JNIEnv*)envPtr;
        var callVoidMethodA = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, JValue*, void>)env->Functions[63];
        var args = stackalloc JValue[1];
        args[0].Z = arg ? (byte)1 : (byte)0;
        callVoidMethodA(envPtr, obj, methodId, args);
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

    public static int AttachCurrentThreadAsDaemon(IntPtr vmPtr, out IntPtr envPtr, IntPtr args)
    {
        var vm = (JavaVM*)vmPtr;
        // AttachCurrentThreadAsDaemon is at index 7
        var attach = (delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int>)vm->Functions[7];
        fixed (IntPtr* pEnv = &envPtr)
        {
            return attach(vmPtr, pEnv, args);
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
