using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace QQDatabaseExplorer.Utilities;

public static class ShellFileLocator
{
    public static bool ShowFileInFolder(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsShellFileLocator.ShowFileInFolder(filePath);

            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo
            {
                FileName = opener,
                ArgumentList = { directory },
                UseShellExecute = false,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static class WindowsShellFileLocator
    {
        public static bool ShowFileInFolder(string filePath)
        {
            var coInitializeResult = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            var shouldUninitialize = coInitializeResult is S_OK or S_FALSE;

            var itemIdList = ILCreateFromPathW(Path.GetFullPath(filePath));
            if (itemIdList == IntPtr.Zero)
            {
                if (shouldUninitialize)
                    CoUninitialize();

                return false;
            }

            try
            {
                return SHOpenFolderAndSelectItems(itemIdList, 0, IntPtr.Zero, 0) == 0;
            }
            finally
            {
                ILFree(itemIdList);
                if (shouldUninitialize)
                    CoUninitialize();
            }
        }

        private const int S_OK = 0;
        private const int S_FALSE = 1;
        private const uint COINIT_APARTMENTTHREADED = 0x2;

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder,
            uint cidl,
            IntPtr apidl,
            uint dwFlags);
    }
}
