namespace Clusterpack

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control.Tasks.Builders
open FSharp.Control.Tasks.Builders.Unsafe
open FSharp.Core
open FSharp.Core.Atomic.Operators

type internal EndpointWriter(connection: Connection) =
    inherit BoundedActor<EndpointMessage>(128)
    override this.Receive(msg) = unitVtask {
        do! connection.Send(msg, this.CancellationToken)
    }

type internal EndpointReader(connection: Connection, locals: ConcurrentDictionary<ChannelId, Addressable>) =
    let cancellation = new CancellationTokenSource()
    let receiving =
        connection.Incoming
        |> AsyncSeq.takeWhile (fun _ -> not cancellation.Token.IsCancellationRequested)
        |> AsyncSeq.iter (fun envelope -> unitVtask {
            match envelope with
            | Envelope(struct(_, channelId), msg) ->
                match locals.TryGetValue(channelId) with
                | true, addressable ->
                    do! addressable.Pass(msg, CancellationToken.None)
                | false, _ ->
                    printfn "Couldn't send message (%O) to (%O) - channel cannot be found" msg channelId  
        })
    member this.DisposeAsync() : ValueTask =
        cancellation.Cancel()
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
        do! reader.DisposeAsync()
        do! writer.DisposeAsync(false)
        do! connection.DisposeAsync()
    }
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync()

[<Sealed>]
type Node(transport: Transport) as this =
    let selfId = transport.Manifest.NodeId
    let disposed = atom false
    let cancellation = new CancellationTokenSource()
    let channels = ConcurrentDictionary<ChannelId, Addressable>()
    let remotesByEndpoint = ConcurrentDictionary<Endpoint, Task<EndpointManager>>()
    let remotesById = Dictionary<NodeId, EndpointManager>()
    
    let createEndpointManager (connection: Connection) =
        new EndpointManager(connection, channels)
        
    let addConnection (connection: Connection) = Func<Endpoint,Task<EndpointManager>>(fun _ -> task {
        let mgr = createEndpointManager connection
        remotesById.Add(connection.Manifest.NodeId, mgr)
        return mgr
    })
    let updateConnection (connection: Connection) = Func<Endpoint, Task<EndpointManager>,Task<EndpointManager>>(fun _ old -> task {
        do! connection.DisposeAsync() //TODO: replace?
        return! old
    })

    let connectionListener =
        transport.Accepted
        |> AsyncSeq.takeWhile (fun _ -> not cancellation.Token.IsCancellationRequested)
        |> AsyncSeq.iter (fun connection -> unitVtask {
            do! remotesByEndpoint.AddOrUpdate(connection.Manifest.Endpoint, (addConnection connection), (updateConnection connection)) |> Task.ignore
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
            match remotesById.TryGetValue nodeId with
            | true, mgr -> mgr.TryGet channelId
            | _ -> None
        
    member this.Connect (endpoint: Endpoint, cancellationToken: CancellationToken) : ValueTask<NodeId>  = vtask {
        let! mgr = remotesByEndpoint.GetOrAdd(endpoint, Func<_,_>(fun e -> task {
            let! connection = transport.Connect(endpoint, cancellationToken)
            
            if connection.Manifest.NodeId = selfId then
                do! connection.DisposeAsync()
                failwithf "Cannot connect to '%s': remote node ID is identical to local one: %i" endpoint selfId
                
            let mgr = createEndpointManager connection
            remotesById.Add(connection.Manifest.NodeId, mgr)
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
            for entry in remotesById do
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
