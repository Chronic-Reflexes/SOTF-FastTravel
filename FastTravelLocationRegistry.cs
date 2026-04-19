using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sons.Gameplay;
using UnityEngine;

namespace FastTravel
{
    public sealed class FastTravelBedLocation
    {
        public int BedObjectId { get; }
        public int SleepInteractId { get; }
        public string DisplayName { get; }
        public string ObjectName { get; }
        public string SceneName { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public float LastSeenTime { get; }
        public float LastInteractedTime { get; }
        public bool IsActive { get; }

        public FastTravelBedLocation(
            int bedObjectId,
            int sleepInteractId,
            string displayName,
            string objectName,
            string sceneName,
            Vector3 position,
            Quaternion rotation,
            float lastSeenTime,
            float lastInteractedTime,
            bool isActive)
        {
            BedObjectId = bedObjectId;
            SleepInteractId = sleepInteractId;
            DisplayName = displayName;
            ObjectName = objectName;
            SceneName = sceneName;
            Position = position;
            Rotation = rotation;
            LastSeenTime = lastSeenTime;
            LastInteractedTime = lastInteractedTime;
            IsActive = isActive;
        }
    }

    public static class FastTravelLocationRegistry
    {
        private static readonly bool TrackOnlyPlayerPlacedBeds = true;
        private const int MaxBedNameLength = 40;
        private const float KeyGridSize = 0.5f;

        private sealed class LocationState
        {
            public int BedObjectId;
            public int SleepInteractId;
            public int SequenceNumber;
            public string CustomName;
            public string PersistentKey;
            public string ObjectName;
            public string SceneName;
            public Vector3 Position;
            public Quaternion Rotation;
            public float LastSeenTime;
            public float LastInteractedTime;
            public bool IsActive;

            public FastTravelBedLocation ToSnapshot()
            {
                string displayName = string.IsNullOrEmpty(CustomName)
                    ? "Bed " + SequenceNumber
                    : CustomName;

                return new FastTravelBedLocation(
                    BedObjectId,
                    SleepInteractId,
                    displayName,
                    ObjectName,
                    SceneName,
                    Position,
                    Rotation,
                    LastSeenTime,
                    LastInteractedTime,
                    IsActive);
            }
        }

        private static readonly Dictionary<int, LocationState> _locationsByBedObjectId = new Dictionary<int, LocationState>();
        private static readonly Dictionary<int, int> _bedObjectIdBySleepInteractId = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> _isTrackableBedByObjectId = new Dictionary<int, bool>();
        private static readonly Dictionary<string, string> _persistedNameByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool _persistedNamesLoaded;

        private static int _nextSequenceNumber = 1;

        public static void TrackBed(SleepInteract sleepInteract)
        {
            if (sleepInteract == null)
                return;

            EnsurePersistedNamesLoaded();

            var bedObject = sleepInteract.gameObject;
            if (bedObject == null)
                return;

            int bedObjectId = bedObject.GetInstanceID();
            int sleepInteractId = sleepInteract.GetInstanceID();
            bool hasExistingEntry = _locationsByBedObjectId.TryGetValue(bedObjectId, out var location);

            if (TrackOnlyPlayerPlacedBeds)
            {
                if (!_isTrackableBedByObjectId.TryGetValue(bedObjectId, out bool isTrackable))
                {
                    isTrackable = IsLikelyPlayerPlacedBed(sleepInteract);
                    _isTrackableBedByObjectId[bedObjectId] = isTrackable;
                }

                if (!isTrackable)
                {
                    // Keep previously tracked beds to avoid disappearing entries due transient object state/name changes.
                    if (!hasExistingEntry)
                        return;

                    _isTrackableBedByObjectId[bedObjectId] = true;
                }
            }

            if (!hasExistingEntry)
            {
                location = new LocationState
                {
                    BedObjectId = bedObjectId,
                    SleepInteractId = sleepInteractId,
                    SequenceNumber = _nextSequenceNumber++,
                    LastInteractedTime = -1f
                };

                _locationsByBedObjectId[bedObjectId] = location;
            }

            location.SleepInteractId = sleepInteractId;
            location.ObjectName = string.IsNullOrEmpty(bedObject.name) ? "Bed" : bedObject.name;
            location.SceneName = GetSceneNameSafe(bedObject);
            location.Position = sleepInteract.transform != null ? sleepInteract.transform.position : bedObject.transform.position;
            location.Rotation = sleepInteract.transform != null ? sleepInteract.transform.rotation : bedObject.transform.rotation;
            location.LastSeenTime = Time.unscaledTime;
            location.IsActive = bedObject.activeInHierarchy;
            location.PersistentKey = BuildPersistentBedKey(location.SceneName, location.Position, location.ObjectName);

            if (string.IsNullOrEmpty(location.CustomName) && !string.IsNullOrEmpty(location.PersistentKey))
            {
                if (_persistedNameByKey.TryGetValue(location.PersistentKey, out string persistedName) && !string.IsNullOrEmpty(persistedName))
                {
                    location.CustomName = persistedName;
                }
            }

            _bedObjectIdBySleepInteractId[sleepInteractId] = bedObjectId;
        }

