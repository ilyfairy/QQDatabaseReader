using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace QQDatabaseExplorer.Utilities;

public static class ClipboardFileWriter
{
    public static bool TrySetFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        return OperatingSystem.IsWindows() &&
               WindowsClipboardFileWriter.TrySetFile(Path.GetFullPath(filePath));
    }

    private static class WindowsClipboardFileWriter
    {
        private const uint CfHdrop = 15;
        private const uint GmemMoveable = 0x0002;
        private const uint GmemZeroinit = 0x0040;
        private const uint CopyDropEffect = 1;
        private const int DropFilesHeaderSize = 20;

        public static bool TrySetFile(string filePath)
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        return SetFileCore(filePath);
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }

                Thread.Sleep(25 * (attempt + 1));
            }

            return false;
        }

        private static bool SetFileCore(string filePath)
        {
            var dropFilesHandle = CreateDropFilesHandle(filePath);
            if (dropFilesHandle == IntPtr.Zero)
                return false;

            var preferredDropEffectFormat = RegisterClipboardFormatW("Preferred DropEffect");
            var dropEffectHandle = preferredDropEffectFormat == 0
                ? IntPtr.Zero
                : CreateDropEffectHandle();

            if (!EmptyClipboard())
            {
                GlobalFree(dropFilesHandle);
                if (dropEffectHandle != IntPtr.Zero)
                    GlobalFree(dropEffectHandle);

                return false;
            }

            if (SetClipboardData(CfHdrop, dropFilesHandle) == IntPtr.Zero)
            {
                GlobalFree(dropFilesHandle);
                if (dropEffectHandle != IntPtr.Zero)
                    GlobalFree(dropEffectHandle);

                return false;
            }

            if (dropEffectHandle != IntPtr.Zero &&
                SetClipboardData(preferredDropEffectFormat, dropEffectHandle) == IntPtr.Zero)
            {
                GlobalFree(dropEffectHandle);
            }

            return true;
        }

        private static IntPtr CreateDropFilesHandle(string filePath)
        {
            var filePathBytes = Encoding.Unicode.GetBytes(filePath);
            var byteCount = DropFilesHeaderSize + filePathBytes.Length + 4;
            var handle = GlobalAlloc(GmemMoveable | GmemZeroinit, (UIntPtr)byteCount);
            if (handle == IntPtr.Zero)
                return IntPtr.Zero;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return IntPtr.Zero;
            }

            try
            {
                Marshal.WriteInt32(pointer, 0, DropFilesHeaderSize);
                Marshal.WriteInt32(pointer, 4, 0);
                Marshal.WriteInt32(pointer, 8, 0);
                Marshal.WriteInt32(pointer, 12, 0);
                Marshal.WriteInt32(pointer, 16, 1);
                Marshal.Copy(filePathBytes, 0, IntPtr.Add(pointer, DropFilesHeaderSize), filePathBytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            return handle;
        }

        private static IntPtr CreateDropEffectHandle()
        {
            var handle = GlobalAlloc(GmemMoveable | GmemZeroinit, (UIntPtr)sizeof(uint));
            if (handle == IntPtr.Zero)
                return IntPtr.Zero;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return IntPtr.Zero;
            }

            try
            {
                Marshal.WriteInt32(pointer, unchecked((int)CopyDropEffect));
            }
            finally
            {
                GlobalUnlock(handle);
            }

            return handle;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint RegisterClipboardFormatW(string lpszFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
