using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace QQDatabaseExplorer.Controls;

public sealed class SmoothScrollViewer
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrollViewer, ScrollViewer, bool>(
            "IsEnabled",
            false);

    private const double WheelStep = 96;
    private const double AltWheelViewportRatio = 0.45;
    private const double AltWheelMinimumMultiplier = 2;
    private const double FastAnimationMilliseconds = 90;
    private const double FinishDistance = 0.75;
    private const double InverseDeltaRampStartPixels = 120;
    private const double InverseDeltaRampEndPixels = 480;
    private const double InverseDeltaMinDurationMilliseconds = 100;
    private const double InverseDeltaMaxDurationMilliseconds = 200;
    private const double MinimumRetargetDurationMilliseconds = 45;
    private const double VelocityDurationFudgeFactor = 2.5;
    private const double MaximumCarriedVelocity = 9000;
    private const double WheelTrackingTimeConstantMilliseconds = 48;
    private const double MaximumFrameSeconds = 0.05;

    private static readonly ConditionalWeakTable<ScrollViewer, SmoothScrollState> States = new();
    private static bool _isInitialized;

    private SmoothScrollViewer()
    {
    }

    public static void Initialize()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;
        ScrollViewer.PointerWheelChangedEvent.AddClassHandler<ScrollViewer>(
            OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        ScrollViewer.OffsetProperty.Changed.AddClassHandler<ScrollViewer>((scrollViewer, e) =>
            OnOffsetChanged(scrollViewer, e.GetNewValue<Vector>()));
    }

    public static bool GetIsEnabled(ScrollViewer scrollViewer)
    {
        return scrollViewer.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(ScrollViewer scrollViewer, bool value)
    {
        scrollViewer.SetValue(IsEnabledProperty, value);
    }

    public static void CancelAnimation(ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null || !States.TryGetValue(scrollViewer, out var state))
            return;

        CancelAnimation(scrollViewer, state);
    }

    public static void ScrollTo(ScrollViewer? scrollViewer, Vector targetOffset, bool fast = false)
    {
        if (scrollViewer is null)
            return;

        var state = States.GetValue(scrollViewer, _ => new SmoothScrollState());
        AnimateTo(scrollViewer, state, targetOffset, fast, preserveVelocity: false);
    }

    private static void OnPointerWheelChanged(ScrollViewer scrollViewer, PointerWheelEventArgs e)
    {
        if (e.Handled ||
            !GetIsEnabled(scrollViewer) ||
            e.Delta == default ||
            ShouldDeferToNestedScrollViewer(scrollViewer, e))
        {
            return;
        }

        var state = States.GetValue(scrollViewer, _ => new SmoothScrollState());
        if (!TryGetWheelTargetOffset(scrollViewer, state, e, out var targetOffset, out var shouldConsume))
        {
            if (!scrollViewer.IsScrollChainingEnabled)
                e.Handled = true;

            return;
        }

        if (!shouldConsume)
        {
            if (!scrollViewer.IsScrollChainingEnabled)
                e.Handled = true;

            return;
        }

        e.Handled = true;
        TrackWheelTo(scrollViewer, state, targetOffset);
    }

    private static void OnOffsetChanged(ScrollViewer scrollViewer, Vector newOffset)
    {
        if (!States.TryGetValue(scrollViewer, out var state) || state.IsApplyingFrame)
            return;

        state.StartOffset = newOffset;
        state.TargetOffset = newOffset;
        state.StartVelocity = default;
        state.CurrentVelocity = default;
        state.IsAnimating = false;
        state.IsFast = false;
        state.IsWheelTracking = false;
        state.AnimationStartTime = null;
        state.LastFrameTime = null;
        state.AnimationId++;
    }

    private static bool ShouldDeferToNestedScrollViewer(ScrollViewer scrollViewer, PointerWheelEventArgs e)
    {
        if (e.Source is not Visual source)
            return false;

        foreach (var nestedScrollViewer in source.GetSelfAndVisualAncestors().OfType<ScrollViewer>())
        {
            if (ReferenceEquals(nestedScrollViewer, scrollViewer))
                break;

            if (!GetIsEnabled(nestedScrollViewer))
                continue;

            var nestedState = States.GetValue(nestedScrollViewer, _ => new SmoothScrollState());
            if (CanScrollOrContinue(nestedScrollViewer, nestedState, e) ||
                !nestedScrollViewer.IsScrollChainingEnabled)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanScrollOrContinue(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        PointerWheelEventArgs e)
    {
        return TryGetWheelTargetOffset(scrollViewer, state, e, out var targetOffset, out var shouldConsume) &&
               shouldConsume &&
               (targetOffset != scrollViewer.Offset || Distance(scrollViewer.Offset, state.TargetOffset) > FinishDistance);
    }

    private static bool TryGetWheelTargetOffset(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        PointerWheelEventArgs e,
        out Vector targetOffset,
        out bool shouldConsume)
    {
        shouldConsume = false;
        targetOffset = scrollViewer.Offset;

        if (!IsScrollable(scrollViewer))
            return false;

        var delta = AdjustWheelDelta(scrollViewer, e);
        var baseOffset = state.IsAnimating ? state.TargetOffset : scrollViewer.Offset;
        baseOffset = ClampOffset(scrollViewer, baseOffset);

        var targetX = baseOffset.X;
        var targetY = baseOffset.Y;
        var maxOffset = GetMaxOffset(scrollViewer);

        if (maxOffset.X > 0 && !IsZero(delta.X))
        {
            var step = GetWheelStep(scrollViewer.Viewport.Width, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            targetX = Math.Clamp(baseOffset.X - delta.X * step, 0, maxOffset.X);
        }

        if (maxOffset.Y > 0 && !IsZero(delta.Y))
        {
            var step = GetWheelStep(scrollViewer.Viewport.Height, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            targetY = Math.Clamp(baseOffset.Y - delta.Y * step, 0, maxOffset.Y);
        }

        targetOffset = new Vector(targetX, targetY);
        shouldConsume = targetOffset != baseOffset ||
                        state.IsAnimating && Distance(scrollViewer.Offset, state.TargetOffset) > FinishDistance;
        return true;
    }

    private static Vector AdjustWheelDelta(ScrollViewer scrollViewer, PointerWheelEventArgs e)
    {
        var delta = e.Delta;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && IsZero(delta.X))
            return new Vector(delta.Y, delta.X);

        return scrollViewer.FlowDirection == FlowDirection.RightToLeft
            ? delta.WithX(-delta.X)
            : delta;
    }

    private static double GetWheelStep(double viewportLength, bool isAltPressed)
    {
        if (!isAltPressed)
            return WheelStep;

        var viewportStep = double.IsFinite(viewportLength) && viewportLength > 0
            ? viewportLength * AltWheelViewportRatio
            : 0;
        return Math.Max(WheelStep * AltWheelMinimumMultiplier, viewportStep);
    }

    private static void AnimateTo(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        Vector targetOffset,
        bool fast,
        bool preserveVelocity)
    {
        state.StartOffset = ClampOffset(scrollViewer, scrollViewer.Offset);
        state.TargetOffset = ClampOffset(scrollViewer, targetOffset);
        state.StartVelocity = preserveVelocity
            ? GetCarriedVelocity(state.TargetOffset - state.StartOffset, state.CurrentVelocity)
            : default;
        state.DurationMilliseconds = fast
            ? FastAnimationMilliseconds
            : GetWheelAnimationDurationMilliseconds(
                state.TargetOffset - state.StartOffset,
                state.StartVelocity);
        state.IsFast = fast;
        state.IsWheelTracking = false;
        state.AnimationStartTime = null;
        state.LastFrameTime = null;

        if (ShouldSnapToTarget(state.StartOffset, state.TargetOffset))
        {
            StopAtTarget(scrollViewer, state, state.TargetOffset);
            return;
        }

        if (state.IsAnimating)
            return;

        // 跟随渲染帧执行，避免 DispatcherTimer 的固定间隔和渲染管线错开。
        state.IsAnimating = true;
        var animationId = ++state.AnimationId;
        RequestNextFrame(scrollViewer, state, animationId);
    }

    private static void TrackWheelTo(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        Vector targetOffset)
    {
        state.TargetOffset = ClampOffset(scrollViewer, targetOffset);
        state.IsFast = false;
        state.IsWheelTracking = true;

        if (ShouldSnapToTarget(scrollViewer.Offset, state.TargetOffset))
        {
            StopAtTarget(scrollViewer, state, state.TargetOffset);
            return;
        }

        if (state.IsAnimating)
            return;

        state.StartOffset = ClampOffset(scrollViewer, scrollViewer.Offset);
        state.StartVelocity = default;
        state.CurrentVelocity = default;
        state.AnimationStartTime = null;
        state.LastFrameTime = null;
        state.IsAnimating = true;
        var animationId = ++state.AnimationId;
        RequestNextFrame(scrollViewer, state, animationId);
    }

    private static void RequestNextFrame(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        long animationId)
    {
        if (TopLevel.GetTopLevel(scrollViewer) is not { } topLevel)
        {
            FinishAnimation(scrollViewer, state, animationId);
            return;
        }

        topLevel.RequestAnimationFrame(timestamp => OnAnimationFrame(scrollViewer, state, animationId, timestamp));
    }

    private static void OnAnimationFrame(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        long animationId,
        TimeSpan timestamp)
    {
        if (animationId != state.AnimationId || !state.IsAnimating)
            return;

        state.TargetOffset = ClampOffset(scrollViewer, state.TargetOffset);
        if (state.IsWheelTracking)
        {
            OnWheelTrackingFrame(scrollViewer, state, animationId, timestamp);
            return;
        }

        if (state.AnimationStartTime is null)
        {
            state.AnimationStartTime = timestamp;
            state.LastFrameTime = timestamp;
            RequestNextFrame(scrollViewer, state, animationId);
            return;
        }

        var elapsedMilliseconds = Math.Max(0, (timestamp - state.AnimationStartTime.Value).TotalMilliseconds);
        var durationMilliseconds = Math.Max(MinimumRetargetDurationMilliseconds, state.DurationMilliseconds);
        var progress = Math.Clamp(elapsedMilliseconds / durationMilliseconds, 0, 1);
        if (progress >= 1)
        {
            FinishAnimation(scrollViewer, state, animationId);
            return;
        }

        var nextOffset = GetHermiteOffset(
            state.StartOffset,
            state.TargetOffset,
            state.StartVelocity,
            durationMilliseconds / 1000,
            progress);
        if (!IsFinite(nextOffset))
        {
            FinishAnimation(scrollViewer, state, animationId);
            return;
        }

        nextOffset = ClampToSegment(state.StartOffset, state.TargetOffset, nextOffset);
        UpdateCurrentVelocity(scrollViewer, state, nextOffset, timestamp);
        ApplyOffset(scrollViewer, state, nextOffset);
        if (animationId != state.AnimationId || !state.IsAnimating)
            return;

        RequestNextFrame(scrollViewer, state, animationId);
    }

    private static void OnWheelTrackingFrame(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        long animationId,
        TimeSpan timestamp)
    {
        if (state.LastFrameTime is null)
        {
            state.LastFrameTime = timestamp;
            RequestNextFrame(scrollViewer, state, animationId);
            return;
        }

        var elapsedSeconds = Math.Clamp(
            (timestamp - state.LastFrameTime.Value).TotalSeconds,
            0,
            MaximumFrameSeconds);

        var currentOffset = ClampOffset(scrollViewer, scrollViewer.Offset);
        var distance = Distance(currentOffset, state.TargetOffset);
        if (distance <= FinishDistance)
        {
            FinishAnimation(scrollViewer, state, animationId);
            return;
        }

        var factor = 1 - Math.Exp(-elapsedSeconds / (WheelTrackingTimeConstantMilliseconds / 1000));
        var nextOffset = currentOffset + (state.TargetOffset - currentOffset) * factor;
        if (!IsFinite(nextOffset))
        {
            FinishAnimation(scrollViewer, state, animationId);
            return;
        }

        nextOffset = ClampToSegment(currentOffset, state.TargetOffset, nextOffset);
        UpdateCurrentVelocity(scrollViewer, state, nextOffset, timestamp);
        ApplyOffset(scrollViewer, state, nextOffset);
        if (animationId != state.AnimationId || !state.IsAnimating)
            return;

        RequestNextFrame(scrollViewer, state, animationId);
    }

    private static void FinishAnimation(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        long animationId)
    {
        if (animationId != state.AnimationId)
            return;

        state.TargetOffset = ClampOffset(scrollViewer, state.TargetOffset);
        ApplyOffset(scrollViewer, state, state.TargetOffset);
        state.StartOffset = state.TargetOffset;
        state.StartVelocity = default;
        state.CurrentVelocity = default;
        state.IsAnimating = false;
        state.IsFast = false;
        state.IsWheelTracking = false;
        state.AnimationStartTime = null;
        state.LastFrameTime = null;
    }

    private static void StopAtTarget(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        Vector targetOffset)
    {
        state.AnimationId++;
        state.TargetOffset = ClampOffset(scrollViewer, targetOffset);
        ApplyOffset(scrollViewer, state, state.TargetOffset);
        state.StartOffset = state.TargetOffset;
        state.StartVelocity = default;
        state.CurrentVelocity = default;
        state.IsAnimating = false;
        state.IsFast = false;
        state.IsWheelTracking = false;
        state.AnimationStartTime = null;
        state.LastFrameTime = null;
    }

    private static void CancelAnimation(ScrollViewer scrollViewer, SmoothScrollState state)
    {
        state.AnimationId++;
        state.StartOffset = ClampOffset(scrollViewer, scrollViewer.Offset);
        state.TargetOffset = state.StartOffset;
        state.StartVelocity = default;
        state.CurrentVelocity = default;
        state.IsAnimating = false;
        state.IsFast = false;
        state.IsWheelTracking = false;
        state.AnimationStartTime = null;
        state.LastFrameTime = null;
    }

    private static void ApplyOffset(ScrollViewer scrollViewer, SmoothScrollState state, Vector offset)
    {
        var targetOffset = ClampOffset(scrollViewer, offset);
        state.IsApplyingFrame = true;
        try
        {
            scrollViewer.SetCurrentValue(ScrollViewer.OffsetProperty, targetOffset);
        }
        finally
        {
            state.IsApplyingFrame = false;
        }

        if (Distance(scrollViewer.Offset, targetOffset) > FinishDistance)
            CancelAnimation(scrollViewer, state);
    }

    private static Vector ClampOffset(ScrollViewer scrollViewer, Vector offset)
    {
        var maxOffset = GetMaxOffset(scrollViewer);
        return new Vector(
            Math.Clamp(IsFinite(offset.X) ? offset.X : 0, 0, maxOffset.X),
            Math.Clamp(IsFinite(offset.Y) ? offset.Y : 0, 0, maxOffset.Y));
    }

    private static Vector GetMaxOffset(ScrollViewer scrollViewer)
    {
        return new Vector(
            Math.Max(0, SafeLength(scrollViewer.Extent.Width) - SafeLength(scrollViewer.Viewport.Width)),
            Math.Max(0, SafeLength(scrollViewer.Extent.Height) - SafeLength(scrollViewer.Viewport.Height)));
    }

    private static bool IsScrollable(ScrollViewer scrollViewer)
    {
        var maxOffset = GetMaxOffset(scrollViewer);
        return maxOffset.X > 0 || maxOffset.Y > 0;
    }

    private static double Distance(Vector first, Vector second)
    {
        var x = second.X - first.X;
        var y = second.Y - first.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static bool ShouldSnapToTarget(Vector currentOffset, Vector targetOffset)
    {
        return Distance(currentOffset, targetOffset) <= FinishDistance;
    }

    private static double GetWheelAnimationDurationMilliseconds(Vector delta, Vector velocity)
    {
        var distance = MaximumDimension(delta);
        var duration = InverseDeltaMaxDurationMilliseconds + Math.Clamp(
            distance,
            InverseDeltaRampStartPixels,
            InverseDeltaRampEndPixels) * (InverseDeltaMinDurationMilliseconds - InverseDeltaMaxDurationMilliseconds) /
            (InverseDeltaRampEndPixels - InverseDeltaRampStartPixels) -
            InverseDeltaRampStartPixels * (InverseDeltaMinDurationMilliseconds - InverseDeltaMaxDurationMilliseconds) /
            (InverseDeltaRampEndPixels - InverseDeltaRampStartPixels);

        var dominantVelocity = GetDominantVelocity(delta, velocity);
        if (Math.Abs(dominantVelocity) > 0.001)
        {
            var dominantDelta = GetDominantDelta(delta);
            var boundMilliseconds = dominantDelta / dominantVelocity * VelocityDurationFudgeFactor * 1000;
            if (boundMilliseconds > 0)
                duration = Math.Min(duration, boundMilliseconds);
        }

        return Math.Max(MinimumRetargetDurationMilliseconds, duration);
    }

    private static Vector GetCarriedVelocity(Vector delta, Vector velocity)
    {
        return new Vector(
            GetCarriedVelocity(delta.X, velocity.X),
            GetCarriedVelocity(delta.Y, velocity.Y));
    }

    private static double GetCarriedVelocity(double delta, double velocity)
    {
        if (Math.Abs(delta) <= FinishDistance || delta * velocity <= 0)
            return 0;

        return Math.Clamp(velocity, -MaximumCarriedVelocity, MaximumCarriedVelocity);
    }

    private static void UpdateCurrentVelocity(
        ScrollViewer scrollViewer,
        SmoothScrollState state,
        Vector nextOffset,
        TimeSpan timestamp)
    {
        if (state.LastFrameTime is { } lastFrameTime)
        {
            var elapsedSeconds = Math.Max(0, (timestamp - lastFrameTime).TotalSeconds);
            if (elapsedSeconds > 0.0001)
            {
                var previousOffset = scrollViewer.Offset;
                state.CurrentVelocity = new Vector(
                    (nextOffset.X - previousOffset.X) / elapsedSeconds,
                    (nextOffset.Y - previousOffset.Y) / elapsedSeconds);
            }
        }

        state.LastFrameTime = timestamp;
    }

    private static Vector GetHermiteOffset(
        Vector start,
        Vector target,
        Vector startVelocity,
        double durationSeconds,
        double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var t2 = progress * progress;
        var t3 = t2 * progress;
        var h00 = 2 * t3 - 3 * t2 + 1;
        var h10 = t3 - 2 * t2 + progress;
        var h01 = -2 * t3 + 3 * t2;

        return new Vector(
            h00 * start.X + h10 * durationSeconds * startVelocity.X + h01 * target.X,
            h00 * start.Y + h10 * durationSeconds * startVelocity.Y + h01 * target.Y);
    }

    private static Vector ClampToSegment(Vector start, Vector target, Vector offset)
    {
        return new Vector(
            ClampBetween(offset.X, start.X, target.X),
            ClampBetween(offset.Y, start.Y, target.Y));
    }

    private static double ClampBetween(double value, double first, double second)
    {
        return Math.Clamp(value, Math.Min(first, second), Math.Max(first, second));
    }

    private static double MaximumDimension(Vector vector)
    {
        return Math.Max(Math.Abs(vector.X), Math.Abs(vector.Y));
    }

    private static double GetDominantDelta(Vector delta)
    {
        return Math.Abs(delta.X) > Math.Abs(delta.Y) ? delta.X : delta.Y;
    }

    private static double GetDominantVelocity(Vector delta, Vector velocity)
    {
        return Math.Abs(delta.X) > Math.Abs(delta.Y) ? velocity.X : velocity.Y;
    }

    private static double SafeLength(double value)
    {
        return double.IsFinite(value) && value > 0 ? value : 0;
    }

    private static bool IsFinite(Vector vector)
    {
        return IsFinite(vector.X) && IsFinite(vector.Y);
    }

    private static bool IsFinite(double value)
    {
        return double.IsFinite(value);
    }

    private static bool IsZero(double value)
    {
        return Math.Abs(value) < 0.001;
    }

    private sealed class SmoothScrollState
    {
        public Vector StartOffset { get; set; }
        public Vector TargetOffset { get; set; }
        public Vector StartVelocity { get; set; }
        public Vector CurrentVelocity { get; set; }
        public double DurationMilliseconds { get; set; }
        public long AnimationId { get; set; }
        public bool IsAnimating { get; set; }
        public bool IsFast { get; set; }
        public bool IsWheelTracking { get; set; }
        public bool IsApplyingFrame { get; set; }
        public TimeSpan? AnimationStartTime { get; set; }
        public TimeSpan? LastFrameTime { get; set; }
    }
}
