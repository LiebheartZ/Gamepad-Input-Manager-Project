using GDM.Models;

namespace GDM.Services;

public sealed record ComboEvaluation(IReadOnlyList<ControllerCombo> TriggeredCombos)
{
    public static readonly ComboEvaluation Empty = new([]);
}

public sealed class GamepadComboEvaluator
{
    private const int MaxHoldMilliseconds = 10000;
    private readonly Dictionary<string, Dictionary<Guid, ComboTrackState>> _states =
        new(StringComparer.Ordinal);

    public ComboEvaluation Evaluate(
        string deviceKey,
        GamepadInputSnapshot snapshot,
        IReadOnlyList<ControllerCombo>? combos)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (combos is null || combos.Count == 0)
        {
            _states.Remove(deviceKey);
            return ComboEvaluation.Empty;
        }

        Dictionary<Guid, ComboTrackState> deviceStates = _states.TryGetValue(
            deviceKey,
            out Dictionary<Guid, ComboTrackState>? existing)
            ? existing
            : _states[deviceKey] = [];

        var triggered = new List<ControllerCombo>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ControllerCombo combo in combos)
        {
            if (combo.ComboId == Guid.Empty || !IsComboPressed(snapshot, combo))
            {
                if (deviceStates.TryGetValue(combo.ComboId, out ComboTrackState? released))
                {
                    released.Reset();
                }

                continue;
            }

            ComboTrackState state = deviceStates.TryGetValue(combo.ComboId, out ComboTrackState? tracked)
                ? tracked
                : deviceStates[combo.ComboId] = new ComboTrackState();

            int requiredHold = combo.RequiresHold
                ? Math.Clamp(combo.HoldMilliseconds, 1, MaxHoldMilliseconds)
                : 0;
            if (requiredHold > 0)
            {
                state.PressedSince ??= now;
                if (state.Fired || now - state.PressedSince.Value < TimeSpan.FromMilliseconds(requiredHold))
                {
                    continue;
                }
            }
            else if (state.Fired)
            {
                continue;
            }

            state.Fired = true;
            triggered.Add(combo);
        }

        return triggered.Count == 0 ? ComboEvaluation.Empty : new ComboEvaluation(triggered);
    }

    public void Reset(string deviceKey)
    {
        if (!string.IsNullOrWhiteSpace(deviceKey))
        {
            _states.Remove(deviceKey);
        }
    }

    public void RemoveStale(ISet<string> activeDeviceKeys)
    {
        foreach (string key in _states.Keys.Where(key => !activeDeviceKeys.Contains(key)).ToArray())
        {
            _states.Remove(key);
        }
    }

    private static bool IsComboPressed(GamepadInputSnapshot snapshot, ControllerCombo combo)
    {
        if (combo.Buttons.Count == 0)
        {
            return false;
        }

        foreach (string buttonId in combo.Buttons)
        {
            string? inputName = GamepadControlCatalog.Buttons
                .FirstOrDefault(button => button.Id == buttonId)
                ?.InputName;
            if (inputName is null ||
                !snapshot.Buttons.Any(button =>
                    button.Name == inputName && button.IsPressed))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ComboTrackState
    {
        public bool Fired { get; set; }
        public DateTimeOffset? PressedSince { get; set; }

        public void Reset()
        {
            Fired = false;
            PressedSince = null;
        }
    }
}
