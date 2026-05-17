using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels;

public partial class MessageFilterDialogViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly ObservableCollection<MessageSenderFilterOption> _allSenderCandidates = [];
    private readonly Dictionary<int, MessageDateFilterOption> _dateOptionsByDay = [];
    private readonly HashSet<int> _selectedDayStartTimes = [];

    public MessageFilterDialogViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public ViewModelToken ViewModelToken { get; } = new();

    public ObservableCollection<MessageSenderFilterOption> SenderCandidates { get; } = [];

    public ObservableCollection<MessageSenderFilterOption> SelectedSenders { get; } = [];

    public ObservableCollection<MessageDateFilterCell> DateCells { get; } = [];

    public MessageFilterCriteria? ResultFilter { get; private set; }

    [ObservableProperty]
    public partial bool IsGroupConversation { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset VisibleMonth { get; set; }

    [ObservableProperty]
    public partial string VisibleMonthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SenderSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SenderIdInput { get; set; } = string.Empty;

    public bool HasSelectedSenders => SelectedSenders.Count > 0;

    public bool HasSelectedDates => _selectedDayStartTimes.Count > 0;

    public void Initialize(MessageFilterDialogRequest request)
    {
        IsGroupConversation = request.IsGroupConversation;
        _dateOptionsByDay.Clear();
        foreach (var dateOption in request.DateOptions
                     .Where(option => option.DayStartTime > 0)
                     .DistinctBy(option => option.DayStartTime)
                     .OrderBy(option => option.DayStartTime))
        {
            _dateOptionsByDay[dateOption.DayStartTime] = dateOption;
        }

        _selectedDayStartTimes.Clear();
        foreach (var dayStartTime in request.CurrentFilter.SelectedDayStartTimes)
        {
            if (_dateOptionsByDay.ContainsKey(dayStartTime))
                _selectedDayStartTimes.Add(dayStartTime);
        }

        VisibleMonth = ResolveInitialVisibleMonth();

        _allSenderCandidates.Clear();
        foreach (var sender in request.SenderCandidates
                     .Where(sender => sender.SenderId != 0)
                     .DistinctBy(sender => sender.SenderId)
                     .OrderBy(sender => sender.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(sender => sender.SenderId))
        {
            _allSenderCandidates.Add(sender);
        }

        SelectedSenders.Clear();
        foreach (var senderId in request.CurrentFilter.SenderIds)
        {
            SelectedSenders.Add(ResolveSenderOption(senderId));
        }

        RefreshSenderCandidates();
        RefreshDateCells();
        OnPropertyChanged(nameof(HasSelectedSenders));
        OnPropertyChanged(nameof(HasSelectedDates));
    }

    partial void OnSenderSearchTextChanged(string value)
    {
        RefreshSenderCandidates();
    }

    partial void OnVisibleMonthChanged(DateTimeOffset value)
    {
        VisibleMonthText = value.ToString("yyyy-MM");
        RefreshDateCells();
    }

    [RelayCommand]
    public void AddSender(MessageSenderFilterOption? sender)
    {
        if (sender is null || SelectedSenders.Any(item => item.SenderId == sender.SenderId))
            return;

        SelectedSenders.Add(sender);
        SenderSearchText = string.Empty;
        RefreshSenderCandidates();
        OnPropertyChanged(nameof(HasSelectedSenders));
    }

    [RelayCommand]
    public void RemoveSender(MessageSenderFilterOption? sender)
    {
        if (sender is null)
            return;

        var matched = SelectedSenders.FirstOrDefault(item => item.SenderId == sender.SenderId);
        if (matched is null)
            return;

        SelectedSenders.Remove(matched);
        RefreshSenderCandidates();
        OnPropertyChanged(nameof(HasSelectedSenders));
    }

    [RelayCommand]
    public void AddManualSenders()
    {
        var senderIds = SenderIdInput
            .Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var senderId) ? senderId : 0)
            .Where(senderId => senderId != 0)
            .Distinct()
            .ToArray();
        if (senderIds.Length == 0)
            return;

        foreach (var senderId in senderIds)
        {
            if (SelectedSenders.Any(item => item.SenderId == senderId))
                continue;

            SelectedSenders.Add(ResolveSenderOption(senderId));
        }

        SenderIdInput = string.Empty;
        RefreshSenderCandidates();
        OnPropertyChanged(nameof(HasSelectedSenders));
    }

    [RelayCommand]
    public void ClearDate()
    {
        _selectedDayStartTimes.Clear();
        RefreshDateCells();
        OnPropertyChanged(nameof(HasSelectedDates));
    }

    [RelayCommand]
    public void ClearSenders()
    {
        SelectedSenders.Clear();
        RefreshSenderCandidates();
        OnPropertyChanged(nameof(HasSelectedSenders));
    }

    [RelayCommand]
    public void ClearAll()
    {
        _selectedDayStartTimes.Clear();
        SelectedSenders.Clear();
        SenderIdInput = string.Empty;
        SenderSearchText = string.Empty;
        RefreshSenderCandidates();
        RefreshDateCells();
        OnPropertyChanged(nameof(HasSelectedSenders));
        OnPropertyChanged(nameof(HasSelectedDates));
    }

    [RelayCommand]
    public void ShowPreviousMonth()
    {
        VisibleMonth = VisibleMonth.AddMonths(-1);
    }

    [RelayCommand]
    public void ShowNextMonth()
    {
        VisibleMonth = VisibleMonth.AddMonths(1);
    }

    [RelayCommand]
    public void ToggleDate(MessageDateFilterCell? cell)
    {
        if (cell is not { CanSelect: true, DayStartTime: { } dayStartTime })
            return;

        if (!_selectedDayStartTimes.Add(dayStartTime))
            _selectedDayStartTimes.Remove(dayStartTime);

        RefreshDateCells();
        OnPropertyChanged(nameof(HasSelectedDates));
    }

    [RelayCommand]
    public void Apply()
    {
        var senderIds = IsGroupConversation
            ? SelectedSenders.Select(sender => sender.SenderId)
            : [];

        ResultFilter = MessageFilterCriteria.CreateForSelectedDays(_selectedDayStartTimes, senderIds);
        _dialogService.Close(ViewModelToken);
    }

    [RelayCommand]
    public void Cancel()
    {
        _dialogService.Close(ViewModelToken);
    }

    private void RefreshSenderCandidates()
    {
        var query = SenderSearchText.Trim();
        var selectedIds = SelectedSenders
            .Select(sender => sender.SenderId)
            .ToHashSet();
        var candidates = _allSenderCandidates
            .Where(sender => !selectedIds.Contains(sender.SenderId))
            .Where(sender =>
                string.IsNullOrWhiteSpace(query) ||
                sender.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                sender.SenderId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (sender.NtUid?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(80)
            .ToArray();

        SenderCandidates.Clear();
        foreach (var candidate in candidates)
        {
            SenderCandidates.Add(candidate);
        }
    }

    private MessageSenderFilterOption ResolveSenderOption(uint senderId)
    {
        return _allSenderCandidates.FirstOrDefault(sender => sender.SenderId == senderId) ??
               new MessageSenderFilterOption(senderId, senderId.ToString());
    }

    private DateTimeOffset ResolveInitialVisibleMonth()
    {
        var initialDay = _selectedDayStartTimes.Count > 0
            ? _selectedDayStartTimes.Min()
            : _dateOptionsByDay.Count > 0
                ? _dateOptionsByDay.Keys.Max()
                : (int)new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        var localDate = DateTimeOffset.FromUnixTimeSeconds(initialDay).LocalDateTime;
        return new DateTimeOffset(new DateTime(localDate.Year, localDate.Month, 1, 0, 0, 0, DateTimeKind.Local));
    }

    private void RefreshDateCells()
    {
        if (VisibleMonth == default)
            return;

        var monthStart = new DateTime(VisibleMonth.Year, VisibleMonth.Month, 1);
        var leadingDays = (int)monthStart.DayOfWeek;
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var cells = new List<MessageDateFilterCell>(42);

        for (var index = 0; index < 42; index++)
        {
            var day = index - leadingDays + 1;
            if (day < 1 || day > daysInMonth)
            {
                cells.Add(new MessageDateFilterCell(null, 0, false, false, false, 0));
                continue;
            }

            var localDate = new DateTime(monthStart.Year, monthStart.Month, day, 0, 0, 0, DateTimeKind.Local);
            var dayStartTime = (int)new DateTimeOffset(localDate).ToUnixTimeSeconds();
            var hasMessages = _dateOptionsByDay.TryGetValue(dayStartTime, out var option);
            cells.Add(new MessageDateFilterCell(
                dayStartTime,
                day,
                true,
                hasMessages,
                _selectedDayStartTimes.Contains(dayStartTime),
                option?.MessageCount ?? 0));
        }

        DateCells.Clear();
        foreach (var cell in cells)
        {
            DateCells.Add(cell);
        }
    }
}
