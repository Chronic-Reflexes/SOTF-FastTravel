using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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
        public string PersistentKey { get; }
        public string ObjectName { get; }
        public string SceneName { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public string OwnerId { get; }
        public string OwnerDisplayName { get; }
        public bool IsOwnedByLocalPlayer { get; }
        public bool IsPublic { get; }
        public float LastSeenTime { get; }
        public float LastInteractedTime { get; }
        public bool IsActive { get; }

        public FastTravelBedLocation(
            int bedObjectId,
            int sleepInteractId,
            string displayName,
            string persistentKey,
            string objectName,
            string sceneName,
            Vector3 position,
            Quaternion rotation,
            string ownerId,
            string ownerDisplayName,
            bool isOwnedByLocalPlayer,
            bool isPublic,
            float lastSeenTime,
            float lastInteractedTime,
            bool isActive)
        {
            BedObjectId = bedObjectId;
            SleepInteractId = sleepInteractId;
            DisplayName = displayName;
            PersistentKey = persistentKey;
            ObjectName = objectName;
            SceneName = sceneName;
            Position = position;
            Rotation = rotation;
            OwnerId = ownerId;
            OwnerDisplayName = ownerDisplayName;
            IsOwnedByLocalPlayer = isOwnedByLocalPlayer;
            IsPublic = isPublic;
            LastSeenTime = lastSeenTime;
            LastInteractedTime = lastInteractedTime;
            IsActive = isActive;
        }
    }

    public sealed class FastTravelPublicBedRecord
    {
        public string PersistentKey { get; }
        public bool IsPublic { get; }
        public string OwnerId { get; }
        public string OwnerDisplayName { get; }
        public string CustomName { get; }

        public FastTravelPublicBedRecord(string persistentKey, bool isPublic, string ownerId, string ownerDisplayName, string customName = null)
        {
            PersistentKey = persistentKey;
            IsPublic = isPublic;
            OwnerId = ownerId;
            OwnerDisplayName = ownerDisplayName;
            CustomName = customName;
        }
    }

    public static class FastTravelLocationRegistry
    {
        private static readonly bool TrackOnlyPlayerPlacedBeds = true;
        private const int MaxBedNameLength = 40;
        private const float KeyGridSize = 0.5f;
        private const string CanonicalWorldSceneName = "blankscene";
        private const string UnknownSceneName = "unknown_scene";

        private sealed class LocationState
        {
            public int BedObjectId;
            public int SleepInteractId;
            public int SequenceNumber;
            public string CustomName;
            public string PersistentKey;
            public string OwnerId;
            public string OwnerDisplayName;
            public bool IsPublic;
            public string ObjectName;
            public string SceneName;
            public Vector3 Position;
            public Quaternion Rotation;
            public float LastSeenTime;
            public float LastInteractedTime;
            public bool IsActive;

            public FastTravelBedLocation ToSnapshot()
            {
                string displayName = FastTravelLocationRegistry.ResolveDisplayName(this);

                bool ownedByLocal = IsOwnerLocal(OwnerId);

                return new FastTravelBedLocation(
                    BedObjectId,
                    SleepInteractId,
                    displayName,
                    PersistentKey,
                    ObjectName,
                    SceneName,
                    Position,
                    Rotation,
                    OwnerId,
                    OwnerDisplayName,
                    ownedByLocal,
                    IsPublic,
                    LastSeenTime,
                    LastInteractedTime,
                    IsActive);
            }
        }

        private static readonly Dictionary<int, LocationState> _locationsByBedObjectId = new Dictionary<int, LocationState>();
        private static readonly Dictionary<int, int> _bedObjectIdBySleepInteractId = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> _isTrackableBedByObjectId = new Dictionary<int, bool>();
        private static readonly Dictionary<string, bool> _publicByPersistentKey = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _ownerIdByPersistentKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _ownerNameByPersistentKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> _authoritativePublicStateByKey = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _authoritativeNameByPersistentKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _persistedNameByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool _persistedStateLoaded;
        private static string _lastPersistedStateSignature;
        private static bool _persistedNamesLoaded;
        private static bool _isLocalServerAuthority = true;
        private static bool _localIdentityResolved;
        private static string _localPlayerId = "local:you";
        private static string _localPlayerName = "You";
        private static int _stateVersion;

        private static int _nextSequenceNumber = 1;

        private static readonly string[] OwnerIdMemberNames =
        {
            "OwnerId",
            "ownerId",
            "_ownerId",
            "OwnerSteamId",
            "ownerSteamId",
            "PlacedById",
            "placedById",
            "CreatorId",
            "creatorId",
            "PlayerId",
            "playerId",
            "SteamId",
            "steamId",
            "ActorNumber",
            "actorNumber",
            "ActorNum",
            "actorNum"
        };

        private static readonly string[] OwnerNameMemberNames =
        {
            "OwnerName",
            "ownerName",
            "_ownerName",
            "PlacedByName",
            "placedByName",
            "CreatorName",
            "creatorName",
            "PlayerName",
            "playerName",
            "DisplayName",
            "displayName",
            "Name",
            "name"
        };

        private static readonly string[] OwnerObjectMemberNames =
        {
            "Owner",
            "owner",
            "_owner",
            "PlacedBy",
            "placedBy",
            "Creator",
            "creator",
            "Player",
            "player"
        };

        public static void TrackBed(SleepInteract sleepInteract)
        {
            FastTravelNetworkingRuntime.Install();

            if (sleepInteract == null)
                return;

            EnsurePersistedStateLoaded();
            EnsurePersistedNamesLoaded();
            EnsureLocalPlayerIdentityResolved();

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
                    LastInteractedTime = -1f,
                    OwnerId = null,
                    OwnerDisplayName = null,
                    IsPublic = false
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

            string previousPersistentKey = location.PersistentKey;
            string computedPersistentKey = BuildPersistentBedKey(location.SceneName, location.Position, location.ObjectName);
            if (!string.IsNullOrEmpty(previousPersistentKey)
                && !string.IsNullOrEmpty(computedPersistentKey)
                && !string.Equals(previousPersistentKey, computedPersistentKey, StringComparison.Ordinal))
            {
                MigratePersistentStateKey(previousPersistentKey, computedPersistentKey);
            }

            location.PersistentKey = computedPersistentKey;

            RestoreStateFromPersistentKey(location);

            if (string.IsNullOrEmpty(location.OwnerId) || string.IsNullOrEmpty(location.OwnerDisplayName))
            {
                ResolveOwnerForBed(sleepInteract, out string ownerId, out string ownerDisplayName);

                if (string.IsNullOrEmpty(location.OwnerId))
                    location.OwnerId = ownerId;

                if (string.IsNullOrEmpty(location.OwnerDisplayName))
                    location.OwnerDisplayName = ownerDisplayName;
            }

            if (!string.IsNullOrEmpty(location.PersistentKey) && _authoritativePublicStateByKey.TryGetValue(location.PersistentKey, out bool authoritativeIsPublic))
            {
                location.IsPublic = authoritativeIsPublic;
            }

            if (string.IsNullOrEmpty(location.CustomName) && !string.IsNullOrEmpty(location.PersistentKey))
            {
                if (_persistedNameByKey.TryGetValue(location.PersistentKey, out string persistedName) && !string.IsNullOrEmpty(persistedName))
                {
                    location.CustomName = persistedName;
                }
            }

            PersistStateByPersistentKey(location);

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

                if (!HasOwnerId(location.OwnerId))
                {
                    location.OwnerId = GetLocalPlayerId();
                    location.OwnerDisplayName = GetLocalPlayerName();
                    location.IsPublic = false;
                    location.LastSeenTime = Time.unscaledTime;
                    PersistStateByPersistentKey(location);

                    ModMain.LogMessage(
                        "FastTravel: Ownership claim -> bedObjectId=" + location.BedObjectId
                        + " key='" + (location.PersistentKey ?? "<null>") + "'"
                        + " ownerId='" + (location.OwnerId ?? "<null>") + "'"
                        + " ownerName='" + (location.OwnerDisplayName ?? "<null>") + "'.");
                }
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

        public static bool RenameBedByObjectId(int bedObjectId, string requestedName, bool localOnlyAlias = false)
        {
            EnsurePersistedNamesLoaded();

            if (!_locationsByBedObjectId.TryGetValue(bedObjectId, out var location))
                return false;

            if (!localOnlyAlias && !IsOwnerLocal(location.OwnerId))
                return false;

            string normalizedName = NormalizeBedName(requestedName);

            bool shouldRouteThroughServer = false;
            string routeMode = "local-authority";
            if (!localOnlyAlias && !_isLocalServerAuthority)
                shouldRouteThroughServer = ShouldRouteVisibilityRequestThroughServer(out routeMode);

            if (shouldRouteThroughServer)
            {
                if (string.IsNullOrEmpty(location.PersistentKey))
                {
                    location.PersistentKey = BuildPersistentBedKey(location.SceneName, location.Position, location.ObjectName);
                }

                if (string.IsNullOrEmpty(location.PersistentKey))
                {
                    ModMain.LogMessage("FastTravel: Rename request rejected -> bedObjectId=" + location.BedObjectId + " reason='Selected bed is not ready for networking yet'.");
                    return false;
                }

                if (!FastTravelPublicBedNetworkSync.TrySendRenameRequest(location.PersistentKey, location.OwnerId, location.OwnerDisplayName, normalizedName, out string failureReason))
                {
                    ModMain.LogMessage(
                        "FastTravel: Rename request failed -> bedObjectId=" + location.BedObjectId
                        + " key='" + (location.PersistentKey ?? "<null>") + "'"
                        + " reason='" + (failureReason ?? "Could not send rename request to server") + "'.");
                    return false;
                }

                ModMain.LogMessage(
                    "FastTravel: Rename request queued -> bedObjectId=" + location.BedObjectId
                    + " key='" + (location.PersistentKey ?? "<null>") + "'"
                    + " customName='" + (normalizedName ?? "<default>") + "'"
                    + " ownerId='" + (location.OwnerId ?? "<null>") + "'"
                    + " route='" + routeMode + "'"
                    + " awaitingAuthoritativeUpdate=true.");

                // Keep the owner's local list responsive while waiting for server confirmation.
                location.CustomName = normalizedName;
                location.LastSeenTime = Time.unscaledTime;
                MarkStateChanged();
                return true;
            }

            location.CustomName = normalizedName;
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

            MarkStateChanged();

            return true;
        }

        public static bool TrySetBedPublicStateByObjectId(int bedObjectId, bool makePublic, out string failureReason)
        {
            failureReason = null;

            if (!_locationsByBedObjectId.TryGetValue(bedObjectId, out var location))
            {
                failureReason = "Selected bed is no longer available.";
                ModMain.LogMessage("FastTravel: Visibility request failed -> bedObjectId=" + bedObjectId + " reason='" + failureReason + "'.");
                return false;
            }

            string localOwnerId = GetLocalPlayerId();
            ModMain.LogMessage(
                "FastTravel: Visibility request -> bedObjectId=" + location.BedObjectId
                + " key='" + (location.PersistentKey ?? "<null>") + "'"
                + " requestedPublic=" + makePublic
                + " currentPublic=" + location.IsPublic
                + " ownerId='" + (location.OwnerId ?? "<null>") + "'"
                + " localId='" + localOwnerId + "'"
                + " authority=" + _isLocalServerAuthority + ".");

            if (!HasOwnerId(location.OwnerId))
            {
                failureReason = "Interact with this bed first to claim ownership.";
                ModMain.LogMessage("FastTravel: Visibility request rejected -> bedObjectId=" + location.BedObjectId + " reason='" + failureReason + "'.");
                return false;
            }

            if (!IsOwnerLocal(location.OwnerId))
            {
                failureReason = "Only the bed owner can change visibility.";
                ModMain.LogMessage("FastTravel: Visibility request rejected -> bedObjectId=" + location.BedObjectId + " reason='" + failureReason + "'.");
                return false;
            }

            bool shouldRouteThroughServer = false;
            string routeMode = "local-authority";
            if (!_isLocalServerAuthority)
                shouldRouteThroughServer = ShouldRouteVisibilityRequestThroughServer(out routeMode);

            if (shouldRouteThroughServer)
            {
                if (string.IsNullOrEmpty(location.PersistentKey))
                {
                    failureReason = "Selected bed is not ready for networking yet.";
                    ModMain.LogMessage("FastTravel: Visibility request rejected -> bedObjectId=" + location.BedObjectId + " reason='" + failureReason + "'.");
                    return false;
                }

                if (!FastTravelPublicBedNetworkSync.TrySendVisibilityRequest(location.PersistentKey, makePublic, location.OwnerId, location.OwnerDisplayName, out failureReason))
                {
                    if (string.IsNullOrEmpty(failureReason))
                        failureReason = "Could not send visibility request to server.";

                    ModMain.LogMessage("FastTravel: Visibility request rejected -> bedObjectId=" + location.BedObjectId + " reason='" + failureReason + "'.");
                    return false;
                }

                ModMain.LogMessage(
                    "FastTravel: Visibility request queued -> bedObjectId=" + location.BedObjectId
                    + " key='" + (location.PersistentKey ?? "<null>") + "'"
                    + " currentPublic=" + location.IsPublic
                    + " requestedPublic=" + makePublic
                    + " ownerId='" + (location.OwnerId ?? "<null>") + "'"
                    + " route='" + routeMode + "'"
                    + " awaitingAuthoritativeUpdate=true.");
                return true;
            }

            if (!_isLocalServerAuthority)
            {
                ModMain.LogMessage(
                    "FastTravel: Visibility request network bypass -> bedObjectId=" + location.BedObjectId
                    + " key='" + (location.PersistentKey ?? "<null>") + "'"
                    + " requestedPublic=" + makePublic
                    + " route='" + routeMode + "'"
                    + " applyingLocalFallback=true.");
            }

            bool previousPublicState = location.IsPublic;
            location.IsPublic = makePublic;
            location.LastSeenTime = Time.unscaledTime;
            PersistStateByPersistentKey(location);

            if (previousPublicState != makePublic)
                MarkStateChanged();

            ModMain.LogMessage(
                "FastTravel: Visibility changed -> bedObjectId=" + location.BedObjectId
                + " key='" + (location.PersistentKey ?? "<null>") + "'"
                + " fromPublic=" + previousPublicState
                + " toPublic=" + makePublic
                + " ownerId='" + (location.OwnerId ?? "<null>") + "'.");
            return true;
        }

        public static bool IsLocalServerAuthority()
        {
            return _isLocalServerAuthority;
        }

        public static int GetStateVersion()
        {
            return _stateVersion;
        }

        public static void SetLocalServerAuthority(bool isServerAuthority)
        {
            _isLocalServerAuthority = isServerAuthority;
            if (isServerAuthority)
            {
                _authoritativePublicStateByKey.Clear();
                _authoritativeNameByPersistentKey.Clear();
            }
        }

        public static List<FastTravelPublicBedRecord> GetPublicStateSnapshotByKey()
        {
            var result = new List<FastTravelPublicBedRecord>(_locationsByBedObjectId.Count);
            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (string.IsNullOrEmpty(location.PersistentKey))
                    continue;

                result.Add(new FastTravelPublicBedRecord(
                    location.PersistentKey,
                    location.IsPublic,
                    location.OwnerId,
                    location.OwnerDisplayName,
                    ResolveAuthoritativeCustomName(location.PersistentKey, location.CustomName)));
            }

            return result;
        }

        public static void ApplyAuthoritativePublicStateByKey(IReadOnlyList<FastTravelPublicBedRecord> records)
        {
            var previousAuthoritativeNames = new Dictionary<string, string>(_authoritativeNameByPersistentKey, StringComparer.Ordinal);
            _authoritativePublicStateByKey.Clear();
            _authoritativeNameByPersistentKey.Clear();
            _isLocalServerAuthority = false;
            bool stateChanged = false;

            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (record == null || string.IsNullOrEmpty(record.PersistentKey))
                        continue;

                    _authoritativePublicStateByKey[record.PersistentKey] = record.IsPublic;

                    if (HasOwnerId(record.OwnerId))
                        _ownerIdByPersistentKey[record.PersistentKey] = record.OwnerId;

                    if (!string.IsNullOrEmpty(record.OwnerDisplayName))
                        _ownerNameByPersistentKey[record.PersistentKey] = record.OwnerDisplayName;

                    string authoritativeName = NormalizeBedName(record.CustomName);
                    if (!string.IsNullOrEmpty(authoritativeName))
                        _authoritativeNameByPersistentKey[record.PersistentKey] = authoritativeName;
                }
            }

            if (!AreStringMapsEqual(previousAuthoritativeNames, _authoritativeNameByPersistentKey))
                stateChanged = true;

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (string.IsNullOrEmpty(location.PersistentKey))
                    continue;

                bool nextIsPublic;
                if (_authoritativePublicStateByKey.TryGetValue(location.PersistentKey, out bool authoritativeIsPublic))
                    nextIsPublic = authoritativeIsPublic;
                else
                    nextIsPublic = false;

                if (location.IsPublic != nextIsPublic)
                {
                    location.IsPublic = nextIsPublic;
                    stateChanged = true;
                }

                if (_ownerIdByPersistentKey.TryGetValue(location.PersistentKey, out string authoritativeOwnerId)
                    && HasOwnerId(authoritativeOwnerId))
                {
                    if (!string.Equals(location.OwnerId, authoritativeOwnerId, StringComparison.Ordinal))
                        stateChanged = true;

                    location.OwnerId = authoritativeOwnerId;
                }

                if (_ownerNameByPersistentKey.TryGetValue(location.PersistentKey, out string authoritativeOwnerName)
                    && !string.IsNullOrEmpty(authoritativeOwnerName))
                {
                    if (!string.Equals(location.OwnerDisplayName, authoritativeOwnerName, StringComparison.Ordinal))
                        stateChanged = true;

                    location.OwnerDisplayName = authoritativeOwnerName;
                }

                PersistStateByPersistentKey(location);
            }

            if (stateChanged)
                MarkStateChanged();
        }

        public static void ApplyAuthoritativePublicStateDelta(FastTravelPublicBedRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.PersistentKey))
                return;

            EnsurePersistedStateLoaded();

            string persistentKey = NormalizePersistentKey(record.PersistentKey);
            if (string.IsNullOrEmpty(persistentKey))
                return;

            bool stateChanged = false;

            _isLocalServerAuthority = false;

            if (!_authoritativePublicStateByKey.TryGetValue(persistentKey, out bool previousAuthoritativeState)
                || previousAuthoritativeState != record.IsPublic)
            {
                stateChanged = true;
            }

            _authoritativePublicStateByKey[persistentKey] = record.IsPublic;

            if (TrySetOptionalNameMapEntry(_authoritativeNameByPersistentKey, persistentKey, record.CustomName))
                stateChanged = true;

            if (HasOwnerId(record.OwnerId))
                _ownerIdByPersistentKey[persistentKey] = record.OwnerId;

            if (!string.IsNullOrEmpty(record.OwnerDisplayName))
                _ownerNameByPersistentKey[persistentKey] = record.OwnerDisplayName;

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!string.Equals(location.PersistentKey, persistentKey, StringComparison.Ordinal))
                    continue;

                if (location.IsPublic != record.IsPublic)
                    stateChanged = true;

                location.IsPublic = record.IsPublic;

                if (HasOwnerId(record.OwnerId))
                {
                    if (!string.Equals(location.OwnerId, record.OwnerId, StringComparison.Ordinal))
                        stateChanged = true;

                    location.OwnerId = record.OwnerId;
                }

                if (!string.IsNullOrEmpty(record.OwnerDisplayName))
                {
                    if (!string.Equals(location.OwnerDisplayName, record.OwnerDisplayName, StringComparison.Ordinal))
                        stateChanged = true;

                    location.OwnerDisplayName = record.OwnerDisplayName;
                }

                location.LastSeenTime = Time.unscaledTime;
                PersistStateByPersistentKey(location);
            }

            if (stateChanged)
                MarkStateChanged();
        }

        public static bool TryGetPublicStateRecordByPersistentKey(string persistentKey, out FastTravelPublicBedRecord record)
        {
            record = null;
            EnsurePersistedStateLoaded();

            persistentKey = NormalizePersistentKey(persistentKey);
            if (string.IsNullOrEmpty(persistentKey))
                return false;

            bool isPublic = _publicByPersistentKey.TryGetValue(persistentKey, out bool persistedPublic) && persistedPublic;
            _ownerIdByPersistentKey.TryGetValue(persistentKey, out string ownerId);
            _ownerNameByPersistentKey.TryGetValue(persistentKey, out string ownerName);
            string customName = ResolveAuthoritativeCustomName(persistentKey, null);

            if (!isPublic && !HasOwnerId(ownerId) && string.IsNullOrEmpty(ownerName) && string.IsNullOrEmpty(customName))
                return false;

            record = new FastTravelPublicBedRecord(persistentKey, isPublic, ownerId, ownerName, customName);
            return true;
        }

        public static bool TryApplyVisibilityRequestFromNetwork(string persistentKey, bool makePublic, string requesterOwnerId, string requesterOwnerDisplayName, out string failureReason)
        {
            failureReason = null;

            EnsurePersistedStateLoaded();

            if (!_isLocalServerAuthority)
            {
                failureReason = "Local instance is not authoritative server.";
                return false;
            }

            if (string.IsNullOrEmpty(persistentKey))
            {
                failureReason = "Persistent key is missing.";
                return false;
            }

            string requestedPersistentKey = persistentKey;
            persistentKey = NormalizePersistentKey(persistentKey);
            if (string.IsNullOrEmpty(persistentKey))
            {
                failureReason = "Persistent key is invalid.";
                return false;
            }

            if (!HasOwnerId(requesterOwnerId))
            {
                failureReason = "Requester owner id is missing.";
                return false;
            }

            string canonicalOwnerId = null;
            string canonicalOwnerName = null;
            bool matchedAnyLocation = false;

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!string.Equals(location.PersistentKey, persistentKey, StringComparison.Ordinal))
                    continue;

                matchedAnyLocation = true;

                if (HasOwnerId(location.OwnerId) && string.IsNullOrEmpty(canonicalOwnerId))
                    canonicalOwnerId = location.OwnerId;

                if (!string.IsNullOrEmpty(location.OwnerDisplayName) && string.IsNullOrEmpty(canonicalOwnerName))
                    canonicalOwnerName = location.OwnerDisplayName;
            }

            if (string.IsNullOrEmpty(canonicalOwnerId) && _ownerIdByPersistentKey.TryGetValue(persistentKey, out string persistedOwnerId) && HasOwnerId(persistedOwnerId))
                canonicalOwnerId = persistedOwnerId;

            if (string.IsNullOrEmpty(canonicalOwnerName) && _ownerNameByPersistentKey.TryGetValue(persistentKey, out string persistedOwnerName) && !string.IsNullOrEmpty(persistedOwnerName))
                canonicalOwnerName = persistedOwnerName;

            if (string.IsNullOrEmpty(canonicalOwnerId))
            {
                canonicalOwnerId = requesterOwnerId;
                canonicalOwnerName = !string.IsNullOrEmpty(requesterOwnerDisplayName)
                    ? requesterOwnerDisplayName
                    : canonicalOwnerName;
            }

            if (!string.Equals(canonicalOwnerId, requesterOwnerId, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Requester is not the owner of this bed.";
                return false;
            }

            bool persistedStateChanged = false;
            bool stateChanged = false;

            persistedStateChanged |= TrySetStateMapEntry(_ownerIdByPersistentKey, persistentKey, canonicalOwnerId);

            if (!string.IsNullOrEmpty(canonicalOwnerName))
                persistedStateChanged |= TrySetStateMapEntry(_ownerNameByPersistentKey, persistentKey, canonicalOwnerName);

            persistedStateChanged |= TrySetStateMapEntry(_publicByPersistentKey, persistentKey, makePublic);

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!string.Equals(location.PersistentKey, persistentKey, StringComparison.Ordinal))
                    continue;

                location.OwnerId = canonicalOwnerId;
                if (!string.IsNullOrEmpty(canonicalOwnerName))
                    location.OwnerDisplayName = canonicalOwnerName;

                if (location.IsPublic != makePublic)
                    stateChanged = true;

                location.IsPublic = makePublic;
                location.LastSeenTime = Time.unscaledTime;
                PersistStateByPersistentKey(location);
            }

            if (!matchedAnyLocation)
            {
                // Keep authoritative state for late-tracked beds that are not in the active dictionary yet.
                _authoritativePublicStateByKey[persistentKey] = makePublic;
            }

            if (persistedStateChanged)
                SavePersistedState();

            if (stateChanged)
                MarkStateChanged();

            ModMain.LogMessage(
                "FastTravel: Server visibility request applied -> key='" + persistentKey + "'"
                + (string.Equals(requestedPersistentKey, persistentKey, StringComparison.Ordinal)
                    ? string.Empty
                    : " requestedKey='" + requestedPersistentKey + "'")
                + " requestedPublic=" + makePublic
                + " ownerId='" + canonicalOwnerId + "'"
                + " matchedActiveBeds=" + matchedAnyLocation + ".");
            return true;
        }

        public static bool TryApplyRenameRequestFromNetwork(string persistentKey, string requesterOwnerId, string requesterOwnerDisplayName, string requestedName, out string failureReason)
        {
            failureReason = null;

            EnsurePersistedStateLoaded();
            EnsurePersistedNamesLoaded();

            if (!_isLocalServerAuthority)
            {
                failureReason = "Local instance is not authoritative server.";
                return false;
            }

            if (string.IsNullOrEmpty(persistentKey))
            {
                failureReason = "Persistent key is missing.";
                return false;
            }

            string requestedPersistentKey = persistentKey;
            persistentKey = NormalizePersistentKey(persistentKey);
            if (string.IsNullOrEmpty(persistentKey))
            {
                failureReason = "Persistent key is invalid.";
                return false;
            }

            if (!HasOwnerId(requesterOwnerId))
            {
                failureReason = "Requester owner id is missing.";
                return false;
            }

            string normalizedName = NormalizeBedName(requestedName);

            string canonicalOwnerId = null;
            string canonicalOwnerName = null;

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!string.Equals(location.PersistentKey, persistentKey, StringComparison.Ordinal))
                    continue;

                if (HasOwnerId(location.OwnerId) && string.IsNullOrEmpty(canonicalOwnerId))
                    canonicalOwnerId = location.OwnerId;

                if (!string.IsNullOrEmpty(location.OwnerDisplayName) && string.IsNullOrEmpty(canonicalOwnerName))
                    canonicalOwnerName = location.OwnerDisplayName;
            }

            if (string.IsNullOrEmpty(canonicalOwnerId)
                && _ownerIdByPersistentKey.TryGetValue(persistentKey, out string persistedOwnerId)
                && HasOwnerId(persistedOwnerId))
            {
                canonicalOwnerId = persistedOwnerId;
            }

            if (string.IsNullOrEmpty(canonicalOwnerName)
                && _ownerNameByPersistentKey.TryGetValue(persistentKey, out string persistedOwnerName)
                && !string.IsNullOrEmpty(persistedOwnerName))
            {
                canonicalOwnerName = persistedOwnerName;
            }

            if (string.IsNullOrEmpty(canonicalOwnerId))
            {
                canonicalOwnerId = requesterOwnerId;
                canonicalOwnerName = !string.IsNullOrEmpty(requesterOwnerDisplayName)
                    ? requesterOwnerDisplayName
                    : canonicalOwnerName;
            }

            if (!string.Equals(canonicalOwnerId, requesterOwnerId, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Requester is not the owner of this bed.";
                return false;
            }

            bool persistedStateChanged = false;
            bool persistedNameChanged = false;
            bool stateChanged = false;

            persistedStateChanged |= TrySetStateMapEntry(_ownerIdByPersistentKey, persistentKey, canonicalOwnerId);

            if (!string.IsNullOrEmpty(canonicalOwnerName))
                persistedStateChanged |= TrySetStateMapEntry(_ownerNameByPersistentKey, persistentKey, canonicalOwnerName);

            if (string.IsNullOrEmpty(normalizedName))
                persistedNameChanged |= _persistedNameByKey.Remove(persistentKey);
            else
                persistedNameChanged |= TrySetStateMapEntry(_persistedNameByKey, persistentKey, normalizedName);

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!string.Equals(location.PersistentKey, persistentKey, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(location.CustomName, normalizedName, StringComparison.Ordinal))
                    stateChanged = true;

                location.OwnerId = canonicalOwnerId;
                if (!string.IsNullOrEmpty(canonicalOwnerName))
                    location.OwnerDisplayName = canonicalOwnerName;

                location.CustomName = normalizedName;
                location.LastSeenTime = Time.unscaledTime;
                PersistStateByPersistentKey(location);
            }

            if (persistedStateChanged)
                SavePersistedState();

            if (persistedNameChanged)
                SavePersistedNames();

            if (stateChanged || persistedNameChanged)
                MarkStateChanged();

            ModMain.LogMessage(
                "FastTravel: Server rename request applied -> key='" + persistentKey + "'"
                + (string.Equals(requestedPersistentKey, persistentKey, StringComparison.Ordinal)
                    ? string.Empty
                    : " requestedKey='" + requestedPersistentKey + "'")
                + " ownerId='" + canonicalOwnerId + "'"
                + " customName='" + (normalizedName ?? "<default>") + "'.");
            return true;
        }

        public static List<FastTravelBedLocation> GetPrivateSnapshot(bool includeInactive)
        {
            var orderedStates = new List<LocationState>(_locationsByBedObjectId.Count);

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!includeInactive && !location.IsActive)
                    continue;

                if (!HasOwnerId(location.OwnerId))
                    continue;

                if (!IsOwnerLocal(location.OwnerId))
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

        public static List<FastTravelBedLocation> GetPublicSnapshot(bool includeInactive)
        {
            var orderedStates = new List<LocationState>(_locationsByBedObjectId.Count);

            foreach (var kv in _locationsByBedObjectId)
            {
                var location = kv.Value;
                if (!includeInactive && !location.IsActive)
                    continue;

                if (!location.IsPublic)
                    continue;

                if (!HasOwnerId(location.OwnerId))
                    continue;

                if (IsOwnerLocal(location.OwnerId))
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

        public static List<FastTravelBedLocation> GetSnapshot(bool includeInactive)
        {
            return GetPrivateSnapshot(includeInactive);
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

        private static string ResolveDisplayName(LocationState location)
        {
            if (location == null)
                return string.Empty;

            string customName = NormalizeBedName(location.CustomName);
            if (!string.IsNullOrEmpty(customName))
                return customName;

            if (!string.IsNullOrEmpty(location.PersistentKey))
            {
                string authoritativeName = ResolveAuthoritativeCustomName(location.PersistentKey, null);
                if (!string.IsNullOrEmpty(authoritativeName))
                    return authoritativeName;
            }

            return "Bed " + location.SequenceNumber;
        }

        private static string ResolveAuthoritativeCustomName(string persistentKey, string currentCustomName)
        {
            string normalizedCurrent = NormalizeBedName(currentCustomName);
            if (!string.IsNullOrEmpty(normalizedCurrent))
                return normalizedCurrent;

            if (string.IsNullOrEmpty(persistentKey))
                return null;

            if (_authoritativeNameByPersistentKey.TryGetValue(persistentKey, out string authoritativeName))
            {
                authoritativeName = NormalizeBedName(authoritativeName);
                if (!string.IsNullOrEmpty(authoritativeName))
                    return authoritativeName;
            }

            if (_persistedNameByKey.TryGetValue(persistentKey, out string persistedName))
            {
                persistedName = NormalizeBedName(persistedName);
                if (!string.IsNullOrEmpty(persistedName))
                    return persistedName;
            }

            return null;
        }

        public static string CreatePersistentBedKey(string sceneName, Vector3 position, string objectName)
        {
            return BuildPersistentBedKey(sceneName, position, objectName);
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

        private static void RestoreStateFromPersistentKey(LocationState location)
        {
            if (location == null || string.IsNullOrEmpty(location.PersistentKey))
                return;

            if (string.IsNullOrEmpty(location.OwnerId) && _ownerIdByPersistentKey.TryGetValue(location.PersistentKey, out string ownerId))
                location.OwnerId = ownerId;

            if (string.IsNullOrEmpty(location.OwnerDisplayName) && _ownerNameByPersistentKey.TryGetValue(location.PersistentKey, out string ownerName))
                location.OwnerDisplayName = ownerName;

            if (_publicByPersistentKey.TryGetValue(location.PersistentKey, out bool isPublic))
                location.IsPublic = isPublic;
        }

        private static void PersistStateByPersistentKey(LocationState location)
        {
            if (location == null || string.IsNullOrEmpty(location.PersistentKey))
                return;

            bool changed = false;
            string key = location.PersistentKey;

            bool keepPublicState = location.IsPublic || HasOwnerId(location.OwnerId) || !string.IsNullOrEmpty(location.OwnerDisplayName);
            if (keepPublicState)
                changed |= TrySetStateMapEntry(_publicByPersistentKey, key, location.IsPublic);
            else
                changed |= _publicByPersistentKey.Remove(key);

            if (HasOwnerId(location.OwnerId))
                changed |= TrySetStateMapEntry(_ownerIdByPersistentKey, key, location.OwnerId);
            else
                changed |= _ownerIdByPersistentKey.Remove(key);

            if (!string.IsNullOrEmpty(location.OwnerDisplayName))
                changed |= TrySetStateMapEntry(_ownerNameByPersistentKey, key, location.OwnerDisplayName);
            else
                changed |= _ownerNameByPersistentKey.Remove(key);

            if (changed)
                SavePersistedState();
        }

        private static bool TrySetStateMapEntry<TValue>(Dictionary<string, TValue> dictionary, string key, TValue value)
        {
            if (dictionary == null || string.IsNullOrEmpty(key))
                return false;

            if (dictionary.TryGetValue(key, out TValue existing)
                && EqualityComparer<TValue>.Default.Equals(existing, value))
            {
                return false;
            }

            dictionary[key] = value;
            return true;
        }

        private static bool TrySetOptionalNameMapEntry(Dictionary<string, string> dictionary, string key, string value)
        {
            if (dictionary == null || string.IsNullOrEmpty(key))
                return false;

            string normalized = NormalizeBedName(value);
            if (string.IsNullOrEmpty(normalized))
                return dictionary.Remove(key);

            return TrySetStateMapEntry(dictionary, key, normalized);
        }

        private static bool AreStringMapsEqual(Dictionary<string, string> left, Dictionary<string, string> right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left == null || right == null)
                return false;

            if (left.Count != right.Count)
                return false;

            foreach (var kv in left)
            {
                if (!right.TryGetValue(kv.Key, out string rightValue))
                    return false;

                if (!string.Equals(kv.Value, rightValue, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static void MarkStateChanged()
        {
            unchecked
            {
                _stateVersion++;
            }
        }

        private static bool IsOwnerLocal(string ownerId)
        {
            if (!HasOwnerId(ownerId))
                return false;

            return string.Equals(ownerId, GetLocalPlayerId(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldRouteVisibilityRequestThroughServer(out string routeMode)
        {
            routeMode = "runtime-unavailable";

            if (!FastTravelNetworkingRuntime.TryGetRuntimeNetworkState(out bool isMultiplayer, out bool isServer, out bool isClient, out bool isDedicatedServer))
                return false;

            routeMode = "mp=" + isMultiplayer + " server=" + isServer + " client=" + isClient + " dedicated=" + isDedicatedServer;

            // Only non-authority remote clients should send visibility requests to server.
            if (isDedicatedServer)
                return false;

            if (!isClient)
                return false;

            if (isServer)
                return false;

            return true;
        }

        private static bool HasOwnerId(string ownerId)
        {
            return !string.IsNullOrEmpty(ownerId);
        }

        private static string GetLocalPlayerId()
        {
            EnsureLocalPlayerIdentityResolved();
            return _localPlayerId;
        }

        private static string GetLocalPlayerName()
        {
            EnsureLocalPlayerIdentityResolved();
            return _localPlayerName;
        }

        private static void EnsureLocalPlayerIdentityResolved()
        {
            if (_localIdentityResolved)
                return;

            _localIdentityResolved = true;

            string fallbackName = Environment.UserName;
            if (!string.IsNullOrEmpty(fallbackName))
                _localPlayerName = fallbackName;

            _localPlayerId = "local:" + _localPlayerName.ToLowerInvariant();

            if (FastTravelNetworkingRuntime.TryGetLocalPlayerIdentity(out string networkPlayerId, out string networkPlayerName))
            {
                if (!string.IsNullOrEmpty(networkPlayerId))
                    _localPlayerId = networkPlayerId;

                if (!string.IsNullOrEmpty(networkPlayerName))
                    _localPlayerName = networkPlayerName;

                return;
            }

            if (TryResolveSteamIdentity(out string steamId, out string steamName))
            {
                _localPlayerId = "steam:" + steamId;
                if (!string.IsNullOrEmpty(steamName))
                    _localPlayerName = steamName;
            }
        }

        private static bool TryResolveSteamIdentity(out string steamId, out string steamName)
        {
            steamId = null;
            steamName = null;

            string[] steamClientTypeNames =
            {
                "Steamworks.SteamClient",
                "Steamworks.SteamClient, Facepunch.Steamworks.Win64",
                "Steamworks.SteamClient, Facepunch.Steamworks"
            };

            for (int i = 0; i < steamClientTypeNames.Length; i++)
            {
                var type = Type.GetType(steamClientTypeNames[i], throwOnError: false);
                if (type == null)
                    continue;

                if (TryReadStaticMemberAsString(type, "SteamId", out string foundSteamId) && !string.IsNullOrEmpty(foundSteamId))
                {
                    steamId = foundSteamId;
                    TryReadStaticMemberAsString(type, "Name", out steamName);
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadStaticMemberAsString(Type type, string memberName, out string value)
        {
            value = null;
            if (type == null || string.IsNullOrEmpty(memberName))
                return false;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.IgnoreCase;

            try
            {
                var property = type.GetProperty(memberName, flags);
                if (property != null)
                {
                    object raw = property.GetValue(null, null);
                    value = ConvertMemberValueToString(raw);
                    if (!string.IsNullOrEmpty(value))
                        return true;
                }

                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    object raw = field.GetValue(null);
                    value = ConvertMemberValueToString(raw);
                    if (!string.IsNullOrEmpty(value))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ResolveOwnerForBed(SleepInteract sleepInteract, out string ownerId, out string ownerDisplayName)
        {
            ownerId = null;
            ownerDisplayName = null;

            if (sleepInteract != null)
            {
                TryExtractOwnerFromObject(sleepInteract, ref ownerId, ref ownerDisplayName);

                var bedObject = sleepInteract.gameObject;
                if (bedObject != null)
                {
                    TryExtractOwnerFromObject(bedObject, ref ownerId, ref ownerDisplayName);

                    var components = bedObject.GetComponents<Component>();
                    for (int i = 0; i < components.Length; i++)
                    {
                        TryExtractOwnerFromObject(components[i], ref ownerId, ref ownerDisplayName);
                    }

                    if (bedObject.transform != null && bedObject.transform.root != null)
                    {
                        var rootObject = bedObject.transform.root.gameObject;
                        if (rootObject != null && rootObject != bedObject)
                        {
                            TryExtractOwnerFromObject(rootObject, ref ownerId, ref ownerDisplayName);

                            var rootComponents = rootObject.GetComponents<Component>();
                            for (int i = 0; i < rootComponents.Length; i++)
                            {
                                TryExtractOwnerFromObject(rootComponents[i], ref ownerId, ref ownerDisplayName);
                            }
                        }
                    }
                }
            }

        }

        private static void TryExtractOwnerFromObject(object source, ref string ownerId, ref string ownerName)
        {
            if (source == null)
                return;

            for (int i = 0; i < OwnerIdMemberNames.Length && string.IsNullOrEmpty(ownerId); i++)
            {
                if (TryReadMemberAsString(source, OwnerIdMemberNames[i], out string foundId))
                    ownerId = foundId;
            }

            for (int i = 0; i < OwnerNameMemberNames.Length && string.IsNullOrEmpty(ownerName); i++)
            {
                if (TryReadMemberAsString(source, OwnerNameMemberNames[i], out string foundName))
                    ownerName = foundName;
            }

            for (int i = 0; i < OwnerObjectMemberNames.Length; i++)
            {
                if (!TryReadMemberValue(source, OwnerObjectMemberNames[i], out object ownerObject) || ownerObject == null)
                    continue;

                if (TryExtractOwnerFromNestedObject(ownerObject, ref ownerId, ref ownerName))
                    return;
            }
        }

        private static bool TryExtractOwnerFromNestedObject(object nestedOwner, ref string ownerId, ref string ownerName)
        {
            if (nestedOwner == null || nestedOwner is string)
                return false;

            bool changed = false;

            for (int i = 0; i < OwnerIdMemberNames.Length && string.IsNullOrEmpty(ownerId); i++)
            {
                if (TryReadMemberAsString(nestedOwner, OwnerIdMemberNames[i], out string foundId))
                {
                    ownerId = foundId;
                    changed = true;
                }
            }

            if (string.IsNullOrEmpty(ownerId))
            {
                if (TryReadMemberAsString(nestedOwner, "Id", out string genericId) ||
                    TryReadMemberAsString(nestedOwner, "SteamId", out genericId) ||
                    TryReadMemberAsString(nestedOwner, "PlayerId", out genericId))
                {
                    ownerId = genericId;
                    changed = true;
                }
            }

            for (int i = 0; i < OwnerNameMemberNames.Length && string.IsNullOrEmpty(ownerName); i++)
            {
                if (TryReadMemberAsString(nestedOwner, OwnerNameMemberNames[i], out string foundName))
                {
                    ownerName = foundName;
                    changed = true;
                }
            }

            if (string.IsNullOrEmpty(ownerName))
            {
                if (TryReadMemberAsString(nestedOwner, "DisplayName", out string genericName) ||
                    TryReadMemberAsString(nestedOwner, "Name", out genericName))
                {
                    ownerName = genericName;
                    changed = true;
                }
            }

            return changed;
        }

        private static bool TryReadMemberAsString(object source, string memberName, out string value)
        {
            value = null;
            if (!TryReadMemberValue(source, memberName, out object rawValue))
                return false;

            value = ConvertMemberValueToString(rawValue);
            return !string.IsNullOrEmpty(value);
        }

        private static bool TryReadMemberValue(object source, string memberName, out object value)
        {
            value = null;
            if (source == null || string.IsNullOrEmpty(memberName))
                return false;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var type = source.GetType();

            try
            {
                var property = type.GetProperty(memberName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(source, null);
                    return true;
                }

                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    value = field.GetValue(source);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static string ConvertMemberValueToString(object value)
        {
            if (value == null)
                return null;

            if (value is string str)
            {
                string trimmed = str.Trim();
                return trimmed.Length > 0 ? trimmed : null;
            }

            if (value is int || value is uint || value is long || value is ulong || value is short || value is ushort || value is byte || value is sbyte)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            if (value is Enum)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            return null;
        }

        private static void EnsurePersistedStateLoaded()
        {
            if (_persistedStateLoaded)
                return;

            _persistedStateLoaded = true;

            try
            {
                string path = GetBedStateFilePath();
                if (!File.Exists(path))
                {
                    _lastPersistedStateSignature = string.Empty;
                    return;
                }

                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 2)
                        continue;

                    string key = NormalizePersistentKey(DecodePersistedToken(parts[0]));
                    if (string.IsNullOrEmpty(key))
                        continue;

                    bool isPublic = string.Equals(parts[1], "1", StringComparison.Ordinal)
                        || string.Equals(parts[1], "true", StringComparison.OrdinalIgnoreCase);

                    string ownerId = parts.Length >= 3 ? DecodePersistedToken(parts[2]) : null;
                    string ownerName = parts.Length >= 4 ? DecodePersistedToken(parts[3]) : null;

                    if (isPublic || HasOwnerId(ownerId) || !string.IsNullOrEmpty(ownerName))
                        _publicByPersistentKey[key] = isPublic;

                    if (HasOwnerId(ownerId))
                        _ownerIdByPersistentKey[key] = ownerId;

                    if (!string.IsNullOrEmpty(ownerName))
                        _ownerNameByPersistentKey[key] = ownerName;
                }

                _lastPersistedStateSignature = string.Join("\n", BuildPersistedStateLines());

                ModMain.LogMessage(
                    "FastTravel: Loaded persisted bed state entries=" + _publicByPersistentKey.Count
                    + " owners=" + _ownerIdByPersistentKey.Count + ".");
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Failed to load bed state: " + ex);
            }
        }

        private static void SavePersistedState()
        {
            try
            {
                string path = GetBedStateFilePath();
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var lines = BuildPersistedStateLines();
                string signature = string.Join("\n", lines);
                if (string.Equals(signature, _lastPersistedStateSignature, StringComparison.Ordinal))
                    return;

                File.WriteAllLines(path, lines.ToArray());
                _lastPersistedStateSignature = signature;
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Failed to save bed state: " + ex);
            }
        }

        private static List<string> BuildPersistedStateLines()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in _publicByPersistentKey)
                keys.Add(kv.Key);

            foreach (var kv in _ownerIdByPersistentKey)
                keys.Add(kv.Key);

            foreach (var kv in _ownerNameByPersistentKey)
                keys.Add(kv.Key);

            var lines = new List<string>(keys.Count);
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                bool isPublic = _publicByPersistentKey.TryGetValue(key, out bool persistedIsPublic) && persistedIsPublic;
                _ownerIdByPersistentKey.TryGetValue(key, out string ownerId);
                _ownerNameByPersistentKey.TryGetValue(key, out string ownerName);

                if (!isPublic && !HasOwnerId(ownerId) && string.IsNullOrEmpty(ownerName))
                    continue;

                lines.Add(
                    EncodePersistedToken(key)
                    + "\t" + (isPublic ? "1" : "0")
                    + "\t" + EncodePersistedToken(ownerId ?? string.Empty)
                    + "\t" + EncodePersistedToken(ownerName ?? string.Empty));
            }

            lines.Sort(StringComparer.Ordinal);
            return lines;
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

                    string key = NormalizePersistentKey(DecodePersistedToken(encodedKey));
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

        private static string GetBedStateFilePath()
        {
            return Path.Combine(Application.persistentDataPath, "FastTravel", "bed_state.json");
        }

        private static string NormalizePersistentKey(string persistentKey)
        {
            if (string.IsNullOrEmpty(persistentKey))
                return null;

            string trimmed = persistentKey.Trim().ToLowerInvariant();
            if (trimmed.Length == 0)
                return null;

            int firstSeparator = trimmed.IndexOf('|');
            if (firstSeparator <= 0)
                return trimmed;

            string sceneToken = trimmed.Substring(0, firstSeparator);
            string remainder = trimmed.Substring(firstSeparator + 1);

            string normalizedScene = NormalizeSceneForPersistentKey(sceneToken);
            if (string.IsNullOrEmpty(normalizedScene))
                normalizedScene = UnknownSceneName;

            return normalizedScene + "|" + remainder;
        }

        private static string NormalizeSceneForPersistentKey(string sceneName)
        {
            string safeScene = string.IsNullOrEmpty(sceneName)
                ? UnknownSceneName
                : sceneName.Trim().ToLowerInvariant();

            if (safeScene.Length == 0)
                return UnknownSceneName;

            if (safeScene.Contains("dontdestroy"))
                return CanonicalWorldSceneName;

            return safeScene;
        }

        private static void MigratePersistentStateKey(string sourceKey, string destinationKey)
        {
            string normalizedSource = NormalizePersistentKey(sourceKey);
            string normalizedDestination = NormalizePersistentKey(destinationKey);

            if (string.IsNullOrEmpty(normalizedSource)
                || string.IsNullOrEmpty(normalizedDestination)
                || string.Equals(normalizedSource, normalizedDestination, StringComparison.Ordinal))
            {
                return;
            }

            bool stateMoved = false;
            stateMoved |= MovePersistentStateEntry(_publicByPersistentKey, normalizedSource, normalizedDestination);
            stateMoved |= MovePersistentStateEntry(_ownerIdByPersistentKey, normalizedSource, normalizedDestination);
            stateMoved |= MovePersistentStateEntry(_ownerNameByPersistentKey, normalizedSource, normalizedDestination);
            stateMoved |= MovePersistentStateEntry(_authoritativePublicStateByKey, normalizedSource, normalizedDestination);
            stateMoved |= MovePersistentStateEntry(_authoritativeNameByPersistentKey, normalizedSource, normalizedDestination);

            bool namesMoved = MovePersistentStateEntry(_persistedNameByKey, normalizedSource, normalizedDestination);

            if (stateMoved)
                SavePersistedState();

            if (namesMoved)
                SavePersistedNames();
        }

        private static bool MovePersistentStateEntry<TValue>(Dictionary<string, TValue> dictionary, string sourceKey, string destinationKey)
        {
            if (dictionary == null || string.IsNullOrEmpty(sourceKey) || string.IsNullOrEmpty(destinationKey))
                return false;

            if (!dictionary.TryGetValue(sourceKey, out TValue value))
                return false;

            bool changed = false;

            if (!dictionary.ContainsKey(destinationKey))
            {
                dictionary[destinationKey] = value;
                changed = true;
            }

            if (dictionary.Remove(sourceKey))
                changed = true;

            return changed;
        }

        private static string BuildPersistentBedKey(string sceneName, Vector3 position, string objectName)
        {
            float x = Quantize(position.x, KeyGridSize);
            float y = Quantize(position.y, KeyGridSize);
            float z = Quantize(position.z, KeyGridSize);

            string safeScene = NormalizeSceneForPersistentKey(sceneName);
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
