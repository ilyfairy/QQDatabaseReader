using System;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class ProtobufAnalyzerDialog : Window
{
    private readonly ProtobufAnalyzerDialogViewModel _viewModel;
    private readonly IClipboardService _clipboard;

    public ProtobufAnalyzerDialog() : this(
        new ProtobufAnalyzerDialogViewModel(),
        null!)
    {
    }

    public ProtobufAnalyzerDialog(
        ProtobufAnalyzerDialogViewModel viewModel,
        IClipboardService clipboard)
    {
        DataContext = viewModel;
        _viewModel = viewModel;
        _clipboard = clipboard ?? App.Host.Services.GetRequiredService<IClipboardService>();
        InitializeComponent();

        KeyDown += OnKeyDown;
        Opened += OnOpened;
    }

    public void LoadProtobuf(string? base64)
    {
        if (!string.IsNullOrEmpty(base64))
            _viewModel.LoadFromBase64(base64);
    }

    public void LoadProtobufHex(string? hex)
    {
        if (!string.IsNullOrEmpty(hex))
            _viewModel.LoadFromHex(hex);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
        }
    }

    private void FieldTreeNode_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: ProtobufFieldNode node } owner)
            return;

        var value = node.CopyValue ?? node.DecodedValue;
        var copyValueMenuItem = new MenuItem
        {
            Header = "复制值",
            IsEnabled = !string.IsNullOrEmpty(value),
        };

        copyValueMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(value))
                await _clipboard.SetTextAsync(value);
        };

        var contextMenu = new ContextMenu
        {
            ItemsSource = new Control[] { copyValueMenuItem },
        };

        OpenContextMenu(owner, contextMenu);
        e.Handled = true;
    }

    private static void OpenContextMenu(Control owner, ContextMenu contextMenu)
    {
        var previousContextMenu = owner.ContextMenu;

        contextMenu.Closed += (_, _) =>
        {
            if (ReferenceEquals(owner.ContextMenu, contextMenu))
                owner.ContextMenu = previousContextMenu;
        };

        owner.ContextMenu = contextMenu;
        contextMenu.Open(owner);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10))
            TryEnableDarkTitleBar();
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                int useDarkMode = 1;
                NativeMethods.DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            }
        }
        catch { }
    }
}

internal static partial class NativeMethods
{
    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(
        nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
