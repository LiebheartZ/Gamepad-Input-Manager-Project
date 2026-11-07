using GDMInputManager.Models;

namespace GDMInputManager.Services;

public sealed class GamepadIdentityService
{
    public IReadOnlyList<GamepadDevice> MergeProviderObservations(
        IReadOnlyList<GamepadDevice> devices,
        IReadOnlyCollection<GamepadRecord> records,
        IReadOnlyDictionary<string, Guid> sessionAssociations)
    {
        var mergedDevices = devices.ToList();
        var consumed = new HashSet<int>();
        var mergedIndexes = new HashSet<int>();
        var recordIdsByDevice = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < devices.Count; index++)
        {
            GamepadDevice device = devices[index];
            if (sessionAssociations.TryGetValue(device.DeviceKey, out Guid associatedRecordId) &&
                records.Any(record => record.RecordId == associatedRecordId))
            {
                recordIdsByDevice[device.DeviceKey] = associatedRecordId;
                continue;
            }

            GamepadRecord? record = FindUniqueMatch(device, records, new HashSet<Guid>());
            recordIdsByDevice[device.DeviceKey] = record?.RecordId;
        }

        PairUnique(
            devices,
            mergedDevices,
            consumed,
            mergedIndexes,
            (first, second) => HasSharedStrongIdentity(first.Identity, second.Identity) ||
                               HaveSameRecord(first, second, recordIdsByDevice));

        PairUnique(
            devices,
            mergedDevices,
            consumed,
            mergedIndexes,
            (first, second) =>
                first.IsConnected &&
                second.IsConnected &&
                first.ConnectionType != ConnectionType.USB &&
                second.ConnectionType != ConnectionType.USB &&
                GetFamilyKey(first) == GetFamilyKey(second));

        var result = new List<GamepadDevice>(devices.Count);
        for (int index = 0; index < devices.Count; index++)
        {
            if (mergedIndexes.Contains(index))
            {
                result.Add(mergedDevices[index]);
            }
            else if (!consumed.Contains(index))
            {
                result.Add(mergedDevices[index]);
            }
        }

        return result;
    }

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

    private static void PairUnique(
        IReadOnlyList<GamepadDevice> devices,
        IList<GamepadDevice> mergedDevices,
        HashSet<int> consumed,
        HashSet<int> mergedIndexes,
        Func<GamepadDevice, GamepadDevice, bool> isMatch)
    {
        for (int index = 0; index < devices.Count; index++)
        {
            if (consumed.Contains(index))
            {
                continue;
            }

            List<int> matches = Enumerable.Range(0, devices.Count)
                .Where(candidateIndex =>
                    candidateIndex != index &&
                    !consumed.Contains(candidateIndex) &&
                    devices[index].Provider != devices[candidateIndex].Provider &&
                    IsSupportedProviderPair(devices[index], devices[candidateIndex]) &&
                    isMatch(devices[index], devices[candidateIndex]))
                .ToList();
            if (matches.Count != 1)
            {
                continue;
            }

            int matchIndex = matches[0];
            int reverseMatchCount = Enumerable.Range(0, devices.Count)
                .Count(candidateIndex =>
                    candidateIndex != matchIndex &&
                    !consumed.Contains(candidateIndex) &&
                    devices[matchIndex].Provider != devices[candidateIndex].Provider &&
                    IsSupportedProviderPair(devices[matchIndex], devices[candidateIndex]) &&
                    isMatch(devices[matchIndex], devices[candidateIndex]));
            if (reverseMatchCount != 1)
            {
                continue;
            }

            consumed.Add(index);
            consumed.Add(matchIndex);
            int sdlIndex = devices[index].Provider == "SDL2" ? index : matchIndex;
            int supplementalIndex = sdlIndex == index ? matchIndex : index;
            mergedDevices[sdlIndex] = Merge(devices[sdlIndex], devices[supplementalIndex]);
            mergedIndexes.Add(sdlIndex);
        }
    }

    private static bool IsSupportedProviderPair(GamepadDevice first, GamepadDevice second)
    {
        return first.Provider == "SDL2" && second.Provider == "Windows Bluetooth" ||
               first.Provider == "Windows Bluetooth" && second.Provider == "SDL2";
    }

    private static bool HaveSameRecord(
        GamepadDevice first,
        GamepadDevice second,
        IReadOnlyDictionary<string, Guid?> recordIdsByDevice)
    {
        return recordIdsByDevice.TryGetValue(first.DeviceKey, out Guid? firstRecordId) &&
               firstRecordId.HasValue &&
               recordIdsByDevice.TryGetValue(second.DeviceKey, out Guid? secondRecordId) &&
               firstRecordId == secondRecordId;
    }

    private static bool HasSharedStrongIdentity(GamepadIdentity first, GamepadIdentity second)
    {
        return Same(first.SerialNumber, second.SerialNumber) ||
               SameAddress(first.BluetoothAddress, second.BluetoothAddress) ||
               Same(first.InstancePath, second.InstancePath);
    }

    private static GamepadDevice Merge(GamepadDevice first, GamepadDevice second)
    {
        GamepadDevice supplemental = second;
        return first with
        {
            Identity = new GamepadIdentity
            {
                SerialNumber = first.Identity.SerialNumber ?? supplemental.Identity.SerialNumber,
                BluetoothAddress = first.Identity.BluetoothAddress ?? supplemental.Identity.BluetoothAddress,
                InstancePath = first.Identity.InstancePath ?? supplemental.Identity.InstancePath
            },
            ConnectionType = first.ConnectionType != ConnectionType.Unknown
                ? first.ConnectionType
                : supplemental.ConnectionType,
            IsConnected = first.IsConnected || supplemental.IsConnected,
            IsPaired = first.IsPaired || supplemental.IsPaired,
            CanUnpair = first.CanUnpair || supplemental.CanUnpair
        };
    }

    private static string GetFamilyKey(GamepadDevice device)
    {
        return device.GamepadType switch
        {
            GamepadType.PlayStationDualShock4 => "PS4",
            GamepadType.PlayStationDualSense => "PS5",
            GamepadType.XboxOne or GamepadType.XboxSeries or GamepadType.Xbox360 => "XBOX",
            _ => string.Concat(device.Name.Where(char.IsLetterOrDigit)).ToUpperInvariant()
        };
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