        public static void MarkBedInactive(SleepInteract sleepInteract)
        {
            if (sleepInteract == null)
                return;

            int sleepInteractId = sleepInteract.GetInstanceID();
            if (!_bedObjectIdBySleepInteractId.TryGetValue(sleepInteractId, out var bedObjectId))
                return;

            if (!_locationsByBedObjectId.TryGetValue(bedObjectId, out var location))
                return;

            location.IsActive = false;
            location.LastSeenTime = Time.unscaledTime;
        }

        public static void MarkBedInteracted(SleepInteract sleepInteract)
        {
            if (sleepInteract == null)
                return;

            int sleepInteractId = sleepInteract.GetInstanceID();
            if (!_bedObjectIdBySleepInteractId.TryGetValue(sleepInteractId, out var bedObjectId))
            {
                TrackBed(sleepInteract);
                if (!_bedObjectIdBySleepInteractId.TryGetValue(sleepInteractId, out bedObjectId))
                    return;
            }

            if (_locationsByBedObjectId.TryGetValue(bedObjectId, out var location))
            {
                location.LastInteractedTime = Time.unscaledTime;
            }
        }

        public static void RemoveBed(SleepInteract sleepInteract)
        {
            if (sleepInteract == null)
                return;

            int sleepInteractId = sleepInteract.GetInstanceID();

            if (_bedObjectIdBySleepInteractId.TryGetValue(sleepInteractId, out var bedObjectId))
            {
                _bedObjectIdBySleepInteractId.Remove(sleepInteractId);
                _locationsByBedObjectId.Remove(bedObjectId);
                _isTrackableBedByObjectId.Remove(bedObjectId);
                return;
            }

            int matchedBedObjectId = -1;
            foreach (var kv in _locationsByBedObjectId)
            {
                if (kv.Value.SleepInteractId == sleepInteractId)
                {
                    matchedBedObjectId = kv.Key;
                    break;
                }
            }

            if (matchedBedObjectId != -1)
            {
                _locationsByBedObjectId.Remove(matchedBedObjectId);
                _isTrackableBedByObjectId.Remove(matchedBedObjectId);
            }
        }

        public static bool TryGetByBedObjectId(int bedObjectId, out FastTravelBedLocation location)
        {
            location = null;
            if (!_locationsByBedObjectId.TryGetValue(bedObjectId, out var state))
                return false;

            location = state.ToSnapshot();
            return true;
        }

        public static bool RenameBedByObjectId(int bedObjectId, string requestedName)
        {
            EnsurePersistedNamesLoaded();

            if (!_locationsByBedObjectId.TryGetValue(bedObjectId, out var location))
                return false;

            location.CustomName = NormalizeBedName(requestedName);
            location.LastSeenTime = Time.unscaledTime;

            if (string.IsNullOrEmpty(location.PersistentKey))
            {
                location.PersistentKey = BuildPersistentBedKey(location.SceneName, location.Position, location.ObjectName);
            }

            if (!string.IsNullOrEmpty(location.PersistentKey))
            {
                if (string.IsNullOrEmpty(location.CustomName))
                    _persistedNameByKey.Remove(location.PersistentKey);
                else
                    _persistedNameByKey[location.PersistentKey] = location.CustomName;

                SavePersistedNames();
            }

            return true;
        }

        public static List<FastTravelBedLocation> GetSnapshot(bool includeInactive)
        {
            var orderedStates = new List<LocationState>(_locationsByBedObjectId.Count);

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!includeInactive && !location.IsActive)
                    continue;

                orderedStates.Add(location);
            }

            orderedStates.Sort(CompareLocationsBySequence);

            var result = new List<FastTravelBedLocation>(orderedStates.Count);
            for (int i = 0; i < orderedStates.Count; i++)
            {
                result.Add(orderedStates[i].ToSnapshot());
            }

            return result;
        }

