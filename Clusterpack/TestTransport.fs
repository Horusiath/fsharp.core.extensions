namespace Clusterpack.Test

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open Clusterpack
open FSharp.Control.Tasks.Builders
open FSharp.Core

[<Sealed>]
type TestConnection internal(manifest: Manifest) =
    let (writer, reader) = Channel.unboundedMpsc ()
    let incoming = reader.ReadAllAsync()
    member this.Manifest = manifest
    interface Connection with
        member this.DisposeAsync() = unitVtask {
            writer.Complete()
        }
        member this.Manifest = manifest
        member this.Incoming = incoming
        member this.Send(msg, cancel) = writer.WriteAsync(msg, cancel)

and [<Sealed>] TestTransport internal(manifest: Manifest, network: TestNetwork) =
    let outbound = ConcurrentDictionary<Endpoint, TestConnection>()
    let inbound = ConcurrentDictionary<Endpoint, TestConnection>()
    let (acceptedWriter, acceptedReader) = Channel.unboundedMpsc<Connection> ()
    member internal this.Accept(conn: TestConnection, cancellationToken: CancellationToken) = vtask {
        let remoteEndpoint = conn.Manifest.Endpoint
        inbound.[remoteEndpoint] <- conn
        do! acceptedWriter.WriteAsync(conn, cancellationToken)
        let conn = TestConnection(manifest)
        outbound.[remoteEndpoint] <- conn
        return conn
    }
    interface Transport with
        member this.DisposeAsync() = unitVtask {
            for entry in outbound do
                do! (entry.Value :> Connection).DisposeAsync()
        }
        member this.Manifest = manifest
        member this.Connect(endpoint: Endpoint, cancellationToken: CancellationToken) = vtask {
            match network.GetTransport(endpoint) with
            | None -> return failwithf "TestNetwork is not configured to have transport at address '%O'" endpoint
            | Some destination ->
                let out = TestConnection(manifest)
                outbound.[endpoint] <- out
                let! inc = destination.Accept(out, cancellationToken)
                inbound.[endpoint] <- inc
                return upcast out
        }
        member this.Accepted: IAsyncEnumerable<Connection> = acceptedReader.ReadAllAsync()

and [<Sealed>] TestNetwork() =
    let transports = ConcurrentDictionary()
    member this.GetTransport(endpoint: Endpoint) : TestTransport option =
        match transports.TryGetValue(endpoint) with
        | true, t  -> Some t
        | false, _ -> None
    member this.CreateTransport(nodeId: NodeId, endpoint: Endpoint) : TestTransport =
        let manifest = { NodeId = nodeId; Endpoint = endpoint }
        let transport = new TestTransport(manifest, this)
        transports.GetOrAdd(endpoint, transport)
    member this.DisposeAsync() = unitVtask {
        for entry in transports do
            do! (entry.Value :> Transport).DisposeAsync()
    }
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync()
    interface IDisposable with member this.Dispose() = this.DisposeAsync().GetAwaiter().GetResult()