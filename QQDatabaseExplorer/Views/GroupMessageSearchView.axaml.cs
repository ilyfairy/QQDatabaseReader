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
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class GroupMessageSearchView : UserControl
{
    private const double LoadMoreThreshold = 260;
    private readonly GroupMessageSearchViewModel _viewModel;
    private ScrollViewer? _resultScrollViewer;
    private int _resetResultScrollRequestId;

    public GroupMessageSearchView(GroupMessageSearchViewModel viewModel)
    {
        _viewModel = viewModel;
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

        var locateMenuItem = new MenuItem
        {
            Header = "定位到聊天消息",
            IsEnabled = result.GroupId != 0 && result.MessageSeq > 0,
        };
        locateMenuItem.Click += async (_, _) => await _viewModel.OpenResultInMessageTabAsync(result);

        var locateAndClearFilterMenuItem = new MenuItem
        {
            Header = "定位到聊天消息并清除筛选",
            IsEnabled = result.GroupId != 0 && result.MessageSeq > 0,
        };
        locateAndClearFilterMenuItem.Click += async (_, _) =>
            await _viewModel.OpenResultInMessageTabAndClearFilterAsync(result);

        var contextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                locateMenuItem,
                locateAndClearFilterMenuItem,
            },
        };

        OpenContextMenu(owner, contextMenu);
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

    private static ScrollViewer? FindScrollViewer(Control control)
    {
        return control.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
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
}
