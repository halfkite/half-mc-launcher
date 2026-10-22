using System;
using System.Runtime.InteropServices;

namespace HalfMcLauncher
{
    /// <summary>
    /// 使用 Windows 现代的 IFileOpenDialog（FOS_PICKFOLDERS）选择目录。
    /// 老式 FolderBrowserDialog 在部分 Windows 11 / 高 DPI 环境下会崩溃或卡死，
    /// 这里直接用系统文件对话框的“选择文件夹”模式。
    /// </summary>
    internal static class FolderPicker
    {
        private const uint FOS_PICKFOLDERS = 0x20;
        private const uint FOS_FORCEFILESYSTEM = 0x40;
        private const uint FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        public static string Pick(IntPtr owner, string title, string initialDirectory)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                dialog.GetOptions(out var options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrWhiteSpace(title)) dialog.SetTitle(title);
                if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                {
                    try
                    {
                        var iid = typeof(IShellItem).GUID;
                        SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref iid, out var folder);
                        dialog.SetFolder(folder);
                        if (folder != null) Marshal.ReleaseComObject(folder);
                    }
                    catch { }
                }
                var result = dialog.Show(owner);
                if (result != 0) return null;
                dialog.GetResult(out var item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return Marshal.PtrToStringUni(path);
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName(string name);
            void GetFileName(out IntPtr name);
            void SetTitle(string title);
            void SetOkButtonLabel(string text);
            void SetFileNameLabel(string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int alignment);
            void SetDefaultExtension(string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindingContext, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdnName, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
