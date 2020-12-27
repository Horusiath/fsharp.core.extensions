namespace Clusterpack.Grpc

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Threading
open System.Threading.Tasks
open Clusterpack
open FSharp.Control.Tasks
open Grpc.Core
open FSharp.Core

[<Sealed>]
type GrpcTransport(nodeId: NodeId, endpoint: Endpoint) =
    static let toServerPort (endpoint: Endpoint) =
        let [|host;port|] = endpoint.Split ':'
        ServerPort(host, int port, ServerCredentials.Insecure)
    static let method =
        let serializer = Hyperion.Serializer()
        let toBytes (msg: 'msg) : byte[] =
            use stream = new MemoryStream()
            serializer.Serialize(msg, stream)
            stream.ToArray()
        let fromBytes (buf: byte[]) : 'msg =
            use stream = new MemoryStream(buf)
            serializer.Deserialize<'msg>(stream)
        let msgMarshaller = Marshallers.Create(Func<_,_>(toBytes), Func<_,_>(fromBytes))
        let manifestMarshaller = Marshallers.Create(Func<_,_>(toBytes), Func<_,_>(fromBytes))
        Method<Manifest, EndpointMessage>(MethodType.ServerStreaming, "clusterpack", "messages", manifestMarshaller, msgMarshaller)
        
    static let callOptions = CallOptions()
        
    let pending = ConcurrentDictionary<Endpoint, (GrpcConnection * TaskCompletionSource<Connection>)>()
    let (acceptedWriter, acceptedReader) = Channel.boundedMpsc<Connection> 16
    let accepted = acceptedReader.ReadAllAsync()
    
    let manifest = { NodeId = nodeId; Endpoint = endpoint }
        
    let communicationChannel (incoming: Manifest) (responseStream: IServerStreamWriter<EndpointMessage>) (context: ServerCallContext) = unitTask {
        match pending.TryRemove(incoming.Endpoint) with
        | true, (conn, promise) ->
            conn.Start(incoming, responseStream)
            promise.SetResult(conn)
            do! conn.Completed
        | false, _ ->
            let sp = toServerPort incoming.Endpoint
            let channel = new Grpc.Core.Channel(sp.Host, sp.Port, ChannelCredentials.Insecure)
            do! channel.ConnectAsync()
            let invoker = DefaultCallInvoker(channel)
            let call = invoker.AsyncServerStreamingCall(method, endpoint, callOptions, manifest)
            let conn = new GrpcConnection(channel, call)
            conn.Start(incoming, responseStream)
            do! acceptedWriter.WriteAsync(conn, context.CancellationToken)
            do! conn.Completed
    }
    
    
    let server =
        let builder = ServerServiceDefinition.CreateBuilder()
                          .AddMethod(method, ServerStreamingServerMethod(communicationChannel))
        let server = Server()
        server.Ports.Add(toServerPort endpoint) |> ignore
        server.Services.Add(builder.Build())
        server.Start()
        server
        
    let connect = Func<Endpoint, (GrpcConnection * TaskCompletionSource<Connection>)>(fun target ->
        let sp = toServerPort target
        let promise = TaskCompletionSource<Connection>()
        let channel = new Grpc.Core.Channel(sp.Host, sp.Port, ChannelCredentials.Insecure)
        channel.ConnectAsync().GetAwaiter().GetResult()
        let invoker = DefaultCallInvoker(channel)
        let call = invoker.AsyncServerStreamingCall(method, target, callOptions, manifest)
        let connection = new GrpcConnection(channel, call)
        (connection, promise)
    )
        
    interface Transport with
        member this.DisposeAsync() = ValueTask(server.ShutdownAsync())
        member this.Manifest = manifest
        member this.Connect(endpoint: Endpoint, cancellationToken: CancellationToken) = vtask {
            let (conn, promise) = pending.GetOrAdd(endpoint, connect)
            use reg = cancellationToken.Register(Action(fun () -> promise.SetCanceled()))
            return! promise.Task           
        }
        member this.Accepted: IAsyncEnumerable<Connection> = accepted

and [<Sealed>] GrpcConnection(channel: Grpc.Core.Channel, response: AsyncServerStreamingCall<EndpointMessage>) =
    let completed = TaskCompletionSource<unit>()
    let getEnumerator cancel =
        { new IAsyncEnumerator<EndpointMessage> with
            member this.MoveNextAsync() = vtask {
                try
                    return! response.ResponseStream.MoveNext(cancel)
                with
                | :? Grpc.Core.RpcException as e when e.Status.StatusCode = StatusCode.Cancelled -> return false
            }
            member this.Current = response.ResponseStream.Current
            member this.DisposeAsync() = ValueTask() }
    let incoming = { new IAsyncEnumerable<_> with member _.GetAsyncEnumerator(cancel) = getEnumerator cancel }
    [<DefaultValue>] val mutable manifest: Manifest
    [<DefaultValue>] val mutable request: IServerStreamWriter<EndpointMessage>
    member this.Start(manifest: Manifest, req: IServerStreamWriter<EndpointMessage>) =
        this.manifest <- manifest
        this.request <- req
    member this.Completed = completed.Task
    interface Connection with
        member this.DisposeAsync() = unitVtask {
            if completed.TrySetResult () then
                do! channel.ShutdownAsync()
        }
        member this.Manifest = this.manifest
        member this.Incoming = incoming
        member this.Send(msg, cancel) = unitVtask {
            do! this.request.WriteAsync(msg)
        }