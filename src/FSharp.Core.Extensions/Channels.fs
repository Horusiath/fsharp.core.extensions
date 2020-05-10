(*

Copyright 2019 Bartosz Sypytkowski

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*)

namespace FSharp.Core

open System
open System.Runtime.CompilerServices
open System.Runtime.ExceptionServices
open System.Threading
open FSharp.Control.Tasks.Builders
open System.Threading.Channels
open System.Threading.Tasks

module Channel =
    
    /// Returns decomposed writer/reader pair components of n-capacity bounded MPSC
    /// (multi-producer/single-consumer) channel.
    let inline boundedMpsc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleWriter=false, SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded MPSC
    /// (multi-producer/single-consumer) channel.
    let inline unboundedMpsc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleWriter=false, SingleReader=true))
        (ch.Writer, ch.Reader)
       
    
    /// Returns decomposed writer/reader pair components of n-capacity bounded MPMC
    /// (multi-producer/multi-consumer) channel.
    let inline boundedMpmc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleWriter=false, SingleReader=false))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded MPMC
    /// (multi-producer/multi-consumer) channel.
    let inline unboundedMpmc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleWriter=false, SingleReader=false))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of n-capacity bounded SPSC
    /// (single-producer/single-consumer) channel.
    let inline boundedSpsc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleWriter=true, SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded SPSC
    /// (single-producer/single-consumer) channel.
    let inline unboundedSpsc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleWriter=true, SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of n-capacity bounded SPMC
    /// (single-producer/multi-consumer) channel.
    let inline boundedSpmc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleWriter=true, SingleReader=false))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded SPMC
    /// (multi-producer/multi-consumer) channel.
    let inline unboundedSpmc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleWriter=true, SingleReader=false))
        (ch.Writer, ch.Reader)
        
    /// Tries to read as much elements as possible from a given reader to fill provided span
    /// without blocking.
    let readTo (span: Span<'a>) (reader: ChannelReader<'a>) : int =
        let mutable i = 0
        let mutable item = Unchecked.defaultof<_>
        while i < span.Length && reader.TryRead(&item) do
            span.[i] <- item
            i <- i+1
        i
         
    let rec private awaitForValue (readers: ChannelReader<'a>[]) = vtask {
        let pending = readers |> Array.map (fun reader -> task {
            let! ok = reader.WaitToReadAsync()
            if not ok then
                return raise (ChannelClosedException("Channel has been closed"))
            else return reader
        })
        let! t = Task.WhenAny(pending)
        let ok, value = t.Result.TryRead()
        
        // there's a risk that another thread has read value in between WaitToReadAsync and TryRead,
        // that's why we need to use recursion
        if ok then return value
        else return! awaitForValue readers
    }
    
    /// Listens or multiple channels, returning value from the one which completed first.
    /// Order in which channels where provided will be assumed priority order in case
    /// when multiple channels have produced their values at the same time. If any of the
    /// channels will be closed while waiting or any exception will be produced, entire
    /// select will fail.
    let select (readers: ChannelReader<'a>[]) : ValueTask<'a> =
        // 1st pass, check in any reader has it's value already prepared
        let mutable found = false
        let mutable i = 0
        let mutable result = Unchecked.defaultof<'a>
        while not found && i < readers.Length do
            let r = readers.[i]
            found <- r.TryRead(&result)
            i <- i + 1
            
        if found then ValueTask<'a>(result)
        else awaitForValue readers
    
    /// Wraps one channel reader into another one, returning a value mapped from the source.    
    let map (f: 'a -> 'b) (reader: ChannelReader<'a>) : ChannelReader<'b> =
        { new ChannelReader<'b>() with
            
            [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
            member _.TryRead(ref) =
                let ok, value = reader.TryRead()
                if not ok then false
                else
                    ref <- f value
                    true
                    
            [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
            member _.WaitToReadAsync(cancellationToken) = reader.WaitToReadAsync(cancellationToken) }
        
type ActorOptions =
    { MailboxSize: int }
    static member Default = { MailboxSize = -1 }
        
open FSharp.Control.Tasks.Builders.Unsafe
        
/// Actor, that will consume sent messages one by one in thread safe manner, always using single thread at the time.
/// Actor takes initial state, that is accessible via `State` property and can be modified by returning updated state
/// in message handler. Actor mailbox is unbounded by default, but its size can be configured by provided options.
///
/// Actor is compliant with `ChannelWriter` API, which make it composable in some scenarios
/// (eg. as target of `AsyncSeq.into`).
///
/// Actor's state is no fully encapsulated, as it may be accessed at any time by using `State` property - therefore
/// avoid using mutable state or modifying it outisde of actor message handler, as it may introduce data races.
///
/// Actor provides `Terminated` task that can be awaited to determine when actor has been stopped (if actor's message
/// handler function raised uncaught exception or actor was `TryComplete`d with exception, this task will complete with
/// failure). Actor can be stopped gently (letting it process all pending messages first) via
/// `DisposeAsync(false)`/`Complete()` or immediately via `DisposeAsync(true)`/`DisposeAsync()`/`Dispose()`.
///
/// Actor provided `CancellationToken` that can be provided as parameter into methods that which should not outlive an
/// actor's lifetime.
[<Sealed>]
type Actor<'msg, 'state>(init: 'state, handler: Actor<'msg,'state> -> 'msg -> ValueTask<'state>, ?options: ActorOptions) as this =
    inherit ChannelWriter<'msg>()
    let cts = new CancellationTokenSource()
    let (writer, reader) =
        let options = options |> Option.defaultValue ActorOptions.Default
        if options.MailboxSize <= 0
        then Channel.unboundedMpsc()
        else Channel.boundedMpsc(options.MailboxSize)
    let mutable state = init
    static let eventLoop (this: Actor<'msg,'state>) = fun () -> uunitTask {
        let mutable cont = true
        let mutable error = Unchecked.defaultof<exn>
        while cont && not this.CancellationTokenSource.IsCancellationRequested do
            try
                let ok, msg = this.Reader.TryRead() 
                if ok then
                    let! state' = this.Handler this msg
                    this.State <- state'
                    cont <- true
                else
                    let! ready = this.Reader.WaitToReadAsync()
                    let ok, msg = this.Reader.TryRead()
                    if ready && ok then
                        let! state' = this.Handler this msg
                        this.State <- state'
                        cont <- true
                    else cont <- false
            with e ->
                error <- e
                cont <- false
        
        this.CancellationTokenSource.Cancel()
        this.Writer.TryComplete(error) |> ignore
        if not (isNull error) then ExceptionDispatchInfo.Capture(error).Throw()        
    } 
    let task = Task.Run (eventLoop this)
    member inline private _.Reader : ChannelReader<'msg> = reader
    member inline private _.Handler = handler
    member inline private _.CancellationTokenSource : CancellationTokenSource = cts
    member inline private _.Writer : ChannelWriter<'msg> = writer
    /// Returns current state of the agent. Keep in mind, that this state is not synchronized with agent work loop, so
    /// the best is to keep it immutable, as any changes may introduce race conditions and violate state encapsulation. 
    member this.State
        with [<MethodImpl(MethodImplOptions.AggressiveInlining)>] get () = state
        and [<MethodImpl(MethodImplOptions.AggressiveInlining)>]  private set (state') = state <- state'
    /// Cancellation token, that will be triggered once actor is being disposed.
    member this.CancellationToken = cts.Token
    /// Task, which completes, once actor finishes its work. If actor terminated abruptly, this task will complete with failure. 
    member this.Terminated : Task = upcast task
    /// Equivalent of channel WriteAsync. Sends a message to be processed by current agent. May be blocking if agent
    /// has bounded mailbox size and its capacity has been reached.
    member this.Send(message: 'msg, ?cancel: CancellationToken) = writer.WriteAsync(message, cancel |> Option.defaultValue CancellationToken.None)
    /// Disposes current actor. If `interrupt` flag was set, it will dispose immediately, discarding all pending
    /// messages. Otherwise it will just close writer channel (so noone can send messages to it), but complete only
    /// after all pending messages has been processed.
    member this.DisposeAsync (interrupt: bool) : ValueTask = unitVtask {
        writer.TryComplete() |> ignore
        if interrupt then
            cts.Cancel()
            return ()
        else do! task
    }
    override this.TryComplete(exn) = writer.TryComplete(exn)
    override this.TryWrite(msg) = writer.TryWrite(msg)
    override this.WaitToWriteAsync(cancel) = writer.WaitToWriteAsync(cancel)
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync(true)
    interface IDisposable with member this.Dispose() = this.DisposeAsync(true).GetAwaiter().GetResult()
    
type Actor<'a> = Actor<'a, unit>
    
[<RequireQualifiedAccess>]
module Actor =
    
    let inline statefulWith (options: ActorOptions) (state: 's) (handler: Actor<'a,'s> -> 'a -> ValueTask<'s>) : Actor<'a,'s> =
        new Actor<'a,'s>(state, handler, options)
        
    let inline stateful state handler = statefulWith ActorOptions.Default state handler
    
    let inline statelessWith (options: ActorOptions) (handler: Actor<'a> -> 'a -> ValueTask) : Actor<'a> =
        new Actor<'a,unit>((), (fun msg actor -> vtask { do! handler msg actor }), options)
        
    let inline stateless (handler: Actor<'a> -> 'a -> ValueTask) : Actor<'a> = statelessWith ActorOptions.Default handler