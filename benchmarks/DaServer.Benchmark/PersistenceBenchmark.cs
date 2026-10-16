using DaServer.Persistence;
using Nino.Core;

namespace DaServer.Benchmark;

/// <summary>
/// Actor records (ADR-068) with 1,024 concurrent callers on one node: Load wakes 1,024 stored actors (each call waits
/// for its actor's record, loaded in batches per shard wake); SaveNow changes 1,024 loaded actors critically and
/// completes when the store accepted all 1,024 writes. Stores: in memory (the runtime's own cost) and PostgreSQL
/// (when DASERVER_TEST_POSTGRES holds a connection string).
/// </summary>
[MemoryDiagnoser]
public class PersistenceBenchmark
{
    private const int Actors = 1024;

    private Node _node = null!;
    private CountingStore _store = null!;
    private long _next = 1;
    private long _first;
    private readonly Reply<long>[] _calls = new Reply<long>[Actors];

    public static IEnumerable<string> Stores() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DASERVER_TEST_POSTGRES")) ? ["Memory"] : ["Memory", "PostgreSql"];

    [ParamsSource(nameof(Stores))] public string Store = "Memory";

    [GlobalSetup]
    public void Setup()
    {
        IActorStore inner = Store == "Memory" ? new InMemoryActorStore()
            : new PostgreSqlActorStore(Environment.GetEnvironmentVariable("DASERVER_TEST_POSTGRES")!, "bench_" + Guid.NewGuid().ToString("N"));
        _store = new CountingStore(inner);
        _node = Clusters.Start(1, shards: 4, configure: o => o.ActorStore = _store).GetAwaiter().GetResult()[0];
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop([_node]).GetAwaiter().GetResult();

    /// <summary>1,024 actors nobody has loaded yet, with a record each.</summary>
    [IterationSetup(Target = nameof(Load))]
    public void Seed()
    {
        _first = _next;
        _next += Actors;
        var writes = new StoredWrite[Actors];
        for (int i = 0; i < Actors; i++)
        {
            var coins = NinoSerializer.Serialize(new Coins(i));
            var record = new byte[8 + coins.Length];
            BitConverter.TryWriteBytes(record, TypeId<Coins>.Value);
            BitConverter.TryWriteBytes(record.AsSpan(4), coins.Length);
            coins.CopyTo(record, 8);
            writes[i] = new StoredWrite(new StoredKey(ActorIds<VaultId>.HandlerTypeId, _first + i), 0, record);
        }

        _store.Inner.SaveAsync(writes, new bool[Actors]).AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task Load()
    {
        for (int i = 0; i < Actors; i++) _calls[i] = _node.Ask(new VaultId(_first + i), new Put(0));
        for (int i = 0; i < Actors; i++)
            if (await _calls[i] != i) throw new InvalidOperationException("the record did not come back");
    }

    /// <summary>The actors of the last load, loaded.</summary>
    [IterationSetup(Target = nameof(SaveNow))]
    public void Loaded()
    {
        Seed();
        Load().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task SaveNow()
    {
        var written = _store.Expect(Actors);
        for (int i = 0; i < Actors; i++) _calls[i] = _node.Ask(new VaultId(_first + i), new Put(1));
        for (int i = 0; i < Actors; i++) await _calls[i];
        await written;
    }

    /// <summary>Counts accepted writes and releases a waiter when the expected number arrived.</summary>
    private sealed class CountingStore(IActorStore inner) : IActorStore
    {
        public readonly IActorStore Inner = inner;
        private readonly Countdown _written = new();

        public Task Expect(int writes) => _written.Reset(writes);

        public ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default) =>
            Inner.LoadAsync(keys, records, ct);

        public async ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken ct = default)
        {
            await Inner.SaveAsync(writes, accepted, ct);
            for (int i = 0; i < accepted.Length; i++)
                if (accepted.Span[i]) _written.Signal();
        }
    }
}
