using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Ursa.Controls;

namespace QQDatabaseExplorer.Views;

public partial class ImagePreviewDialog : UrsaWindow
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 (Win11), 19 (Win10 pre-20H1)
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);

    public ImagePreviewDialog(string imagePath)
    {
        InitializeComponent();
        PreviewImageViewer.SourcePath = imagePath;
        Closed += OnClosed;
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        EnableDarkTitleBar();
    }

    private void EnableDarkTitleBar()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (TryGetPlatformHandle() is { } handle)
        {
            int useDarkMode = 1;
            _ = DwmSetWindowAttribute(
                handle.Handle,
                DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref useDarkMode,
                Marshal.SizeOf<int>());
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        PreviewImageViewer.SourcePath = null;
    }
}
