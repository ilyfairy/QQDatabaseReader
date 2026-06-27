using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Utilities;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class MessageTab : UserControl
{
    private const double LoadPreviousThreshold = 50;
    private const double LoadNextThreshold = 50;
    private const double ScrollDirectionEpsilon = 0.5;
    private const double ReturnToLatestVisibleDistance = 240;
    private const double MessageSelectionDragThreshold = 5;
    private const double MessageSelectionCommitMinSize = 8;
    private const int ScrollAnchorRestoreAttempts = 3;
    private static readonly TimeSpan JumpHighlightHoldDuration = TimeSpan.FromMilliseconds(460);
    private static readonly TimeSpan HoverTimeDelay = TimeSpan.FromSeconds(1);

    private readonly MessageTabViewModel _viewModel;
    private readonly IClipboardService _clipboard;
    private readonly IVoicePlaybackService _voicePlaybackService;
    private readonly Dictionary<Control, CancellationTokenSource> _hoverTimeDelays = new();
    private ScrollViewer? _groupScrollViewer;
    private ScrollViewer? _messageScrollViewer;
    private bool _isLoadingFromScroll;
    private int _scrollOffsetSuppressCount;
    private int _scrollToBottomRequestId;
    private int _scrollToMessageRequestId;
    private readonly Dictionary<AvaQQMessage, int> _messageJumpHighlightVersions = new();
    private Point? _messageSelectionStartPoint;
    private bool _isMessageSelectionDragging;
    private int _messageToastRequestId;
    private TopLevel? _shortcutHost;
    private bool _isCtrlPressed;

    private sealed record ScrollAnchor(AvaQQMessage Message, double RelativeY);

    public MessageTab(
        MessageTabViewModel viewModel,
        ViewModelTokenService viewModelTokenService,
        IClipboardService clipboard,
        IVoicePlaybackService voicePlaybackService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        DataContext = viewModel;
        _viewModel = viewModel;
        _clipboard = clipboard;
        _voicePlaybackService = voicePlaybackService;
        viewModel.View = this;
        _voicePlaybackService.StateChanged += VoicePlaybackService_StateChanged;

        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            AttachMessageScrollViewer();
            AttachShortcutHost();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            DetachMessageScrollViewer();
            DetachShortcutHost();
            _voicePlaybackService.StateChanged -= VoicePlaybackService_StateChanged;
        };
    }

    private Task PlayOrStopVoiceSegmentAsync(AvaQQMessageSegment voiceSegment)
    {
        return voiceSegment.IsVoiceAvailable
            ? _voicePlaybackService.PlayOrStopAsync(voiceSegment.VoiceLocalPath)
            : Task.CompletedTask;
    }

    private void VoicePlaybackService_StateChanged(object? sender, VoicePlaybackStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => _viewModel.UpdateVoicePlaybackState(e.CurrentPlayingPath));
    }

    public void ScrollToBottom()
    {
        var requestId = ++_scrollToBottomRequestId;
        HideMessageItems();
        _ = ScrollToBottomAsync(requestId);
    }

    public void ScrollToBottomFast()
    {
        if (_messageScrollViewer is null)
            return;

        SmoothScrollViewer.ScrollTo(
            _messageScrollViewer,
            _messageScrollViewer.Offset.WithY(GetMaxVerticalOffset()),
            fast: true);
        UpdateReturnToLatestButtonVisibility();
    }

    public void ScrollToTop()
    {
        if (_messageScrollViewer is null)
            return;

        SmoothScrollViewer.ScrollTo(
            _messageScrollViewer,
            _messageScrollViewer.Offset.WithY(0),
            fast: true);
        UpdateReturnToLatestButtonVisibility();
    }

    public void ScrollToMessage(long messageId)
    {
        var requestId = ++_scrollToMessageRequestId;
        _ = ScrollToMessageAsync(messageId, requestId);
    }

    public void ScrollToMessageIfNeeded(long messageId)
    {
        var requestId = ++_scrollToMessageRequestId;
        _ = ScrollToMessageAsync(messageId, requestId, onlyWhenOutsideViewport: true);
    }

    public void HideMessagesUntilNextScrollToBottom()
    {
        _scrollToBottomRequestId++;
        _scrollToMessageRequestId++;
        HideMessageItems();
    }

    public void HideMessagesUntilNextMessageJump()
    {
        _scrollToBottomRequestId++;
        _scrollToMessageRequestId++;
        HideMessageItems();
    }

    public void ShowMessagesImmediately()
    {
        _scrollToBottomRequestId++;
        _scrollToMessageRequestId++;
        ShowMessageItems();
    }

    public async Task WaitForMessageRefreshFrameAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }

    private void AttachMessageScrollViewer()
    {
        Dispatcher.UIThread.Post(() =>
        {
            DetachMessageScrollViewer();
            _groupScrollViewer = GroupScrollViewer;
            _messageScrollViewer = MessageScrollViewer;
            if (_messageScrollViewer is not null)
            {
                _messageScrollViewer.PropertyChanged += OnMessageScrollViewerPropertyChanged;
            }

            UpdateReturnToLatestButtonVisibility();

            MessageItemsControl.AddHandler(
                Control.RequestBringIntoViewEvent,
                OnMessageContentRequestBringIntoView,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
        }, DispatcherPriority.Loaded);
    }

    private void DetachMessageScrollViewer()
    {
        MessageItemsControl.RemoveHandler(Control.RequestBringIntoViewEvent, OnMessageContentRequestBringIntoView);
        _groupScrollViewer = null;

        if (_messageScrollViewer is not null)
        {
            _messageScrollViewer.PropertyChanged -= OnMessageScrollViewerPropertyChanged;
            _messageScrollViewer = null;
        }
    }

    private void OnMessageContentRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is Visual visual &&
            visual.FindAncestorOfType<ItemsControl>(includeSelf: true) == MessageItemsControl)
        {
            e.Handled = true;
        }
    }

    private void OnMessageScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty)
        {
            OnMessageScrollOffsetChanged(e.GetNewValue<Vector>(), e.GetOldValue<Vector>());
        }
        else if (e.Property == ScrollViewer.ExtentProperty ||
                 e.Property == ScrollViewer.ViewportProperty)
        {
            UpdateReturnToLatestButtonVisibility();
        }
    }

    private void OnMessageScrollOffsetChanged(Vector offset, Vector previousOffset)
    {
        UpdateReturnToLatestButtonVisibility(offset);

        if (_messageScrollViewer is null ||
            _scrollOffsetSuppressCount > 0 ||
            _isLoadingFromScroll ||
            _viewModel.IsLoadingPrevious ||
            _viewModel.IsLoadingNext)
        {
            return;
        }

        var offsetDeltaY = offset.Y - previousOffset.Y;
        if (offsetDeltaY < -ScrollDirectionEpsilon &&
            _viewModel.HasOlderMessages &&
            offset.Y <= LoadPreviousThreshold)
        {
            LoadPreviousMessagesFromScroll();
            return;
        }

        var maxOffset = GetMaxVerticalOffset();
        if (offsetDeltaY > ScrollDirectionEpsilon &&
            _viewModel.HasNewerMessages &&
            maxOffset - offset.Y <= LoadNextThreshold)
        {
            LoadNextMessagesFromScroll();
        }
    }

    private async Task ScrollToBottomAsync(int requestId)
    {
        try
        {
            await WaitForMessageLayoutAsync();

            if (requestId != _scrollToBottomRequestId || _messageScrollViewer is null)
                return;

            SetVerticalOffset(GetMaxVerticalOffset());
        }
        finally
        {
            if (requestId == _scrollToBottomRequestId)
                ShowMessageItems();
        }
    }

    private void HideMessageItems()
    {
        MessageItemsControl.Opacity = 0;
        MessageItemsControl.IsHitTestVisible = false;
        UpdateReturnToLatestButtonVisibility();
    }

    private void ShowMessageItems()
    {
        MessageItemsControl.Opacity = 1;
        MessageItemsControl.IsHitTestVisible = true;
        UpdateReturnToLatestButtonVisibility();
    }

    public async Task ScrollToGroupAsync(AvaQQGroup group)
    {
        await WaitForMessageLayoutAsync();

        if (_groupScrollViewer is null ||
            GroupItemsControl.ContainerFromItem(group) is not { } container ||
            container.TranslatePoint(new Point(0, 0), _groupScrollViewer) is not { } relativePoint)
        {
            return;
        }

        var viewportHeight = _groupScrollViewer.Viewport.Height;
        var top = relativePoint.Y;
        var bottom = top + container.Bounds.Height;
        if (top >= 0 && bottom <= viewportHeight)
            return;

        var targetOffset = top < 0
            ? _groupScrollViewer.Offset.Y + top - 8
            : _groupScrollViewer.Offset.Y + bottom - viewportHeight + 8;
        var maxOffset = Math.Max(0, _groupScrollViewer.Extent.Height - viewportHeight);
        _groupScrollViewer.Offset = _groupScrollViewer.Offset.WithY(Math.Clamp(targetOffset, 0, maxOffset));
    }

    private async Task ScrollToMessageAsync(long messageId, int requestId, bool onlyWhenOutsideViewport = false)
    {
        try
        {
            await WaitForMessageLayoutAsync();

            if (requestId != _scrollToMessageRequestId)
                return;

            var item = MessageItemsControl.Items.Cast<object>()
                .OfType<AvaQQMessage>()
                .FirstOrDefault(v => v.MessageId == messageId);
            if (item is null)
                return;

            if (TryGetMessageRelativeBounds(item, out var relativeY, out var messageHeight))
            {
                var viewportHeight = _messageScrollViewer?.Viewport.Height ?? 0;
                if (onlyWhenOutsideViewport &&
                    relativeY >= 0 &&
                    relativeY + messageHeight <= viewportHeight)
                {
                    FlashJumpTarget(item, requestId);
                    return;
                }

                var targetCenterY = relativeY + messageHeight / 2;
                var targetOffsetY = (_messageScrollViewer?.Offset.Y ?? 0) + targetCenterY - viewportHeight / 2;
                if (onlyWhenOutsideViewport && _messageScrollViewer is not null)
                {
                    await SmoothScrollToVerticalOffsetAsync(targetOffsetY);
                }
                else
                {
                    SetVerticalOffset(targetOffsetY);
                }
            }

            FlashJumpTarget(item, requestId);
        }
        finally
        {
            if (requestId == _scrollToMessageRequestId)
                ShowMessageItems();
        }
    }

    private async void FlashJumpTarget(AvaQQMessage message, int scrollRequestId)
    {
        var version = _messageJumpHighlightVersions.GetValueOrDefault(message) + 1;
        _messageJumpHighlightVersions[message] = version;

        try
        {
            if (scrollRequestId != _scrollToMessageRequestId)
            {
                return;
            }

            message.IsJumpHighlightVisible = true;
            await Task.Delay(JumpHighlightHoldDuration);
        }
        finally
        {
            if (_messageJumpHighlightVersions.TryGetValue(message, out var currentVersion) &&
                currentVersion == version)
            {
                message.IsJumpHighlightVisible = false;
                _messageJumpHighlightVersions.Remove(message);
            }
        }
    }

    private async void LoadPreviousMessagesFromScroll()
    {
        if (_messageScrollViewer is null)
            return;

        SmoothScrollViewer.CancelAnimation(_messageScrollViewer);
        _isLoadingFromScroll = true;
        var anchor = CaptureScrollAnchor();
        var fallbackOffset = _messageScrollViewer.Offset;
        var fallbackExtentHeight = _messageScrollViewer.Extent.Height;

        try
        {
            var addedCount = await _viewModel.LoadPreviousMessagesAsync();
            if (addedCount == 0)
            {
                SetVerticalOffset(0);
                return;
            }

            RestoreScrollAnchorAfterLoad(anchor, fallbackOffset, fallbackExtentHeight);
            SmoothScrollViewer.CancelAnimation(_messageScrollViewer);
            UpdateReturnToLatestButtonVisibility();
        }
        catch (Exception ex)
        {
            ShowMessageToast($"加载历史消息失败: {ex.Message}");
        }
        finally
        {
            _isLoadingFromScroll = false;
        }
    }

    private async void LoadNextMessagesFromScroll()
    {
        if (_messageScrollViewer is null)
            return;

        SmoothScrollViewer.CancelAnimation(_messageScrollViewer);
        _isLoadingFromScroll = true;
        var anchor = CaptureScrollAnchor();
        var fallbackOffset = _messageScrollViewer.Offset;
        var fallbackExtentHeight = _messageScrollViewer.Extent.Height;

        try
        {
            var addedCount = await _viewModel.LoadNextMessagesAsync();
            if (addedCount == 0)
            {
                SetVerticalOffset(GetMaxVerticalOffset());
                return;
            }

            RestoreScrollAnchorAfterLoad(anchor, fallbackOffset, fallbackExtentHeight);
            SmoothScrollViewer.CancelAnimation(_messageScrollViewer);
            UpdateReturnToLatestButtonVisibility();
        }
        catch (Exception ex)
        {
            ShowMessageToast($"加载消息失败: {ex.Message}");
        }
        finally
        {
            _isLoadingFromScroll = false;
        }
    }

    private ScrollAnchor? CaptureScrollAnchor()
    {
        if (_messageScrollViewer is null)
            return null;

        Control? bestContainer = null;
        AvaQQMessage? bestMessage = null;
        var bestRelativeY = 0d;
        var bestDistance = double.PositiveInfinity;
        var viewportHeight = _messageScrollViewer.Viewport.Height;

        foreach (var message in MessageItemsControl.Items.Cast<object>().OfType<AvaQQMessage>())
        {
            var container = MessageItemsControl.ContainerFromItem(message);
            if (container is null)
                continue;

            var point = container.TranslatePoint(new Point(0, 0), _messageScrollViewer);
            if (point is null)
                continue;

            var top = point.Value.Y;
            var bottom = top + container.Bounds.Height;
            if (bottom < 0 || top > viewportHeight)
                continue;

            var distance = top >= 0 ? top : Math.Abs(bottom);
            if (distance >= bestDistance)
                continue;

            bestContainer = container;
            bestMessage = message;
            bestRelativeY = top;
            bestDistance = distance;
        }

        if (bestContainer is not null && bestMessage is not null)
            return new ScrollAnchor(bestMessage, bestRelativeY);

        var firstMessage = MessageItemsControl.Items.Cast<object>().OfType<AvaQQMessage>().FirstOrDefault();
        return firstMessage is null
            ? null
            : new ScrollAnchor(firstMessage, 0);
    }

    private void RestoreScrollAnchorAfterLoad(ScrollAnchor? anchor, Vector fallbackOffset, double fallbackExtentHeight)
    {
        _scrollOffsetSuppressCount++;

        try
        {
            ExecuteMessageLayoutPass();

            if (anchor is null)
            {
                RestoreFallbackOffset(fallbackOffset);
                ExecuteMessageLayoutPass();
                return;
            }

            for (var attempt = 0; attempt < ScrollAnchorRestoreAttempts; attempt++)
            {
                if (_messageScrollViewer is null)
                    return;

                if (!TryGetAnchorRelativeY(anchor.Message, out var currentRelativeY))
                    break;

                var delta = currentRelativeY - anchor.RelativeY;
                if (Math.Abs(delta) <= 0.5)
                    return;

                SetVerticalOffset(_messageScrollViewer.Offset.Y + delta);
                ExecuteMessageLayoutPass();
            }

            RestoreFallbackOffset(fallbackOffset, fallbackExtentHeight);
            ExecuteMessageLayoutPass();
        }
        finally
        {
            _scrollOffsetSuppressCount--;
        }
    }

    private bool TryGetAnchorRelativeY(AvaQQMessage message, out double relativeY)
    {
        return TryGetMessageRelativeBounds(message, out relativeY, out _);
    }

    private bool TryGetMessageRelativeBounds(AvaQQMessage message, out double relativeY, out double height)
    {
        relativeY = 0;
        height = 0;

        if (_messageScrollViewer is null || MessageItemsControl.ContainerFromItem(message) is not { } container)
            return false;

        var point = container.TranslatePoint(new Point(0, 0), _messageScrollViewer);
        if (point is null)
            return false;

        relativeY = point.Value.Y;
        height = container.Bounds.Height;
        return true;
    }

    private void RestoreFallbackOffset(Vector fallbackOffset)
    {
        SetVerticalOffset(fallbackOffset.Y);
    }

    private void RestoreFallbackOffset(Vector fallbackOffset, double fallbackExtentHeight)
    {
        if (_messageScrollViewer is null)
            return;

        var extentDelta = _messageScrollViewer.Extent.Height - fallbackExtentHeight;
        if (!double.IsFinite(extentDelta))
        {
            RestoreFallbackOffset(fallbackOffset);
            return;
        }

        SetVerticalOffset(fallbackOffset.Y + Math.Max(0, extentDelta));
    }

    private async Task WaitForMessageLayoutAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }

    private void ExecuteMessageLayoutPass()
    {
        if (!CheckAccess())
            return;

        this.GetLayoutManager()?.ExecuteLayoutPass();
    }

    private double GetMaxVerticalOffset()
    {
        if (_messageScrollViewer is null)
            return 0;

        var maxOffset = _messageScrollViewer.Extent.Height - _messageScrollViewer.Viewport.Height;
        return double.IsFinite(maxOffset) ? Math.Max(0, maxOffset) : 0;
    }

    private double ClampVerticalOffset(double offsetY)
    {
        return double.IsFinite(offsetY)
            ? Math.Clamp(offsetY, 0, GetMaxVerticalOffset())
            : 0;
    }

    private async Task SmoothScrollToVerticalOffsetAsync(double offsetY)
    {
        if (_messageScrollViewer is null)
            return;

        _scrollOffsetSuppressCount++;
        try
        {
            SmoothScrollViewer.ScrollTo(
                _messageScrollViewer,
                _messageScrollViewer.Offset.WithY(ClampVerticalOffset(offsetY)));
            await Task.Delay(TimeSpan.FromMilliseconds(240));
        }
        finally
        {
            _scrollOffsetSuppressCount--;
        }
    }

    private void SetVerticalOffset(double offsetY)
    {
        if (_messageScrollViewer is null || !double.IsFinite(offsetY))
            return;

        _scrollOffsetSuppressCount++;
        try
        {
            SmoothScrollViewer.CancelAnimation(_messageScrollViewer);
            var offset = _messageScrollViewer.Offset;
            _messageScrollViewer.Offset = offset.WithY(Math.Clamp(offsetY, 0, GetMaxVerticalOffset()));
        }
        finally
        {
            _scrollOffsetSuppressCount--;
            UpdateReturnToLatestButtonVisibility();
        }
    }

    private void UpdateReturnToLatestButtonVisibility()
    {
        UpdateReturnToLatestButtonVisibility(_messageScrollViewer?.Offset ?? default);
    }

    private void UpdateReturnToLatestButtonVisibility(Vector offset)
    {
        if (_messageScrollViewer is null)
        {
            ReturnToLatestButton.IsVisible = false;
            JumpToEarliestButton.IsVisible = false;
            return;
        }

        var hasMessages = MessageItemsControl.Items.Cast<object>().OfType<AvaQQMessage>().Any();
        if (!hasMessages || MessageItemsControl.Opacity <= 0)
        {
            ReturnToLatestButton.IsVisible = false;
            JumpToEarliestButton.IsVisible = false;
            return;
        }

        var distanceToBottom = GetMaxVerticalOffset() - offset.Y;
        ReturnToLatestButton.IsVisible =
            _viewModel.HasNewerMessages ||
            distanceToBottom > ReturnToLatestVisibleDistance;
        JumpToEarliestButton.IsVisible = _isCtrlPressed;
    }

    private void AttachShortcutHost()
    {
        var host = TopLevel.GetTopLevel(this);
        if (host is null || ReferenceEquals(host, _shortcutHost))
            return;

        DetachShortcutHost();
        _shortcutHost = host;
        host.AddHandler(KeyDownEvent, ShortcutHost_KeyChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(KeyUpEvent, ShortcutHost_KeyChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void DetachShortcutHost()
    {
        if (_shortcutHost is not null)
        {
            _shortcutHost.RemoveHandler(KeyDownEvent, ShortcutHost_KeyChanged);
            _shortcutHost.RemoveHandler(KeyUpEvent, ShortcutHost_KeyChanged);
            _shortcutHost = null;
        }

        _isCtrlPressed = false;
        if (JumpToEarliestButton is not null)
        {
            JumpToEarliestButton.IsVisible = false;
        }
    }

    private void ShortcutHost_KeyChanged(object? sender, KeyEventArgs e)
    {
        var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (_isCtrlPressed == isCtrlPressed)
            return;

        _isCtrlPressed = isCtrlPressed;
        UpdateReturnToLatestButtonVisibility();
    }

    private void MessageSelectionHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsMessageSelectionIgnoredSource(e.Source))
        {
            e.Pointer.Capture(null);
            return;
        }

        var point = e.GetCurrentPoint(MessageSelectionHost);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        ClearMessageTextSelections();

        _messageSelectionStartPoint = e.GetPosition(MessageSelectionHost);
        _isMessageSelectionDragging = false;
        e.Pointer.Capture(MessageSelectionHost);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void MessageSelectionHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (IsMessageSelectionIgnoredSource(e.Source))
        {
            FinishMessageSelectionDrag();
            return;
        }

        if (_messageSelectionStartPoint is not { } startPoint)
            return;

        var pointerPoint = e.GetCurrentPoint(MessageSelectionHost);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            FinishMessageSelectionDrag();
            return;
        }

        var currentPoint = e.GetPosition(MessageSelectionHost);
        var dragDistance = currentPoint - startPoint;
        if (!_isMessageSelectionDragging &&
            Math.Max(Math.Abs(dragDistance.X), Math.Abs(dragDistance.Y)) < MessageSelectionDragThreshold)
        {
            return;
        }

        if (!_isMessageSelectionDragging)
        {
            _isMessageSelectionDragging = true;
        }

        var selectionRect = CreateNormalizedRect(startPoint, currentPoint);
        ShowMessageSelectionRectangle(selectionRect);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void MessageSelectionHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsMessageSelectionIgnoredSource(e.Source))
        {
            e.Pointer.Capture(null);
            FinishMessageSelectionDrag();
            return;
        }

        if (_messageSelectionStartPoint is null)
            return;

        var releasePoint = e.GetPosition(MessageSelectionHost);
        if (_isMessageSelectionDragging)
        {
            var selectionRect = CreateNormalizedRect(_messageSelectionStartPoint.Value, releasePoint);
            if (IsValidMessageSelectionRect(selectionRect))
            {
                SelectMessagesIntersecting(selectionRect);
            }
        }
        else if (_viewModel.IsMessageMultiSelectMode)
        {
            if (TryGetMessageAtPoint(releasePoint) is { } message)
            {
                _viewModel.ToggleMessageSelection(message);
            }
            else
            {
                _viewModel.ClearMessageSelection();
            }
        }

        e.Pointer.Capture(null);
        FinishMessageSelectionDrag();
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void MessageSelectionHost_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        FinishMessageSelectionDrag();
    }

    private void FinishMessageSelectionDrag()
    {
        _messageSelectionStartPoint = null;
        _isMessageSelectionDragging = false;
        HideMessageSelectionRectangle();
    }

    private void ClearMessageTextSelections()
    {
        foreach (var textBlock in MessageItemsControl.GetVisualDescendants().OfType<MessageSelectableTextBlock>())
        {
            if (textBlock.SelectionStart != textBlock.SelectionEnd)
            {
                textBlock.ClearSelection();
            }
        }
    }

    private void ShowMessageSelectionRectangle(Rect rect)
    {
        Canvas.SetLeft(MessageSelectionRectangle, rect.X);
        Canvas.SetTop(MessageSelectionRectangle, rect.Y);
        MessageSelectionRectangle.Width = rect.Width;
        MessageSelectionRectangle.Height = rect.Height;
        MessageSelectionRectangle.IsVisible = true;
    }

    private void HideMessageSelectionRectangle()
    {
        MessageSelectionRectangle.IsVisible = false;
        MessageSelectionRectangle.Width = 0;
        MessageSelectionRectangle.Height = 0;
    }

    private void SelectMessagesIntersecting(Rect selectionRect)
    {
        if (_messageScrollViewer is null ||
            !IsValidMessageSelectionRect(selectionRect) ||
            TryGetMessageViewportRect() is not { } viewportRect)
        {
            return;
        }

        var effectiveSelectionRect = selectionRect.Intersect(viewportRect);
        if (effectiveSelectionRect.Width <= 0 || effectiveSelectionRect.Height <= 0)
            return;

        var selectedMessages = new List<AvaQQMessage>();
        foreach (var message in MessageItemsControl.Items.Cast<object>().OfType<AvaQQMessage>())
        {
            if (!message.CanSelect)
                continue;

            var container = MessageItemsControl.ContainerFromItem(message);
            if (container is null || !container.IsVisible)
                continue;

            if (TryGetMessageSelectionRowRect(container) is not { } itemRect ||
                !HasPositiveIntersection(itemRect, viewportRect))
                continue;

            if (GetVerticalIntersection(effectiveSelectionRect, itemRect) >= MessageSelectionCommitMinSize)
            {
                selectedMessages.Add(message);
            }
        }

        _viewModel.AddSelectedMessages(selectedMessages);
    }

    private Rect? TryGetMessageSelectionRowRect(Control container)
    {
        var topLeft = container.TranslatePoint(new Point(0, 0), MessageSelectionHost);
        if (topLeft is null)
            return null;

        var rowRect = new Rect(
            new Point(0, topLeft.Value.Y),
            new Size(MessageSelectionHost.Bounds.Width, container.Bounds.Height));
        return IsFinitePositiveRect(rowRect) ? rowRect : null;
    }

    private AvaQQMessage? TryGetMessageAtPoint(Point point)
    {
        if (TryGetMessageViewportRect() is not { } viewportRect ||
            !IsPointInside(point, viewportRect))
        {
            return null;
        }

        foreach (var message in MessageItemsControl.Items.Cast<object>().OfType<AvaQQMessage>())
        {
            if (!message.CanSelect)
                continue;

            var container = MessageItemsControl.ContainerFromItem(message);
            if (container is null || !container.IsVisible)
                continue;

            if (TryGetMessageSelectionRowRect(container) is not { } rowRect ||
                !HasPositiveIntersection(rowRect, viewportRect))
            {
                continue;
            }

            if (point.Y >= rowRect.Top && point.Y <= rowRect.Bottom)
                return message;
        }

        return null;
    }

    private Rect? TryGetMessageViewportRect()
    {
        if (_messageScrollViewer is null)
            return null;

        var topLeft = _messageScrollViewer.TranslatePoint(new Point(0, 0), MessageSelectionHost);
        if (topLeft is null)
            return null;

        var rect = new Rect(topLeft.Value, _messageScrollViewer.Bounds.Size);
        return IsFinitePositiveRect(rect) ? rect : null;
    }

    private static bool IsValidMessageSelectionRect(Rect rect)
    {
        return rect.Width >= MessageSelectionCommitMinSize &&
               rect.Height >= MessageSelectionCommitMinSize;
    }

    private static Rect CreateNormalizedRect(Point startPoint, Point endPoint)
    {
        var left = Math.Min(startPoint.X, endPoint.X);
        var top = Math.Min(startPoint.Y, endPoint.Y);
        var right = Math.Max(startPoint.X, endPoint.X);
        var bottom = Math.Max(startPoint.Y, endPoint.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static bool HasPositiveIntersection(Rect first, Rect second)
    {
        return first.Left < second.Right &&
               first.Right > second.Left &&
               first.Top < second.Bottom &&
               first.Bottom > second.Top;
    }

    private static bool IsPointInside(Point point, Rect rect)
    {
        return point.X >= rect.Left &&
               point.X <= rect.Right &&
               point.Y >= rect.Top &&
               point.Y <= rect.Bottom;
    }

    private static double GetVerticalIntersection(Rect first, Rect second)
    {
        var top = Math.Max(first.Top, second.Top);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        var height = bottom - top;
        return height > 0 ? height : 0;
    }

    private static bool IsFinitePositiveRect(Rect rect)
    {
        return double.IsFinite(rect.X) &&
               double.IsFinite(rect.Y) &&
               double.IsFinite(rect.Width) &&
               double.IsFinite(rect.Height) &&
               rect.Width > 0 &&
               rect.Height > 0;
    }

    private static bool IsMessageSelectionIgnoredSource(object? source)
    {
        if (source is not Visual visual)
            return false;

        return visual.FindAncestorOfType<MessageSelectableTextBlock>(includeSelf: true) is not null ||
               visual.FindAncestorOfType<Button>(includeSelf: true) is not null ||
               visual.GetSelfAndVisualAncestors()
                   .OfType<Control>()
                   .Any(control => string.Equals(control.Name, "ReplyPreviewBorder", StringComparison.Ordinal)) ||
               visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;
    }

    private async void MessageItem_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } control)
            return;

        if (_viewModel.AlwaysShowMessageTime)
        {
            message.IsHoverTimeVisible = true;
            return;
        }

        CancelHoverTimeDelay(control);
        var cancellationTokenSource = new CancellationTokenSource();
        _hoverTimeDelays[control] = cancellationTokenSource;

        try
        {
            await Task.Delay(HoverTimeDelay, cancellationTokenSource.Token);
            if (!cancellationTokenSource.IsCancellationRequested)
            {
                message.IsHoverTimeVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void GroupItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQGroup group })
            return;

        var point = e.GetCurrentPoint(sender as Control);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;

        var preserveRangeSelection = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _viewModel.SelectGroupRange(group, preserveRangeSelection);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.ToggleGroupSelection(group);
        }
        else
        {
            _viewModel.SelectSingleGroup(group);
        }
    }

    private void GroupItem_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQGroup group } control)
            return;

        e.Handled = true;

        var copyId = GetConversationCopyId(group);
        var copyIdMenuItem = new MenuItem
        {
            Header = IsPrivateConversation(group.ConversationType) ? "复制 QQ号" : "复制群号",
            IsEnabled = !string.IsNullOrWhiteSpace(copyId),
        };
        copyIdMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(copyId))
                await CopyTextToClipboard(copyId);
        };

        var copyNameMenuItem = new MenuItem
        {
            Header = IsPrivateConversation(group.ConversationType) ? "复制昵称" : "复制群名",
            IsEnabled = !string.IsNullOrWhiteSpace(group.GroupName),
        };
        copyNameMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(group.GroupName))
            {
                await CopyTextToClipboard(group.GroupName);
            }
        };

        var exportMenuItem = new MenuItem
        {
            Header = "导出聊天记录",
            IsEnabled = _viewModel.CanExportConversation(group),
        };
        exportMenuItem.Click += async (_, _) => await App.Host.Services.GetRequiredService<IDialogService>()
            .ShowChatExportDialog(group, _viewModel.ViewModelToken);

        var contextMenu = new ContextMenu
        {
            ItemsSource = new Control[] { copyIdMenuItem, copyNameMenuItem, new Separator(), exportMenuItem },
        };
        ContextMenuHelper.Open(control, contextMenu);
    }

    private static bool IsPrivateConversation(AvaConversationType conversationType)
    {
        return conversationType is AvaConversationType.Private or AvaConversationType.PCQQPrivate;
    }

    private static string GetConversationCopyId(AvaQQGroup group)
    {
        return group.ConversationType switch
        {
            AvaConversationType.Private or AvaConversationType.PCQQPrivate =>
                group.PrivateUin == 0 ? string.Empty : group.PrivateUin.ToString(),
            AvaConversationType.Icalingua when group.IcalinguaRoomId < 0 =>
                (-group.IcalinguaRoomId).ToString(),
            AvaConversationType.Icalingua when group.IcalinguaRoomId > 0 =>
                group.IcalinguaRoomId.ToString(),
            _ => group.GroupId == 0 ? string.Empty : group.GroupId.ToString(),
        };
    }

    private void MessageAvatar_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } control)
            return;

        e.Handled = true;

        var copyQqIdMenuItem = new MenuItem
        {
            Header = "复制 QQ号",
            IsEnabled = message.SenderId != 0,
        };
        copyQqIdMenuItem.Click += async (_, _) => await CopyTextToClipboard(message.SenderId.ToString());

        var copyGroupNickNameMenuItem = new MenuItem
        {
            Header = "复制群昵称",
            IsEnabled = !string.IsNullOrWhiteSpace(message.Name),
        };
        copyGroupNickNameMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(message.Name))
            {
                await CopyTextToClipboard(message.Name);
            }
        };

        var menuItems = new List<Control> { copyQqIdMenuItem, copyGroupNickNameMenuItem };
        AddClearFilterAndJumpMenuItem(menuItems, message);

        var contextMenu = new ContextMenu
        {
            ItemsSource = menuItems,
        };
        ContextMenuHelper.Open(control, contextMenu);
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

        if (TryGetMiniAppSegmentFromContextRequest(control, e) is { } sourceMiniAppSegment)
        {
            e.Handled = true;
            OpenMiniAppContextMenu(control, sourceMiniAppSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetImageSegmentFromContextRequest(control, e) is { } sourceImageSegment)
        {
            e.Handled = true;
            OpenImageContextMenu(control, sourceImageSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetVoiceSegmentFromContextRequest(control, e) is { } sourceVoiceSegment)
        {
            e.Handled = true;
            OpenVoiceContextMenu(control, sourceVoiceSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetVideoSegmentFromContextRequest(control, e) is { } sourceVideoSegment)
        {
            e.Handled = true;
            OpenVideoContextMenu(control, sourceVideoSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetFileSegmentFromContextRequest(control, e) is { } sourceFileSegment)
        {
            e.Handled = true;
            OpenFileContextMenu(control, sourceFileSegment, message.ProtobufBase64, message);
            return;
        }

        e.Handled = true;
        OpenCopyContextMenu(control, MessageCopyPayload.FromMessage(message), message.ProtobufBase64, message: message);
    }

    private void SystemHintSourceName_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } control)
            return;

        if (!message.SystemHintSourceIsUser &&
            !message.HasSystemHintSourceQqId)
        {
            return;
        }

        OpenSystemHintNameContextMenu(
            control,
            message,
            message.SystemHintSourceUin);
        e.Handled = true;
    }

    private void SystemHintTargetName_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } control)
            return;

        if (!message.SystemHintTargetIsUser)
            return;

        OpenSystemHintNameContextMenu(
            control,
            message,
            message.SystemHintTargetUin);
        e.Handled = true;
    }

    private async void SystemHint_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } ||
            !message.CanJumpToSystemHintTarget)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.JumpToSystemHintTargetMessageAsync(message);
    }

    private async void MessageBubble_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control ||
            TryGetImageSegmentFromTappedEvent(control, e) is not { } imageSegment ||
            imageSegment.ImageLocalPath is not { } imagePath ||
            !File.Exists(imagePath))
        {
            return;
        }

        if (!imageSegment.IsImageAvailable)
            return;

        e.Handled = true;
        await ShowImagePreviewDialog(imagePath);
    }

    private async void MessageBubble_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } control)
        {
            return;
        }

        if (TryGetSharedContactSegmentFromTappedEvent(control, e)?.SharedContact?.JumpUrl is { Length: > 0 } jumpUrl)
        {
            e.Handled = true;
            await OpenUriAsync(jumpUrl);
            return;
        }

        if (TryGetMiniAppSegmentFromTappedEvent(control, e)?.MiniApp?.JumpUrl is { Length: > 0 } miniAppUrl)
        {
            e.Handled = true;
            await OpenUriAsync(miniAppUrl);
            return;
        }

        if (TryGetForwardedMessageSegmentFromTappedEvent(control, e) is { ForwardedMessage: { } card })
        {
            e.Handled = true;
            await ShowForwardedMessageDialog(card, message.ForwardedMessages);
            return;
        }

        if (TryGetVideoSegmentFromTappedEvent(control, e) is { } videoSegment)
        {
            e.Handled = true;
            await OpenVideoFileAsync(videoSegment);
            return;
        }

        if (TryGetFileSegmentFromTappedEvent(control, e) is { } fileSegment &&
            fileSegment.IsFileAvailable &&
            !string.IsNullOrWhiteSpace(fileSegment.FileLocalPath) &&
            File.Exists(fileSegment.FileLocalPath))
        {
            e.Handled = true;
            ShellFileLocator.ShowFileInFolder(fileSegment.FileLocalPath);
            return;
        }

        return;
    }

    private void MessageItem_Tapped(object? sender, TappedEventArgs e)
    {
        if (!_viewModel.IsMessageMultiSelectMode ||
            _isMessageSelectionDragging ||
            sender is not Control { DataContext: AvaQQMessage message })
        {
            return;
        }

        if (e.Source is Visual visual &&
            visual.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        _viewModel.ToggleMessageSelection(message);
        e.Handled = true;
    }

    private void MessageText_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not MessageSelectableTextBlock { DataContext: AvaQQMessage message } textBlock)
            return;

        OpenMessageTextContextMenu(textBlock, message, e, textBlock);
    }

    private async void MessageText_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not MessageSelectableTextBlock textBlock)
            return;

        var imageSegment = TryGetImageSegmentFromTappedEvent(textBlock, e) ??
                           MessageInlineRenderer.GetImageSegmentAt(textBlock, e.GetPosition(textBlock));
        if (imageSegment?.ImageLocalPath is not { } imagePath || !File.Exists(imagePath))
            return;

        if (!imageSegment.IsImageAvailable)
            return;

        e.Handled = true;
        await ShowImagePreviewDialog(imagePath);
    }

    private async void MessageText_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not MessageSelectableTextBlock { DataContext: AvaQQMessage message } textBlock)
            return;

        var forwardedSegment = TryGetForwardedMessageSegmentFromTappedEvent(textBlock, e) ??
                               MessageInlineRenderer.GetForwardedMessageSegmentAt(textBlock, e.GetPosition(textBlock));
        if (forwardedSegment?.ForwardedMessage is { } card)
        {
            e.Handled = true;
            await ShowForwardedMessageDialog(card, message.ForwardedMessages);
            return;
        }

        var miniAppSegment = TryGetMiniAppSegmentFromTappedEvent(textBlock, e) ??
                             MessageInlineRenderer.GetMiniAppSegmentAt(textBlock, e.GetPosition(textBlock));
        if (miniAppSegment?.MiniApp?.JumpUrl is { Length: > 0 } miniAppUrl)
        {
            e.Handled = true;
            await OpenUriAsync(miniAppUrl);
            return;
        }

        var videoSegment = TryGetVideoSegmentFromTappedEvent(textBlock, e) ??
                           MessageInlineRenderer.GetVideoSegmentAt(textBlock, e.GetPosition(textBlock));
        if (videoSegment is not null)
        {
            e.Handled = true;
            await OpenVideoFileAsync(videoSegment);
            return;
        }

        var fileSegment = TryGetFileSegmentFromTappedEvent(textBlock, e) ??
                          MessageInlineRenderer.GetFileSegmentAt(textBlock, e.GetPosition(textBlock));
        if (fileSegment?.IsFileAvailable == true &&
            !string.IsNullOrWhiteSpace(fileSegment.FileLocalPath) &&
            File.Exists(fileSegment.FileLocalPath))
        {
            e.Handled = true;
            ShellFileLocator.ShowFileInFolder(fileSegment.FileLocalPath);
            return;
        }

        var sharedContactSegment = TryGetSharedContactSegmentFromTappedEvent(textBlock, e) ??
                                   MessageInlineRenderer.GetSharedContactSegmentAt(textBlock, e.GetPosition(textBlock));
        if (sharedContactSegment?.SharedContact?.JumpUrl is not { Length: > 0 } jumpUrl)
        {
            return;
        }

        e.Handled = true;
        await OpenUriAsync(jumpUrl);
    }

    private async void MessageText_VoiceSegmentPressed(object? sender, VoiceSegmentPressedEventArgs e)
    {
        await PlayOrStopVoiceSegmentAsync(e.Segment);
    }

    private async void ReplyPreview_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } ||
            message.Reply is null)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.JumpToReplyMessageAsync(message);
    }

    private async void MessageText_CopyingToClipboard(object? sender, RoutedEventArgs e)
    {
        if (sender is not MessageSelectableTextBlock textBlock)
            return;

        var selectedPayload = MessageInlineRenderer.GetSelectedPayload(textBlock);
        if (!selectedPayload.HasContent)
            return;

        e.Handled = true;
        await CopyPayloadToClipboard(selectedPayload);
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
        var mentionUin = TryGetMentionUinFromContextRequest(textBlock, message, e);
        if (selectedPayload.HasContent)
        {
            OpenCopyContextMenu(menuOwner, selectedPayload, message.ProtobufBase64, mentionUin, message);
            return;
        }

        if (TryGetImageSegmentFromContextRequest(textBlock, e) is { } imageSegment)
        {
            OpenImageContextMenu(menuOwner, imageSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetVoiceSegmentFromContextRequest(textBlock, e) is { } voiceSegment)
        {
            OpenVoiceContextMenu(menuOwner, voiceSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetVideoSegmentFromContextRequest(textBlock, e) is { } videoSegment)
        {
            OpenVideoContextMenu(menuOwner, videoSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetFileSegmentFromContextRequest(textBlock, e) is { } fileSegment)
        {
            OpenFileContextMenu(menuOwner, fileSegment, message.ProtobufBase64, message);
            return;
        }

        if (TryGetMiniAppSegmentFromContextRequest(textBlock, e) is { } miniAppSegment)
        {
            OpenMiniAppContextMenu(menuOwner, miniAppSegment, message.ProtobufBase64, message);
            return;
        }

        OpenCopyContextMenu(menuOwner, MessageCopyPayload.FromMessage(message), message.ProtobufBase64, mentionUin, message);
    }

    private void OpenMiniAppContextMenu(
        Control owner,
        AvaQQMessageSegment miniAppSegment,
        string? protobufBase64,
        AvaQQMessage message)
    {
        var contextMenu = new ContextMenu();
        var messagePayload = MessageCopyPayload.FromMessage(message);
        var copyMessageMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = messagePayload.HasContent,
        };
        copyMessageMenuItem.Click += async (_, _) => await CopyPayloadToClipboard(messagePayload);

        var copyLinkMenuItem = new MenuItem
        {
            Header = "复制链接",
            IsEnabled = !string.IsNullOrWhiteSpace(miniAppSegment.MiniApp?.JumpUrl),
        };
        copyLinkMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(miniAppSegment.MiniApp?.JumpUrl))
            {
                await CopyTextToClipboard(miniAppSegment.MiniApp.JumpUrl);
            }
        };

        var copyProtobufMenuItem = new MenuItem
        {
            Header = "复制 Protobuf Base64",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        copyProtobufMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                await CopyTextToClipboard(protobufBase64);
            }
        };

        var analyzeProtobufMenuItem = new MenuItem
        {
            Header = "分析 Protobuf",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        analyzeProtobufMenuItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                ShowProtobufAnalyzer(protobufBase64);
            }
        };

        var menuItems = new List<Control>
        {
            copyMessageMenuItem,
            copyLinkMenuItem,
        };
        AddClearFilterAndJumpMenuItem(menuItems, message);
        menuItems.AddRange(
        [
            new Separator(),
            copyProtobufMenuItem,
            analyzeProtobufMenuItem,
        ]);

        contextMenu.ItemsSource = menuItems;
        ContextMenuHelper.Open(owner, contextMenu);
    }

    private void OpenImageContextMenu(
        Control owner,
        AvaQQMessageSegment imageSegment,
        string? protobufBase64,
        AvaQQMessage message)
    {
        if (!imageSegment.IsImageAvailable)
        {
            OpenCopyContextMenu(owner, MessageCopyPayload.FromMessage(message), protobufBase64, message: message);
            return;
        }

        var contextMenu = new ContextMenu();
        var messagePayload = MessageCopyPayload.FromMessage(message);
        var copyMessageMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = messagePayload.HasContent,
        };
        copyMessageMenuItem.Click += async (_, _) => await CopyPayloadToClipboard(messagePayload);

        var copyImageMenuItem = new MenuItem
        {
            Header = "复制图片",
            IsEnabled = !string.IsNullOrWhiteSpace(imageSegment.ImageLocalPath) &&
                        File.Exists(imageSegment.ImageLocalPath),
        };
        copyImageMenuItem.Click += async (_, _) => await CopyImageToClipboard(imageSegment.ImageLocalPath);

        var locateFileMenuItem = CreateLocateFileMenuItem(imageSegment.ImageLocalPath);

        var copyProtobufMenuItem = new MenuItem
        {
            Header = "复制 Protobuf Base64",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        copyProtobufMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                await CopyTextToClipboard(protobufBase64);
            }
        };

        var analyzeProtobufMenuItem = new MenuItem
        {
            Header = "分析 Protobuf",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        analyzeProtobufMenuItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                ShowProtobufAnalyzer(protobufBase64);
            }
        };

        var menuItems = new List<Control>
        {
            copyMessageMenuItem,
            copyImageMenuItem,
            locateFileMenuItem,
        };
        AddClearFilterAndJumpMenuItem(menuItems, message);
        menuItems.AddRange(
        [
            new Separator(),
            copyProtobufMenuItem,
            analyzeProtobufMenuItem,
        ]);

        contextMenu.ItemsSource = menuItems;
        ContextMenuHelper.Open(owner, contextMenu);
    }

    private void OpenVoiceContextMenu(
        Control owner,
        AvaQQMessageSegment voiceSegment,
        string? protobufBase64,
        AvaQQMessage message)
    {
        var contextMenu = new ContextMenu();
        var messagePayload = MessageCopyPayload.FromMessage(message);
        var copyMessageMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = messagePayload.HasContent,
        };
        copyMessageMenuItem.Click += async (_, _) => await CopyPayloadToClipboard(messagePayload);

        var locateFileMenuItem = CreateLocateFileMenuItem(voiceSegment.VoiceLocalPath);

        var copyProtobufMenuItem = new MenuItem
        {
            Header = "复制 Protobuf Base64",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        copyProtobufMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                await CopyTextToClipboard(protobufBase64);
            }
        };

        var analyzeProtobufMenuItem = new MenuItem
        {
            Header = "分析 Protobuf",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        analyzeProtobufMenuItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                ShowProtobufAnalyzer(protobufBase64);
            }
        };

        var menuItems = new List<Control>
        {
            copyMessageMenuItem,
            locateFileMenuItem,
        };
        AddClearFilterAndJumpMenuItem(menuItems, message);
        menuItems.AddRange(
        [
            new Separator(),
            copyProtobufMenuItem,
            analyzeProtobufMenuItem,
        ]);

        contextMenu.ItemsSource = menuItems;
        ContextMenuHelper.Open(owner, contextMenu);
    }

    private void OpenVideoContextMenu(
        Control owner,
        AvaQQMessageSegment videoSegment,
        string? protobufBase64,
        AvaQQMessage message)
    {
        var contextMenu = new ContextMenu();
        var messagePayload = MessageCopyPayload.FromMessage(message);
        var copyMessageMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = messagePayload.HasContent,
        };
        copyMessageMenuItem.Click += async (_, _) => await CopyPayloadToClipboard(messagePayload);

        var copyVideoMenuItem = new MenuItem
        {
            Header = "复制视频",
            IsEnabled = videoSegment.IsVideoAvailable &&
                        !string.IsNullOrWhiteSpace(videoSegment.VideoLocalPath) &&
                        File.Exists(videoSegment.VideoLocalPath),
        };
        copyVideoMenuItem.Click += async (_, _) => await CopyFileToClipboard(videoSegment.VideoLocalPath);

        var openVideoMenuItem = new MenuItem
        {
            Header = "打开视频",
            IsEnabled = videoSegment.IsVideoAvailable &&
                        !string.IsNullOrWhiteSpace(videoSegment.VideoLocalPath) &&
                        File.Exists(videoSegment.VideoLocalPath),
        };
        openVideoMenuItem.Click += async (_, _) => await OpenVideoFileAsync(videoSegment);

        var locateFileMenuItem = CreateLocateFileMenuItem(videoSegment.VideoLocalPath);

        var copyProtobufMenuItem = new MenuItem
        {
            Header = "复制 Protobuf Base64",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        copyProtobufMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                await CopyTextToClipboard(protobufBase64);
            }
        };

        var analyzeProtobufMenuItem = new MenuItem
        {
            Header = "分析 Protobuf",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        analyzeProtobufMenuItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                ShowProtobufAnalyzer(protobufBase64);
            }
        };

        var menuItems = new List<Control>
        {
            copyMessageMenuItem,
            copyVideoMenuItem,
            openVideoMenuItem,
            locateFileMenuItem,
        };
        AddClearFilterAndJumpMenuItem(menuItems, message);
        menuItems.AddRange(
        [
            new Separator(),
            copyProtobufMenuItem,
            analyzeProtobufMenuItem,
        ]);

        contextMenu.ItemsSource = menuItems;
        ContextMenuHelper.Open(owner, contextMenu);
    }

    private void OpenFileContextMenu(
        Control owner,
        AvaQQMessageSegment fileSegment,
        string? protobufBase64,
        AvaQQMessage message)
    {
        var contextMenu = new ContextMenu();
        var messagePayload = MessageCopyPayload.FromMessage(message);
        var copyMessageMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = messagePayload.HasContent,
        };
        copyMessageMenuItem.Click += async (_, _) => await CopyPayloadToClipboard(messagePayload);

        var copyFileMenuItem = new MenuItem
        {
            Header = "复制文件",
            IsEnabled = fileSegment.IsFileAvailable &&
                        !string.IsNullOrWhiteSpace(fileSegment.FileLocalPath) &&
                        File.Exists(fileSegment.FileLocalPath),
        };
        copyFileMenuItem.Click += async (_, _) => await CopyFileToClipboard(fileSegment.FileLocalPath);

        var locateFileMenuItem = CreateLocateFileMenuItem(fileSegment.FileLocalPath);

        var copyProtobufMenuItem = new MenuItem
        {
            Header = "复制 Protobuf Base64",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        copyProtobufMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                await CopyTextToClipboard(protobufBase64);
            }
        };

        var analyzeProtobufMenuItem = new MenuItem
        {
            Header = "分析 Protobuf",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        analyzeProtobufMenuItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                ShowProtobufAnalyzer(protobufBase64);
            }
        };

        var menuItems = new List<Control>
        {
            copyMessageMenuItem,
            copyFileMenuItem,
            locateFileMenuItem,
        };
        AddClearFilterAndJumpMenuItem(menuItems, message);
        menuItems.AddRange(
        [
            new Separator(),
            copyProtobufMenuItem,
            analyzeProtobufMenuItem,
        ]);

        contextMenu.ItemsSource = menuItems;
        ContextMenuHelper.Open(owner, contextMenu);
    }

    private static MenuItem CreateLocateFileMenuItem(string? localPath)
    {
        var menuItem = new MenuItem
        {
            Header = "定位文件",
            IsEnabled = !string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath),
        };
        menuItem.Click += (_, _) => ShellFileLocator.ShowFileInFolder(localPath);
        return menuItem;
    }

    private void OpenCopyContextMenu(
        Control owner,
        MessageCopyPayload copyPayload,
        string? protobufBase64,
        string? mentionUin = null,
        AvaQQMessage? message = null)
    {
        var contextMenu = new ContextMenu();
        var copyMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = copyPayload.HasContent,
        };
        copyMenuItem.Click += async (_, _) => await CopyPayloadToClipboard(copyPayload);

        var copyMentionUinMenuItem = new MenuItem
        {
            Header = "复制被艾特QQ号",
            IsEnabled = IsNumericId(mentionUin),
        };
        copyMentionUinMenuItem.Click += async (_, _) =>
        {
            if (IsNumericId(mentionUin))
            {
                await CopyTextToClipboard(mentionUin!);
            }
        };

        var copyProtobufMenuItem = new MenuItem
        {
            Header = "复制 Protobuf Base64",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        copyProtobufMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                await CopyTextToClipboard(protobufBase64);
            }
        };

        var analyzeProtobufMenuItem = new MenuItem
        {
            Header = "分析 Protobuf",
            IsEnabled = !string.IsNullOrEmpty(protobufBase64),
        };
        analyzeProtobufMenuItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(protobufBase64))
            {
                ShowProtobufAnalyzer(protobufBase64);
            }
        };

        var menuItems = new List<Control> { copyMenuItem };
        if (IsNumericId(mentionUin))
            menuItems.Add(copyMentionUinMenuItem);

        AddClearFilterAndJumpMenuItem(menuItems, message);
        menuItems.AddRange(
        [
            new Separator(),
            copyProtobufMenuItem,
            analyzeProtobufMenuItem,
        ]);

        contextMenu.ItemsSource = menuItems;

        ContextMenuHelper.Open(owner, contextMenu);
    }

    private void AddClearFilterAndJumpMenuItem(List<Control> menuItems, AvaQQMessage? message)
    {
        if (!_viewModel.CanClearMessageFilterAndJumpToMessage(message))
            return;

        var menuItem = new MenuItem
        {
            Header = "清除筛选并跳转",
        };
        menuItem.Click += async (_, _) =>
        {
            if (message is not null)
                await _viewModel.ClearMessageFilterAndJumpToMessageAsync(message);
        };

        menuItems.Add(new Separator());
        menuItems.Add(menuItem);
    }

    private void OpenSystemHintNameContextMenu(
        Control owner,
        AvaQQMessage message,
        string uin)
    {
        var copyPayload = MessageCopyPayload.FromMessage(message);
        var copyMessageMenuItem = new MenuItem
        {
            Header = "复制",
            IsEnabled = copyPayload.HasContent,
        };
        copyMessageMenuItem.Click += async (_, _) => await CopyPayloadToClipboard(copyPayload);

        var copyQqIdMenuItem = new MenuItem
        {
            Header = "复制 QQ号",
            IsEnabled = IsNumericId(uin),
        };
        copyQqIdMenuItem.Click += async (_, _) =>
        {
            if (IsNumericId(uin))
            {
                await CopyTextToClipboard(uin);
            }
        };

        var copyProtobufMenuItem = new MenuItem
        {
            Header = "复制 Protobuf Base64",
            IsEnabled = !string.IsNullOrEmpty(message.ProtobufBase64),
        };
        copyProtobufMenuItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(message.ProtobufBase64))
            {
                await CopyTextToClipboard(message.ProtobufBase64);
            }
        };

        var analyzeProtobufMenuItem = new MenuItem
        {
            Header = "分析 Protobuf",
            IsEnabled = !string.IsNullOrEmpty(message.ProtobufBase64),
        };
        analyzeProtobufMenuItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(message.ProtobufBase64))
            {
                ShowProtobufAnalyzer(message.ProtobufBase64);
            }
        };

        var menuItems = new List<Control>
        {
            copyMessageMenuItem,
            copyQqIdMenuItem,
        };
        AddClearFilterAndJumpMenuItem(menuItems, message);
        menuItems.AddRange(
        [
            new Separator(),
            copyProtobufMenuItem,
            analyzeProtobufMenuItem,
        ]);

        var contextMenu = new ContextMenu
        {
            ItemsSource = menuItems,
        };
        ContextMenuHelper.Open(owner, contextMenu);
    }

    private static bool IsNumericId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.All(char.IsAsciiDigit);
    }

    private async void CopySelectedMessages_Click(object? sender, RoutedEventArgs e)
    {
        await CopySelectedMessagesToClipboard();
    }

    private void CancelMessageSelection_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ClearMessageSelection();
    }

    private async void ReturnToLatestButton_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.ReturnToLatestMessagesAsync();
    }

    private async void OpenMessageFilterButton_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.OpenMessageFilterDialogAsync();
    }

    private async void JumpToEarliestButton_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.ReturnToEarliestMessagesAsync();
    }

    private async void ClearMessageFilterButton_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.ClearMessageFilterAsync();
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

    private string? TryGetMentionUinFromContextRequest(
        MessageSelectableTextBlock textBlock,
        AvaQQMessage message,
        ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(textBlock, out var position))
            return null;

        var segment = MessageInlineRenderer.GetTextSegmentAt(textBlock, position);
        if (segment is not { IsMention: true })
            return null;

        var uin = _viewModel.ResolveMentionUin(message.GroupId, segment.MentionUid);
        return IsNumericId(uin) ? uin : null;
    }

    private static AvaQQMessageSegment? TryGetImageSegmentFromContextRequest(MessageSelectableTextBlock textBlock, ContextRequestedEventArgs e)
    {
        if (TryGetImageSegmentFromEventSource(e.Source) is { } imageSegment)
            return imageSegment;

        return e.TryGetPosition(textBlock, out var position)
            ? MessageInlineRenderer.GetImageSegmentAt(textBlock, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetVoiceSegmentFromContextRequest(MessageSelectableTextBlock textBlock, ContextRequestedEventArgs e)
    {
        if (TryGetVoiceSegmentFromEventSource(e.Source) is { } voiceSegment)
            return voiceSegment;

        return e.TryGetPosition(textBlock, out var position)
            ? MessageInlineRenderer.GetVoiceSegmentAt(textBlock, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetVideoSegmentFromContextRequest(MessageSelectableTextBlock textBlock, ContextRequestedEventArgs e)
    {
        if (TryGetVideoSegmentFromEventSource(e.Source) is { } videoSegment)
            return videoSegment;

        return e.TryGetPosition(textBlock, out var position)
            ? MessageInlineRenderer.GetVideoSegmentAt(textBlock, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetFileSegmentFromContextRequest(MessageSelectableTextBlock textBlock, ContextRequestedEventArgs e)
    {
        if (TryGetFileSegmentFromEventSource(e.Source) is { } fileSegment)
            return fileSegment;

        return e.TryGetPosition(textBlock, out var position)
            ? MessageInlineRenderer.GetFileSegmentAt(textBlock, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetMiniAppSegmentFromContextRequest(MessageSelectableTextBlock textBlock, ContextRequestedEventArgs e)
    {
        if (TryGetMiniAppSegmentFromEventSource(e.Source) is { } miniAppSegment)
            return miniAppSegment;

        return e.TryGetPosition(textBlock, out var position)
            ? MessageInlineRenderer.GetMiniAppSegmentAt(textBlock, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetImageSegmentFromContextRequest(Control root, ContextRequestedEventArgs e)
    {
        if (TryGetImageSegmentFromEventSource(e.Source) is { } imageSegment)
            return imageSegment;

        return e.TryGetPosition(root, out var position)
            ? MessageInlineRenderer.GetImageSegmentByBounds(root, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetVoiceSegmentFromContextRequest(Control root, ContextRequestedEventArgs e)
    {
        if (TryGetVoiceSegmentFromEventSource(e.Source) is { } voiceSegment)
            return voiceSegment;

        return e.TryGetPosition(root, out var position)
            ? MessageInlineRenderer.GetVoiceSegmentByBounds(root, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetVideoSegmentFromContextRequest(Control root, ContextRequestedEventArgs e)
    {
        if (TryGetVideoSegmentFromEventSource(e.Source) is { } videoSegment)
            return videoSegment;

        return e.TryGetPosition(root, out var position)
            ? MessageInlineRenderer.GetVideoSegmentByBounds(root, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetFileSegmentFromContextRequest(Control root, ContextRequestedEventArgs e)
    {
        if (TryGetFileSegmentFromEventSource(e.Source) is { } fileSegment)
            return fileSegment;

        return e.TryGetPosition(root, out var position)
            ? MessageInlineRenderer.GetFileSegmentByBounds(root, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetMiniAppSegmentFromContextRequest(Control root, ContextRequestedEventArgs e)
    {
        if (TryGetMiniAppSegmentFromEventSource(e.Source) is { } miniAppSegment)
            return miniAppSegment;

        return e.TryGetPosition(root, out var position)
            ? MessageInlineRenderer.GetMiniAppSegmentByBounds(root, position)
            : null;
    }

    private static AvaQQMessageSegment? TryGetImageSegmentFromTappedEvent(Control root, TappedEventArgs e)
    {
        return TryGetImageSegmentFromEventSource(e.Source) ??
               MessageInlineRenderer.GetImageSegmentByBounds(root, e.GetPosition(root));
    }

    private static AvaQQMessageSegment? TryGetForwardedMessageSegmentFromTappedEvent(Control root, TappedEventArgs e)
    {
        return TryGetForwardedMessageSegmentFromEventSource(e.Source) ??
               MessageInlineRenderer.GetForwardedMessageSegmentByBounds(root, e.GetPosition(root));
    }

    private static AvaQQMessageSegment? TryGetSharedContactSegmentFromTappedEvent(Control root, TappedEventArgs e)
    {
        return TryGetSharedContactSegmentFromEventSource(e.Source) ??
               MessageInlineRenderer.GetSharedContactSegmentByBounds(root, e.GetPosition(root));
    }

    private static AvaQQMessageSegment? TryGetMiniAppSegmentFromTappedEvent(Control root, TappedEventArgs e)
    {
        return TryGetMiniAppSegmentFromEventSource(e.Source) ??
               MessageInlineRenderer.GetMiniAppSegmentByBounds(root, e.GetPosition(root));
    }

    private static AvaQQMessageSegment? TryGetVideoSegmentFromTappedEvent(Control root, TappedEventArgs e)
    {
        return TryGetVideoSegmentFromEventSource(e.Source) ??
               MessageInlineRenderer.GetVideoSegmentByBounds(root, e.GetPosition(root));
    }

    private static AvaQQMessageSegment? TryGetFileSegmentFromTappedEvent(Control root, TappedEventArgs e)
    {
        return TryGetFileSegmentFromEventSource(e.Source) ??
               MessageInlineRenderer.GetFileSegmentByBounds(root, e.GetPosition(root));
    }

    private static AvaQQMessageSegment? TryGetImageSegmentFromEventSource(object? source)
    {
        return source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(MessageInlineRenderer.GetImageSegment)
                .FirstOrDefault(segment => segment is not null)
            : null;
    }

    private static AvaQQMessageSegment? TryGetVoiceSegmentFromEventSource(object? source)
    {
        return source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(MessageInlineRenderer.GetVoiceSegment)
                .FirstOrDefault(segment => segment is not null)
            : null;
    }

    private static AvaQQMessageSegment? TryGetVideoSegmentFromEventSource(object? source)
    {
        return source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(MessageInlineRenderer.GetVideoSegment)
                .FirstOrDefault(segment => segment is not null)
            : null;
    }

    private static AvaQQMessageSegment? TryGetFileSegmentFromEventSource(object? source)
    {
        return source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(MessageInlineRenderer.GetFileSegment)
                .FirstOrDefault(segment => segment is not null)
            : null;
    }

    private static AvaQQMessageSegment? TryGetForwardedMessageSegmentFromEventSource(object? source)
    {
        return source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(MessageInlineRenderer.GetForwardedMessageSegment)
                .FirstOrDefault(segment => segment is not null)
            : null;
    }

    private static AvaQQMessageSegment? TryGetSharedContactSegmentFromEventSource(object? source)
    {
        return source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(control => control.DataContext)
                .OfType<AvaQQMessageSegment>()
                .FirstOrDefault(segment => segment.Type == AvaQQMessageSegmentType.SharedContact)
            : null;
    }

    private static AvaQQMessageSegment? TryGetMiniAppSegmentFromEventSource(object? source)
    {
        return source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(MessageInlineRenderer.GetMiniAppSegment)
                .FirstOrDefault(segment => segment is not null)
            : null;
    }


    private async Task CopyTextToClipboard(string text)
    {
        await _clipboard.SetTextAsync(text);
    }

    private async Task CopyPayloadToClipboard(MessageCopyPayload payload)
    {
        await _clipboard.SetMessagePayloadAsync(this, payload);
    }

    private async Task CopySelectedMessagesToClipboard()
    {
        var selectedMessages = _viewModel.SelectedMessages;
        if (selectedMessages.Count == 0)
            return;

        var payload = MessageBatchCopyPayload.FromMessages(selectedMessages);
        await CopyBatchPayloadToClipboard(payload);
        ShowMessageToast($"已复制 {selectedMessages.Count} 条消息");
        _viewModel.ClearMessageSelection();
    }

    private async Task CopyBatchPayloadToClipboard(MessageBatchCopyPayload payload)
    {
        await _clipboard.SetMessageBatchPayloadAsync(this, payload);
    }

    private void ShowMessageToast(string message)
    {
        var requestId = ++_messageToastRequestId;
        MessageToastText.Text = message;
        MessageToastBorder.IsVisible = true;

        _ = HideMessageToastAsync(requestId);
    }

    private async Task HideMessageToastAsync(int requestId)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.6));
        if (requestId != _messageToastRequestId)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (requestId == _messageToastRequestId)
                MessageToastBorder.IsVisible = false;
        });
    }

    private async Task CopyImageToClipboard(string? imagePath)
    {
        await _clipboard.SetImageAsync(this, imagePath);
    }

    private async Task CopyFileToClipboard(string? filePath)
    {
        await _clipboard.SetFileAsync(this, filePath);
    }

    private async Task ShowImagePreviewDialog(string imagePath)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var dialog = new ImagePreviewDialog(imagePath);
        await dialog.ShowDialog(owner);
    }

    private async Task OpenUriAsync(string url)
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

    private static Task OpenVideoFileAsync(AvaQQMessageSegment videoSegment)
    {
        if (!videoSegment.IsVideoAvailable ||
            string.IsNullOrWhiteSpace(videoSegment.VideoLocalPath) ||
            !File.Exists(videoSegment.VideoLocalPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = videoSegment.VideoLocalPath,
                UseShellExecute = true,
            });
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    private Task ShowForwardedMessageDialog(
        ForwardedMessageCard card,
        IReadOnlyList<AvaQQMessage> forwardedMessages)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return Task.CompletedTask;

        var dialog = new ForwardedMessageDialog(card, forwardedMessages);
        dialog.Show(owner);
        return Task.CompletedTask;
    }

    private static void ShowProtobufAnalyzer(string protobufBase64)
    {
        try
        {
            var dialog = App.Host.Services.GetRequiredService<ProtobufAnalyzerDialog>();
            dialog.LoadProtobuf(protobufBase64);
            dialog.Show();
        }
        catch
        {
            var dialog = new ProtobufAnalyzerDialog();
            dialog.LoadProtobuf(protobufBase64);
            dialog.Show();
        }
    }

    private void MessageItem_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: AvaQQMessage message } control)
            return;

        CancelHoverTimeDelay(control);
        message.IsHoverTimeVisible = _viewModel.AlwaysShowMessageTime;
    }

    private void CancelHoverTimeDelay(Control control)
    {
        if (!_hoverTimeDelays.Remove(control, out var cancellationTokenSource))
            return;

        cancellationTokenSource.Cancel();
        cancellationTokenSource.Dispose();
    }
}
