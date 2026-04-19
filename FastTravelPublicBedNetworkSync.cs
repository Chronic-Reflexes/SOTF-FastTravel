using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FastTravel
{
    internal static class FastTravelPublicBedNetworkSync
    {
        private const int PublicBedStatePacketIdHash = unchecked((int)0x46545042); // FTPB
        private const int PublicBedStatePacketSchemaVersion = 4;
        private const int PublicBedVisibilityDeltaPacketIdHash = unchecked((int)0x46545044); // FTPD
        private const int PublicBedVisibilityDeltaPacketSchemaVersion = 1;
        private const int PublicBedVisibilityRequestPacketIdHash = unchecked((int)0x46545251); // FTRQ
        private const int PublicBedVisibilityRequestSchemaVersion = 1;
        private const int PublicBedRenameRequestPacketIdHash = unchecked((int)0x4654524E); // FTRN
        private const int PublicBedRenameRequestSchemaVersion = 1;
        private const int PublicBedSnapshotRequestPacketIdHash = unchecked((int)0x46545352); // FTSR
        private const int PublicBedSnapshotRequestSchemaVersion = 1;
        private const float SnapshotRequestRetryIntervalSeconds = 2.5f;
        private const float SnapshotRequestServeCooldownSeconds = 1.25f;
        private const int MaxOutboundSnapshotChunkEntries = 6;
        private const int MaxOutboundSnapshotChunkPayloadBytes = 900;
        private const int MaxSnapshotChunkCount = 512;
        private const int MaxSnapshotRequestAttempts = 4;
        private const int MaxInboundRecords = 2048;
        private const int MaxKeyByteLength = 4096;
        private const int MaxOwnerIdByteLength = 512;
        private const int MaxOwnerNameByteLength = 512;
        private const int MaxCustomNameByteLength = 256;

        private static readonly Queue<List<FastTravelPublicBedRecord>> PendingInboundSnapshots =
            new Queue<List<FastTravelPublicBedRecord>>();

        private sealed class OutboundEntry
        {
            public bool IsPublic;
            public byte[] KeyBytes;
            public byte[] OwnerIdBytes;
            public byte[] OwnerDisplayNameBytes;
            public byte[] CustomNameBytes;
        }

        private sealed class ParsedSnapshotPacket
        {
            public int SnapshotSequence;
            public int ChunkIndex;
            public int ChunkCount;
            public List<FastTravelPublicBedRecord> Records;
        }

        private static bool _resolvedNetworkingTypes;
        private static bool _networkingTypesFailedLogged;

        private static Type _netUtilsType;
        private static Type _packetsType;
        private static Type _netRegistrationType;
        private static Type _eventPacketType;
        private static Type _globalTargetsType;
        private static Type _netEventType;

        private static MethodInfo _packetsInitMethod;
        private static MethodInfo _getPacketMethod;
        private static MethodInfo _sendPacketMethod;
        private static MethodInfo _registerPacketMethod;
        private static MethodInfo _unregisterPacketMethod;

        private static FieldInfo _registeredEventsField;
        private static PropertyInfo _eventPacketPacketProperty;
        private static PropertyInfo _udpPacketUserTokenProperty;

        private static Type _udpPacketType;
        private static Type _boltConnectionType;
        private static Type _boltPacketType;
        private static FieldInfo _boltPacketUdpPacketField;
        private static PropertyInfo _boltPacketUdpPacketProperty;

        private static MethodInfo _udpWriteInt;
        private static MethodInfo _udpWriteByte;
        private static MethodInfo _udpWriteUShort;
        private static MethodInfo _udpReadInt;
        private static MethodInfo _udpReadByte;
        private static MethodInfo _udpReadUShort;

        private static bool _packetRegistrationActive;
        private static object _publicStatePacketRegistrationInstance;
        private static object _visibilityDeltaPacketRegistrationInstance;
        private static object _visibilityRequestPacketRegistrationInstance;
        private static object _renameRequestPacketRegistrationInstance;
        private static object _snapshotRequestPacketRegistrationInstance;
        private static int _resolvedPublicStatePacketIdHash = PublicBedStatePacketIdHash;
        private static int _resolvedVisibilityDeltaPacketIdHash = PublicBedVisibilityDeltaPacketIdHash;
        private static int _resolvedVisibilityRequestPacketIdHash = PublicBedVisibilityRequestPacketIdHash;
        private static int _resolvedRenameRequestPacketIdHash = PublicBedRenameRequestPacketIdHash;
        private static int _resolvedSnapshotRequestPacketIdHash = PublicBedSnapshotRequestPacketIdHash;

        private static string _lastBroadcastSignature;
        private static bool _multiplayerLogged;
        private static bool _lastMultiplayer;
        private static bool _handshakeReadyLogged;
        private static bool _waitingForRegistrationLogged;
        private static int _broadcastSendCount;
        private static int _inboundPacketCount;
        private static string _lastInboundSignature;
        private static string _lastAppliedInboundSignature;
        private static bool _serverTargetFallbackLogged;
        private static bool _usingLegacyRegistrationPath;
        private static bool _legacyRegistrationPathLogged;
        private static int _snapshotRequestAttemptCount;
        private static float _nextSnapshotRequestAt;
        private static bool _snapshotRequestSatisfied;
        private static float _lastSnapshotRequestServedAt;
        private static string _lastSnapshotRequestServedSignature;
        private static int _outboundSnapshotSequence;
        private static int _inboundSnapshotAssemblySequence = -1;
        private static int _inboundSnapshotAssemblyChunkCount;
        private static int _lastCompletedInboundSnapshotSequence = -1;
        private static readonly HashSet<int> _inboundSnapshotReceivedChunkIndexes = new HashSet<int>();
        private static readonly Dictionary<string, FastTravelPublicBedRecord> _inboundSnapshotRecordsByKey =
            new Dictionary<string, FastTravelPublicBedRecord>(StringComparer.Ordinal);
        private static bool _multiplayerDropLogged;
        private static string _lastBroadcastTargetName;
        private static bool _packetUserTokenShimAppliedLogged;
        private static bool _packetUserTokenOverrideLogged;

        public static void Tick()
        {
            ApplyPendingInboundSnapshots();

            bool isMultiplayer = IsMultiplayer();
            if (!_multiplayerLogged || _lastMultiplayer != isMultiplayer)
            {
                _multiplayerLogged = true;
                _lastMultiplayer = isMultiplayer;
                ModMain.LogMessage("FastTravel: Public bed sync " + (isMultiplayer ? "enabled" : "idle") + ".");
            }

            if (!isMultiplayer)
            {
                if (_packetRegistrationActive && !_multiplayerDropLogged)
                {
                    _multiplayerDropLogged = true;
                    LogSync("Multiplayer state dropped; retaining packet listeners for dedicated stability.");
                }

                _lastBroadcastSignature = null;
                _handshakeReadyLogged = false;
                _waitingForRegistrationLogged = false;
                _snapshotRequestAttemptCount = 0;
                _nextSnapshotRequestAt = 0f;
                _snapshotRequestSatisfied = false;
                ResetInboundSnapshotAssembly();
                return;
            }

            _multiplayerDropLogged = false;

            EnsurePacketRegistered();

            if (!_packetRegistrationActive)
            {
                if (!_waitingForRegistrationLogged)
                {
                    _waitingForRegistrationLogged = true;
                    LogSync("Waiting for packet listener registration before handshake can proceed.");
                }

                return;
            }

            if (_waitingForRegistrationLogged)
            {
                _waitingForRegistrationLogged = false;
                LogSync("Packet listener registration is now active.");
            }

            if (!_handshakeReadyLogged)
            {
                _handshakeReadyLogged = true;
                LogSync("Handshake ready. publicStatePacketIdHash=" + _resolvedPublicStatePacketIdHash + " deltaPacketIdHash=" + _resolvedVisibilityDeltaPacketIdHash + " requestPacketIdHash=" + _resolvedVisibilityRequestPacketIdHash + " renameRequestPacketIdHash=" + _resolvedRenameRequestPacketIdHash + " snapshotRequestPacketIdHash=" + _resolvedSnapshotRequestPacketIdHash + " schemaVersion=" + PublicBedStatePacketSchemaVersion + ".");
            }

            bool isLocalServerAuthority = FastTravelLocationRegistry.IsLocalServerAuthority();
            if (!isLocalServerAuthority)
            {
                EnsureClientSnapshotRequested();
                return;
            }

            if (!PrepareNetworkingTypes())
                return;

            var snapshot = FastTravelLocationRegistry.GetPublicStateSnapshotByKey();
            var outboundEntries = BuildOrderedOutboundEntries(snapshot);
            string signature = BuildSignature(outboundEntries);

            bool changed = !string.Equals(signature, _lastBroadcastSignature, StringComparison.Ordinal);
            if (!changed)
                return;

            if (TryBroadcastSnapshot(outboundEntries, "state-change"))
            {
                _lastBroadcastSignature = signature;
            }
        }

        public static void Shutdown()
        {
            lock (PendingInboundSnapshots)
            {
                PendingInboundSnapshots.Clear();
            }

            _handshakeReadyLogged = false;
            _waitingForRegistrationLogged = false;
            _inboundPacketCount = 0;
            _lastInboundSignature = null;
            _lastAppliedInboundSignature = null;
            _snapshotRequestAttemptCount = 0;
            _nextSnapshotRequestAt = 0f;
            _snapshotRequestSatisfied = false;
            _lastSnapshotRequestServedAt = 0f;
            _lastSnapshotRequestServedSignature = null;
            _outboundSnapshotSequence = 0;
            _lastCompletedInboundSnapshotSequence = -1;
            ResetInboundSnapshotAssembly();
        }

        private static void ApplyPendingInboundSnapshots()
        {
            while (true)
            {
                List<FastTravelPublicBedRecord> records;
                lock (PendingInboundSnapshots)
                {
                    if (PendingInboundSnapshots.Count == 0)
                        break;

                    records = PendingInboundSnapshots.Dequeue();
                }

                if (records == null)
                    continue;

                if (FastTravelLocationRegistry.IsLocalServerAuthority())
                    continue;

                string appliedSignature = BuildRecordSignature(records);
                bool appliedChanged = !string.Equals(appliedSignature, _lastAppliedInboundSignature, StringComparison.Ordinal);
                if (appliedChanged || _inboundPacketCount <= 3)
                {
                    LogSync(
                        "Applying authoritative snapshot -> records=" + records.Count
                        + " public=" + CountPublicRecords(records)
                        + " withOwner=" + CountOwnedRecords(records)
                        + " sample=" + DescribeRecords(records, 3) + ".");
                }

                _lastAppliedInboundSignature = appliedSignature;

                FastTravelLocationRegistry.ApplyAuthoritativePublicStateByKey(records);
            }
        }

        private static void ResetInboundSnapshotAssembly()
        {
            _inboundSnapshotAssemblySequence = -1;
            _inboundSnapshotAssemblyChunkCount = 0;
            _inboundSnapshotReceivedChunkIndexes.Clear();
            _inboundSnapshotRecordsByKey.Clear();
        }

        private static bool TryAccumulateInboundSnapshotChunk(ParsedSnapshotPacket packet, out List<FastTravelPublicBedRecord> assembledRecords, out int receivedChunkCount)
        {
            assembledRecords = null;
            receivedChunkCount = 0;

            if (packet == null || packet.ChunkCount <= 1)
                return false;

            if (packet.SnapshotSequence == _lastCompletedInboundSnapshotSequence)
                return false;

            bool needsReset =
                packet.SnapshotSequence != _inboundSnapshotAssemblySequence
                || packet.ChunkCount != _inboundSnapshotAssemblyChunkCount;

            if (needsReset)
            {
                ResetInboundSnapshotAssembly();
                _inboundSnapshotAssemblySequence = packet.SnapshotSequence;
                _inboundSnapshotAssemblyChunkCount = packet.ChunkCount;
            }

            if (!_inboundSnapshotReceivedChunkIndexes.Add(packet.ChunkIndex))
            {
                receivedChunkCount = _inboundSnapshotReceivedChunkIndexes.Count;
                return false;
            }

            var records = packet.Records;
            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (record == null || string.IsNullOrEmpty(record.PersistentKey))
                        continue;

                    _inboundSnapshotRecordsByKey[record.PersistentKey] = record;
                }
            }

            receivedChunkCount = _inboundSnapshotReceivedChunkIndexes.Count;
            if (receivedChunkCount < _inboundSnapshotAssemblyChunkCount)
                return false;

            assembledRecords = _inboundSnapshotRecordsByKey.Values
                .OrderBy(r => r.PersistentKey, StringComparer.Ordinal)
                .ToList();

            _lastCompletedInboundSnapshotSequence = _inboundSnapshotAssemblySequence;
            ResetInboundSnapshotAssembly();
            return true;
        }

        private static List<OutboundEntry> BuildOrderedOutboundEntries(IReadOnlyList<FastTravelPublicBedRecord> snapshot)
        {
            var result = new List<OutboundEntry>();
            if (snapshot == null || snapshot.Count == 0)
                return result;

            var ordered = new List<FastTravelPublicBedRecord>(snapshot.Count);
            for (int i = 0; i < snapshot.Count; i++)
            {
                var record = snapshot[i];
                if (record == null || string.IsNullOrEmpty(record.PersistentKey))
                    continue;

                // Ownerless private beds are not needed for public/private list rendering and only bloat snapshot payloads.
                bool hasOwner = !string.IsNullOrEmpty(record.OwnerId);
                if (!record.IsPublic && !hasOwner)
                    continue;

                ordered.Add(record);
            }

            ordered.Sort((a, b) => string.Compare(a.PersistentKey, b.PersistentKey, StringComparison.Ordinal));

            for (int i = 0; i < ordered.Count; i++)
            {
                var record = ordered[i];
                string key = record.PersistentKey;

                byte[] bytes = Encoding.UTF8.GetBytes(key);
                if (bytes.Length == 0 || bytes.Length > MaxKeyByteLength)
                    continue;

                byte[] ownerIdBytes = EncodeOptionalUtf8(record.OwnerId, MaxOwnerIdByteLength);
                byte[] ownerNameBytes = EncodeOptionalUtf8(record.OwnerDisplayName, MaxOwnerNameByteLength);
                byte[] customNameBytes = EncodeOptionalUtf8(record.CustomName, MaxCustomNameByteLength);

                result.Add(new OutboundEntry
                {
                    IsPublic = record.IsPublic,
                    KeyBytes = bytes,
                    OwnerIdBytes = ownerIdBytes,
                    OwnerDisplayNameBytes = ownerNameBytes,
                    CustomNameBytes = customNameBytes
                });
            }

            return result;
        }

        private static byte[] EncodeOptionalUtf8(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0)
                return null;

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length == 0 || bytes.Length > maxLength)
                return null;

            return bytes;
        }

        private static string BuildSignature(List<OutboundEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(entries.Count * 20);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                sb.Append(entry.IsPublic ? '1' : '0');
                sb.Append(':');
                sb.Append(Convert.ToBase64String(entry.KeyBytes));
                sb.Append(':');
                sb.Append(entry.OwnerIdBytes != null ? Convert.ToBase64String(entry.OwnerIdBytes) : "-");
                sb.Append(':');
                sb.Append(entry.OwnerDisplayNameBytes != null ? Convert.ToBase64String(entry.OwnerDisplayNameBytes) : "-");
                sb.Append(':');
                sb.Append(entry.CustomNameBytes != null ? Convert.ToBase64String(entry.CustomNameBytes) : "-");
                sb.Append(';');
            }

            return sb.ToString();
        }

        private static string BuildCurrentAuthoritativeSignature()
        {
            var snapshot = FastTravelLocationRegistry.GetPublicStateSnapshotByKey();
            var outboundEntries = BuildOrderedOutboundEntries(snapshot);
            return BuildSignature(outboundEntries);
        }

        private static List<List<OutboundEntry>> BuildSnapshotChunks(List<OutboundEntry> entries)
        {
            var chunks = new List<List<OutboundEntry>>();
            if (entries == null || entries.Count == 0)
            {
                chunks.Add(new List<OutboundEntry>());
                return chunks;
            }

            int chunkEntryLimit = Mathf.Max(1, MaxOutboundSnapshotChunkEntries);
            int chunkPayloadLimit = Mathf.Max(128, MaxOutboundSnapshotChunkPayloadBytes);
            int index = 0;

            while (index < entries.Count)
            {
                // Keep the final chunk as an overflow bucket if we exceed the configured chunk cap.
                if (chunks.Count >= MaxSnapshotChunkCount - 1)
                {
                    int remaining = entries.Count - index;
                    var overflowChunk = new List<OutboundEntry>(remaining);
                    for (int i = index; i < entries.Count; i++)
                        overflowChunk.Add(entries[i]);

                    chunks.Add(overflowChunk);
                    break;
                }

                var chunk = new List<OutboundEntry>(Mathf.Min(chunkEntryLimit, entries.Count - index));
                while (index < entries.Count && chunk.Count < chunkEntryLimit)
                {
                    var candidate = entries[index];
                    if (candidate == null)
                    {
                        index++;
                        continue;
                    }

                    if (chunk.Count == 0)
                    {
                        // Always include at least one entry. If it is oversized, it gets its own chunk.
                        chunk.Add(candidate);
                        index++;
                        continue;
                    }

                    chunk.Add(candidate);
                    int projectedPayloadBytes = CalculatePayloadSizeBytes(chunk);
                    if (projectedPayloadBytes > chunkPayloadLimit)
                    {
                        chunk.RemoveAt(chunk.Count - 1);
                        break;
                    }

                    index++;
                }

                if (chunk.Count == 0)
                {
                    chunk.Add(entries[index]);
                    index++;
                }

                chunks.Add(chunk);
            }

            if (chunks.Count == 0)
                chunks.Add(new List<OutboundEntry>());

            return chunks;
        }

        private static bool TryBroadcastSnapshot(List<OutboundEntry> entries, string reason)
        {
            var chunks = BuildSnapshotChunks(entries);
            if (chunks == null || chunks.Count == 0)
            {
                chunks = new List<List<OutboundEntry>>
                {
                    new List<OutboundEntry>()
                };
            }

            int snapshotSequence = unchecked(_outboundSnapshotSequence + 1);
            if (snapshotSequence <= 0)
                snapshotSequence = 1;

            _outboundSnapshotSequence = snapshotSequence;

            var targetCandidates = ResolveBroadcastTargetValues(_globalTargetsType);
            if (targetCandidates == null || targetCandidates.Count == 0)
            {
                LogSync("Broadcast aborted: no valid global target enum value.");
                return false;
            }

            string failureReasons = null;

            for (int targetIndex = 0; targetIndex < targetCandidates.Count; targetIndex++)
            try
            {
                object globalTarget = targetCandidates[targetIndex];
                string targetName = globalTarget != null ? globalTarget.ToString() : "<null>";

                int totalPayloadBytes = 0;
                int maxChunkPayloadBytes = 0;
                for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                {
                    var chunkEntries = chunks[chunkIndex];
                    int payloadSizeBytes = CalculatePayloadSizeBytes(chunkEntries);
                    totalPayloadBytes += payloadSizeBytes;
                    if (payloadSizeBytes > maxChunkPayloadBytes)
                        maxChunkPayloadBytes = payloadSizeBytes;

                    object eventPacket = _getPacketMethod.Invoke(null, new object[]
                    {
                        _resolvedPublicStatePacketIdHash,
                        payloadSizeBytes,
                        globalTarget
                    });

                    if (eventPacket == null)
                    {
                        LogSync("Broadcast aborted: packet allocation returned null.");
                        return false;
                    }

                    object udpPacket = _eventPacketPacketProperty.GetValue(eventPacket, null);
                    if (udpPacket == null)
                    {
                        LogSync("Broadcast aborted: packet payload stream is null.");
                        return false;
                    }

                    if (!TryWriteInt(udpPacket, PublicBedStatePacketSchemaVersion)
                        || !TryWriteInt(udpPacket, snapshotSequence)
                        || !TryWriteUShort(udpPacket, (ushort)chunkIndex)
                        || !TryWriteUShort(udpPacket, (ushort)chunks.Count))
                    {
                        return false;
                    }

                    int count = chunkEntries != null ? chunkEntries.Count : 0;
                    if (!TryWriteInt(udpPacket, count))
                        return false;

                    for (int i = 0; i < count; i++)
                    {
                        var entry = chunkEntries[i];
                        if (!TryWriteByte(udpPacket, entry.IsPublic ? (byte)1 : (byte)0))
                            return false;

                        if (!TryWriteUShort(udpPacket, (ushort)entry.KeyBytes.Length))
                            return false;

                        for (int j = 0; j < entry.KeyBytes.Length; j++)
                        {
                            if (!TryWriteByte(udpPacket, entry.KeyBytes[j]))
                                return false;
                        }

                        int ownerIdLength = entry.OwnerIdBytes != null ? entry.OwnerIdBytes.Length : 0;
                        if (!TryWriteUShort(udpPacket, (ushort)ownerIdLength))
                            return false;

                        for (int j = 0; j < ownerIdLength; j++)
                        {
                            if (!TryWriteByte(udpPacket, entry.OwnerIdBytes[j]))
                                return false;
                        }

                        int ownerNameLength = entry.OwnerDisplayNameBytes != null ? entry.OwnerDisplayNameBytes.Length : 0;
                        if (!TryWriteUShort(udpPacket, (ushort)ownerNameLength))
                            return false;

                        for (int j = 0; j < ownerNameLength; j++)
                        {
                            if (!TryWriteByte(udpPacket, entry.OwnerDisplayNameBytes[j]))
                                return false;
                        }

                        int customNameLength = entry.CustomNameBytes != null ? entry.CustomNameBytes.Length : 0;
                        if (!TryWriteUShort(udpPacket, (ushort)customNameLength))
                            return false;

                        for (int j = 0; j < customNameLength; j++)
                        {
                            if (!TryWriteByte(udpPacket, entry.CustomNameBytes[j]))
                                return false;
                        }
                    }

                    // null target connection id means broadcast via global target selection.
                    if (!TrySendEventPacket(eventPacket, out string sendFailureReason))
                    {
                        LogSync("Broadcast aborted: " + sendFailureReason + ".");
                        return false;
                    }
                }

                _broadcastSendCount++;
                if (!string.Equals(_lastBroadcastTargetName, targetName, StringComparison.Ordinal))
                {
                    _lastBroadcastTargetName = targetName;
                    LogSync("Broadcast target selected -> " + targetName + ".");
                }

                int entryCount = entries != null ? entries.Count : 0;
                LogSync(
                    "Broadcast sent #" + _broadcastSendCount
                    + " reason=" + reason
                    + " target=" + targetName
                    + " sequence=" + snapshotSequence
                    + " chunks=" + chunks.Count
                    + " entries=" + entryCount
                    + " public=" + CountPublicEntries(entries)
                    + " payloadBytes=" + totalPayloadBytes
                    + " maxChunkPayloadBytes=" + maxChunkPayloadBytes
                    + " sample=" + DescribeOutboundEntries(entries, 3) + ".");

                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException;
                string errorReason = inner != null
                    ? inner.GetType().Name + ": " + inner.Message
                    : tie.Message;

                string failedTargetName = targetCandidates[targetIndex] != null
                    ? targetCandidates[targetIndex].ToString()
                    : "<null>";

                AppendFailureReason(ref failureReasons, failedTargetName, errorReason);
            }
            catch (Exception ex)
            {
                string failedTargetName = targetCandidates[targetIndex] != null
                    ? targetCandidates[targetIndex].ToString()
                    : "<null>";

                AppendFailureReason(ref failureReasons, failedTargetName, ex.Message);
            }

            LogSync("Failed to broadcast public bed snapshot: " + (failureReasons ?? "unknown send failure") + ".");
            return false;
        }

        private static bool TryBroadcastVisibilityDelta(FastTravelPublicBedRecord record, string reason)
        {
            if (record == null || string.IsNullOrEmpty(record.PersistentKey))
                return false;

            byte[] keyBytes = Encoding.UTF8.GetBytes(record.PersistentKey);
            if (keyBytes.Length == 0 || keyBytes.Length > MaxKeyByteLength)
                return false;

            byte[] ownerIdBytes = EncodeOptionalUtf8(record.OwnerId, MaxOwnerIdByteLength);
            byte[] ownerNameBytes = EncodeOptionalUtf8(record.OwnerDisplayName, MaxOwnerNameByteLength);

            var targetCandidates = ResolveBroadcastTargetValues(_globalTargetsType);
            if (targetCandidates == null || targetCandidates.Count == 0)
                return false;

            string failureReasons = null;

            for (int targetIndex = 0; targetIndex < targetCandidates.Count; targetIndex++)
            try
            {
                object globalTarget = targetCandidates[targetIndex];
                string targetName = globalTarget != null ? globalTarget.ToString() : "<null>";
                int payloadSizeBytes = CalculateVisibilityDeltaPayloadSizeBytes(keyBytes, ownerIdBytes, ownerNameBytes);

                object eventPacket = _getPacketMethod.Invoke(null, new object[]
                {
                    _resolvedVisibilityDeltaPacketIdHash,
                    payloadSizeBytes,
                    globalTarget
                });

                if (eventPacket == null)
                    return false;

                object udpPacket = _eventPacketPacketProperty.GetValue(eventPacket, null);
                if (udpPacket == null)
                    return false;

                if (!TryWriteInt(udpPacket, PublicBedVisibilityDeltaPacketSchemaVersion)
                    || !TryWriteByte(udpPacket, record.IsPublic ? (byte)1 : (byte)0)
                    || !TryWriteUShort(udpPacket, (ushort)keyBytes.Length))
                {
                    return false;
                }

                for (int i = 0; i < keyBytes.Length; i++)
                {
                    if (!TryWriteByte(udpPacket, keyBytes[i]))
                        return false;
                }

                int ownerIdLength = ownerIdBytes != null ? ownerIdBytes.Length : 0;
                if (!TryWriteUShort(udpPacket, (ushort)ownerIdLength))
                    return false;

                for (int i = 0; i < ownerIdLength; i++)
                {
                    if (!TryWriteByte(udpPacket, ownerIdBytes[i]))
                        return false;
                }

                int ownerNameLength = ownerNameBytes != null ? ownerNameBytes.Length : 0;
                if (!TryWriteUShort(udpPacket, (ushort)ownerNameLength))
                    return false;

                for (int i = 0; i < ownerNameLength; i++)
                {
                    if (!TryWriteByte(udpPacket, ownerNameBytes[i]))
                        return false;
                }

                if (!TrySendEventPacket(eventPacket, out string sendFailureReason))
                {
                    LogSync("Visibility delta send failed: " + sendFailureReason + ".");
                    return false;
                }

                LogSync(
                    "Visibility delta broadcast"
                    + " reason=" + reason
                    + " target=" + targetName
                    + " key='" + record.PersistentKey + "'"
                    + " public=" + record.IsPublic
                    + " ownerId='" + (record.OwnerId ?? "<none>") + "'"
                    + " payloadBytes=" + payloadSizeBytes + ".");

                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException;
                string errorReason = inner != null
                    ? inner.GetType().Name + ": " + inner.Message
                    : tie.Message;

                string failedTargetName = targetCandidates[targetIndex] != null
                    ? targetCandidates[targetIndex].ToString()
                    : "<null>";

                AppendFailureReason(ref failureReasons, failedTargetName, errorReason);
            }
            catch (Exception ex)
            {
                string failedTargetName = targetCandidates[targetIndex] != null
                    ? targetCandidates[targetIndex].ToString()
                    : "<null>";

                AppendFailureReason(ref failureReasons, failedTargetName, ex.Message);
            }

            LogSync("Failed to broadcast visibility delta: " + (failureReasons ?? "unknown send failure") + ".");
            return false;
        }

        private static void AppendFailureReason(ref string failureReasons, string targetName, string reason)
        {
            string entry = "target=" + (targetName ?? "<null>") + " error='" + (reason ?? "<none>") + "'";

            if (string.IsNullOrEmpty(failureReasons))
            {
                failureReasons = entry;
                return;
            }

            failureReasons += "; " + entry;
        }

        private static void EnsureClientSnapshotRequested()
        {
            if (_snapshotRequestSatisfied || _inboundPacketCount > 0)
                return;

            if (_snapshotRequestAttemptCount >= MaxSnapshotRequestAttempts)
                return;

            if (Time.unscaledTime < _nextSnapshotRequestAt)
                return;

            _snapshotRequestAttemptCount++;
            _nextSnapshotRequestAt = Time.unscaledTime + SnapshotRequestRetryIntervalSeconds;

            if (TrySendSnapshotRequest(out string failureReason))
                return;

            LogSync("Snapshot request send failed. attempt=" + _snapshotRequestAttemptCount + " reason='" + (failureReason ?? "<none>") + "'.");
        }

        private static int CalculatePayloadSizeBytes(List<OutboundEntry> entries)
        {
            int size = sizeof(int) + sizeof(int) + sizeof(ushort) + sizeof(ushort) + sizeof(int);
            int count = entries != null ? entries.Count : 0;
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                int keyLength = entry?.KeyBytes != null ? entry.KeyBytes.Length : 0;
                int ownerIdLength = entry?.OwnerIdBytes != null ? entry.OwnerIdBytes.Length : 0;
                int ownerNameLength = entry?.OwnerDisplayNameBytes != null ? entry.OwnerDisplayNameBytes.Length : 0;
                int customNameLength = entry?.CustomNameBytes != null ? entry.CustomNameBytes.Length : 0;
                size += sizeof(byte) + sizeof(ushort) + keyLength + sizeof(ushort) + ownerIdLength + sizeof(ushort) + ownerNameLength + sizeof(ushort) + customNameLength;
            }

            // Allow headroom for internal packet metadata/bits packing.
            size += 32;

            if (size < 64)
                return 64;

            if (size > ushort.MaxValue)
                return ushort.MaxValue;

            return size;
        }

        private static List<object> ResolveBroadcastTargetValues(Type globalTargetsType)
        {
            if (globalTargetsType == null || !globalTargetsType.IsEnum)
                return null;

            var result = new List<object>();

            string[] preferredNames =
            {
                "AllClients",
                "Everyone",
                "All",
                "Clients",
                "Others",
                "EveryoneExceptOwner"
            };

            for (int i = 0; i < preferredNames.Length; i++)
            {
                string name = preferredNames[i];
                if (Enum.IsDefined(globalTargetsType, name))
                    result.Add(Enum.Parse(globalTargetsType, name));
            }

            Array values = Enum.GetValues(globalTargetsType);
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    object value = values.GetValue(i);
                    if (value == null)
                        continue;

                    bool alreadyIncluded = false;
                    for (int j = 0; j < result.Count; j++)
                    {
                        if (Equals(result[j], value))
                        {
                            alreadyIncluded = true;
                            break;
                        }
                    }

                    if (!alreadyIncluded)
                        result.Add(value);
                }
            }

            if (result.Count == 0)
            {
                object fallback = Activator.CreateInstance(globalTargetsType);
                if (fallback != null)
                    result.Add(fallback);
            }

            return result;
        }

        public static bool TrySendVisibilityRequest(string persistentKey, bool makePublic, string ownerId, string ownerDisplayName, out string failureReason)
        {
            failureReason = null;

            if (string.IsNullOrEmpty(persistentKey))
            {
                failureReason = "Bed key is missing.";
                return false;
            }

            if (string.IsNullOrEmpty(ownerId))
            {
                failureReason = "Owner identity is missing.";
                return false;
            }

            if (!PrepareNetworkingTypes())
            {
                failureReason = "Network packet APIs are unavailable.";
                return false;
            }

            EnsurePacketRegistered();

            byte[] keyBytes = Encoding.UTF8.GetBytes(persistentKey);
            if (keyBytes.Length == 0 || keyBytes.Length > MaxKeyByteLength)
            {
                failureReason = "Bed key is invalid.";
                return false;
            }

            byte[] ownerIdBytes = EncodeOptionalUtf8(ownerId, MaxOwnerIdByteLength);
            if (ownerIdBytes == null || ownerIdBytes.Length == 0)
            {
                failureReason = "Owner id is invalid.";
                return false;
            }

            byte[] ownerNameBytes = EncodeOptionalUtf8(ownerDisplayName, MaxOwnerNameByteLength);

            object serverTarget = ResolveServerTargetValue(_globalTargetsType);
            if (serverTarget == null)
            {
                failureReason = "Could not resolve server packet target.";
                return false;
            }

            try
            {
                int payloadSizeBytes = CalculateVisibilityRequestPayloadSizeBytes(keyBytes, ownerIdBytes, ownerNameBytes);

                object eventPacket = _getPacketMethod.Invoke(null, new object[]
                {
                    _resolvedVisibilityRequestPacketIdHash,
                    payloadSizeBytes,
                    serverTarget
                });

                if (eventPacket == null)
                {
                    failureReason = "Could not allocate request packet.";
                    return false;
                }

                object udpPacket = _eventPacketPacketProperty.GetValue(eventPacket, null);
                if (udpPacket == null)
                {
                    failureReason = "Request packet payload stream is missing.";
                    return false;
                }

                if (!TryWriteInt(udpPacket, PublicBedVisibilityRequestSchemaVersion)
                    || !TryWriteByte(udpPacket, makePublic ? (byte)1 : (byte)0)
                    || !TryWriteUShort(udpPacket, (ushort)keyBytes.Length))
                {
                    failureReason = "Failed writing request header.";
                    return false;
                }

                for (int i = 0; i < keyBytes.Length; i++)
                {
                    if (!TryWriteByte(udpPacket, keyBytes[i]))
                    {
                        failureReason = "Failed writing request bed key.";
                        return false;
                    }
                }

                if (!TryWriteUShort(udpPacket, (ushort)ownerIdBytes.Length))
                {
                    failureReason = "Failed writing request owner id length.";
                    return false;
                }

                for (int i = 0; i < ownerIdBytes.Length; i++)
                {
                    if (!TryWriteByte(udpPacket, ownerIdBytes[i]))
                    {
                        failureReason = "Failed writing request owner id.";
                        return false;
                    }
                }

                int ownerNameLength = ownerNameBytes != null ? ownerNameBytes.Length : 0;
                if (!TryWriteUShort(udpPacket, (ushort)ownerNameLength))
                {
                    failureReason = "Failed writing request owner name length.";
                    return false;
                }

                for (int i = 0; i < ownerNameLength; i++)
                {
                    if (!TryWriteByte(udpPacket, ownerNameBytes[i]))
                    {
                        failureReason = "Failed writing request owner name.";
                        return false;
                    }
                }

                if (!TrySendEventPacket(eventPacket, out string sendFailureReason))
                {
                    failureReason = sendFailureReason;
                    return false;
                }

                LogSync(
                    "Visibility request sent -> key='" + persistentKey + "'"
                    + " requestedPublic=" + makePublic
                    + " ownerId='" + ownerId + "'"
                    + " payloadBytes=" + payloadSizeBytes + ".");
                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException;
                failureReason = inner != null ? inner.Message : tie.Message;
                LogSync("Failed sending visibility request: " + failureReason);
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                LogSync("Failed sending visibility request: " + ex.Message);
                return false;
            }
        }

        public static bool TrySendRenameRequest(string persistentKey, string ownerId, string ownerDisplayName, string customName, out string failureReason)
        {
            failureReason = null;

            if (string.IsNullOrEmpty(persistentKey))
            {
                failureReason = "Bed key is missing.";
                return false;
            }

            if (string.IsNullOrEmpty(ownerId))
            {
                failureReason = "Owner identity is missing.";
                return false;
            }

            if (!PrepareNetworkingTypes())
            {
                failureReason = "Network packet APIs are unavailable.";
                return false;
            }

            EnsurePacketRegistered();

            byte[] keyBytes = Encoding.UTF8.GetBytes(persistentKey);
            if (keyBytes.Length == 0 || keyBytes.Length > MaxKeyByteLength)
            {
                failureReason = "Bed key is invalid.";
                return false;
            }

            byte[] ownerIdBytes = EncodeOptionalUtf8(ownerId, MaxOwnerIdByteLength);
            if (ownerIdBytes == null || ownerIdBytes.Length == 0)
            {
                failureReason = "Owner id is invalid.";
                return false;
            }

            byte[] ownerNameBytes = EncodeOptionalUtf8(ownerDisplayName, MaxOwnerNameByteLength);
            byte[] customNameBytes = EncodeOptionalUtf8(customName, MaxCustomNameByteLength);

            object serverTarget = ResolveServerTargetValue(_globalTargetsType);
            if (serverTarget == null)
            {
                failureReason = "Could not resolve server packet target.";
                return false;
            }

            try
            {
                int payloadSizeBytes = CalculateRenameRequestPayloadSizeBytes(keyBytes, ownerIdBytes, ownerNameBytes, customNameBytes);

                object eventPacket = _getPacketMethod.Invoke(null, new object[]
                {
                    _resolvedRenameRequestPacketIdHash,
                    payloadSizeBytes,
                    serverTarget
                });

                if (eventPacket == null)
                {
                    failureReason = "Could not allocate rename request packet.";
                    return false;
                }

                object udpPacket = _eventPacketPacketProperty.GetValue(eventPacket, null);
                if (udpPacket == null)
                {
                    failureReason = "Rename request packet payload stream is missing.";
                    return false;
                }

                if (!TryWriteInt(udpPacket, PublicBedRenameRequestSchemaVersion)
                    || !TryWriteUShort(udpPacket, (ushort)keyBytes.Length))
                {
                    failureReason = "Failed writing rename request header.";
                    return false;
                }

                for (int i = 0; i < keyBytes.Length; i++)
                {
                    if (!TryWriteByte(udpPacket, keyBytes[i]))
                    {
                        failureReason = "Failed writing rename request bed key.";
                        return false;
                    }
                }

                if (!TryWriteUShort(udpPacket, (ushort)ownerIdBytes.Length))
                {
                    failureReason = "Failed writing rename request owner id length.";
                    return false;
                }

                for (int i = 0; i < ownerIdBytes.Length; i++)
                {
                    if (!TryWriteByte(udpPacket, ownerIdBytes[i]))
                    {
                        failureReason = "Failed writing rename request owner id.";
                        return false;
                    }
                }

                int ownerNameLength = ownerNameBytes != null ? ownerNameBytes.Length : 0;
                if (!TryWriteUShort(udpPacket, (ushort)ownerNameLength))
                {
                    failureReason = "Failed writing rename request owner name length.";
                    return false;
                }

                for (int i = 0; i < ownerNameLength; i++)
                {
                    if (!TryWriteByte(udpPacket, ownerNameBytes[i]))
                    {
                        failureReason = "Failed writing rename request owner name.";
                        return false;
                    }
                }

                int customNameLength = customNameBytes != null ? customNameBytes.Length : 0;
                if (!TryWriteUShort(udpPacket, (ushort)customNameLength))
                {
                    failureReason = "Failed writing rename request custom name length.";
                    return false;
                }

                for (int i = 0; i < customNameLength; i++)
                {
                    if (!TryWriteByte(udpPacket, customNameBytes[i]))
                    {
                        failureReason = "Failed writing rename request custom name.";
                        return false;
                    }
                }

                if (!TrySendEventPacket(eventPacket, out string sendFailureReason))
                {
                    failureReason = sendFailureReason;
                    return false;
                }

                LogSync(
                    "Rename request sent -> key='" + persistentKey + "'"
                    + " ownerId='" + ownerId + "'"
                    + " customName='" + (customName ?? "<default>") + "'"
                    + " payloadBytes=" + payloadSizeBytes + ".");
                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException;
                failureReason = inner != null ? inner.Message : tie.Message;
                LogSync("Failed sending rename request: " + failureReason);
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                LogSync("Failed sending rename request: " + ex.Message);
                return false;
            }
        }

        public static bool TrySendSnapshotRequest(out string failureReason)
        {
            failureReason = null;

            if (!PrepareNetworkingTypes())
            {
                failureReason = "Network packet APIs are unavailable.";
                return false;
            }

            EnsurePacketRegistered();

            object serverTarget = ResolveServerTargetValue(_globalTargetsType);
            if (serverTarget == null)
            {
                failureReason = "Could not resolve server packet target.";
                return false;
            }

            try
            {
                int payloadSizeBytes = CalculateSnapshotRequestPayloadSizeBytes();

                object eventPacket = _getPacketMethod.Invoke(null, new object[]
                {
                    _resolvedSnapshotRequestPacketIdHash,
                    payloadSizeBytes,
                    serverTarget
                });

                if (eventPacket == null)
                {
                    failureReason = "Could not allocate snapshot request packet.";
                    return false;
                }

                object udpPacket = _eventPacketPacketProperty.GetValue(eventPacket, null);
                if (udpPacket == null)
                {
                    failureReason = "Snapshot request packet payload stream is missing.";
                    return false;
                }

                if (!TryWriteInt(udpPacket, PublicBedSnapshotRequestSchemaVersion))
                {
                    failureReason = "Failed writing snapshot request header.";
                    return false;
                }

                if (!TrySendEventPacket(eventPacket, out string sendFailureReason))
                {
                    failureReason = sendFailureReason;
                    return false;
                }

                LogSync("Snapshot request sent. attempt=" + _snapshotRequestAttemptCount + " payloadBytes=" + payloadSizeBytes + ".");
                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException;
                failureReason = inner != null ? inner.Message : tie.Message;
                LogSync("Failed sending snapshot request: " + failureReason);
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                LogSync("Failed sending snapshot request: " + ex.Message);
                return false;
            }
        }

        private static int CalculateSnapshotRequestPayloadSizeBytes()
        {
            int size = sizeof(int) + 16;
            if (size < 32)
                return 32;

            if (size > ushort.MaxValue)
                return ushort.MaxValue;

            return size;
        }

        private static int CalculateVisibilityDeltaPayloadSizeBytes(byte[] keyBytes, byte[] ownerIdBytes, byte[] ownerNameBytes)
        {
            int keyLength = keyBytes != null ? keyBytes.Length : 0;
            int ownerIdLength = ownerIdBytes != null ? ownerIdBytes.Length : 0;
            int ownerNameLength = ownerNameBytes != null ? ownerNameBytes.Length : 0;

            int size = sizeof(int) + sizeof(byte)
                       + sizeof(ushort) + keyLength
                       + sizeof(ushort) + ownerIdLength
                       + sizeof(ushort) + ownerNameLength
                       + 16;

            if (size < 64)
                return 64;

            if (size > ushort.MaxValue)
                return ushort.MaxValue;

            return size;
        }

        private static int CalculateVisibilityRequestPayloadSizeBytes(byte[] keyBytes, byte[] ownerIdBytes, byte[] ownerNameBytes)
        {
            int keyLength = keyBytes != null ? keyBytes.Length : 0;
            int ownerIdLength = ownerIdBytes != null ? ownerIdBytes.Length : 0;
            int ownerNameLength = ownerNameBytes != null ? ownerNameBytes.Length : 0;

            int size = sizeof(int) + sizeof(byte)
                       + sizeof(ushort) + keyLength
                       + sizeof(ushort) + ownerIdLength
                       + sizeof(ushort) + ownerNameLength
                       + 16;

            if (size < 64)
                return 64;

            if (size > ushort.MaxValue)
                return ushort.MaxValue;

            return size;
        }

        private static int CalculateRenameRequestPayloadSizeBytes(byte[] keyBytes, byte[] ownerIdBytes, byte[] ownerNameBytes, byte[] customNameBytes)
        {
            int keyLength = keyBytes != null ? keyBytes.Length : 0;
            int ownerIdLength = ownerIdBytes != null ? ownerIdBytes.Length : 0;
            int ownerNameLength = ownerNameBytes != null ? ownerNameBytes.Length : 0;
            int customNameLength = customNameBytes != null ? customNameBytes.Length : 0;

            int size = sizeof(int)
                       + sizeof(ushort) + keyLength
                       + sizeof(ushort) + ownerIdLength
                       + sizeof(ushort) + ownerNameLength
                       + sizeof(ushort) + customNameLength
                       + 16;

            if (size < 64)
                return 64;

            if (size > ushort.MaxValue)
                return ushort.MaxValue;

            return size;
        }

        private static object ResolveServerTargetValue(Type globalTargetsType)
        {
            if (globalTargetsType == null || !globalTargetsType.IsEnum)
                return null;

            string[] preferredNames =
            {
                "OnlyServer",
                "Server",
                "Host",
                "MasterClient"
            };

            for (int i = 0; i < preferredNames.Length; i++)
            {
                string name = preferredNames[i];
                if (Enum.IsDefined(globalTargetsType, name))
                    return Enum.Parse(globalTargetsType, name);
            }

            var broadcastTargets = ResolveBroadcastTargetValues(globalTargetsType);
            object fallbackTarget = (broadcastTargets != null && broadcastTargets.Count > 0)
                ? broadcastTargets[0]
                : null;
            if (fallbackTarget != null && !_serverTargetFallbackLogged)
            {
                _serverTargetFallbackLogged = true;
                LogSync("Server-only target enum name not found; falling back to broadcast target for visibility requests.");
            }

            return fallbackTarget;
        }

        private static void EnsurePacketRegistered()
        {
            if (_packetRegistrationActive)
                return;

            if (!PrepareNetworkingTypes())
                return;

            if (_netRegistrationType == null || _udpPacketType == null || _boltConnectionType == null)
                return;

            if (!_usingLegacyRegistrationPath && _registerPacketMethod == null)
                return;

            try
            {
                if (_packetsInitMethod != null)
                    _packetsInitMethod.Invoke(null, null);

                if (_usingLegacyRegistrationPath && !_legacyRegistrationPathLogged)
                {
                    _legacyRegistrationPathLogged = true;
                    LogSync("Using legacy packet registration path (RegisteredEvents map fallback).");
                }

                if (TryGetRegisteredEventsMap(out IDictionary existingEvents)
                    && existingEvents.Contains(_resolvedPublicStatePacketIdHash)
                    && existingEvents.Contains(_resolvedVisibilityDeltaPacketIdHash)
                    && existingEvents.Contains(_resolvedVisibilityRequestPacketIdHash)
                    && existingEvents.Contains(_resolvedRenameRequestPacketIdHash)
                    && existingEvents.Contains(_resolvedSnapshotRequestPacketIdHash))
                {
                    _packetRegistrationActive = true;
                    LogSync(
                        "Packet listeners already registered. mapCount=" + existingEvents.Count
                        + " publicStateHash=" + _resolvedPublicStatePacketIdHash
                        + " deltaHash=" + _resolvedVisibilityDeltaPacketIdHash
                        + " requestHash=" + _resolvedVisibilityRequestPacketIdHash
                        + " renameRequestHash=" + _resolvedRenameRequestPacketIdHash
                        + " snapshotRequestHash=" + _resolvedSnapshotRequestPacketIdHash + ".");
                    return;
                }

                _publicStatePacketRegistrationInstance = _publicStatePacketRegistrationInstance
                    ?? CreatePacketRegistrationInstance(_resolvedPublicStatePacketIdHash, nameof(OnPublicStatePacketReadGeneric));

                _visibilityDeltaPacketRegistrationInstance = _visibilityDeltaPacketRegistrationInstance
                    ?? CreatePacketRegistrationInstance(_resolvedVisibilityDeltaPacketIdHash, nameof(OnVisibilityDeltaPacketReadGeneric));

                _visibilityRequestPacketRegistrationInstance = _visibilityRequestPacketRegistrationInstance
                    ?? CreatePacketRegistrationInstance(_resolvedVisibilityRequestPacketIdHash, nameof(OnVisibilityRequestPacketReadGeneric));

                _renameRequestPacketRegistrationInstance = _renameRequestPacketRegistrationInstance
                    ?? CreatePacketRegistrationInstance(_resolvedRenameRequestPacketIdHash, nameof(OnRenameRequestPacketReadGeneric));

                _snapshotRequestPacketRegistrationInstance = _snapshotRequestPacketRegistrationInstance
                    ?? CreatePacketRegistrationInstance(_resolvedSnapshotRequestPacketIdHash, nameof(OnSnapshotRequestPacketReadGeneric));

                if (_publicStatePacketRegistrationInstance == null
                    || _visibilityDeltaPacketRegistrationInstance == null
                    || _visibilityRequestPacketRegistrationInstance == null
                    || _renameRequestPacketRegistrationInstance == null
                    || _snapshotRequestPacketRegistrationInstance == null)
                {
                    LogSync("Failed to create packet registration instances.");
                    return;
                }

                if (_usingLegacyRegistrationPath)
                {
                    if (!TryGetRegisteredEventsMap(out IDictionary registrationMap))
                    {
                        LogSync("Could not access packet registration map.");
                        return;
                    }

                    registrationMap[_resolvedPublicStatePacketIdHash] = _publicStatePacketRegistrationInstance;
                    registrationMap[_resolvedVisibilityDeltaPacketIdHash] = _visibilityDeltaPacketRegistrationInstance;
                    registrationMap[_resolvedVisibilityRequestPacketIdHash] = _visibilityRequestPacketRegistrationInstance;
                    registrationMap[_resolvedRenameRequestPacketIdHash] = _renameRequestPacketRegistrationInstance;
                    registrationMap[_resolvedSnapshotRequestPacketIdHash] = _snapshotRequestPacketRegistrationInstance;
                }
                else
                {
                    _registerPacketMethod.Invoke(null, new[] { _publicStatePacketRegistrationInstance });
                    _registerPacketMethod.Invoke(null, new[] { _visibilityDeltaPacketRegistrationInstance });
                    _registerPacketMethod.Invoke(null, new[] { _visibilityRequestPacketRegistrationInstance });
                    _registerPacketMethod.Invoke(null, new[] { _renameRequestPacketRegistrationInstance });
                    _registerPacketMethod.Invoke(null, new[] { _snapshotRequestPacketRegistrationInstance });
                }

                _packetRegistrationActive = true;

                string mapCountText = "n/a";
                if (TryGetRegisteredEventsMap(out IDictionary mapCountMap))
                    mapCountText = mapCountMap.Count.ToString();

                LogSync(
                    "Registered packet listeners. mapCount=" + mapCountText
                    + " publicStateHash=" + _resolvedPublicStatePacketIdHash
                    + " deltaHash=" + _resolvedVisibilityDeltaPacketIdHash
                    + " requestHash=" + _resolvedVisibilityRequestPacketIdHash
                    + " renameRequestHash=" + _resolvedRenameRequestPacketIdHash
                    + " snapshotRequestHash=" + _resolvedSnapshotRequestPacketIdHash + ".");
            }
            catch (Exception ex)
            {
                LogSync("Failed to register public bed packet listener: " + ex.Message);
            }
        }

        private static void EnsurePacketUnregistered()
        {
            if (!_packetRegistrationActive)
                return;

            try
            {
                int removedCount = 0;

                if (_usingLegacyRegistrationPath)
                {
                    if (TryGetRegisteredEventsMap(out IDictionary removeMap))
                    {
                        if (removeMap.Contains(_resolvedPublicStatePacketIdHash))
                        {
                            removeMap.Remove(_resolvedPublicStatePacketIdHash);
                            removedCount++;
                        }

                        if (removeMap.Contains(_resolvedVisibilityDeltaPacketIdHash))
                        {
                            removeMap.Remove(_resolvedVisibilityDeltaPacketIdHash);
                            removedCount++;
                        }

                        if (removeMap.Contains(_resolvedVisibilityRequestPacketIdHash))
                        {
                            removeMap.Remove(_resolvedVisibilityRequestPacketIdHash);
                            removedCount++;
                        }

                        if (removeMap.Contains(_resolvedRenameRequestPacketIdHash))
                        {
                            removeMap.Remove(_resolvedRenameRequestPacketIdHash);
                            removedCount++;
                        }

                        if (removeMap.Contains(_resolvedSnapshotRequestPacketIdHash))
                        {
                            removeMap.Remove(_resolvedSnapshotRequestPacketIdHash);
                            removedCount++;
                        }
                    }
                }
                else if (_unregisterPacketMethod != null)
                {
                    if (_publicStatePacketRegistrationInstance != null)
                    {
                        object removedPublic = _unregisterPacketMethod.Invoke(null, new[] { _publicStatePacketRegistrationInstance });
                        if (removedPublic is bool didRemovePublic && didRemovePublic)
                            removedCount++;
                    }

                    if (_visibilityRequestPacketRegistrationInstance != null)
                    {
                        object removedRequest = _unregisterPacketMethod.Invoke(null, new[] { _visibilityRequestPacketRegistrationInstance });
                        if (removedRequest is bool didRemoveRequest && didRemoveRequest)
                            removedCount++;
                    }

                    if (_visibilityDeltaPacketRegistrationInstance != null)
                    {
                        object removedDelta = _unregisterPacketMethod.Invoke(null, new[] { _visibilityDeltaPacketRegistrationInstance });
                        if (removedDelta is bool didRemoveDelta && didRemoveDelta)
                            removedCount++;
                    }

                    if (_renameRequestPacketRegistrationInstance != null)
                    {
                        object removedRenameRequest = _unregisterPacketMethod.Invoke(null, new[] { _renameRequestPacketRegistrationInstance });
                        if (removedRenameRequest is bool didRemoveRenameRequest && didRemoveRenameRequest)
                            removedCount++;
                    }

                    if (_snapshotRequestPacketRegistrationInstance != null)
                    {
                        object removedSnapshotRequest = _unregisterPacketMethod.Invoke(null, new[] { _snapshotRequestPacketRegistrationInstance });
                        if (removedSnapshotRequest is bool didRemoveSnapshotRequest && didRemoveSnapshotRequest)
                            removedCount++;
                    }
                }

                string mapCountText = "n/a";
                if (TryGetRegisteredEventsMap(out IDictionary registeredEvents))
                    mapCountText = registeredEvents.Count.ToString();

                LogSync("Unregistered packet listeners. removed=" + removedCount + " mapCount=" + mapCountText + ".");
            }
            catch (Exception ex)
            {
                LogSync("Failed to unregister public bed packet listener: " + ex.Message);
            }
            finally
            {
                _packetRegistrationActive = false;
            }
        }

        private static object CreatePacketRegistrationInstance(int packetIdHash, string callbackMethodName)
        {
            if (_netRegistrationType == null || _udpPacketType == null || _boltConnectionType == null)
                return null;

            Type actionType = typeof(Action<,>).MakeGenericType(_udpPacketType, _boltConnectionType);
            MethodInfo genericHandler = typeof(FastTravelPublicBedNetworkSync).GetMethod(callbackMethodName, BindingFlags.NonPublic | BindingFlags.Static);
            if (genericHandler == null)
                return null;

            MethodInfo closedHandler = genericHandler.MakeGenericMethod(_udpPacketType, _boltConnectionType);
            Delegate readDelegate = Delegate.CreateDelegate(actionType, closedHandler);

            ConstructorInfo ctor = _netRegistrationType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), actionType },
                null);

            if (ctor == null)
            {
                LogSync("Unable to locate packet registration constructor.");
                return null;
            }

            return ctor.Invoke(new object[] { packetIdHash, readDelegate });
        }

        private static bool TryGetRegisteredEventsMap(out IDictionary map)
        {
            map = null;

            if (_registeredEventsField == null)
                return false;

            try
            {
                object current = _registeredEventsField.GetValue(null);
                if (current == null && _packetsInitMethod != null)
                {
                    _packetsInitMethod.Invoke(null, null);
                    current = _registeredEventsField.GetValue(null);
                }

                map = current as IDictionary;
                return map != null;
            }
            catch (Exception ex)
            {
                LogSync("Failed accessing packet registration map: " + ex.Message);
                return false;
            }
        }

        private static void OnPublicStatePacketReadGeneric<TPacket, TConnection>(TPacket packet, TConnection connection)
        {
            OnPublicStatePacketReadInternal(packet, connection);
        }

        private static void OnVisibilityDeltaPacketReadGeneric<TPacket, TConnection>(TPacket packet, TConnection connection)
        {
            OnVisibilityDeltaPacketReadInternal(packet, connection);
        }

        private static void OnVisibilityRequestPacketReadGeneric<TPacket, TConnection>(TPacket packet, TConnection connection)
        {
            OnVisibilityRequestPacketReadInternal(packet, connection);
        }

        private static void OnRenameRequestPacketReadGeneric<TPacket, TConnection>(TPacket packet, TConnection connection)
        {
            OnRenameRequestPacketReadInternal(packet, connection);
        }

        private static void OnSnapshotRequestPacketReadGeneric<TPacket, TConnection>(TPacket packet, TConnection connection)
        {
            OnSnapshotRequestPacketReadInternal(packet, connection);
        }

        private static void OnPublicStatePacketReadInternal(object udpPacket, object connection)
        {
            if (udpPacket == null)
                return;

            if (FastTravelLocationRegistry.IsLocalServerAuthority())
                return;

            if (!PrepareNetworkingTypes())
                return;

            int packetNumber = ++_inboundPacketCount;
            string connectionDescriptor = DescribeConnection(connection);

            if (!TryReadSnapshot(udpPacket, out var snapshotPacket))
            {
                LogSync("Inbound snapshot parse failed for packet #" + packetNumber + " from " + connectionDescriptor + ".");
                return;
            }

            if (snapshotPacket.ChunkCount <= 1)
            {
                var records = snapshotPacket.Records;
                string inboundSignature = BuildRecordSignature(records);
                bool inboundChanged = !string.Equals(inboundSignature, _lastInboundSignature, StringComparison.Ordinal);
                if (inboundChanged || packetNumber <= 3)
                {
                    LogSync(
                        "Inbound snapshot received #" + packetNumber
                        + " from " + connectionDescriptor
                        + " records=" + records.Count
                        + " public=" + CountPublicRecords(records)
                        + " withOwner=" + CountOwnedRecords(records)
                        + " sample=" + DescribeRecords(records, 3) + ".");
                }

                _lastInboundSignature = inboundSignature;
                _snapshotRequestSatisfied = true;

                lock (PendingInboundSnapshots)
                {
                    PendingInboundSnapshots.Enqueue(records);

                    if (inboundChanged || packetNumber <= 3)
                    {
                        LogSync("Inbound snapshot queued. pendingQueue=" + PendingInboundSnapshots.Count + ".");
                    }
                }

                return;
            }

            int chunkOneBased = snapshotPacket.ChunkIndex + 1;
            if (packetNumber <= 3)
            {
                LogSync(
                    "Inbound snapshot chunk received #" + packetNumber
                    + " from " + connectionDescriptor
                    + " sequence=" + snapshotPacket.SnapshotSequence
                    + " chunk=" + chunkOneBased + "/" + snapshotPacket.ChunkCount
                    + " records=" + snapshotPacket.Records.Count + ".");
            }

            if (!TryAccumulateInboundSnapshotChunk(snapshotPacket, out var assembledRecords, out int receivedChunkCount))
            {
                LogSync(
                    "Inbound snapshot chunk buffered"
                    + " sequence=" + snapshotPacket.SnapshotSequence
                    + " chunk=" + chunkOneBased + "/" + snapshotPacket.ChunkCount
                    + " received=" + receivedChunkCount + "/" + snapshotPacket.ChunkCount + ".");
                return;
            }

            string assembledSignature = BuildRecordSignature(assembledRecords);
            bool assembledChanged = !string.Equals(assembledSignature, _lastInboundSignature, StringComparison.Ordinal);
            if (assembledChanged || packetNumber <= 3)
            {
                LogSync(
                    "Inbound snapshot assembled"
                    + " sequence=" + snapshotPacket.SnapshotSequence
                    + " chunks=" + snapshotPacket.ChunkCount
                    + " records=" + assembledRecords.Count
                    + " public=" + CountPublicRecords(assembledRecords)
                    + " withOwner=" + CountOwnedRecords(assembledRecords)
                    + " sample=" + DescribeRecords(assembledRecords, 3) + ".");
            }

            _lastInboundSignature = assembledSignature;
            _snapshotRequestSatisfied = true;

            lock (PendingInboundSnapshots)
            {
                PendingInboundSnapshots.Enqueue(assembledRecords);

                if (assembledChanged || packetNumber <= 3)
                {
                    LogSync("Inbound snapshot queued. pendingQueue=" + PendingInboundSnapshots.Count + ".");
                }
            }
        }

        private static void OnVisibilityDeltaPacketReadInternal(object udpPacket, object connection)
        {
            if (udpPacket == null)
                return;

            if (FastTravelLocationRegistry.IsLocalServerAuthority())
                return;

            if (!PrepareNetworkingTypes())
                return;

            if (!TryReadVisibilityDelta(udpPacket, out var record))
            {
                LogSync("Visibility delta parse failed from " + DescribeConnection(connection) + ".");
                return;
            }

            FastTravelLocationRegistry.ApplyAuthoritativePublicStateDelta(record);
            _snapshotRequestSatisfied = true;

            LogSync(
                "Visibility delta applied from " + DescribeConnection(connection)
                + " key='" + record.PersistentKey + "'"
                + " public=" + record.IsPublic
                + " ownerId='" + (record.OwnerId ?? "<none>") + "'.");
        }

        private static void OnVisibilityRequestPacketReadInternal(object udpPacket, object connection)
        {
            if (udpPacket == null)
                return;

            if (!FastTravelLocationRegistry.IsLocalServerAuthority())
                return;

            if (!PrepareNetworkingTypes())
                return;

            if (!TryReadVisibilityRequest(udpPacket, out string persistentKey, out bool makePublic, out string ownerId, out string ownerDisplayName))
            {
                LogSync("Visibility request parse failed from " + DescribeConnection(connection) + ".");
                return;
            }

            if (FastTravelLocationRegistry.TryApplyVisibilityRequestFromNetwork(persistentKey, makePublic, ownerId, ownerDisplayName, out string failureReason))
            {
                LogSync(
                    "Visibility request applied from " + DescribeConnection(connection)
                    + " key='" + persistentKey + "'"
                    + " requestedPublic=" + makePublic
                    + " ownerId='" + (ownerId ?? "<null>") + "'.");

                LogSync("Visibility request accepted; authoritative replication deferred to server tick state-change broadcast.");
            }
            else
            {
                LogSync(
                    "Visibility request rejected from " + DescribeConnection(connection)
                    + " key='" + (persistentKey ?? "<null>") + "'"
                    + " requestedPublic=" + makePublic
                    + " ownerId='" + (ownerId ?? "<null>") + "'"
                    + " reason='" + (failureReason ?? "<none>") + "'.");
            }
        }

        private static void OnRenameRequestPacketReadInternal(object udpPacket, object connection)
        {
            if (udpPacket == null)
                return;

            if (!FastTravelLocationRegistry.IsLocalServerAuthority())
                return;

            if (!PrepareNetworkingTypes())
                return;

            if (!TryReadRenameRequest(udpPacket, out string persistentKey, out string ownerId, out string ownerDisplayName, out string customName))
            {
                LogSync("Rename request parse failed from " + DescribeConnection(connection) + ".");
                return;
            }

            if (FastTravelLocationRegistry.TryApplyRenameRequestFromNetwork(persistentKey, ownerId, ownerDisplayName, customName, out string failureReason))
            {
                LogSync(
                    "Rename request applied from " + DescribeConnection(connection)
                    + " key='" + persistentKey + "'"
                    + " ownerId='" + (ownerId ?? "<null>") + "'"
                    + " customName='" + (customName ?? "<default>") + "'.");
            }
            else
            {
                LogSync(
                    "Rename request rejected from " + DescribeConnection(connection)
                    + " key='" + (persistentKey ?? "<null>") + "'"
                    + " ownerId='" + (ownerId ?? "<null>") + "'"
                    + " customName='" + (customName ?? "<default>") + "'"
                    + " reason='" + (failureReason ?? "<none>") + "'.");
            }
        }

        private static void OnSnapshotRequestPacketReadInternal(object udpPacket, object connection)
        {
            if (udpPacket == null)
                return;

            if (!FastTravelLocationRegistry.IsLocalServerAuthority())
                return;

            if (!PrepareNetworkingTypes())
                return;

            if (!TryReadSnapshotRequest(udpPacket))
            {
                LogSync("Snapshot request parse failed from " + DescribeConnection(connection) + ".");
                return;
            }

            var snapshot = FastTravelLocationRegistry.GetPublicStateSnapshotByKey();
            var outboundEntries = BuildOrderedOutboundEntries(snapshot);
            string signature = BuildSignature(outboundEntries);

            bool duplicateSnapshotServe =
                string.Equals(_lastSnapshotRequestServedSignature, signature, StringComparison.Ordinal)
                && (Time.unscaledTime - _lastSnapshotRequestServedAt) < SnapshotRequestServeCooldownSeconds;

            if (duplicateSnapshotServe)
            {
                LogSync("Snapshot request skipped (cooldown) for " + DescribeConnection(connection) + ".");
                return;
            }

            if (TryBroadcastSnapshot(outboundEntries, "client-sync-request"))
            {
                _lastBroadcastSignature = signature;
                _lastSnapshotRequestServedAt = Time.unscaledTime;
                _lastSnapshotRequestServedSignature = signature;
                LogSync("Snapshot request served for " + DescribeConnection(connection) + ".");
            }
        }

        private static bool TryReadSnapshot(object udpPacket, out ParsedSnapshotPacket packet)
        {
            packet = null;

            if (!TryReadInt(udpPacket, out int schemaVersion))
                return false;

            bool supportsOwnerMetadata = schemaVersion >= 2;
            bool supportsCustomName = schemaVersion >= 4;
            if (schemaVersion < 1 || schemaVersion > PublicBedStatePacketSchemaVersion)
                return false;

            int snapshotSequence = 0;
            int chunkIndex = 0;
            int chunkCount = 1;

            if (schemaVersion >= 3)
            {
                if (!TryReadInt(udpPacket, out snapshotSequence))
                    return false;

                if (!TryReadUShort(udpPacket, out ushort chunkIndexRaw))
                    return false;

                if (!TryReadUShort(udpPacket, out ushort chunkCountRaw))
                    return false;

                chunkIndex = chunkIndexRaw;
                chunkCount = chunkCountRaw;

                if (chunkCount <= 0 || chunkCount > MaxSnapshotChunkCount)
                    return false;

                if (chunkIndex < 0 || chunkIndex >= chunkCount)
                    return false;
            }

            if (!TryReadInt(udpPacket, out int count))
                return false;

            if (count < 0 || count > MaxInboundRecords)
                return false;

            var parsed = new List<FastTravelPublicBedRecord>(count);

            for (int i = 0; i < count; i++)
            {
                if (!TryReadByte(udpPacket, out byte isPublicByte))
                    return false;

                if (!TryReadUShort(udpPacket, out ushort keyLength))
                    return false;

                if (keyLength == 0 || keyLength > MaxKeyByteLength)
                    return false;

                byte[] keyBytes = new byte[keyLength];
                for (int j = 0; j < keyLength; j++)
                {
                    if (!TryReadByte(udpPacket, out byte keyByte))
                        return false;

                    keyBytes[j] = keyByte;
                }

                string key = Encoding.UTF8.GetString(keyBytes);
                if (string.IsNullOrEmpty(key))
                    continue;

                string ownerId = null;
                string ownerDisplayName = null;
                string customName = null;

                if (supportsOwnerMetadata)
                {
                    if (!TryReadUShort(udpPacket, out ushort ownerIdLength))
                        return false;

                    if (ownerIdLength > MaxOwnerIdByteLength)
                        return false;

                    if (ownerIdLength > 0)
                    {
                        byte[] ownerIdBytes = new byte[ownerIdLength];
                        for (int j = 0; j < ownerIdLength; j++)
                        {
                            if (!TryReadByte(udpPacket, out byte ownerIdByte))
                                return false;

                            ownerIdBytes[j] = ownerIdByte;
                        }

                        ownerId = Encoding.UTF8.GetString(ownerIdBytes);
                    }

                    if (!TryReadUShort(udpPacket, out ushort ownerNameLength))
                        return false;

                    if (ownerNameLength > MaxOwnerNameByteLength)
                        return false;

                    if (ownerNameLength > 0)
                    {
                        byte[] ownerNameBytes = new byte[ownerNameLength];
                        for (int j = 0; j < ownerNameLength; j++)
                        {
                            if (!TryReadByte(udpPacket, out byte ownerNameByte))
                                return false;

                            ownerNameBytes[j] = ownerNameByte;
                        }

                        ownerDisplayName = Encoding.UTF8.GetString(ownerNameBytes);
                    }
                }

                if (supportsCustomName)
                {
                    if (!TryReadUShort(udpPacket, out ushort customNameLength))
                        return false;

                    if (customNameLength > MaxCustomNameByteLength)
                        return false;

                    if (customNameLength > 0)
                    {
                        byte[] customNameBytes = new byte[customNameLength];
                        for (int j = 0; j < customNameLength; j++)
                        {
                            if (!TryReadByte(udpPacket, out byte customNameByte))
                                return false;

                            customNameBytes[j] = customNameByte;
                        }

                        customName = Encoding.UTF8.GetString(customNameBytes);
                    }
                }

                parsed.Add(new FastTravelPublicBedRecord(key, isPublicByte != 0, ownerId, ownerDisplayName, customName));
            }

            packet = new ParsedSnapshotPacket
            {
                SnapshotSequence = snapshotSequence,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Records = parsed
            };

            return true;
        }

        private static bool TryReadVisibilityRequest(object udpPacket, out string persistentKey, out bool makePublic, out string ownerId, out string ownerDisplayName)
        {
            persistentKey = null;
            makePublic = false;
            ownerId = null;
            ownerDisplayName = null;

            if (!TryReadInt(udpPacket, out int schemaVersion))
                return false;

            if (schemaVersion != PublicBedVisibilityRequestSchemaVersion)
                return false;

            if (!TryReadByte(udpPacket, out byte makePublicByte))
                return false;

            makePublic = makePublicByte != 0;

            if (!TryReadUShort(udpPacket, out ushort keyLength))
                return false;

            if (keyLength == 0 || keyLength > MaxKeyByteLength)
                return false;

            byte[] keyBytes = new byte[keyLength];
            for (int i = 0; i < keyLength; i++)
            {
                if (!TryReadByte(udpPacket, out byte keyByte))
                    return false;

                keyBytes[i] = keyByte;
            }

            persistentKey = Encoding.UTF8.GetString(keyBytes);
            if (string.IsNullOrEmpty(persistentKey))
                return false;

            if (!TryReadUShort(udpPacket, out ushort ownerIdLength))
                return false;

            if (ownerIdLength == 0 || ownerIdLength > MaxOwnerIdByteLength)
                return false;

            byte[] ownerIdBytes = new byte[ownerIdLength];
            for (int i = 0; i < ownerIdLength; i++)
            {
                if (!TryReadByte(udpPacket, out byte ownerIdByte))
                    return false;

                ownerIdBytes[i] = ownerIdByte;
            }

            ownerId = Encoding.UTF8.GetString(ownerIdBytes);
            if (string.IsNullOrEmpty(ownerId))
                return false;

            if (!TryReadUShort(udpPacket, out ushort ownerNameLength))
                return false;

            if (ownerNameLength > MaxOwnerNameByteLength)
                return false;

            if (ownerNameLength > 0)
            {
                byte[] ownerNameBytes = new byte[ownerNameLength];
                for (int i = 0; i < ownerNameLength; i++)
                {
                    if (!TryReadByte(udpPacket, out byte ownerNameByte))
                        return false;

                    ownerNameBytes[i] = ownerNameByte;
                }

                ownerDisplayName = Encoding.UTF8.GetString(ownerNameBytes);
            }

            return true;
        }

        private static bool TryReadRenameRequest(object udpPacket, out string persistentKey, out string ownerId, out string ownerDisplayName, out string customName)
        {
            persistentKey = null;
            ownerId = null;
            ownerDisplayName = null;
            customName = null;

            if (!TryReadInt(udpPacket, out int schemaVersion))
                return false;

            if (schemaVersion != PublicBedRenameRequestSchemaVersion)
                return false;

            if (!TryReadUShort(udpPacket, out ushort keyLength))
                return false;

            if (keyLength == 0 || keyLength > MaxKeyByteLength)
                return false;

            byte[] keyBytes = new byte[keyLength];
            for (int i = 0; i < keyLength; i++)
            {
                if (!TryReadByte(udpPacket, out byte keyByte))
                    return false;

                keyBytes[i] = keyByte;
            }

            persistentKey = Encoding.UTF8.GetString(keyBytes);
            if (string.IsNullOrEmpty(persistentKey))
                return false;

            if (!TryReadUShort(udpPacket, out ushort ownerIdLength))
                return false;

            if (ownerIdLength == 0 || ownerIdLength > MaxOwnerIdByteLength)
                return false;

            byte[] ownerIdBytes = new byte[ownerIdLength];
            for (int i = 0; i < ownerIdLength; i++)
            {
                if (!TryReadByte(udpPacket, out byte ownerIdByte))
                    return false;

                ownerIdBytes[i] = ownerIdByte;
            }

            ownerId = Encoding.UTF8.GetString(ownerIdBytes);
            if (string.IsNullOrEmpty(ownerId))
                return false;

            if (!TryReadUShort(udpPacket, out ushort ownerNameLength))
                return false;

            if (ownerNameLength > MaxOwnerNameByteLength)
                return false;

            if (ownerNameLength > 0)
            {
                byte[] ownerNameBytes = new byte[ownerNameLength];
                for (int i = 0; i < ownerNameLength; i++)
                {
                    if (!TryReadByte(udpPacket, out byte ownerNameByte))
                        return false;

                    ownerNameBytes[i] = ownerNameByte;
                }

                ownerDisplayName = Encoding.UTF8.GetString(ownerNameBytes);
            }

            if (!TryReadUShort(udpPacket, out ushort customNameLength))
                return false;

            if (customNameLength > MaxCustomNameByteLength)
                return false;

            if (customNameLength > 0)
            {
                byte[] customNameBytes = new byte[customNameLength];
                for (int i = 0; i < customNameLength; i++)
                {
                    if (!TryReadByte(udpPacket, out byte customNameByte))
                        return false;

                    customNameBytes[i] = customNameByte;
                }

                customName = Encoding.UTF8.GetString(customNameBytes);
            }

            return true;
        }

        private static bool TryReadVisibilityDelta(object udpPacket, out FastTravelPublicBedRecord record)
        {
            record = null;

            if (!TryReadInt(udpPacket, out int schemaVersion))
                return false;

            if (schemaVersion != PublicBedVisibilityDeltaPacketSchemaVersion)
                return false;

            if (!TryReadByte(udpPacket, out byte isPublicByte))
                return false;

            if (!TryReadUShort(udpPacket, out ushort keyLength))
                return false;

            if (keyLength == 0 || keyLength > MaxKeyByteLength)
                return false;

            byte[] keyBytes = new byte[keyLength];
            for (int i = 0; i < keyLength; i++)
            {
                if (!TryReadByte(udpPacket, out byte keyByte))
                    return false;

                keyBytes[i] = keyByte;
            }

            string key = Encoding.UTF8.GetString(keyBytes);
            if (string.IsNullOrEmpty(key))
                return false;

            if (!TryReadUShort(udpPacket, out ushort ownerIdLength))
                return false;

            if (ownerIdLength > MaxOwnerIdByteLength)
                return false;

            string ownerId = null;
            if (ownerIdLength > 0)
            {
                byte[] ownerIdBytes = new byte[ownerIdLength];
                for (int i = 0; i < ownerIdLength; i++)
                {
                    if (!TryReadByte(udpPacket, out byte ownerIdByte))
                        return false;

                    ownerIdBytes[i] = ownerIdByte;
                }

                ownerId = Encoding.UTF8.GetString(ownerIdBytes);
            }

            if (!TryReadUShort(udpPacket, out ushort ownerNameLength))
                return false;

            if (ownerNameLength > MaxOwnerNameByteLength)
                return false;

            string ownerDisplayName = null;
            if (ownerNameLength > 0)
            {
                byte[] ownerNameBytes = new byte[ownerNameLength];
                for (int i = 0; i < ownerNameLength; i++)
                {
                    if (!TryReadByte(udpPacket, out byte ownerNameByte))
                        return false;

                    ownerNameBytes[i] = ownerNameByte;
                }

                ownerDisplayName = Encoding.UTF8.GetString(ownerNameBytes);
            }

            record = new FastTravelPublicBedRecord(key, isPublicByte != 0, ownerId, ownerDisplayName);
            return true;
        }

        private static bool TryReadSnapshotRequest(object udpPacket)
        {
            if (!TryReadInt(udpPacket, out int schemaVersion))
                return false;

            return schemaVersion == PublicBedSnapshotRequestSchemaVersion;
        }

        private static bool PrepareNetworkingTypes()
        {
            if (_resolvedNetworkingTypes)
                return true;

            try
            {
                _netUtilsType = ResolveType("SonsSdk.Networking.NetUtils", "SonsSdk");
                _packetsType = ResolveType("SonsSdk.Networking.Packets", "SonsSdk");
                if (_packetsType == null)
                {
                    if (!_networkingTypesFailedLogged)
                    {
                        _networkingTypesFailedLogged = true;
                        LogSync("Could not resolve SonsSdk.Networking.Packets type.");
                    }

                    return false;
                }

                _netRegistrationType = _packetsType.GetNestedType("NetRegistration", BindingFlags.Public | BindingFlags.NonPublic);
                _eventPacketType = _packetsType.GetNestedType("EventPacket", BindingFlags.Public | BindingFlags.NonPublic);
                _netEventType = ResolveType("SonsSdk.Networking.NetEvent", "SonsSdk")
                    ?? ResolveType("NetEvent", "SonsSdk");

                _packetsInitMethod = _packetsType.GetMethod("Init", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _registeredEventsField = _packetsType.GetField("RegisteredEvents", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _registerPacketMethod = FindStaticMethod(_packetsType, "Register", _netEventType);
                _unregisterPacketMethod = FindStaticMethod(_packetsType, "Unregister", _netEventType);

                bool hasOfficialRegistration = _registerPacketMethod != null && _unregisterPacketMethod != null && _netEventType != null;
                bool hasLegacyRegistration = _registeredEventsField != null;
                _usingLegacyRegistrationPath = !hasOfficialRegistration && hasLegacyRegistration;

                var getPacketMethods = _packetsType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => string.Equals(m.Name, "GetPacket", StringComparison.Ordinal))
                    .ToArray();

                for (int i = 0; i < getPacketMethods.Length; i++)
                {
                    var method = getPacketMethods[i];
                    var parameters = method.GetParameters();
                    if (parameters.Length != 3)
                        continue;

                    if (parameters[0].ParameterType != typeof(int) || parameters[1].ParameterType != typeof(int))
                        continue;

                    if (!parameters[2].ParameterType.IsEnum)
                        continue;

                    _getPacketMethod = method;
                    _globalTargetsType = parameters[2].ParameterType;
                    break;
                }

                _sendPacketMethod = _packetsType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => string.Equals(m.Name, "SendPacket", StringComparison.Ordinal)
                                         && m.GetParameters().Length == 2);

                if (_eventPacketType != null)
                    _eventPacketPacketProperty = _eventPacketType.GetProperty("Packet", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (_eventPacketPacketProperty != null)
                {
                    _udpPacketType = _eventPacketPacketProperty.PropertyType;
                    _udpPacketUserTokenProperty = _udpPacketType.GetProperty("UserToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _udpWriteInt = FindMethod(_udpPacketType, "WriteInt", typeof(int));
                    _udpWriteByte = FindMethod(_udpPacketType, "WriteByte", typeof(byte));
                    _udpWriteUShort = FindMethod(_udpPacketType, "WriteUShort", typeof(ushort));
                    _udpReadInt = FindMethod(_udpPacketType, "ReadInt");
                    _udpReadByte = FindMethod(_udpPacketType, "ReadByte");
                    _udpReadUShort = FindMethod(_udpPacketType, "ReadUShort");
                }

                _boltPacketType = ResolveType("Bolt.Packet", "bolt") ?? ResolveType("Packet", "bolt");
                if (_boltPacketType != null)
                {
                    _boltPacketUdpPacketField = _boltPacketType.GetField("UdpPacket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_netRegistrationType != null)
                {
                    var ctor = _netRegistrationType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(c => c.GetParameters().Length == 2);

                    if (ctor != null)
                    {
                        var ctorParams = ctor.GetParameters();
                        if (ctorParams.Length == 2)
                        {
                            Type actionType = ctorParams[1].ParameterType;
                            if (actionType.IsGenericType)
                            {
                                Type[] genericArgs = actionType.GetGenericArguments();
                                if (genericArgs.Length == 2)
                                {
                                    _udpPacketType = _udpPacketType ?? genericArgs[0];
                                    _boltConnectionType = genericArgs[1];
                                }
                            }
                        }
                    }
                }

                bool hasRequiredMethods =
                    _getPacketMethod != null &&
                    _sendPacketMethod != null &&
                    _netRegistrationType != null &&
                    (hasOfficialRegistration || hasLegacyRegistration) &&
                    _eventPacketPacketProperty != null &&
                    _udpPacketType != null &&
                    _boltConnectionType != null &&
                    _udpWriteInt != null &&
                    _udpWriteByte != null &&
                    _udpWriteUShort != null &&
                    _udpReadInt != null &&
                    _udpReadByte != null &&
                    _udpReadUShort != null;

                if (!hasRequiredMethods)
                {
                    if (!_networkingTypesFailedLogged)
                    {
                        _networkingTypesFailedLogged = true;
                        LogSync("Public bed network sync did not find all required packet APIs.");
                    }

                    return false;
                }

                _resolvedNetworkingTypes = true;
                LogSync("Public bed packet APIs resolved.");
                return true;
            }
            catch (Exception ex)
            {
                if (!_networkingTypesFailedLogged)
                {
                    _networkingTypesFailedLogged = true;
                    LogSync("Failed resolving network packet APIs: " + ex.Message);
                }

                return false;
            }
        }

        private static Type ResolveType(string fullTypeName, string preferredAssemblySimpleName)
        {
            if (string.IsNullOrEmpty(fullTypeName))
                return null;

            if (!string.IsNullOrEmpty(preferredAssemblySimpleName))
            {
                Type t = Type.GetType(fullTypeName + ", " + preferredAssemblySimpleName, throwOnError: false);
                if (t != null)
                    return t;
            }

            Type direct = Type.GetType(fullTypeName, throwOnError: false);
            if (direct != null)
                return direct;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var asm = assemblies[i];
                if (asm == null)
                    continue;

                Type resolved = asm.GetType(fullTypeName, throwOnError: false, ignoreCase: false);
                if (resolved != null)
                    return resolved;
            }

            return null;
        }

        private static MethodInfo FindMethod(Type type, string name, params Type[] parameters)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (parameters == null || parameters.Length == 0)
            {
                return type.GetMethod(name, flags, null, Type.EmptyTypes, null);
            }

            return type.GetMethod(name, flags, null, parameters, null);
        }

        private static MethodInfo FindStaticMethod(Type type, string name, Type firstParameterType)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo[] methods = type.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                if (firstParameterType != null && parameters[0].ParameterType != firstParameterType)
                    continue;

                return method;
            }

            return null;
        }

        private static bool IsMultiplayer()
        {
            if (FastTravelNetworkingRuntime.TryGetRuntimeNetworkState(out bool runtimeMultiplayer, out bool runtimeServer, out bool runtimeClient, out bool runtimeDedicatedServer))
            {
                if (runtimeServer || runtimeClient || runtimeDedicatedServer)
                    return true;

                if (runtimeMultiplayer)
                    return true;
            }

            if (!PrepareNetworkingTypes())
                return false;

            if (_netUtilsType == null)
                return false;

            if (TryReadStaticBool(_netUtilsType, "IsMultiplayer", out bool isMultiplayer))
            {
                if (isMultiplayer)
                    return true;
            }

            if (TryReadStaticBool(_netUtilsType, "isMultiplayer", out isMultiplayer))
            {
                if (isMultiplayer)
                    return true;
            }

            // Dedicated servers can report IsMultiplayer=false during startup windows.
            if (TryReadStaticBool(_netUtilsType, "IsServer", out bool isServer) && isServer)
                return true;

            if (TryReadStaticBool(_netUtilsType, "isServer", out isServer) && isServer)
                return true;

            if (TryReadStaticBool(_netUtilsType, "IsClient", out bool isClient) && isClient)
                return true;

            if (TryReadStaticBool(_netUtilsType, "isClient", out isClient) && isClient)
                return true;

            if (TryReadStaticBool(_netUtilsType, "IsDedicatedServer", out bool isDedicatedServer) && isDedicatedServer)
                return true;

            if (TryReadStaticBool(_netUtilsType, "isDedicatedServer", out isDedicatedServer) && isDedicatedServer)
                return true;

            return false;
        }

        private static bool TryReadStaticBool(Type type, string memberName, out bool value)
        {
            value = false;
            if (type == null || string.IsNullOrEmpty(memberName))
                return false;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.IgnoreCase;

            try
            {
                var property = type.GetProperty(memberName, flags);
                if (property != null)
                {
                    object raw = property.GetValue(null, null);
                    if (TryConvertToBool(raw, out value))
                        return true;
                }

                var method = type.GetMethod("get_" + memberName, flags, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    object raw = method.Invoke(null, null);
                    if (TryConvertToBool(raw, out value))
                        return true;
                }

                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    object raw = field.GetValue(null);
                    if (TryConvertToBool(raw, out value))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryConvertToBool(object raw, out bool value)
        {
            value = false;
            if (raw == null)
                return false;

            if (raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }

            if (raw is int intValue)
            {
                value = intValue != 0;
                return true;
            }

            if (raw is string str)
            {
                if (bool.TryParse(str, out bool parsedBool))
                {
                    value = parsedBool;
                    return true;
                }

                if (int.TryParse(str, out int parsedInt))
                {
                    value = parsedInt != 0;
                    return true;
                }
            }

            return false;
        }

        private static bool TryWriteInt(object udpPacket, int value)
        {
            if (_udpWriteInt == null || udpPacket == null)
                return false;

            _udpWriteInt.Invoke(udpPacket, new object[] { value });
            return true;
        }

        private static bool TryWriteByte(object udpPacket, byte value)
        {
            if (_udpWriteByte == null || udpPacket == null)
                return false;

            _udpWriteByte.Invoke(udpPacket, new object[] { value });
            return true;
        }

        private static bool TryWriteUShort(object udpPacket, ushort value)
        {
            if (_udpWriteUShort == null || udpPacket == null)
                return false;

            _udpWriteUShort.Invoke(udpPacket, new object[] { value });
            return true;
        }

        private static bool TryReadInt(object udpPacket, out int value)
        {
            value = 0;
            if (_udpReadInt == null || udpPacket == null)
                return false;

            object raw = _udpReadInt.Invoke(udpPacket, null);
            if (raw == null)
                return false;

            value = Convert.ToInt32(raw);
            return true;
        }

        private static bool TryReadByte(object udpPacket, out byte value)
        {
            value = 0;
            if (_udpReadByte == null || udpPacket == null)
                return false;

            object raw = _udpReadByte.Invoke(udpPacket, null);
            if (raw == null)
                return false;

            value = Convert.ToByte(raw);
            return true;
        }

        private static bool TryReadUShort(object udpPacket, out ushort value)
        {
            value = 0;
            if (_udpReadUShort == null || udpPacket == null)
                return false;

            object raw = _udpReadUShort.Invoke(udpPacket, null);
            if (raw == null)
                return false;

            value = Convert.ToUInt16(raw);
            return true;
        }

        private static bool TrySendEventPacket(object eventPacket, out string failureReason)
        {
            failureReason = null;

            if (eventPacket == null)
            {
                failureReason = "Event packet instance is null";
                return false;
            }

            if (_eventPacketPacketProperty == null || _sendPacketMethod == null)
            {
                failureReason = "Packet send API is unavailable";
                return false;
            }

            object udpPacket;
            try
            {
                udpPacket = _eventPacketPacketProperty.GetValue(eventPacket, null);
            }
            catch (Exception ex)
            {
                failureReason = "Failed to read event packet payload stream: " + ex.Message;
                return false;
            }

            if (udpPacket == null)
            {
                failureReason = "Packet payload stream is null";
                return false;
            }

            if (!TryAttachPacketUserTokenShim(udpPacket, out failureReason))
                return false;

            try
            {
                _sendPacketMethod.Invoke(null, new object[] { eventPacket, null });
                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException;
                failureReason = inner != null
                    ? inner.GetType().Name + ": " + inner.Message
                    : tie.Message;
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        private static bool TryAttachPacketUserTokenShim(object udpPacket, out string failureReason)
        {
            failureReason = null;

            if (udpPacket == null)
            {
                failureReason = "Packet payload stream is null";
                return false;
            }

            if (!EnsurePacketUserTokenShimTypesResolved(out failureReason))
                return false;

            if (_udpPacketUserTokenProperty == null || !_udpPacketUserTokenProperty.CanWrite)
            {
                failureReason = "UdpPacket.UserToken setter is unavailable";
                return false;
            }

            if (_boltPacketType == null)
            {
                failureReason = "Bolt.Packet token shim type is unavailable";
                return false;
            }

            if (_boltPacketUdpPacketField == null && (_boltPacketUdpPacketProperty == null || !_boltPacketUdpPacketProperty.CanWrite))
            {
                failureReason = "Bolt.Packet UdpPacket member is unavailable";
                return false;
            }

            try
            {
                object existingToken = null;
                if (_udpPacketUserTokenProperty.CanRead)
                    existingToken = _udpPacketUserTokenProperty.GetValue(udpPacket, null);

                object boltPacketToken = existingToken;
                if (boltPacketToken == null || !_boltPacketType.IsInstanceOfType(boltPacketToken))
                {
                    if (existingToken != null && !_packetUserTokenOverrideLogged)
                    {
                        _packetUserTokenOverrideLogged = true;
                        LogSync("Replacing non-Bolt packet user token type '" + existingToken.GetType().FullName + "' to avoid loss callback crashes.");
                    }

                    boltPacketToken = Activator.CreateInstance(_boltPacketType, true);
                    if (boltPacketToken == null)
                    {
                        failureReason = "Failed to allocate Bolt.Packet token shim";
                        return false;
                    }
                }

                if (_boltPacketUdpPacketField != null)
                {
                    _boltPacketUdpPacketField.SetValue(boltPacketToken, udpPacket);
                }
                else
                {
                    _boltPacketUdpPacketProperty.SetValue(boltPacketToken, udpPacket, null);
                }

                _udpPacketUserTokenProperty.SetValue(udpPacket, boltPacketToken, null);

                if (!_packetUserTokenShimAppliedLogged)
                {
                    _packetUserTokenShimAppliedLogged = true;
                    LogSync("Applied Bolt.Packet user-token shim for custom packet sends.");
                }

                return true;
            }
            catch (Exception ex)
            {
                failureReason = "Failed to attach packet user token shim: " + ex.Message;
                return false;
            }
        }

        private static bool EnsurePacketUserTokenShimTypesResolved(out string failureReason)
        {
            failureReason = null;

            if (_udpPacketUserTokenProperty == null && _udpPacketType != null)
            {
                _udpPacketUserTokenProperty = _udpPacketType.GetProperty("UserToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_boltPacketType == null)
            {
                _boltPacketType = ResolveType("Bolt.Packet", "bolt") ?? ResolveType("Packet", "bolt");
            }

            if (_boltPacketType == null)
            {
                _boltPacketType = ResolveBoltPacketTypeByShape();
            }

            if (_boltPacketType == null)
            {
                failureReason = "Bolt.Packet token shim type is unavailable";
                return false;
            }

            if (_boltPacketUdpPacketField == null)
            {
                _boltPacketUdpPacketField = _boltPacketType.GetField("UdpPacket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_boltPacketUdpPacketProperty == null)
            {
                _boltPacketUdpPacketProperty = _boltPacketType.GetProperty("UdpPacket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_boltPacketUdpPacketField == null && (_boltPacketUdpPacketProperty == null || !_boltPacketUdpPacketProperty.CanWrite))
            {
                failureReason = "Bolt.Packet UdpPacket member is unavailable";
                return false;
            }

            if (_udpPacketUserTokenProperty == null || !_udpPacketUserTokenProperty.CanWrite)
            {
                failureReason = "UdpPacket.UserToken setter is unavailable";
                return false;
            }

            return true;
        }

        private static Type ResolveBoltPacketTypeByShape()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var asm = assemblies[i];
                if (asm == null)
                    continue;

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null || types.Length == 0)
                    continue;

                for (int j = 0; j < types.Length; j++)
                {
                    var type = types[j];
                    if (type == null || !string.Equals(type.Name, "Packet", StringComparison.Ordinal))
                        continue;

                    var reliableEventsField = type.GetField("ReliableEvents", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var udpPacketField = type.GetField("UdpPacket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var udpPacketProperty = type.GetProperty("UdpPacket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (reliableEventsField == null)
                        continue;

                    if (udpPacketField == null && (udpPacketProperty == null || !udpPacketProperty.CanWrite))
                        continue;

                    return type;
                }
            }

            return null;
        }

        private static int CountPublicEntries(List<OutboundEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return 0;

            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && entries[i].IsPublic)
                    count++;
            }

            return count;
        }

        private static int CountPublicRecords(IReadOnlyList<FastTravelPublicBedRecord> records)
        {
            if (records == null || records.Count == 0)
                return 0;

            int count = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i] != null && records[i].IsPublic)
                    count++;
            }

            return count;
        }

        private static int CountOwnedRecords(IReadOnlyList<FastTravelPublicBedRecord> records)
        {
            if (records == null || records.Count == 0)
                return 0;

            int count = 0;
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record != null && !string.IsNullOrEmpty(record.OwnerId))
                    count++;
            }

            return count;
        }

        private static string BuildRecordSignature(IReadOnlyList<FastTravelPublicBedRecord> records)
        {
            if (records == null || records.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(records.Count * 24);
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record == null || string.IsNullOrEmpty(record.PersistentKey))
                    continue;

                sb.Append(record.IsPublic ? '1' : '0');
                sb.Append(':');
                sb.Append(record.PersistentKey);
                sb.Append(':');
                sb.Append(record.OwnerId ?? string.Empty);
                sb.Append(':');
                sb.Append(record.CustomName ?? string.Empty);
                sb.Append(';');
            }

            return sb.ToString();
        }

        private static string DescribeOutboundEntries(List<OutboundEntry> entries, int maxCount)
        {
            if (entries == null || entries.Count == 0 || maxCount <= 0)
                return "none";

            int take = Mathf.Min(maxCount, entries.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < take; i++)
            {
                var entry = entries[i];
                if (entry == null)
                    continue;

                if (sb.Length > 0)
                    sb.Append(", ");

                sb.Append("{");
                sb.Append(entry.IsPublic ? "public" : "private");
                sb.Append(" key='");
                sb.Append(entry.KeyBytes != null ? Encoding.UTF8.GetString(entry.KeyBytes) : "<null>");
                sb.Append("' owner='");
                sb.Append(entry.OwnerIdBytes != null ? Encoding.UTF8.GetString(entry.OwnerIdBytes) : "<none>");
                sb.Append("' name='");
                sb.Append(entry.CustomNameBytes != null ? Encoding.UTF8.GetString(entry.CustomNameBytes) : "<none>");
                sb.Append("'}");
            }

            if (entries.Count > take)
            {
                sb.Append(" ... +");
                sb.Append(entries.Count - take);
            }

            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static string DescribeRecords(IReadOnlyList<FastTravelPublicBedRecord> records, int maxCount)
        {
            if (records == null || records.Count == 0 || maxCount <= 0)
                return "none";

            int take = Mathf.Min(maxCount, records.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < take; i++)
            {
                var record = records[i];
                if (record == null)
                    continue;

                if (sb.Length > 0)
                    sb.Append(", ");

                sb.Append("{");
                sb.Append(record.IsPublic ? "public" : "private");
                sb.Append(" key='");
                sb.Append(record.PersistentKey ?? "<null>");
                sb.Append("' owner='");
                sb.Append(record.OwnerId ?? "<none>");
                sb.Append("' name='");
                sb.Append(record.CustomName ?? "<none>");
                sb.Append("'}");
            }

            if (records.Count > take)
            {
                sb.Append(" ... +");
                sb.Append(records.Count - take);
            }

            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static string DescribeConnection(object connection)
        {
            if (connection == null)
                return "<null-connection>";

            try
            {
                return connection.ToString();
            }
            catch
            {
                return connection.GetType().Name;
            }
        }

        private static void LogSync(string message)
        {
            string role = FastTravelLocationRegistry.IsLocalServerAuthority() ? "server" : "client";
            ModMain.LogMessage("FastTravel: NetSync[" + role + "] " + message);
        }
    }
}
