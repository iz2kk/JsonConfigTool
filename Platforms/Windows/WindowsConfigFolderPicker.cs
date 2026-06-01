#if WINDOWS
using System.Runtime.InteropServices;
using ConfigTool.Services;
using Microsoft.Maui.ApplicationModel;
using WinRT.Interop;

namespace ConfigTool.Platforms.Windows;

public sealed class WindowsConfigFolderPicker : IConfigFolderPicker
{
    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        return MainThread.InvokeOnMainThreadAsync(() => PickFolderOnUiThread(cancellationToken));
    }

    private static string? PickFolderOnUiThread(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IFileOpenDialog? dialog = null;
        IShellItem? result = null;

        try
        {
            // Native Win32 common item dialog: large Explorer-style folder picker, no WPF/WinForms dependency.
            // Create through Activator first, then cast from object.
            // Directly casting `new FileOpenDialog()` can fail compilation on some SDK/tooling versions.
            dialog = CreateFileOpenDialog();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options
                | FileOpenOptions.PickFolders
                | FileOpenOptions.ForceFileSystem
                | FileOpenOptions.PathMustExist
                | FileOpenOptions.NoChangeDir);
            dialog.SetTitle("Chọn thư mục config");
            dialog.SetOkButtonLabel("Chọn thư mục");
            dialog.SetFileNameLabel("Thư mục config");

            var ownerHwnd = TryGetMauiWindowHandle();
            var hr = dialog.Show(ownerHwnd);
            if (hr == NativeHResults.UserCancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(hr);
            cancellationToken.ThrowIfCancellationRequested();

            dialog.GetResult(out result);
            result.GetDisplayName(ShellDisplayName.FileSystemPath, out var pathPointer);
            try
            {
                var selectedPath = Marshal.PtrToStringUni(pathPointer);
                return !string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath)
                    ? selectedPath
                    : null;
            }
            finally
            {
                if (pathPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }
        }
        finally
        {
            if (result is not null)
            {
                Marshal.FinalReleaseComObject(result);
            }

            if (dialog is not null)
            {
                Marshal.FinalReleaseComObject(dialog);
            }
        }
    }

    private static IntPtr TryGetMauiWindowHandle()
    {
        try
        {
            var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            var nativeWindow = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            return nativeWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(nativeWindow);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static class NativeHResults
    {
        public const int UserCancelled = unchecked((int)0x800704C7);
    }

    [Flags]
    private enum FileOpenOptions : uint
    {
        NoChangeDir = 0x00000008,
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800
    }

    private enum ShellDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    private static IFileOpenDialog CreateFileOpenDialog()
    {
        var dialogType = Type.GetTypeFromCLSID(FileOpenDialogClsid, throwOnError: true);
        var dialogObject = Activator.CreateInstance(dialogType!)
            ?? throw new InvalidOperationException("Không tạo được Windows native folder dialog.");

        return (IFileOpenDialog)dialogObject;
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FileOpenOptions fos);
        void GetOptions(out FileOpenOptions pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(ShellDisplayName sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
#endif
