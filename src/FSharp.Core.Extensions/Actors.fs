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
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open System.Runtime.CompilerServices
open FSharp.Control.Tasks.Builders.Unsafe
   
[<RequireQualifiedAccess>]
module internal ActorStatus =
    let [<Literal>] Idle = 0
    let [<Literal>] Enqueued = 1
    let [<Literal>] WriteClosed = 2
   
type ActorOptions =
    { MailboxSize: int }
    static member Default = { MailboxSize = -1 }
         
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
[<AbstractClass>]
type UnboundedActor<'msg>() =
    inherit ChannelWriter<'msg>()
    [<DefaultValue(false)>] val mutable internal status: int
    let queue = ConcurrentQueue()
    let cts = new CancellationTokenSource()
    let promise = Promise<unit>()
    abstract Receive: 'msg -> ValueTask
    /// Cancellation token, that will be triggered once actor is being disposed.
    member this.CancellationToken = cts.Token
    /// Task, which completes, once actor finishes its work. If actor terminated abruptly, this task will complete with failure. 
    member this.Terminated : Task = upcast promise.Task
    /// Equivalent of channel WriteAsync. Sends a message to be processed by current agent. May be blocking if agent
    /// has bounded mailbox size and its capacity has been reached.
    member this.Send(message: 'msg, ?cancel: CancellationToken) = this.WriteAsync(message, cancel |> Option.defaultValue Unchecked.defaultof<_>)
    /// Disposes current actor. If `interrupt` flag was set, it will dispose immediately, discarding all pending
    /// messages. Otherwise it will just close writer channel (so noone can send messages to it), but complete only
    /// after all pending messages has been processed.
    member this.DisposeAsync (interrupt: bool) : ValueTask =
         if interrupt then
             this.Interrupt(null)
             Unchecked.defaultof<_>
         else
            this.TryComplete() |> ignore
            ValueTask(promise.Task)
    
    member private this.Interrupt(e: exn) =
        Interlocked.Exchange(&this.status, ActorStatus.WriteClosed) |> ignore
        cts.Cancel()
        ignore <| if isNull e then promise.TrySetResult () else promise.TrySetException(e)
    member private this.Continue (vt: ValueTask) = System.Action(fun () ->
        if vt.IsFaulted then this.Interrupt(vt.AsTask().Exception)
        else this.Execute())
    member private this.Execute() =
        let mutable msg = Unchecked.defaultof<_>
        try
            let mutable cont = queue.TryDequeue(&msg)
            let mutable isSync = true
            while cont && isSync do
                let t = this.Receive msg
                isSync <- t.IsCompletedSuccessfully
                if t.IsCompletedSuccessfully then
                    cont <- queue.TryDequeue(&msg)
                else
                    t.GetAwaiter().UnsafeOnCompleted(this.Continue t)
            if isSync then
                let prev = Interlocked.CompareExchange(&this.status, ActorStatus.Idle, ActorStatus.Enqueued)
                if prev = ActorStatus.WriteClosed then promise.TrySetResult () |> ignore
        with e ->
            this.Interrupt(e)
            
    override this.WaitToWriteAsync(cancel) =
        if cancel.IsCancellationRequested then ValueTask<bool>(Task.FromCanceled<bool>(cancel))
        elif this.status = ActorStatus.WriteClosed then Unchecked.defaultof<_>
        else ValueTask<_>(true)
        
    override this.TryWrite(msg) =
        let prev = Interlocked.CompareExchange(&this.status, ActorStatus.Enqueued, ActorStatus.Idle)
        if prev = ActorStatus.WriteClosed then false
        else
            //TODO: if actor status is Idle and queue is empty, we could execute it directly without enqueue
            queue.Enqueue(msg) 
            if prev = ActorStatus.Idle then this.Execute()
            true
    override this.TryComplete(e) =
        let prev = Interlocked.Exchange(&this.status, ActorStatus.WriteClosed)
        if prev <> ActorStatus.WriteClosed then
            cts.Cancel()
            if prev = ActorStatus.Idle then
                ignore <| if isNull e then promise.TrySetResult () else promise.TrySetException(e)
            true
        else false
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync(true)
    interface IDisposable with member this.Dispose() = this.DisposeAsync(true).GetAwaiter().GetResult()
                
        