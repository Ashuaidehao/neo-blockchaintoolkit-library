using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Neo.Persistence;
using OneOf;
using None = OneOf.Types.None;

namespace Neo.BlockchainToolkit.Persistence
{
    using TrackingMap = ImmutableDictionary<ReadOnlyMemory<byte>, OneOf<ReadOnlyMemory<byte>, None>>;

    public partial class MemoryTrackingStore : IStore
    {
        readonly IReadOnlyStore store;
        TrackingMap trackingMap = TrackingMap.Empty.WithComparers(ReadOnlyMemoryComparer.Default);

        public MemoryTrackingStore(IReadOnlyStore store)
        {
            this.store = store;
        }

        public void Dispose()
        {
            (store as IDisposable)?.Dispose();
        }

        public ISnapshot GetSnapshot() => new Snapshot(store, trackingMap, this.CommitSnapshot);

        public bool Contains(byte[]? key) => TryGet(key) != null;

        public byte[]? TryGet(byte[]? key) => trackingMap.TryGet(store, key);

        public IEnumerable<(byte[] Key, byte[] Value)> Seek(byte[]? key, SeekDirection direction)
            => trackingMap.Seek(store, key, direction);

        public void Put(byte[]? key, byte[]? value)
        {
            if (value is null) throw new NullReferenceException(nameof(value));
            AtomicUpdate(ref trackingMap, key, (ReadOnlyMemory<byte>)value);
        }

        public void Delete(byte[]? key)
        {
            AtomicUpdate(ref trackingMap, key, default(None));
        }

        static void AtomicUpdate(ref TrackingMap trackingMap, byte[]? key, OneOf<ReadOnlyMemory<byte>, None> value)
        {
            key = key is null ? Array.Empty<byte>() : key.AsSpan().ToArray();
            value = value.TryPickT0(out var buffer, out var none)
                ? (ReadOnlyMemory<byte>)buffer.ToArray()
                : none;

            var priorCollection = Volatile.Read(ref trackingMap);
            do
            {
                var updatedCollection = priorCollection.SetItem(key, value);
                var interlockedResult = Interlocked.CompareExchange(ref trackingMap, updatedCollection, priorCollection);
                if (object.ReferenceEquals(priorCollection, interlockedResult)) break;
                priorCollection = interlockedResult;
            }
            while (true);
        }

        void CommitSnapshot(TrackingMap writeBatchMap)
        {
            var priorCollection = Volatile.Read(ref trackingMap);
            do
            {
                var updatedCollection = Volatile.Read(ref trackingMap);
                foreach (var change in writeBatchMap)
                {
                    updatedCollection = updatedCollection.SetItem(change.Key, change.Value);
                }

                var interlockedResult = Interlocked.CompareExchange(ref trackingMap, updatedCollection, priorCollection);
                if (object.ReferenceEquals(priorCollection, interlockedResult)) break;
                priorCollection = interlockedResult;
            }
            while (true);
        }
    }
}