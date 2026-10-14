using System;
using System.Threading;
using DaServer.Buffers;
using DaServer.Cluster;
using DaServer.Net;
using DaServer.Protocol;
using DaServer.Rpc;
using Nino.Core;

namespace DaServer.Runtime;

// Cluster events: serialized once, one Event frame per handling implementation (RequestId = implementation id,
// MethodId = event id, Key = event key). Routes come from the gossiped handlers in the ClusterView.
public sealed partial class Node
{
    public void Publish<TEvent>(in TEvent e) where TEvent : struct, IEvent =>
        Publish(in e, View.EventRoutes(EventType<TEvent>.Id), onlyImplementation: 0);

    /// <summary>Deliver <paramref name="e"/> only to the implementation serving actor contract <paramref name="contractId"/>.</summary>
    internal void PublishTo<TEvent>(uint contractId, in TEvent e) where TEvent : struct, IKeyedEvent
    {
        if (View.TryGetActorImplementation(contractId, out uint implementationId))
            Publish(in e, View.EventRoutes(EventType<TEvent>.Id), implementationId);
    }

    private void Publish<TEvent>(in TEvent e, EventRoute[] routes, uint onlyImplementation) where TEvent : struct, IEvent
    {
        if (routes.Length == 0) return;
        long key = EventType<TEvent>.KeyOf(in e);
        var w = FrameWriter.Rent();
        try
        {
            var nino = w.Nino;
            NinoSerializer.Serialize(e, ref nino);
            for (int i = 0; i < routes.Length; i++)
            {
                ref readonly var route = ref routes[i];
                if (onlyImplementation != 0 && route.ImplementationId != onlyImplementation) continue;
                if (route.Actor && !EventType<TEvent>.Keyed) continue; // rejected at build time (analyzer)
                SendEvent(in route, EventType<TEvent>.Id, key, w, hops: 0);
            }
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>Send the event in <paramref name="w"/> (payload written) to the node handling <paramref name="route"/>.</summary>
    private void SendEvent(in EventRoute route, uint eventId, long key, FrameWriter w, byte hops)
    {
        var target = View.EventTarget(in route, key, Id, (uint)Interlocked.Increment(ref _roundRobin));
        if (target == null) return;
        w.SetHeader(FrameKind.Event, route.ImplementationId, eventId, key, hops: hops);
        if (target.NodeId == Id)
            SelectShard(route.ImplementationId, route.Actor, key).Post(new WorkItem(WorkKind.Dispatch, FrameWriter.CopyOf(w.Frame), null));
        else
            GetLink(target).Send(w.Frame);
    }

    /// <summary>Event frame from another node: to the shard of its implementation, or on to the right node.</summary>
    private void OnInboundEvent(in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var impl = ImplementationById(header.RequestId);
        if (impl == null)
        {
            ForwardEvent(FrameWriter.CopyOf(frame));
            return;
        }

        SelectShard(impl.Id, impl.Kind == RpcContractKind.Actor, header.Key)
            .Post(new WorkItem(WorkKind.Dispatch, FrameWriter.CopyOf(frame), null));
    }

    /// <summary>
    /// Re-route an event this node cannot handle (stale view at the sender, ownership moved). Dropped after
    /// <see cref="MaxHops"/> or when nobody handles it any more (at most once). Takes ownership of <paramref name="frame"/>.
    /// </summary>
    internal void ForwardEvent(FrameWriter frame)
    {
        try
        {
            var header = FrameHeader.Read(frame.Frame);
            if (header.Hops >= MaxHops) return;
            var routes = View.EventRoutes(header.MethodId);
            for (int i = 0; i < routes.Length; i++)
            {
                if (routes[i].ImplementationId != header.RequestId) continue;
                SendEvent(in routes[i], header.MethodId, header.Key, frame, (byte)(header.Hops + 1));
                return;
            }
        }
        finally
        {
            frame.Dispose();
        }
    }
}
