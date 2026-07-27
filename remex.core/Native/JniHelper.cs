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

/// <summary>
/// Hand-rolled JNI bindings for the NativeAOT library the Android app loads as
/// <c>libRemexCore.so</c>.
/// </summary>
/// <remarks>
/// Every method here reaches Java through the JNI function table by ORDINAL - the magic numbers
/// in each body are indices into <c>JNIEnv-&gt;Functions</c>, fixed by the JNI specification.
/// That is the load-bearing fact about this file: a wrong index is not a compile error and not
/// an exception, it is a call through a mistyped function pointer, which is undefined behaviour
/// and usually a process abort with no managed stack. Do not adjust an index without checking it
/// against the JNI spec.
/// <para>
/// TWO RULES GOVERN EVERYTHING BELOW. First, no JNI call may run while a Java exception is
/// pending - the runtime aborts the process with SIGABRT rather than reporting an error - which
/// is why the string helpers check and clear before continuing. Second, references have
/// lifetimes: anything JNI hands back is a LOCAL reference, valid only until the native call
/// returns to Java, so anything kept beyond that must be promoted with
/// <see cref="NewGlobalRef"/> and released with <see cref="DeleteGlobalRef"/>.
/// </para>
/// <para>
/// Reflection-free and allocation-light by necessity: this assembly is compiled with NativeAOT,
/// so the usual interop conveniences are unavailable.
/// </para>
/// </remarks>
public static unsafe class JniHelper
{
    /// <summary>Copies a Java string into managed memory, or null if it cannot be read.</summary>
    /// <remarks>
    /// Returns null for a null env or string, for a pending Java exception (which it CLEARS, since
    /// leaving one set would abort the process on the next JNI call), and for any failure to obtain
    /// the characters. The caller therefore cannot distinguish "Java gave us null" from "something
    /// went wrong" - deliberately, because at this boundary both mean the same thing: there is no
    /// usable value. The borrowed character buffer is always released, including on the throwing path.
    /// </remarks>
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

    /// <summary>Creates a Java string from managed text, returning a LOCAL reference.</summary>
    /// <remarks>
    /// The result is owned by the caller for the duration of the current native call and should be
    /// released with <see cref="DeleteLocalRef"/> once passed on - a loop that creates strings
    /// without releasing them exhausts the local reference table, which is a fixed, small capacity.
    /// Returns <see cref="IntPtr.Zero"/> for a null input, which Java sees as null.
    /// </remarks>
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

    /// <summary>Obtains the process-wide JavaVM, which unlike a JNIEnv is valid on any thread.</summary>
    /// <returns>JNI_OK (0) on success; any other value means <paramref name="vmPtr"/> is unusable.</returns>
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

