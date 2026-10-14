using System;
using System.Collections.Generic;
using System.Threading;

namespace DaServer.Rpc;

public enum RpcContractKind : byte
{
    Service = 1,
    Actor = 2,
    ClientCallback = 3,
}

/// <summary>Whether and how game clients may call a contract (from its marker interface).</summary>
public enum ClientAccess : byte
{
    /// <summary>Servers only (<see cref="IService"/>, <see cref="IActor"/>).</summary>
    None = 0,

    /// <summary><see cref="IPublicService"/>.</summary>
    Service = 1,

    /// <summary><see cref="ISessionActor"/>: the key comes from the session binding.</summary>
    SessionKey = 2,

    /// <summary><see cref="IPublicActor"/>: the client chooses the key.</summary>
    AnyKey = 3,
}

/// <summary>Metadata of one contract (interface), registered by generated code.</summary>
public sealed class RpcContractInfo
{
    public RpcContractInfo(Type contract, uint id, RpcContractKind kind, ClientAccess clientAccess,
        RpcDispatcher dispatcher, RpcMethodInfo[] methods)
    {
        Contract = contract;
        Id = id;
        Kind = kind;
        ClientAccess = clientAccess;
        Dispatcher = dispatcher;
        Methods = methods;
        foreach (var m in methods) m.Owner = this;
    }

    public Type Contract { get; }

    /// <summary>FNV-1a of the interface full name; advertised in membership for routing.</summary>
    public uint Id { get; }

    public RpcContractKind Kind { get; }
    public ClientAccess ClientAccess { get; }
    public RpcDispatcher Dispatcher { get; }
    public RpcMethodInfo[] Methods { get; }

    public override string ToString() => Contract.FullName ?? Contract.Name;
}

public sealed class RpcMethodInfo
{
    public RpcMethodInfo(uint id, string name, bool oneWay)
    {
        Id = id;
        Name = name;
        OneWay = oneWay;
    }

    public uint Id { get; }
    public string Name { get; }

    /// <summary>void method: no response is sent.</summary>
    public bool OneWay { get; }

    public RpcContractInfo Owner { get; internal set; } = null!;

    public override string ToString() => Owner + "." + Name;
}

/// <summary>
/// Process-wide contract registry filled by generated module initializers (no reflection, no config strings).
/// Lookups are lock-free over an immutable snapshot (frozen on .NET 8+).
/// </summary>
public static class RpcRegistry
{
    private static readonly object Gate = new object();
    private static Snapshot _snapshot = new Snapshot(new Dictionary<uint, RpcMethodInfo>(), new Dictionary<uint, RpcContractInfo>(),
        new Dictionary<Type, RpcContractInfo>());

    private sealed class Snapshot
    {
        public readonly System.Collections.Frozen.FrozenDictionary<uint, RpcMethodInfo> Methods;
        public readonly System.Collections.Frozen.FrozenDictionary<uint, RpcContractInfo> Contracts;
        public readonly System.Collections.Frozen.FrozenDictionary<Type, RpcContractInfo> ByType;

        public Snapshot(Dictionary<uint, RpcMethodInfo> m, Dictionary<uint, RpcContractInfo> c, Dictionary<Type, RpcContractInfo> t)
        {
            Methods = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(m);
            Contracts = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(c);
            ByType = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(t);
        }
    }

    /// <summary>Register a contract. Idempotent per contract type; throws on wire-id collisions.</summary>
    public static void Register(RpcContractInfo contract)
    {
        lock (Gate)
        {
            var current = _snapshot;
            if (current.ByType.ContainsKey(contract.Contract))
                return;

            var methods = new Dictionary<uint, RpcMethodInfo>(current.Methods);
            var contracts = new Dictionary<uint, RpcContractInfo>(current.Contracts);
            var byType = new Dictionary<Type, RpcContractInfo>(current.ByType);

            if (contracts.TryGetValue(contract.Id, out var existingContract))
                throw new InvalidOperationException($"RPC contract id collision: {contract} and {existingContract}");

            foreach (var m in contract.Methods)
            {
                if (methods.TryGetValue(m.Id, out var existing))
                    throw new InvalidOperationException(
                        $"RPC method id collision 0x{m.Id:x8}: {m} and {existing}. Use [WireId] on one of them.");
                methods.Add(m.Id, m);
            }

            contracts.Add(contract.Id, contract);
            byType.Add(contract.Contract, contract);
            Volatile.Write(ref _snapshot, new Snapshot(methods, contracts, byType));
        }
    }

    public static bool TryGetMethod(uint methodId, out RpcMethodInfo method)
    {
        return Volatile.Read(ref _snapshot).Methods.TryGetValue(methodId, out method!);
    }

    public static bool TryGetContract(uint contractId, out RpcContractInfo contract)
    {
        return Volatile.Read(ref _snapshot).Contracts.TryGetValue(contractId, out contract!);
    }

    public static RpcContractInfo GetContract(Type contractType)
    {
        if (!Volatile.Read(ref _snapshot).ByType.TryGetValue(contractType, out var contract))
            throw new InvalidOperationException(
                $"{contractType} is not a registered RPC contract (is the DaServer generator referenced by its assembly?)");
        return contract;
    }

    public static IEnumerable<RpcContractInfo> Contracts => Volatile.Read(ref _snapshot).Contracts.Values;
}
