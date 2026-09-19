#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using GreenMagic;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic.Questing
{
    /// <summary>
    /// A bounded observation of the original client's 25 raw quest slots.
    /// Identity completeness is independent of optional quest metadata. Matching
    /// samples detect observed changes; they are NOT a frame lock, session lease,
    /// or proof against an intervening change-and-reversion (ABA).
    /// </summary>
    public sealed class QuestLogSnapshot
    {
        private readonly QuestLog _log;
        private readonly QuestLog.RawQuestObservation? _raw;

        internal QuestLogSnapshot(QuestLog log, QuestLog.RawQuestObservation? raw,
            IEnumerable<uint> accepted, IEnumerable<uint> ready, IEnumerable<uint> failed,
            IEnumerable<PlayerQuest> quests, bool identityComplete, bool metadataComplete)
        {
            _log = log;
            _raw = raw;
            AcceptedQuestIds = Array.AsReadOnly(accepted.ToArray());
            ReadyQuestIds = Array.AsReadOnly(ready.ToArray());
            FailedQuestIds = Array.AsReadOnly(failed.ToArray());
            Quests = Array.AsReadOnly(quests.ToArray());
            IsIdentityComplete = identityComplete;
            IsComplete = identityComplete && metadataComplete;
        }

        public ReadOnlyCollection<uint> AcceptedQuestIds { get; }
        /// <summary>Accepted IDs whose observed raw Completed flag was set.</summary>
        public ReadOnlyCollection<uint> ReadyQuestIds { get; }
        /// <summary>Accepted IDs whose observed raw Failed flag was set.</summary>
        public ReadOnlyCollection<uint> FailedQuestIds { get; }
        /// <summary>
        /// Successfully materialized metadata handles, not frozen live completion
        /// results. This collection alone never establishes log completeness.
        /// </summary>
        public ReadOnlyCollection<PlayerQuest> Quests { get; }
        public bool IsIdentityComplete { get; }
        public bool IsComplete { get; }

        /// <summary>
        /// Compares the observed log/player/memory/address/raw-GUID owners only.
        /// It does not certify continuous session ownership between observations.
        /// </summary>
        public bool HasSameOwner(QuestLogSnapshot? other) =>
            other != null && IsIdentityComplete && other.IsIdentityComplete &&
            ReferenceEquals(_log, other._log) && _raw != null && other._raw != null &&
            _raw.HasSameOwner(other._raw);

        internal bool BelongsTo(QuestLog log) => ReferenceEquals(_log, log);
        internal QuestLog.RawQuestObservation? Raw => _raw;
    }

    public partial class QuestLog
    {
        private const int RawQuestSlotCount = 25;
        private const int RawQuestSlotSize = 20;
        private const uint RawQuestSlotsOffset = 158U * 4U;
        private const int RawQuestSlotsLength = RawQuestSlotCount * RawQuestSlotSize;

        /// <summary>
        /// Reads raw acceptance independently of metadata hydration. Unreadable or
        /// partially read memory is unknown, never evidence of an empty log.
        /// Legacy list/completion APIs retain their existing contracts.
        /// </summary>
        public QuestLogSnapshot CaptureSnapshot()
        {
            var accepted = new List<uint>();
            var ready = new List<uint>();
            var failed = new List<uint>();
            var quests = new List<PlayerQuest>();
            RawQuestObservation? first = null;
            bool identityComplete = false;
            bool metadataComplete = true;
            try
            {
                bool firstRead = TryReadRawQuestObservation(out first);
                if (first == null)
                    return new QuestLogSnapshot(this, null, accepted, ready, failed, quests, false, false);

                var distinct = new HashSet<uint>();
                bool validIds = true;
                for (int slot = 0; slot < RawQuestSlotCount; slot++)
                {
                    int offset = slot * RawQuestSlotSize;
                    uint id = first.UInt32At(offset);
                    if (id == 0)
                        continue;
                    accepted.Add(id);
                    uint flags = first.UInt32At(offset + 4);
                    if ((flags & (uint)WoWDescriptorQuestFlags.Completed) != 0)
                        ready.Add(id);
                    if ((flags & (uint)WoWDescriptorQuestFlags.Failed) != 0)
                        failed.Add(id);
                    if (id > int.MaxValue || !distinct.Add(id))
                        validIds = false;
                }

                // Preserve observed occupied IDs even when the owner changed or
                // optional metadata is unavailable. Such an observation is not authority.
                if (!firstRead || !validIds)
                    return new QuestLogSnapshot(this, first, accepted, ready, failed, quests, false, false);

                foreach (uint id in accepted)
                {
                    try
                    {
                        PlayerQuest? quest = PlayerQuest.FromId(id);
                        if (quest == null || quest.Id != id)
                            metadataComplete = false;
                        else
                            quests.Add(quest);
                    }
                    catch (Exception error) when (IsOrdinaryObservationFailure(error))
                    {
                        metadataComplete = false;
                    }
                }

                identityComplete = TryReadRawQuestObservation(out RawQuestObservation? after) &&
                    after != null && first.Matches(after);
            }
            catch (Exception error) when (IsOrdinaryObservationFailure(error))
            {
                identityComplete = false;
                metadataComplete = false;
            }
            return new QuestLogSnapshot(this, first, accepted, ready, failed, quests,
                identityComplete, metadataComplete);
        }

        /// <summary>
        /// Re-observes raw identity/progress without hydrating metadata again.
        /// A true result is a matching observation, not permission for a later side effect.
        /// </summary>
        public bool IsSnapshotCurrent(QuestLogSnapshot? snapshot)
        {
            if (snapshot == null || !snapshot.IsIdentityComplete || !snapshot.BelongsTo(this) ||
                snapshot.Raw == null || !snapshot.Raw.HasCurrentReferences())
                return false;
            try
            {
                return TryReadRawQuestObservation(out RawQuestObservation? current) &&
                    current != null && snapshot.Raw.Matches(current);
            }
            catch (Exception error) when (IsOrdinaryObservationFailure(error))
            {
                return false;
            }
        }

        private static bool IsOrdinaryObservationFailure(Exception error) =>
            error is not ThreadInterruptedException && error is not OperationCanceledException;

        private static bool TryReadRawQuestObservation(out RawQuestObservation? observation)
        {
            observation = null;
            LocalPlayer? player = ObjectManager.Me;
            Memory? memory = ObjectManager.Wow;
            if (player == null || memory == null || !ObjectManager.IsInGame || !player.IsValid)
                return false;
            uint baseAddress = player.BaseAddress;
            if (baseAddress == 0)
                return false;

            // Only these raw reads bypass the cache. Restore the caller's state
            // before metadata/world consumers run; do not clear unrelated cache entries.
            using (memory.TemporaryCacheState(false))
            {
                uint descriptorAddress = ReadRawUInt32(memory, checked(baseAddress + 8U));
                if (descriptorAddress == 0)
                    return false;
                ulong objectGuid = ReadRawUInt64(memory, checked(baseAddress + 48U));
                ulong descriptorGuid = ReadRawUInt64(memory, descriptorAddress);
                uint slotsAddress = checked(descriptorAddress + RawQuestSlotsOffset);
                _ = checked(slotsAddress + (uint)RawQuestSlotsLength - 1U);
                byte[] slots = ReadRawBytes(memory, slotsAddress, RawQuestSlotsLength);
                observation = new RawQuestObservation(player, memory, baseAddress,
                    descriptorAddress, objectGuid, descriptorGuid, slots);
                return objectGuid != 0 && objectGuid == descriptorGuid &&
                    observation.HasCurrentReferences() &&
                    ReadRawUInt32(memory, checked(baseAddress + 8U)) == descriptorAddress &&
                    ReadRawUInt64(memory, checked(baseAddress + 48U)) == objectGuid &&
                    ReadRawUInt64(memory, descriptorAddress) == descriptorGuid;
            }
        }

        private static byte[] ReadRawBytes(Memory memory, uint address, int count)
        {
            byte[]? bytes = memory.ReadBytes(address, count);
            if (bytes == null || bytes.Length != count)
                throw new InvalidOperationException("Quest-log raw observation unavailable.");
            return bytes;
        }
        private static uint ReadRawUInt32(Memory memory, uint address) =>
            BitConverter.ToUInt32(ReadRawBytes(memory, address, sizeof(uint)), 0);
        private static ulong ReadRawUInt64(Memory memory, uint address) =>
            BitConverter.ToUInt64(ReadRawBytes(memory, address, sizeof(ulong)), 0);

        internal sealed class RawQuestObservation
        {
            private readonly LocalPlayer _player;
            private readonly Memory _memory;
            private readonly uint _baseAddress;
            private readonly uint _descriptorAddress;
            private readonly ulong _objectGuid;
            private readonly ulong _descriptorGuid;
            private readonly byte[] _slots;

            internal RawQuestObservation(LocalPlayer player, Memory memory, uint baseAddress,
                uint descriptorAddress, ulong objectGuid, ulong descriptorGuid, byte[] slots)
            {
                _player = player; _memory = memory; _baseAddress = baseAddress;
                _descriptorAddress = descriptorAddress; _objectGuid = objectGuid;
                _descriptorGuid = descriptorGuid; _slots = (byte[])slots.Clone();
            }
            internal uint UInt32At(int offset) => BitConverter.ToUInt32(_slots, offset);
            internal bool HasCurrentReferences() =>
                ReferenceEquals(_player, ObjectManager.Me) && ReferenceEquals(_memory, ObjectManager.Wow) &&
                _player.BaseAddress == _baseAddress;
            internal bool HasSameOwner(RawQuestObservation other) =>
                ReferenceEquals(_player, other._player) && ReferenceEquals(_memory, other._memory) &&
                _baseAddress == other._baseAddress && _descriptorAddress == other._descriptorAddress &&
                _objectGuid == other._objectGuid && _descriptorGuid == other._descriptorGuid;
            internal bool Matches(RawQuestObservation other) =>
                HasSameOwner(other) && _slots.SequenceEqual(other._slots);
        }
    }
}
