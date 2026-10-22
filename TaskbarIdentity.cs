using System.IO;
using System.Runtime.InteropServices;

namespace NotepadRedo;

/// <summary>
/// Gives a window of an instance running from a local copy (<see cref="LocalCopy"/>) the taskbar
/// identity of the exe the user started. Without it, the taskbar knows the window only by the copy
/// it runs from: a pin made from it would launch that copy, which the next build's sweep deletes
/// (<see cref="OldBuilds"/>), and the copy's windows wouldn't group with a pin of the original.
/// </summary>
internal static class TaskbarIdentity
{
    /// <summary>Set <paramref name="hwnd"/>'s AppUserModel ID and relaunch command to those of
    /// <paramref name="exe"/>. Best-effort: a failure costs only the pin, so it is logged.</summary>
    public static void Apply(IntPtr hwnd, string exe)
    {
        try
        {
            Write(hwnd, store =>
            {
                // Windows ignores the relaunch properties without an ID. The ID goes last, so that
                // they are in place when it takes effect.
                Set(store, RelaunchCommand, $"\"{exe}\"");
                Set(store, RelaunchDisplayName, "NotepadRedo");
                Set(store, RelaunchIcon, $"{exe},0");
                Set(store, AppId, IdFor(exe));
            });
        }
        catch (Exception ex) { CrashLog.Log("Setting the window's taskbar identity failed", ex); }
    }

    /// <summary>Remove what <see cref="Apply"/> set. Windows asks for this before the window is
    /// destroyed.</summary>
    public static void Clear(IntPtr hwnd)
    {
        try
        {
            Write(hwnd, store =>
            {
                foreach (var key in new[] { AppId, RelaunchCommand, RelaunchDisplayName, RelaunchIcon })
                {
                    var key2 = key;
                    var empty = new PropVariant();   // VT_EMPTY
                    store.SetValue(ref key2, ref empty);
                }
            });
        }
        catch (Exception ex) { CrashLog.Log("Clearing the window's taskbar identity failed", ex); }
    }

    /// <summary>The AppUserModel ID for windows started for <paramref name="exe"/>: from the exe's
    /// name, so each themed variant groups by itself, as it would running from its own file.</summary>
    internal static string IdFor(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_');
        return "NotepadRedo." + string.Concat(name);
    }

    private static void Write(IntPtr hwnd, Action<IPropertyStore> change)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            change(store);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static void Set(IPropertyStore store, PropertyKey key, string value)
    {
        var variant = new PropVariant { Type = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant)); }
        finally { Marshal.FreeCoTaskMem(variant.Pointer); }
    }

    // System.AppUserModel.* (propkey.h).
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly PropertyKey RelaunchCommand = new(AppUserModel, 2);
    private static readonly PropertyKey RelaunchIcon = new(AppUserModel, 3);
    private static readonly PropertyKey RelaunchDisplayName = new(AppUserModel, 4);
    private static readonly PropertyKey AppId = new(AppUserModel, 5);
    private const ushort VtLpwstr = 31;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>A PROPVARIANT holding at most a pointer: 16 bytes on x86, 24 on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        public IntPtr Pointer;
        private IntPtr _rest;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
