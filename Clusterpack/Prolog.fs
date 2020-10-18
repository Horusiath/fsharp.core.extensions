namespace Clusterpack

open System
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open System.Runtime.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control.Tasks

[<AutoOpen>]
module Prolog = 

    open System

    type NodeId = uint32
    type ChannelId = uint32
    type Address = ValueTuple<NodeId, ChannelId>
    
    type Endpoint = string

type Manifest =
    { NodeId: NodeId
      Endpoint: Endpoint }
    
type EndpointMessage =
    | Envelope of recipient:Address * message:obj
    
type ConnectionClosedException(endpoint: Endpoint) =
    inherit Exception(sprintf "Connection '%s' has been closed" endpoint)
    member this.Endpoint = endpoint
    
[<Interface>]
type Connection =
    inherit IAsyncDisposable
    abstract Manifest: Manifest
    abstract Send: EndpointMessage * CancellationToken -> ValueTask
    abstract ReceiveNext: CancellationToken -> ValueTask<EndpointMessage>

[<Interface>]
type Transport =
    inherit IAsyncDisposable
    abstract Manifest: Manifest
    abstract Connect: Endpoint * CancellationToken -> ValueTask<Connection>
    abstract Accepted: IAsyncEnumerator<Connection>

[<Interface>]
type internal Addressable =
    abstract Address: Address
    abstract Pass: message:obj * CancellationToken -> ValueTask
    abstract TryComplete: exn -> bool
    
[<AbstractClass>]
type Proxy<'msg> (address: Address) =
    inherit ChannelWriter<'msg>()
    member this.Address = address
    interface Addressable with
        member this.TryComplete(err) = this.TryComplete(err)
        member this.Address = this.Address
        member this.Pass(msg, cancel) = this.WriteAsync (downcast msg, cancel)
        
   
and [<Sealed>] internal LocalProxy<'msg>(address: Address, target: ChannelWriter<'msg>) =
    inherit Proxy<'msg>(address)
    override this.TryWrite(msg) : bool = target.TryWrite(msg)
    override this.WaitToWriteAsync(cancel) = target.WaitToWriteAsync(cancel)
    
and [<Sealed>] internal RemoteProxy<'msg>(address: Address, writer: ChannelWriter<EndpointMessage>) =
    inherit Proxy<'msg>(address)
    override this.TryWrite(msg: 'msg) : bool = writer.TryWrite(Envelope(address, msg :> obj))
    override this.WaitToWriteAsync(cancel) = writer.WaitToWriteAsync(cancel)