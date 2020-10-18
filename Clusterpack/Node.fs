namespace Clusterpack

open System
open System
open System.Collections.Concurrent
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control.Tasks.Builders
open FSharp.Control.Tasks.Builders.Unsafe
open FSharp.Core
open FSharp.Core.Atomic.Operators
open Grpc.Core

type internal EndpointWriter(connection: Connection) =
    inherit BoundedActor<EndpointMessage>(128)
    override this.Receive(msg) = unitVtask {
        do! connection.Send(msg, this.CancellationToken)
        printfn "sent %O" msg
    }

type internal EndpointReader(connection: Connection, locals: ConcurrentDictionary<ChannelId, Addressable>) =
    let cancel = new CancellationTokenSource()
    let receive (cancel: CancellationToken) = unitVtask {
        let! envelope = connection.ReceiveNext(cancel)
        printfn "received %O" envelope
        match envelope  with
        | Envelope(struct(_, channelId), msg) ->
            match locals.TryGetValue(channelId) with
            | true, addressable ->
                do! addressable.Pass(msg, cancel)
            | false, _ ->
                printfn "Couldn't send message (%O) to (%O) - channel cannot be found" msg channelId
    }
    let worker = Task.runCancellable cancel.Token (fun () -> task {
        try
            while not cancel.Token.IsCancellationRequested do
                do! receive cancel.Token
        with e ->
            printfn "Failed to receive message from '%s': %O" connection.Manifest.Endpoint e
    })
    member this.DisposeAsync() : ValueTask =
        cancel.Cancel()
        ValueTask()
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync()
        
type internal EndpointManager(connection: Connection, locals: ConcurrentDictionary<ChannelId, Addressable>) =
    let nodeId = connection.Manifest.NodeId
    let remotes = ConcurrentDictionary<ChannelId, Addressable>()
    let reader = EndpointReader(connection, locals)
    let writer = new EndpointWriter(connection)
    member this.NodeId : NodeId = nodeId
    member this.TryGet<'msg>(channelId: ChannelId) : Proxy<'msg> option =
        Some (downcast remotes.GetOrAdd(channelId, RemoteProxy<'msg>(struct(nodeId, channelId), writer)))
    member this.DisposeAsync() : ValueTask = unitVtask {
        do! writer.DisposeAsync(true)
        do! reader.DisposeAsync()
        do! connection.DisposeAsync()
    }
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync()

[<Sealed>]
type Node(transport: Transport) as this =
    let selfId = transport.Manifest.NodeId
    let disposed = atom false
    let cancellation = new CancellationTokenSource()
    let channels = ConcurrentDictionary<ChannelId, Addressable>()
    let remoteEndpointsByAddress = ConcurrentDictionary<Endpoint, Task<EndpointManager>>()
    let remoteEndpointsById = Dictionary<NodeId, EndpointManager>()
    
    let createEndpointManager (connection: Connection) =
        new EndpointManager(connection, channels)
        
    let addConnection (connection: Connection) = Func<Endpoint,Task<EndpointManager>>(fun _ -> task {
        let mgr = createEndpointManager connection
        remoteEndpointsById.Add(connection.Manifest.NodeId, mgr)
        return mgr
    })
    let updateConnection (connection: Connection) = Func<Endpoint, Task<EndpointManager>,Task<EndpointManager>>(fun _ old -> task {
        do! connection.DisposeAsync() //TODO: replace?
        return! old
    })
        
    let acceptIncomingConnection (connection: Connection) = unitVtask {
        let t : Task = upcast remoteEndpointsByAddress.AddOrUpdate(connection.Manifest.Endpoint, (addConnection connection), (updateConnection connection))
        do! t
    }
    
    let connectionListener = Task.runCancellable cancellation.Token (fun () -> task {
        let mutable cont = true
        try
            while not cancellation.Token.IsCancellationRequested && cont do
                    let! accepted = transport.Accepted.MoveNextAsync()
                    if accepted then
                        do! acceptIncomingConnection transport.Accepted.Current
                    else cont <- false
        with e ->
            printfn "Failed while trying to accept incoming connection: %O" e
    })
    
    let failIfDisposed () = if !disposed then raise (ObjectDisposedException("Node " + string selfId + " has been disposed"))
           
    member this.Manifest = transport.Manifest
            
    /// Takes ownership over given channel, returning a reference that can be used to send
    /// messages to that channel from different threads or machines.
    member this.Wrap<'ch, 'msg when 'ch :> ChannelWriter<'msg>>(fac: Address -> 'ch) : Proxy<'msg> =
        failIfDisposed ()
        let channelId = Random.uint32()
        let addr : Address = struct(selfId, channelId)
        let writer = fac addr
        let ch = LocalProxy<'msg>(addr, writer)
        if channels.TryAdd(channelId, ch) then upcast ch
        else failwithf "duplicate address '%O' found on node %O" addr selfId
        
    /// Try to return a channel reference based on a given address.
    member this.Proxy<'msg>(address: Address) : Proxy<'msg> option =
        failIfDisposed ()
        let struct(nodeId, channelId) = address
        if nodeId = selfId then
            match channels.TryGetValue(channelId) with
            | true, (:? Proxy<'msg> as ch) -> Some ch
            | _ -> None
        else
            match remoteEndpointsById.TryGetValue nodeId with
            | true, mgr -> mgr.TryGet channelId
            | _ -> None
        
    member this.Connect (endpoint: Endpoint, cancellationToken: CancellationToken) : ValueTask<NodeId>  = vtask {
        let! mgr = remoteEndpointsByAddress.GetOrAdd(endpoint, Func<_,_>(fun e -> task {
            let! connection = transport.Connect(endpoint, cancellationToken)
            let mgr = createEndpointManager connection
            remoteEndpointsById.Add(connection.Manifest.NodeId, mgr)
            return mgr            
        }))
        return mgr.NodeId
    }
        
    member this.DisposeAsync() = uunitVtask {
        if not (disposed := true) then
            cancellation.Cancel()
            let errors = ResizeArray()
            for entry in channels do
                try ignore (entry.Value.TryComplete null) with e -> errors.Add e
            for entry in remoteEndpointsById do
                try do! entry.Value.DisposeAsync() with e -> errors.Add e
            try
                do! transport.DisposeAsync()
            with e ->
                errors.Add e
                
            if errors.Count > 0 then
                raise (AggregateException errors)
    }
    interface IDisposable with member this.Dispose() = this.DisposeAsync().GetAwaiter().GetResult()
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync()
