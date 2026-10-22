using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NotepadRedo;

/// <summary>
/// Gives the windows of an instance running from a local copy (<see cref="LocalCopy"/>) the taskbar
/// identity of the exe the user started. Without it, the taskbar knows them only by the copy they
/// run from: a pin made from one would launch that copy, which a later build's sweep deletes
/// (<see cref="OldBuilds"/>). With it, they group under an ID of their own, and a pin made from them
/// launches the original. (A pin made on the exe file itself is another matter: Windows derives its
/// ID from the path by an undocumented rule, so it can't be matched, and shows as a separate button.)
/// </summary>
internal static class TaskbarIdentity
{
    /// <summary>Give every window this process shows the identity of <paramref name="exe"/>: the
    /// process's ID at once, before any window exists, and each window's relaunch command as it
    /// loads. Call once, at startup, before the first window.</summary>
    public static void ApplyToEveryWindow(string exe)
    {
        try { Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(IdFor(exe))); }
        catch (Exception ex) { CrashLog.Log("Setting the process's taskbar identity failed", ex); }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window && PresentationSource.FromVisual(window) is HwndSource source
                    && Applied.Add(source.Handle))
                {
                    Apply(source.Handle, exe);
                    source.AddHook(ClearOnDestroy);
                }
            }));
    }

    /// <summary>The windows given the identity, so that one loaded again isn't hooked twice.</summary>
    private static readonly HashSet<IntPtr> Applied = new();

    private static IntPtr ClearOnDestroy(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DESTROY = 0x0002;
        if (msg == WM_DESTROY)
        {
            Clear(hwnd);
            Applied.Remove(hwnd);
        }
        return IntPtr.Zero;
    }

    /// <summary>Set <paramref name="hwnd"/>'s AppUserModel ID and relaunch command to those of
    /// <paramref name="exe"/>. Best-effort: a failure costs only the pin, so it is logged.</summary>
    private static void Apply(IntPtr hwnd, string exe)
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
    private static void Clear(IntPtr hwnd)
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
