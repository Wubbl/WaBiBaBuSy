using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WaBiBaBuSy.WallpaperEngine.Native;

/// <summary>
/// Where the Recycle Bin icon sits on the desktop (BiBaBo's easter egg, design §8): the desktop's shell view through
/// <c>IShellWindows.FindWindowSW(SWC_DESKTOP)</c> → <c>IShellBrowser.QueryActiveShellView</c> → <c>IFolderView</c>, the
/// item whose parsing name is the Recycle Bin's CLSID (locale-independent). Screen pixels; null when the bin is not on the
/// desktop or the shell cannot be asked. Call from an STA thread.
/// </summary>
public static class RecycleBinLocator
{
    private const string RecycleBinParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SVGIO_ALLVIEW = 2, SHGDN_FORPARSING = 0x8000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    public static Rectangle? Find()
    {
        object? shellWindows = null;
        try
        {
            shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!);
            var windows = (IShellWindows)shellWindows!;
            object loc = 0, root = Type.Missing;   // CSIDL_DESKTOP
            var dispatch = windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            var provider = (IServiceProvider)dispatch;
            var sid = SID_STopLevelBrowser;
            var iid = typeof(IShellBrowser).GUID;
            provider.QueryService(ref sid, ref iid, out var browserObj);
            var browser = (IShellBrowser)browserObj;
            browser.QueryActiveShellView(out var view);
            var folderView = (IFolderView)view;

            SHGetDesktopFolder(out var desktop);
            folderView.ItemCount(SVGIO_ALLVIEW, out int count);
            for (int i = 0; i < count; i++)
            {
                if (folderView.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero) continue;
                try
                {
                    if (!IsRecycleBin(desktop, pidl)) continue;
                    folderView.GetItemPosition(pidl, out var pt);
                    var spacing = new POINT();
                    folderView.GetSpacing(ref spacing);
                    // Positions are in the desktop list view's client space, whose origin is the virtual screen's top-left.
                    int x = pt.X + GetSystemMetrics(SM_XVIRTUALSCREEN), y = pt.Y + GetSystemMetrics(SM_YVIRTUALSCREEN);
                    return new Rectangle(x, y, Math.Max(32, spacing.X), Math.Max(32, spacing.Y));
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecycleBin] Shell query failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (shellWindows != null) Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    private static bool IsRecycleBin(IShellFolder desktop, IntPtr pidl)
    {
        if (desktop.GetDisplayNameOf(pidl, SHGDN_FORPARSING, out var strret) != 0) return false;
        var buffer = new System.Text.StringBuilder(260);
        if (StrRetToBuf(ref strret, pidl, buffer, buffer.Capacity) != 0) return false;
        return string.Equals(buffer.ToString(), RecycleBinParsingName, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Explicit, Size = 272)]
    private struct STRRET { [FieldOffset(0)] public uint uType; }

    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder folder);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrRetToBuf(ref STRRET pstr, IntPtr pidl, System.Text.StringBuilder buf, int cch);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
        void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, ref object pvarLoc);
        void OnActivated(int lCookie, bool fActive);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In] ref object pvarLoc, [In] ref object pvarLocRoot, int swClass, out int pHwnd, int swfwOptions);
        void OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
        void ProcessAttachDetach(bool fAttach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(bool enter);
        // IShellBrowser — only QueryActiveShellView is called; the others keep the vtable order
        [PreserveSig] int InsertMenusSB();
        [PreserveSig] int SetMenuSB();
        [PreserveSig] int RemoveMenusSB();
        [PreserveSig] int SetStatusTextSB();
        [PreserveSig] int EnableModelessSB();
        [PreserveSig] int TranslateAcceleratorSB();
        [PreserveSig] int BrowseObject();
        [PreserveSig] int GetViewStateStream();
        [PreserveSig] int GetControlWindow();
        [PreserveSig] int SendControlMsg();
        void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object ppshv);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Item(int index, out IntPtr ppidl);
        [PreserveSig] int ItemCount(int flags, out int count);
        [PreserveSig] int Items(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int item);
        [PreserveSig] int GetFocusedItem(out int item);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT pt);
        [PreserveSig] int GetSpacing(ref POINT pt);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName();
        [PreserveSig] int EnumObjects();
        [PreserveSig] int BindToObject();
        [PreserveSig] int BindToStorage();
        [PreserveSig] int CompareIDs();
        [PreserveSig] int CreateViewObject();
        [PreserveSig] int GetAttributesOf();
        [PreserveSig] int GetUIObjectOf();
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, int flags, out STRRET name);
    }
}
