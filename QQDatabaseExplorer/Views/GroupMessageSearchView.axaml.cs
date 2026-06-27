using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.ComponentModel;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Utilities;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class GroupMessageSearchView : UserControl
{
    private const double LoadMoreThreshold = 260;
    private readonly GroupMessageSearchViewModel _viewModel;
    private readonly IClipboardService _clipboard;
    private ScrollViewer? _resultScrollViewer;
    private int _resetResultScrollRequestId;

    public GroupMessageSearchView(
        GroupMessageSearchViewModel viewModel,
        IClipboardService clipboard)
    {
        _viewModel = viewModel;
        _clipboard = clipboard;
        DataContext = viewModel;
        InitializeComponent();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        AttachedToVisualTree += (_, _) => AttachScrollViewers();
    }

    private async void QueryTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await _viewModel.Search();
    }

    private async void SearchResultItem_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var result = sender is Control { DataContext: AvaGroupMessageSearchResult item }
            ? item
            : FindSearchResult(e.Source);
        if (result is null)
            return;

        e.Handled = true;
        await _viewModel.OpenResultInMessageTabAsync(result);
    }

    private void AttachScrollViewers()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_resultScrollViewer is not null)
                _resultScrollViewer.PropertyChanged -= OnResultScrollViewerPropertyChanged;

            _resultScrollViewer = FindScrollViewer(SearchResultListBox);
            if (_resultScrollViewer is not null)
                _resultScrollViewer.PropertyChanged += OnResultScrollViewerPropertyChanged;
        }, DispatcherPriority.Loaded);
    }

    private async void OnResultScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty || _resultScrollViewer is null)
            return;

        var maxOffset = Math.Max(0, _resultScrollViewer.Extent.Height - _resultScrollViewer.Viewport.Height);
        if (maxOffset - _resultScrollViewer.Offset.Y > LoadMoreThreshold)
            return;

        await _viewModel.LoadMoreResultsAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupMessageSearchViewModel.SelectedGroup))
        {
            ResetResultScrollToTop();
        }
    }

    private void ResetResultScrollToTop()
    {
        var requestId = ++_resetResultScrollRequestId;
        SetResultScrollToTop();

        Dispatcher.UIThread.Post(() =>
        {
            if (requestId == _resetResultScrollRequestId)
                SetResultScrollToTop();
        }, DispatcherPriority.Loaded);

        Dispatcher.UIThread.Post(() =>
        {
            if (requestId == _resetResultScrollRequestId)
                SetResultScrollToTop();
        }, DispatcherPriority.Background);
    }

    private void SetResultScrollToTop()
    {
        if (_resultScrollViewer is null)
            return;

        SmoothScrollViewer.CancelAnimation(_resultScrollViewer);
        _resultScrollViewer.Offset = _resultScrollViewer.Offset.WithY(0);
    }

    private void SearchResultItem_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var result = sender is Control { DataContext: AvaGroupMessageSearchResult item }
            ? item
            : FindSearchResult(e.Source);
        if (result is null || sender is not Control owner)
            return;

        e.Handled = true;

        var copyMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = result.CanLocate,
        };
        copyMenuItem.Click += async (_, _) =>
        {
            var payload = await _viewModel.CreateSearchResultCopyPayloadAsync(result);
            await _clipboard.SetMessagePayloadAsync(owner, payload);
        };

        var locateMenuItem = new MenuItem
        {
            Header = "定位到聊天消息",
            IsEnabled = result.CanLocate,
        };
        locateMenuItem.Click += async (_, _) => await _viewModel.OpenResultInMessageTabAsync(result);

        var locateAndClearFilterMenuItem = new MenuItem
        {
            Header = "定位到聊天消息并清除筛选",
            IsEnabled = result.CanLocate,
        };
        locateAndClearFilterMenuItem.Click += async (_, _) =>
            await _viewModel.OpenResultInMessageTabAndClearFilterAsync(result);

        var contextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                copyMenuItem,
                new Separator(),
                locateMenuItem,
                locateAndClearFilterMenuItem,
            },
        };

        ContextMenuHelper.Open(owner, contextMenu);
    }

    private void SearchGroupItem_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var group = sender is Control { DataContext: AvaGroupMessageSearchGroup item }
            ? item
            : FindSearchGroup(e.Source);
        if (group is null || sender is not Control owner)
            return;

        e.Handled = true;

        var (header, id) = CreateCopyConversationIdMenu(group);
        var copyMenuItem = new MenuItem
        {
            Header = header,
            IsEnabled = !string.IsNullOrWhiteSpace(id),
        };
        copyMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(id))
                await _clipboard.SetTextAsync(id);
        };

        ContextMenuHelper.Open(owner, new ContextMenu
        {
            ItemsSource = new Control[] { copyMenuItem },
        });
    }

    private static AvaGroupMessageSearchResult? FindSearchResult(object? source)
    {
        return source switch
        {
            Control { DataContext: AvaGroupMessageSearchResult result } => result,
            Visual visual => visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(control => control.DataContext)
                .OfType<AvaGroupMessageSearchResult>()
                .FirstOrDefault(),
            _ => null,
        };
    }

    private static AvaGroupMessageSearchGroup? FindSearchGroup(object? source)
    {
        return source switch
        {
            Control { DataContext: AvaGroupMessageSearchGroup group } => group,
            Visual visual => visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(control => control.DataContext)
                .OfType<AvaGroupMessageSearchGroup>()
                .FirstOrDefault(),
            _ => null,
        };
    }

    private static (string Header, string? Id) CreateCopyConversationIdMenu(AvaGroupMessageSearchGroup group)
    {
        return group.ConversationType switch
        {
            AvaConversationType.Group or AvaConversationType.PCQQGroup =>
                ("复制群号", group.GroupId == 0 ? null : group.GroupId.ToString()),
            AvaConversationType.Private or AvaConversationType.PCQQPrivate =>
                ("复制QQ号", group.PeerUin == 0 ? null : group.PeerUin.ToString()),
            AvaConversationType.AndroidMobileQQGroup =>
                ("复制群号", group.AndroidMobileQQPeerUin),
            AvaConversationType.AndroidMobileQQPrivate =>
                ("复制QQ号", group.AndroidMobileQQPeerUin),
            AvaConversationType.Icalingua when group.IcalinguaRoomId < 0 =>
                ("复制群号", (-group.IcalinguaRoomId).ToString()),
            AvaConversationType.Icalingua when group.IcalinguaRoomId > 0 =>
                ("复制QQ号", group.IcalinguaRoomId.ToString()),
            _ => ("复制QQ号", null),
        };
    }

    private static ScrollViewer? FindScrollViewer(Control control)
    {
        return control.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
    }

}
