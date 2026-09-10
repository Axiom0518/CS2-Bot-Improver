using System;
using System.Collections.Concurrent;

namespace BotState;

// Cross-plugin intel snapshots (AppDomain). P4 NadeSystem can read without
// coupling to BotState internals. Kind values match BeliefKind ordinals.
public static class BotIntelHub
{
    public const string DomainKey = "CS2BotImprover.BotIntel.v1";

    public const int KindSeen = 0;
    public const int KindHeard = 1;
    public const int KindDamage = 2;
    public const int KindGun = 3;

    public struct Snapshot
    {
        public bool Active;
        public float X;
        public float Y;
        public float Z;
        public float Radius;
        public float Confidence;
        public float ExpiresAt;
        public float UpdatedAt;
        public int Kind;
    }

    private static readonly ConcurrentDictionary<int, Snapshot> Store = CreateStore();

    private static ConcurrentDictionary<int, Snapshot> CreateStore()
    {
        var existing = AppDomain.CurrentDomain.GetData(DomainKey) as ConcurrentDictionary<int, Snapshot>;
        if (existing != null) return existing;
        var created = new ConcurrentDictionary<int, Snapshot>();
        AppDomain.CurrentDomain.SetData(DomainKey, created);
        return created;
    }

    public static void Publish(int botIndex, in Snapshot snap)
    {
        Store[botIndex] = snap;
    }

    public static void Clear(int botIndex)
    {
        Store.TryRemove(botIndex, out _);
    }

    public static void ClearAll()
    {
        Store.Clear();
    }

    public static bool TryGet(int botIndex, out Snapshot snap)
        => Store.TryGetValue(botIndex, out snap) && snap.Active;

    // For future NadeSystem / other plugins.
    public static ConcurrentDictionary<int, Snapshot> GetStore() => Store;
}
