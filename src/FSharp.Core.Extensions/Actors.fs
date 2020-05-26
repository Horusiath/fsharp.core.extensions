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
   
// This is much better optimized than TAwaiter.OnCompleted(Action action) behind the scenes.
// As IAsyncStateMachineBox (created from an IAsyncStateMachine) enjoys a lot of fast paths.
// Making it the only alloc done vs OnCompleted delegate + task continuation + threadpool item
[<Struct; NoComparison; NoEquality>]
type private Callback(builder: AsyncValueTaskMethodBuilder, task: ValueTask, continuation: Action<ValueTask, obj>, state: obj) =

    interface IAsyncStateMachine with
        member __.SetStateMachine(stateMachine) = builder.SetStateMachine(stateMachine)
        member x.MoveNext() =
            let mutable x = x
            try
                let mutable awaiter = task.GetAwaiter()
                if not <| awaiter.IsCompleted then
                    builder.AwaitUnsafeOnCompleted(&awaiter, &x)
                else
                    continuation.Invoke(task, state)
            with e ->
                printfn "ETF: %O" e 
                // As we might be running inline swallow any exception, continuation can deal with it.
                ()

    static member Start(task: ValueTask, state: obj, continuation: Action<ValueTask, obj>) =
        let mutable builder = AsyncValueTaskMethodBuilder()
        let mutable stateMachine = Callback(builder, task, continuation, state)
        builder.Start(&stateMachine)
        
[<RequireQualifiedAccess>]
module private ActorStatus =
    let [<Literal>] Idle = 0
    let [<Literal>] Enqueued = 1
    let [<Literal>] WriteClosed = 2
    let ValueTaskAction = Action<ValueTask,obj>(fun task cont -> (cont :?> Action<ValueTask>).Invoke(task))
         
[<Extension>]
type CallbackExtensions =

    [<Extension>]
    static member OnCompleted(task: ValueTask, continuation: Action<ValueTask>) =
        Callback.Start(task, continuation, ActorStatus.ValueTaskAction)
        
/// Actor, that will consume sent messages one by one in thread safe manner, always using single thread at the time.
/// Actor takes initial state, that is accessible via `State` property and can be modified by returning updated state
/// in message handler. Actor mailbox is unbounded by default, but its size can be configured by provided options.
///
/// Actor is compliant with `ChannelWriter` API, which make it composable in some scenarios
/// (eg. as target of `AsyncSeq.into`).
///
/// Actor's state is no fully encapsulated, as it may be accessed at any time by using `State` property - therefore
/// avoid using mutable state or modifying it outside of actor message handler, as it may introduce data races.
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
    member inline this.Send(message: 'msg, ?cancel: CancellationToken) =
        this.WriteAsync(message, cancel |> Option.defaultValue Unchecked.defaultof<_>)
    
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
        
    member private this.Continue = System.Action<ValueTask>(fun vt ->
        if vt.IsFaulted then this.Interrupt(vt.AsTask().Exception.InnerException)
        else this.Execute())
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member private this.Handle(msg: 'msg inref) =
        let t = this.Receive msg
        if not t.IsCompletedSuccessfully then
            t.OnCompleted(this.Continue)
            false
        else true
        
    member private this.Execute() =
        let mutable msg = Unchecked.defaultof<_>
        try
            let mutable cont = queue.TryDequeue(&msg)
            let mutable isSync = true
            while cont && isSync do
                isSync <- this.Handle &msg
                if isSync then
                    cont <- queue.TryDequeue(&msg)
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
        match Interlocked.CompareExchange(&this.status, ActorStatus.Enqueued, ActorStatus.Idle) with
        | ActorStatus.Idle ->
            // actor was idle -> queue must have been empty in current execution context,
            // so we can try to execute the message without even going via the queue
            try
                if this.Handle &msg then
                    if queue.IsEmpty then
                        let prev = Interlocked.CompareExchange(&this.status, ActorStatus.Idle, ActorStatus.Enqueued)
                        if prev = ActorStatus.WriteClosed && queue.IsEmpty then promise.TrySetResult () |> ignore
                    else
                        // in the meantime queue was filled from another thread, so we reschedule this actor to thread pool
                        // the rationale here is: if we have only pair of actors talking with each other, we can do it in sync
                        // (like two ordinary objects calling each others methods), but if there are many actors
                        // communicating with current one and `this.Receive` would be synchronous, we could potentially
                        // run into scenario, where we starve others as there's no yielding (since the actor-actor
                        // message sending is not always async)
                        ThreadPool.UnsafeQueueUserWorkItem(this, true) |> ignore
                        
                    // TODO: there's possibility that sending messages to this actor will result in occupying current
                    // thread infinitely. On the receiver side we could just put `Task.Yield` somewhere in the execution
                    // path to fix it. But how (if ever) do we want to address that on sender side?
                    // Make a separate `SendAsync` method? 
            with e ->
                this.Interrupt e
            true
        | ActorStatus.WriteClosed -> false
        | _ -> queue.Enqueue(msg); true
        
    /// Equivalent of channel WriteAsync. Sends a message to be processed by current agent. May be blocking if agent
    /// has bounded mailbox size and its capacity has been reached.
    member this.SendAsync(message: 'msg, ?cancel: CancellationToken) : ValueTask =
        match cancel with
        | Some c when c.IsCancellationRequested -> ValueTask(Task.FromCanceled(c))
        | _ ->
            match Interlocked.CompareExchange(&this.status, ActorStatus.Enqueued, ActorStatus.Idle) with
            | ActorStatus.Idle ->
                queue.Enqueue(message)
                ThreadPool.UnsafeQueueUserWorkItem(this, true) |> ignore
                Unchecked.defaultof<_>
            | ActorStatus.WriteClosed -> raise (ObjectDisposedException(this.ToString()))
            | _ -> queue.Enqueue(message); Unchecked.defaultof<_>
            
    override this.TryComplete(e) =
        let prev = Interlocked.Exchange(&this.status, ActorStatus.WriteClosed)
        if prev <> ActorStatus.WriteClosed then
            cts.Cancel()
            if prev = ActorStatus.Idle then
                ignore <| if isNull e then promise.TrySetResult () else promise.TrySetException(e)
            true
        else false
    interface IThreadPoolWorkItem with member this.Execute() = this.Execute()        
    interface IAsyncDisposable with member this.DisposeAsync() = this.DisposeAsync(true)
    interface IDisposable with member this.Dispose() = this.DisposeAsync(true).GetAwaiter().GetResult()
                
        