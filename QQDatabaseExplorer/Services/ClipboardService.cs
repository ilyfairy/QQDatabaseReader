using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public class ClipboardService : IClipboardService
{
    private static readonly DataFormat<byte[]> HtmlDataFormat = DataFormat.CreateBytesPlatformFormat("HTML Format");
    private static readonly DataFormat<byte[]> TextHtmlDataFormat = DataFormat.CreateBytesPlatformFormat("text/html");
    private static readonly DataFormat<byte[]> QQRichEditDataFormat = DataFormat.CreateBytesPlatformFormat("QQ_Unicode_RichEdit_Format");
    private static readonly DataFormat<byte[]> QQMultiMsgRichEditDataFormat = DataFormat.CreateBytesPlatformFormat("QQ_MultiMsg_RichEdit_Format");

    private IClipboard? _cachedClipboard;

    public async Task SetTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    public async Task SetMessagePayloadAsync(Control owner, MessageCopyPayload payload)
    {
        if (!payload.HasContent)
            return;

        if (payload.SingleLocalImagePath is { } singleImagePath)
        {
            await SetImageAsync(owner, singleImagePath);
            return;
        }

        var clipboard = GetClipboard(owner);
        if (clipboard is null)
        {
            await SetTextAsync(payload.PlainText);
            return;
        }

        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(HtmlDataFormat, CreateClipboardHtml(payload.Html));
        item.Set(TextHtmlDataFormat, Encoding.UTF8.GetBytes(payload.Html));
        item.Set(DataFormat.Text, payload.PlainText);
        if (payload.QQRichEditBytes is { Length: > 0 } qqRichEditBytes)
        {
            item.Set(QQRichEditDataFormat, qqRichEditBytes);
        }

        transfer.Add(item);

        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }

    public async Task SetMessageBatchPayloadAsync(Control owner, MessageBatchCopyPayload payload)
    {
        if (!payload.HasContent)
            return;

        var clipboard = GetClipboard(owner);
        if (clipboard is null)
        {
            await SetTextAsync(payload.PlainText);
            return;
        }

        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(HtmlDataFormat, CreateClipboardHtml(payload.HtmlFragment));
        item.Set(TextHtmlDataFormat, Encoding.UTF8.GetBytes(payload.HtmlDocument));
        item.Set(DataFormat.Text, payload.PlainText);
        if (payload.QQMultiMsgBytes is { Length: > 0 } qqMultiMsgBytes)
        {
            item.Set(QQMultiMsgRichEditDataFormat, qqMultiMsgBytes);
        }

        transfer.Add(item);

        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }

    public async Task SetImageAsync(Control owner, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return;

        if (TopLevel.GetTopLevel(owner) is not { } topLevel ||
            topLevel.Clipboard is not { } clipboard)
        {
            return;
        }

        var transfer = new DataTransfer();
        var item = new DataTransferItem();

        item.Set(DataFormat.Bitmap, () => TryCreateBitmap(imagePath));
        transfer.Add(item);

        if (await topLevel.StorageProvider.TryGetFileFromPathAsync(imagePath) is { } storageFile)
            transfer.Add(DataTransferItem.CreateFile(storageFile));

        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }

    private IClipboard? GetClipboard(Control owner)
    {
        return TopLevel.GetTopLevel(owner)?.Clipboard ?? GetClipboard();
    }

    private IClipboard? GetClipboard()
    {
        if (_cachedClipboard is not null)
            return _cachedClipboard;

        var window = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;

        if (window is null) return null;

        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is not null)
            _cachedClipboard = clipboard;

        return clipboard;
    }

    private static Bitmap? TryCreateBitmap(string imagePath)
    {
        try
        {
            using var stream = LocalImageFile.OpenDisplayStream(imagePath);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] CreateClipboardHtml(string fragment)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        const string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:0000000000\r\n" +
            "EndHTML:0000000000\r\n" +
            "StartFragment:0000000000\r\n" +
            "EndFragment:0000000000\r\n";

        var htmlPrefix = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>{startMarker}";
        var htmlSuffix = $"{endMarker}</body></html>";
        var html = htmlPrefix + fragment + htmlSuffix;

        var startHtml = Encoding.UTF8.GetByteCount(headerTemplate);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(htmlPrefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(html);

        var header =
            "Version:0.9\r\n" +
            $"StartHTML:{startHtml:0000000000}\r\n" +
            $"EndHTML:{endHtml:0000000000}\r\n" +
            $"StartFragment:{startFragment:0000000000}\r\n" +
            $"EndFragment:{endFragment:0000000000}\r\n";

        return Encoding.UTF8.GetBytes(header + html);
    }
}
