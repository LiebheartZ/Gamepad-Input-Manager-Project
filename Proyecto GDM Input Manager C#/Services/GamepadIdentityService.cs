using GDMInputManager.Models;

namespace GDMInputManager.Services;

public sealed class GamepadIdentityService
{
    public GamepadRecord? FindUniqueMatch(
        GamepadDevice device,
        IReadOnlyCollection<GamepadRecord> records,
        IReadOnlySet<Guid> alreadyMatched)
    {
        GamepadIdentity identity = device.Identity;
        if (!string.IsNullOrWhiteSpace(identity.SerialNumber))
        {
            GamepadRecord? serialMatch = GetUniqueCandidate(
                records,
                alreadyMatched,
                record => Same(record.Identity.SerialNumber, identity.SerialNumber));
            if (serialMatch != null || HasAnyCandidate(
                    records,
                    alreadyMatched,
                    record => Same(record.Identity.SerialNumber, identity.SerialNumber)))
            {
                return serialMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(identity.BluetoothAddress))
        {
            GamepadRecord? addressMatch = GetUniqueCandidate(
                records,
                alreadyMatched,
                record => SameAddress(record.Identity.BluetoothAddress, identity.BluetoothAddress));
            if (addressMatch != null || HasAnyCandidate(
                    records,
                    alreadyMatched,
                    record => SameAddress(record.Identity.BluetoothAddress, identity.BluetoothAddress)))
            {
                return addressMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(identity.InstancePath))
        {
            return GetUniqueCandidate(
                records,
                alreadyMatched,
                record => Same(record.Identity.InstancePath, identity.InstancePath));
        }

        return null;
    }

    private static GamepadRecord? GetUniqueCandidate(
        IEnumerable<GamepadRecord> records,
        IReadOnlySet<Guid> alreadyMatched,
        Func<GamepadRecord, bool> predicate)
    {
        List<GamepadRecord> candidates = records
            .Where(record => !alreadyMatched.Contains(record.RecordId) && predicate(record))
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool HasAnyCandidate(
        IEnumerable<GamepadRecord> records,
        IReadOnlySet<Guid> alreadyMatched,
        Func<GamepadRecord, bool> predicate)
    {
        return records.Any(record => !alreadyMatched.Contains(record.RecordId) && predicate(record));
    }

    private static bool Same(string? first, string? second)
    {
        return !string.IsNullOrWhiteSpace(first) &&
               !string.IsNullOrWhiteSpace(second) &&
               string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameAddress(string? first, string? second)
    {
        static string Normalize(string? value)
        {
            return string.Concat((value ?? string.Empty).Where(Uri.IsHexDigit)).ToUpperInvariant();
        }

        string normalizedFirst = Normalize(first);
        return normalizedFirst.Length == 12 && normalizedFirst == Normalize(second);
    }
}
