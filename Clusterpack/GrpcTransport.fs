namespace Clusterpack.Grpc

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Threading
open System.Threading.Tasks
open Clusterpack
open FSharp.Control.Tasks.Builders
open Grpc.Core
open FSharp.Core

[<Sealed>]
type GrpcTransport(nodeId: NodeId, endpoint: Endpoint, trace: TextWriter) =
    static let toServerPort (endpoint: Endpoint) =
        let [|host;port|] = endpoint.Split ':'
        ServerPort(host, int port, ServerCredentials.Insecure)
    static let method =
        let serializer = Hyperion.Serializer()
        let toBytes (msg: 'msg) : byte[] =
            use stream = new MemoryStream()
            serializer.Serialize(msg, stream)
            let bytes = stream.ToArray()
            bytes
        let fromBytes (buf: byte[]) : 'msg =
            use stream = new MemoryStream(buf)
            let msg = serializer.Deserialize<'msg>(stream)
            msg
        let msgMarshaller = Marshallers.Create(Func<_,_>(toBytes), Func<_,_>(fromBytes))
        let manifestMarshaller = Marshallers.Create(Func<_,_>(toBytes), Func<_,_>(fromBytes))
        Method<Manifest, EndpointMessage>(MethodType.ServerStreaming, "clusterpack", "messages", manifestMarshaller, msgMarshaller)
        
    static let callOptions = CallOptions()
        
    let pending = ConcurrentDictionary<Endpoint, (GrpcConnection * TaskCompletionSource<Connection>)>()
    let (acceptedWriter, acceptedReader) = Channel.boundedMpsc<Connection> 16
    let accepted = acceptedReader.ReadAllAsync().GetAsyncEnumerator()
    
    let manifest = { NodeId = nodeId; Endpoint = endpoint }
        
    let communicationChannel (incoming: Manifest) (responseStream: IServerStreamWriter<EndpointMessage>) (context: ServerCallContext) = unitTask {
        match pending.TryRemove(incoming.Endpoint) with
        | true, (conn, promise) ->
            trace.WriteLine(sprintf "'%s' received connection back from '%s'" endpoint incoming.Endpoint)
            conn.Start(incoming, responseStream)
            promise.SetResult(conn)
        | false, _ ->
            trace.WriteLine(sprintf "'%s' received connection from '%s'" endpoint incoming.Endpoint)
            let sp = toServerPort incoming.Endpoint
            let channel = new Grpc.Core.Channel(sp.Host, sp.Port, ChannelCredentials.Insecure)
            do! channel.ConnectAsync()
            let invoker = DefaultCallInvoker(channel)
            let call = invoker.AsyncServerStreamingCall(method, endpoint, callOptions, manifest)
            let connection = new GrpcConnection(channel, call)
            connection.Start(incoming, responseStream)
            do! acceptedWriter.WriteAsync(connection, context.CancellationToken)
    }
    
    
    let server =
        let builder = ServerServiceDefinition.CreateBuilder().AddMethod(method, ServerStreamingServerMethod(communicationChannel))
        let server = Server()
        server.Ports.Add(toServerPort endpoint) |> ignore
        server.Services.Add(builder.Build())
        server.Start()
        trace.WriteLine(sprintf "'%s' is up" endpoint)
        server
        
    let connect = Func<Endpoint, (GrpcConnection * TaskCompletionSource<Connection>)>(fun target ->
        trace.WriteLine(sprintf "'%s' connecting to '%s'" endpoint target)
        let sp = toServerPort target
        let promise = TaskCompletionSource<Connection>()
        let channel = new Grpc.Core.Channel(sp.Host, sp.Port, ChannelCredentials.Insecure)
        channel.ConnectAsync().GetAwaiter().GetResult()
        let invoker = DefaultCallInvoker(channel)
        let call = invoker.AsyncServerStreamingCall(method, target, callOptions, manifest)
        let connection = new GrpcConnection(channel, call)
        (connection, promise)
    )
        
    new (nodeId: NodeId, endpoint: Endpoint) =
        GrpcTransport(nodeId, endpoint, Console.Out)
        
    interface Transport with
        member this.DisposeAsync() = ValueTask(server.ShutdownAsync())
        member this.Manifest = manifest
        member this.Connect(endpoint: Endpoint, cancellationToken: CancellationToken) = vtask {
            let (conn, promise) = pending.GetOrAdd(endpoint, connect)
            use reg = cancellationToken.Register(Action(fun () -> promise.SetCanceled()))
            return! promise.Task           
        }
        member this.Accepted: IAsyncEnumerator<Connection> = accepted

and [<Sealed>] GrpcConnection(channel: Grpc.Core.Channel, response: AsyncServerStreamingCall<EndpointMessage>) =
    [<DefaultValue>] val mutable manifest: Manifest
    [<DefaultValue>] val mutable request: IServerStreamWriter<EndpointMessage>
    member this.Start(manifest: Manifest, req: IServerStreamWriter<EndpointMessage>) =
        this.manifest <- manifest
        this.request <- req
    interface Connection with
        member this.DisposeAsync() = unitVtask {
            do! channel.ShutdownAsync()
        }
        member this.Manifest = this.manifest
        member this.Send(msg, cancel) = unitVtask {
            do! this.request.WriteAsync(msg)
        }
        member this.ReceiveNext(cancel) = vtask {
            let hasValues = response.ResponseStream.MoveNext(cancel).GetAwaiter().GetResult()
            return if hasValues then response.ResponseStream.Current else raise (ConnectionClosedException(this.manifest.Endpoint))
        }