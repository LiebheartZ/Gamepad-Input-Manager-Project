using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GDM.Models;

namespace GDM.ViewModels;

public sealed class ProfileMappingRow : INotifyPropertyChanged
{
    private string _selectedTargetId;
    private bool _isInverted;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceId { get; }
    public string SourceDisplayName { get; }
    public ObservableCollection<GamepadControlOption> Targets { get; }
    public bool CanInvert => SourceId is
        "leftX" or "leftY" or "rightX" or "rightY" or "leftTrigger" or "rightTrigger";
    public bool IsInverted
    {
        get => _isInverted;
        set
        {
            if (_isInverted == value)
            {
                return;
            }

            _isInverted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInverted)));
        }
    }

    public string SelectedTargetId
    {
        get => _selectedTargetId;
        set
        {
            if (string.Equals(_selectedTargetId, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedTargetId = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTargetId)));
        }
    }

    public ProfileMappingRow(
        GamepadControlOption source,
        IReadOnlyList<GamepadControlOption> targets,
        string selectedTargetId,
        bool isInverted = false)
    {
        SourceId = source.Id;
        SourceDisplayName = source.DisplayName;
        Targets = new ObservableCollection<GamepadControlOption>(targets);
        _selectedTargetId = selectedTargetId;
        _isInverted = isInverted;
    }
}