        private static int CompareLocationsBySequence(LocationState a, LocationState b)
        {
            int comparedSequence = a.SequenceNumber.CompareTo(b.SequenceNumber);
            if (comparedSequence != 0)
                return comparedSequence;

            int comparedName = string.Compare(a.CustomName, b.CustomName, StringComparison.Ordinal);
            if (comparedName != 0)
                return comparedName;

            return a.BedObjectId.CompareTo(b.BedObjectId);
        }

        private static string GetSceneNameSafe(GameObject gameObject)
        {
            try
            {
                return gameObject.scene.IsValid() ? gameObject.scene.name : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeBedName(string input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            string trimmed = input.Trim();
            if (trimmed.Length == 0)
                return null;

            if (trimmed.Length > MaxBedNameLength)
                trimmed = trimmed.Substring(0, MaxBedNameLength);

            return trimmed;
        }

        private static bool IsLikelyPlayerPlacedBed(SleepInteract sleepInteract)
        {
            if (sleepInteract == null)
                return false;

            var go = sleepInteract.gameObject;
            if (go == null)
                return false;

            string self = (go.name ?? string.Empty).ToLowerInvariant();
            string root = (go.transform != null && go.transform.root != null && go.transform.root.gameObject != null
                ? go.transform.root.gameObject.name
                : string.Empty).ToLowerInvariant();

            if (self.Contains("sleepandsave") || root.Contains("sleepandsave"))
                return true;

            if (self.Contains("shelter") || root.Contains("shelter") || self.Contains("tarp") || root.Contains("tarp"))
                return true;

            if (go.transform != null)
            {
                if (go.transform.Find("SleepGpsLocator") != null)
                    return true;

                var children = go.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    if (child == null)
                        continue;

                    string childName = (child.name ?? string.Empty).ToLowerInvariant();
                    if (childName.Contains("sleepgpslocator"))
                        return true;
                }
            }

            // Most world beds come through as plain SleepInteract names; keep them out of singleplayer registry.
            if (self.Equals("sleepinteract") || self.StartsWith("sleepinteract "))
                return false;

            if (root.Equals("sleepinteract") || root.StartsWith("sleepinteract "))
                return false;

            return false;
        }

        private static void EnsurePersistedNamesLoaded()
        {
            if (_persistedNamesLoaded)
                return;

            _persistedNamesLoaded = true;
            _persistedNameByKey.Clear();

            try
            {
                string path = GetBedNamesFilePath();
                if (!File.Exists(path))
                    return;

                string json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json))
                    return;

                var lines = json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int tabIndex = line.IndexOf('\t');
                    if (tabIndex <= 0 || tabIndex >= line.Length - 1)
                        continue;

                    string encodedKey = line.Substring(0, tabIndex);
                    string encodedName = line.Substring(tabIndex + 1);

                    string key = DecodePersistedToken(encodedKey);
                    string name = DecodePersistedToken(encodedName);

                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name))
                        continue;

                    _persistedNameByKey[key] = name;
                }

                ModMain.LogMessage("FastTravel: Loaded " + _persistedNameByKey.Count + " persisted bed name(s).");
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Failed to load bed names: " + ex);
            }
        }

        private static void SavePersistedNames()
        {
            try
            {
                string path = GetBedNamesFilePath();
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var lines = new List<string>(_persistedNameByKey.Count);
                foreach (var kv in _persistedNameByKey)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value))
                        continue;

                    string encodedKey = EncodePersistedToken(kv.Key);
                    string encodedValue = EncodePersistedToken(kv.Value);
                    lines.Add(encodedKey + "\t" + encodedValue);
                }

                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Failed to save bed names: " + ex);
            }
        }

        private static string GetBedNamesFilePath()
        {
            return Path.Combine(Application.persistentDataPath, "FastTravel", "bed_names.json");
        }

        private static string BuildPersistentBedKey(string sceneName, Vector3 position, string objectName)
        {
            float x = Quantize(position.x, KeyGridSize);
            float y = Quantize(position.y, KeyGridSize);
            float z = Quantize(position.z, KeyGridSize);

            string safeScene = string.IsNullOrEmpty(sceneName) ? "unknown_scene" : sceneName.Trim().ToLowerInvariant();
            string safeObject = string.IsNullOrEmpty(objectName) ? "bed" : objectName.Trim().ToLowerInvariant();

            return safeScene + "|" + x + "|" + y + "|" + z + "|" + safeObject;
        }

        private static float Quantize(float value, float step)
        {
            if (step <= 0.0001f)
                return value;

            return Mathf.Round(value / step) * step;
        }

        private static string EncodePersistedToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(bytes);
        }

        private static string DecodePersistedToken(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return string.Empty;

            try
            {
                var bytes = Convert.FromBase64String(encoded);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
