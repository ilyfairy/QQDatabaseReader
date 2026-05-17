using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using Ursa.Controls;

namespace QQDatabaseExplorer.Views;

public partial class ForwardedMessageDialog : UrsaWindow
{
    private readonly IClipboardService _clipboard;

    public ForwardedMessageDialog(
        ForwardedMessageCard card,
        IReadOnlyList<AvaQQMessage> messages)
    {
        InitializeComponent();
        _clipboard = App.Host.Services.GetRequiredService<IClipboardService>();

        var title = CreateDialogTitle(card.Title);
        Title = title;
        DialogTitleTextBlock.Text = title;
        MessageItemsControl.ItemsSource = messages.Count > 0
            ? messages
            : CreatePreviewFallback(card);
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

    private void MessageBubble_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } control)
            return;

        if (FindSourceSelectableTextBlock(e) is { } textBlock)
        {
            OpenMessageTextContextMenu(textBlock, message, e, textBlock);
            return;
        }

        e.Handled = true;
        OpenCopyContextMenu(control, MessageCopyPayload.FromMessage(message));
    }

    private void MessageText_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not MessageSelectableTextBlock { DataContext: AvaQQMessage message } textBlock)
            return;

        OpenMessageTextContextMenu(textBlock, message, e, textBlock);
    }

    private async void MessageText_CopyingToClipboard(object? sender, RoutedEventArgs e)
    {
        if (sender is not MessageSelectableTextBlock textBlock)
            return;

        var selectedPayload = MessageInlineRenderer.GetSelectedPayload(textBlock);
        if (!selectedPayload.HasContent)
            return;

        e.Handled = true;
        await _clipboard.SetMessagePayloadAsync(this, selectedPayload);
    }

    private void MessageText_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not MessageSelectableTextBlock { DataContext: AvaQQMessage message } textBlock)
            return;

        var forwardedSegment = MessageInlineRenderer.GetForwardedMessageSegmentAt(textBlock, e.GetPosition(textBlock));
        if (forwardedSegment?.ForwardedMessage is { } card)
        {
            e.Handled = true;
            var dialog = new ForwardedMessageDialog(card, message.ForwardedMessages);
            dialog.Show(this);
            return;
        }

        var sharedContactSegment = MessageInlineRenderer.GetSharedContactSegmentAt(textBlock, e.GetPosition(textBlock));
        if (sharedContactSegment?.SharedContact?.JumpUrl is not { Length: > 0 } jumpUrl)
            return;

        e.Handled = true;
        _ = OpenUriAsync(jumpUrl);
    }

    private void OpenMessageTextContextMenu(
        MessageSelectableTextBlock textBlock,
        AvaQQMessage message,
        ContextRequestedEventArgs e,
        Control menuOwner)
    {
        e.Handled = true;

        var selectedPayload = IsContextRequestInsideSelection(textBlock, e)
            ? MessageInlineRenderer.GetSelectedPayload(textBlock)
            : MessageCopyPayload.Empty;

        OpenCopyContextMenu(
            menuOwner,
            selectedPayload.HasContent ? selectedPayload : MessageCopyPayload.FromMessage(message));
    }

    private void OpenCopyContextMenu(Control owner, MessageCopyPayload copyPayload)
    {
        var copyMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = copyPayload.HasContent,
        };
        copyMenuItem.Click += async (_, _) => await _clipboard.SetMessagePayloadAsync(this, copyPayload);

        OpenContextMenu(owner, new ContextMenu { ItemsSource = new Control[] { copyMenuItem } });
    }

    private static MessageSelectableTextBlock? FindSourceSelectableTextBlock(ContextRequestedEventArgs e)
    {
        return e.Source switch
        {
            MessageSelectableTextBlock textBlock => textBlock,
            Visual visual => visual.FindAncestorOfType<MessageSelectableTextBlock>(includeSelf: true),
            _ => null,
        };
    }

    private static bool IsContextRequestInsideSelection(MessageSelectableTextBlock textBlock, ContextRequestedEventArgs e)
    {
        if (textBlock.SelectionStart == textBlock.SelectionEnd)
            return false;

        if (!e.TryGetPosition(textBlock, out var position))
            return true;

        var hit = textBlock.HitTestText(position);
        var firstSelection = Math.Min(textBlock.SelectionStart, textBlock.SelectionEnd);
        var lastSelection = Math.Max(textBlock.SelectionStart, textBlock.SelectionEnd);

        return hit.TextPosition >= firstSelection && hit.TextPosition <= lastSelection;
    }

    private static void OpenContextMenu(Control owner, ContextMenu contextMenu)
    {
        var previousContextMenu = owner.ContextMenu;
        contextMenu.Closed += (_, _) =>
        {
            if (ReferenceEquals(owner.ContextMenu, contextMenu))
            {
                owner.ContextMenu = previousContextMenu;
            }
        };

        owner.ContextMenu = contextMenu;
        contextMenu.Open(owner);
    }

    private async System.Threading.Tasks.Task OpenUriAsync(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            await launcher.LaunchUriAsync(uri);
        }
        catch
        {
        }
    }

    private static string CreateDialogTitle(string? cardTitle)
    {
        if (string.IsNullOrWhiteSpace(cardTitle))
            return "群聊的转发记录";

        var title = cardTitle.Trim();
        return title.Contains("聊天记录", StringComparison.Ordinal)
            ? title.Replace("聊天记录", "转发记录", StringComparison.Ordinal)
            : title;
    }

    private static IReadOnlyList<AvaQQMessage> CreatePreviewFallback(ForwardedMessageCard card)
    {
        if (card.PreviewLines.Count == 0)
        {
            return
            [
                new AvaQQMessage
                {
                    Name = "系统",
                    Segments =
                    [
                        AvaQQMessageSegment.CreateUnsupportedText("[本地没有缓存完整转发内容，只能显示卡片摘要]")
                    ],
                    DisplayText = "[本地没有缓存完整转发内容，只能显示卡片摘要]",
                },
            ];
        }

        return card.PreviewLines
            .Select(line =>
            {
                var (name, text) = SplitPreviewLine(line);
                return new AvaQQMessage
                {
                    Name = name,
                    Segments = [AvaQQMessageSegment.CreateText(text)],
                    DisplayText = text,
                    IsHoverTimeVisible = true,
                };
            })
            .ToArray();
    }

    private static (string Name, string Text) SplitPreviewLine(string line)
    {
        var index = line.IndexOf(':');
        if (index <= 0 || index >= line.Length - 1)
            return ("预览", line);

        return (line[..index].Trim(), line[(index + 1)..].TrimStart());
    }
}
