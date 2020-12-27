namespace Clusterpack

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control.Tasks
open FSharp.Control.Tasks.Affine.Unsafe
open FSharp.Core
open FSharp.Core
open FSharp.Core.Operators
open FSharp.Core.Atomic.Operators
        
[<Sealed>]
type internal EndpointHandle(connection: Connection, locals: ConcurrentDictionary<ChannelId, Addressable>, outboundSender: ChannelWriter<EndpointMessage>, outboundReceiver: ChannelReader<EndpointMessage>, cancellationToken: CancellationToken) =
    let nodeId = connection.Manifest.NodeId
    let remotes = ConcurrentDictionary<ChannelId, Addressable>()
    let completed = Promise<unit>()
    let cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
    let writer = Task.runCancellable cts.Token (fun () -> task {
        try
            let token = cts.Token
            while not token.IsCancellationRequested do
                let! msg = outboundReceiver.ReadAsync()
                do! connection.Send(msg, token)
            completed.TrySetResult () |> ignore
        with e ->
            completed.TrySetException e |> ignore
            cts.Cancel()
    })
    let reader = Task.runCancellable cts.Token (fun () -> task {
        let incoming = connection.Incoming.GetAsyncEnumerator(cts.Token)
        try
            let token = cts.Token
            let! canMove = incoming.MoveNextAsync()
            let mutable cont = canMove
            while not token.IsCancellationRequested && cont do
                match incoming.Current with 
                | Envelope(struct(_, channelId), msg) ->
                    match locals.TryGetValue(channelId) with
                    | true, addressable ->
                        do! addressable.Pass(msg, CancellationToken.None)
                    | false, _ ->
                        printfn "Couldn't send message (%O) to (%O) - channel cannot be found" msg channelId
                let! hasNext = incoming.MoveNextAsync()
                cont <- hasNext
            incoming.DisposeAsync().GetAwaiter().GetResult()
            completed.TrySetResult () |> ignore
        with e ->
            completed.TrySetException e |> ignore
            incoming.DisposeAsync().GetAwaiter().GetResult()
            cts.Cancel()
    })
    member this.Connection = connection
    member this.OutboundSender = outboundSender
    member this.OutboundReceiver = outboundReceiver
    member this.NodeId : NodeId = nodeId
    member this.TryGet<'msg>(channelId: ChannelId) : Proxy<'msg> option =
        Some (downcast remotes.GetOrAdd(channelId, RemoteProxy<'msg>(struct(nodeId, channelId), outboundSender)))
    member this.Completion = completed.Task
    member this.DisposeAsync() : ValueTask = unitVtask {
        do! connection.DisposeAsync()
        do! completed.Task
    }
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync()
        
type EndpointManagerMessage =
    | Connect of endpoint: Endpoint * cancellation:CancellationToken * promise:Promise<NodeId>
    | Restart of nodeId:NodeId * endpoint: Endpoint
    | Accepted of connection:Connection
    
[<Sealed>]
type internal EndpointManager(transport: Transport, locals: ConcurrentDictionary<ChannelId, Addressable>) =
    inherit UnboundedActor<EndpointManagerMessage>()
    let selfId = transport.Manifest.NodeId
    let remotesByEndpoint = Dictionary<Endpoint, EndpointHandle>()
    let mutable remotesById = Map.empty
    member this.RemotesById with  [<MethodImpl(MethodImplOptions.AggressiveInlining)>] get () = Volatile.Read (&remotesById)
    member private this.Restartable (handle: EndpointHandle) =
        let manifest = handle.Connection.Manifest
        let nodeId = manifest.NodeId
        let endpoint = manifest.Endpoint
        handle.Completion |> Task.rescue (fun e ->
            eprintfn "Connection (%i, '%s') failed due to: %O" nodeId endpoint e
            this.SendAsync(Restart(nodeId, endpoint)) |> ignore
        ) |> ignore
    
    member private this.AddHandle(handle: EndpointHandle) =
        let manifest = handle.Connection.Manifest
        Interlocked.Exchange(&remotesById, Map.add manifest.NodeId handle remotesById) |> ignore
        remotesByEndpoint.[manifest.Endpoint] <- handle
        this.Restartable handle
    
    override this.Receive(msg) = uunitVtask {
        match msg with
        | Connect(endpoint, cancel, promise) ->
            match remotesByEndpoint.TryGetValue endpoint with
            | true, handle -> promise.SetResult handle.Connection.Manifest.NodeId
            | false, _ ->
                try
                    let! connection = transport.Connect(endpoint, cancel)
                    let nodeId = connection.Manifest.NodeId 
                    if nodeId = selfId then
                        do! connection.DisposeAsync()
                        promise.SetException (InvalidOperationException (sprintf "Cannot connect to '%s': remote node ID is identical to local one: %i" endpoint selfId))
                    else
                        let (snd, rcv) = Channel.boundedMpsc 256
                        
                        let handle = new EndpointHandle(connection, locals, snd, rcv, this.CancellationToken)
                        this.AddHandle handle
                        promise.SetResult nodeId
                with e ->
                    promise.SetException e
                    
        | Accepted(connection) ->
            match remotesById.TryGetValue(connection.Manifest.NodeId) with
            | true, existing ->
                let (snd, rcv) = existing.OutboundSender, existing.OutboundReceiver
                do! existing.DisposeAsync()
                let handle = new EndpointHandle(connection, locals, snd, rcv, this.CancellationToken)
                this.AddHandle handle
            | false, _ ->
                let (snd, rcv) = Channel.boundedMpsc 256
                let handle = new EndpointHandle(connection, locals, snd, rcv, this.CancellationToken)
                this.AddHandle handle
                
        | Restart(nodeId, endpoint) ->
            let! connection = transport.Connect(endpoint, this.CancellationToken)                    
            if connection.Manifest.NodeId <> nodeId then
                do! connection.DisposeAsync()
                eprintfn "Cannot restart connection to '%s': remote node ID (%i) after restart changed to (%i)" endpoint nodeId connection.Manifest.NodeId
                remotesByEndpoint.Remove endpoint |> ignore
                remotesById.Remove nodeId |> ignore
            else
                let (snd, rcv) = Channel.boundedMpsc 256
                let handle = new EndpointHandle(connection, locals, snd, rcv, this.CancellationToken)
                this.AddHandle handle
    }
        
[<Sealed>]
type Node(transport: Transport) as this =
    let selfId = transport.Manifest.NodeId
    let disposed = atom false
    let cancellation = new CancellationTokenSource()
    let channels = ConcurrentDictionary<ChannelId, Addressable>()
    let endpointManager = new EndpointManager(transport, channels)
    let accepted =
        transport.Accepted
        |> AsyncSeq.map Accepted
        |> AsyncSeq.into false endpointManager
    
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
            match endpointManager.RemotesById.TryGetValue nodeId with
            | true, handle -> handle.TryGet channelId
            | _ -> None
        
    member this.Connect (endpoint: Endpoint, cancellationToken: CancellationToken) =
        let promise = Promise()
        endpointManager.Send(Connect(endpoint, cancellationToken, promise)) |> ignore
        promise.Task
        
    member this.DisposeAsync() = uunitVtask {
        if not (disposed := true) then
            cancellation.Cancel()
            let errors = ResizeArray()
            for entry in channels do
                try ignore (entry.Value.TryComplete null) with e -> errors.Add e
            try do! endpointManager.DisposeAsync(true) with e -> errors.Add e
            for e in endpointManager.RemotesById do
                try do! e.Value.DisposeAsync() with e -> errors.Add e
            try
                do! transport.DisposeAsync()
            with e ->
                errors.Add e
                
            if errors.Count > 0 then
                raise (AggregateException errors)
    }
    interface IDisposable with member this.Dispose() = this.DisposeAsync().GetAwaiter().GetResult()
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync()