    /// <summary>Promotes a reference so it survives beyond the current native call.</summary>
    /// <remarks>
    /// Every global reference must be matched by <see cref="DeleteGlobalRef"/>; they are never
    /// collected otherwise, and they pin the Java object alive with them.
    /// </remarks>
    public static IntPtr NewGlobalRef(IntPtr envPtr, IntPtr obj)
    {
        var env = (JNIEnv*)envPtr;
        // NewGlobalRef is at index 21
        var newGlobalRef = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)env->Functions[21];
        return newGlobalRef(envPtr, obj);
    }

    /// <summary>Releases a reference taken with <see cref="NewGlobalRef"/>.</summary>
    public static void DeleteGlobalRef(IntPtr envPtr, IntPtr obj)
    {
        var env = (JNIEnv*)envPtr;
        // DeleteGlobalRef is at index 22
        var deleteGlobalRef = (delegate* unmanaged<IntPtr, IntPtr, void>)env->Functions[22];
        deleteGlobalRef(envPtr, obj);
    }

    /// <summary>Releases a local reference early, rather than waiting for the call to return.</summary>
    /// <remarks>Ignores <see cref="IntPtr.Zero"/>, so it is safe to call unconditionally.</remarks>
    public static void DeleteLocalRef(IntPtr envPtr, IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        var env = (JNIEnv*)envPtr;
        // DeleteLocalRef is at index 23
        var deleteLocalRef = (delegate* unmanaged<IntPtr, IntPtr, void>)env->Functions[23];
        deleteLocalRef(envPtr, obj);
    }

    /// <summary>Returns the class of an object, as a LOCAL reference the caller should release.</summary>
    public static IntPtr GetObjectClass(IntPtr envPtr, IntPtr obj)
    {
        var env = (JNIEnv*)envPtr;
        // GetObjectClass is at index 31
        var getObjectClass = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)env->Functions[31];
        return getObjectClass(envPtr, obj);
    }

    /// <summary>Resolves an instance method by name and JVM type signature.</summary>
    /// <param name="sig">
    /// A JVM descriptor such as <c>(Ljava/lang/String;)V</c>, not a Java-source signature. A
    /// mismatch here is the usual cause of a silent JNI failure: the lookup returns
    /// <see cref="IntPtr.Zero"/> and leaves a pending exception, so the NEXT JNI call aborts the
    /// process rather than this one reporting anything.
    /// </param>
    /// <remarks>
    /// The method ID stays valid as long as the class is loaded, so callers cache it rather than
    /// resolving per call. Note this does NOT check or clear the pending exception on failure -
    /// callers that might miss are expected to follow with <see cref="ExceptionCheck"/>.
    /// </remarks>
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

    /// <summary>True when a Java exception is pending on this thread.</summary>
    /// <remarks>
    /// Must be consulted after any call that can throw, because a pending exception makes every
    /// subsequent JNI call fatal rather than merely failing.
    /// </remarks>
    public static bool ExceptionCheck(IntPtr envPtr)
    {
        var env = (JNIEnv*)envPtr;
        // ExceptionCheck is at index 228
        var exceptionCheck = (delegate* unmanaged<IntPtr, byte>)env->Functions[228];
        return exceptionCheck(envPtr) != 0;
    }

    /// <summary>Discards the pending Java exception so JNI calls are legal again.</summary>
    /// <remarks>Discards the detail with it - log before clearing if the reason matters.</remarks>
    public static void ExceptionClear(IntPtr envPtr)
    {
        var env = (JNIEnv*)envPtr;
        // ExceptionClear is at index 17
        var exceptionClear = (delegate* unmanaged<IntPtr, void>)env->Functions[17];
        exceptionClear(envPtr);
    }

    /// <summary>Invokes a void instance method taking one object argument.</summary>
    public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, IntPtr arg)
    {
        var env = (JNIEnv*)envPtr;
        // CallVoidMethodA is at index 63 (fixed-signature jvalue[] variant of CallVoidMethod)
        var callVoidMethodA = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, JValue*, void>)env->Functions[63];
        var args = stackalloc JValue[1];
        args[0].L = arg;
        callVoidMethodA(envPtr, obj, methodId, args);
    }

    /// <summary>Invokes a void instance method taking one boolean argument.</summary>
    /// <remarks>
    /// A separate overload rather than a generic one because JNI passes arguments by their exact
    /// primitive width; handing a bool through the object-argument overload above would corrupt the
    /// call frame rather than fail to compile.
    /// </remarks>
    public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, bool arg)
    {
        var env = (JNIEnv*)envPtr;
        var callVoidMethodA = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, JValue*, void>)env->Functions[63];
        var args = stackalloc JValue[1];
        args[0].Z = arg ? (byte)1 : (byte)0;
        callVoidMethodA(envPtr, obj, methodId, args);
    }

    /// <summary>Allocates a Java byte array, returned as a LOCAL reference.</summary>
    public static IntPtr NewByteArray(IntPtr envPtr, int len)
    {
        var env = (JNIEnv*)envPtr;
        // NewByteArray is at index 176
        var newByteArray = (delegate* unmanaged<IntPtr, int, IntPtr>)env->Functions[176];
        return newByteArray(envPtr, len);
    }

    /// <summary>Copies managed bytes into a Java array previously allocated by JNI.</summary>
    /// <remarks>
    /// Bounds are checked by the JVM, not here: an out-of-range region raises a Java exception
    /// rather than returning an error, which then makes the next JNI call fatal.
    /// </remarks>
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

    /// <summary>Attaches the calling native thread to the JVM so it can make JNI calls.</summary>
    /// <remarks>
    /// A JNIEnv is per-thread and cannot be shared, so any thread this library creates must attach
    /// before calling into Java and <see cref="DetachCurrentThread"/> before it exits - an attached
    /// thread that dies without detaching keeps the JVM from shutting down cleanly.
    /// </remarks>
    /// <returns>JNI_OK (0) on success.</returns>
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

    /// <summary>Attaches as a daemon thread, which does not hold the JVM open on shutdown.</summary>
    /// <remarks>The right choice for background workers that may outlive the work that started them.</remarks>
    /// <returns>JNI_OK (0) on success.</returns>
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

    /// <summary>Detaches the calling thread, invalidating its JNIEnv.</summary>
    /// <returns>JNI_OK (0) on success.</returns>
    public static int DetachCurrentThread(IntPtr vmPtr)
    {
        var vm = (JavaVM*)vmPtr;
        // DetachCurrentThread is at index 5
        var detachCurrentThread = (delegate* unmanaged<IntPtr, int>)vm->Functions[5];
        return detachCurrentThread(vmPtr);
    }

    [DllImport("log", EntryPoint = "__android_log_print", CallingConvention = CallingConvention.Cdecl)]
    private static extern int __android_log_print(int prio, string tag, string fmt, string msg);

    /// <summary>Writes an error to logcat directly, bypassing JNI entirely.</summary>
    /// <remarks>
    /// Deliberately not routed through Java: this is what remains usable when the JNI path itself is
    /// broken, when no thread is attached, or when an exception is pending and any JNI call would
    /// abort the process. It is the diagnostic of last resort at this boundary.
    /// </remarks>
    public static void AndroidLogE(string tag, string message)
    {
        __android_log_print(6, tag, "%s", message);
    }
}
