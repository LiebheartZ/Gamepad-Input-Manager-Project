using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GDM.Models;

namespace GDM.ViewModels;

public sealed class ComboButtonChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public ComboButtonChoice(string id, string displayName, bool isSelected = false)
    {
        Id = id;
        DisplayName = displayName;
        _isSelected = isSelected;
    }
}

public sealed class ProfileComboRow : INotifyPropertyChanged
{
    private string _displayName;
    private bool _requiresHold;
    private string _holdMilliseconds;
    private string _actionType;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid ComboId { get; }
    public ObservableCollection<ComboButtonChoice> TriggerButtons { get; }
    public ObservableCollection<ComboButtonChoice> TargetButtons { get; }
    public IReadOnlyList<ComboActionOption> ActionOptions { get; } = ComboActionTypes.Options;

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public bool RequiresHold
    {
        get => _requiresHold;
        set => SetProperty(ref _requiresHold, value);
    }

    public string HoldMilliseconds
    {
        get => _holdMilliseconds;
        set => SetProperty(ref _holdMilliseconds, value);
    }

    public string ActionType
    {
        get => _actionType;
        set => SetProperty(ref _actionType, value);
    }

    public ProfileComboRow(
        Guid comboId,
        string displayName,
        IEnumerable<string> triggerButtonIds,
        bool requiresHold,
        int holdMilliseconds,
        string actionType,
        IEnumerable<string> targetButtonIds)
    {
        ComboId = comboId;
        _displayName = displayName;
        _requiresHold = requiresHold;
        _holdMilliseconds = holdMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _actionType = ComboActionTypes.Normalize(actionType);
        HashSet<string> triggers = new(triggerButtonIds, StringComparer.Ordinal);
        TriggerButtons = new ObservableCollection<ComboButtonChoice>(
            GamepadControlCatalog.Buttons.Select(button =>
                new ComboButtonChoice(button.Id, button.DisplayName, triggers.Contains(button.Id))));
        TargetButtons = [];
        RebuildTargetButtons(GamepadControlCatalog.ButtonTargets, targetButtonIds);
    }

    public void RebuildTargetButtons(
        IReadOnlyList<GamepadControlOption> targets,
        IEnumerable<string>? selectedIds = null)
    {
        HashSet<string> selected = selectedIds is null
            ? new HashSet<string>(SelectedTargetButtonIds, StringComparer.Ordinal)
            : new HashSet<string>(selectedIds, StringComparer.Ordinal);
        TargetButtons.Clear();
        foreach (GamepadControlOption target in targets)
        {
            if (target.Id == GamepadControlCatalog.NoButtonTarget.Id)
            {
                continue;
            }

            TargetButtons.Add(new ComboButtonChoice(
                target.Id,
                target.DisplayName,
                selected.Contains(target.Id)));
        }

        foreach (string orphanId in selected.Where(id =>
                     targets.All(target => target.Id != id)))
        {
            TargetButtons.Add(new ComboButtonChoice(orphanId, $"{orphanId} (no compatible)", true));
        }
    }

    public IReadOnlyList<string> SelectedTriggerButtonIds =>
        TriggerButtons.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToArray();

    public IReadOnlyList<string> SelectedTargetButtonIds =>
        TargetButtons.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToArray();

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
