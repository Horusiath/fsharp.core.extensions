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
open System.Collections.Immutable
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open System.Threading.Tasks.Sources
open FSharp.Core
open FSharp.Core.Atomic.Operators

module private LockUtils =
    let counter = atom 0
    let current: AsyncLocal<int> = AsyncLocal()
    let inline lockId () =
        if current.Value = 0 then
            current.Value <- Atomic.incr counter 
        current.Value
        
    let inline asToken (i: int): int16 =
        let hi = (i &&& 0xffff0000) >>> 16
        let lo = i &&& 0x0000ffff
        int16 (hi ^^^ lo)
        
    let disposedException = ObjectDisposedException "RWLock has been disposed"

type private LockState =
    | Write of writeLocks:int * readLocks:int * ownerId:int
    | Read of Map<int, int>
    static member Default = Read Map.empty
    member this.ReadLocks(id: int) =
        match this with
        | Read map ->
            match map.TryGetValue id with
            | true, i -> i
            | false, _ -> 0
        | Write(_, readLocks, ownerId) when ownerId = id -> readLocks
        | _ -> 0
    member this.WriteLocks(id: int) =
        match this with
        | Write(writeLocks, _, ownerId) when ownerId = id -> writeLocks
        | _ -> 0
    member this.IsOnlyHolder(id: int) =
        match this with
        | Read map when Map.isEmpty map || (map.Count = 1 && Map.containsKey id map) -> true
        | Write(_,_, ownerId) when ownerId = id -> true
        | _ -> false
    member this.AdjustRead(id: int, delta: int) =
        match this with
        | Read map ->
            let total =
                match map.TryGetValue(id) with
                | true, total -> total
                | false, _    -> 0
            let modified = total + delta
            if modified > 0 then Read (Map.add id modified map)
            elif modified = 0 then Read (Map.remove id map)
            else failwithf "Bug (read lock): task %i is releasing the lock it doesn't hold." id
        | Write(writes, reads, ownerId) when ownerId = id ->
            let modified = reads + delta
            if modified >= 0 then Write(writes, modified, ownerId)
            else failwithf "Bug (write lock): task %i is releasing the lock it doesn't hold." id
        | other -> other // another task is holding a write lock
    member this.IsReadLocked =
        match this with
        | Read map when Map.isEmpty map -> false
        | Write(_, reads, _) when reads <> 0 -> false
        | _ -> true
    member this.IsWriteLocked =
        match this with
        | Write _ -> true
        | _ -> false

[<Sealed>]      
type internal LockAwaiter<'h> private() =
    static let pool = ConcurrentStack<LockAwaiter<'h>>()
    [<DefaultValue(false)>] val mutable status: ValueTaskSourceStatus
    [<DefaultValue(false)>] val mutable token: int16
    [<DefaultValue(false)>] val mutable handle: 'h
    [<DefaultValue(false)>] val mutable cancellationToken: CancellationToken
    [<DefaultValue(false)>] val mutable continuation: Action<obj>
    [<DefaultValue(false)>] val mutable state: obj
    [<DefaultValue(false)>] val mutable ec: ExecutionContext
    [<DefaultValue(false)>] val mutable sc: SynchronizationContext
    
    static member Rent() =
        match pool.TryPop() with
        | true, awaiter -> awaiter
        | false, _ -> LockAwaiter<'h>()
        
    static member Return(awaiter) = pool.Push awaiter
    
    member this.Reset(handle: 'h, token: int16, cancellationToken: CancellationToken) =
        if cancellationToken.IsCancellationRequested then
            this.status <- ValueTaskSourceStatus.Canceled
        else
            this.status <- ValueTaskSourceStatus.Pending
        this.handle <- handle
        this.token <- token
        this.cancellationToken <- cancellationToken
        this.continuation <- null
        this.state <- null
        this.ec <- null
        this.sc <- null
        
    member private this.Finish() =
        if not (isNull this.sc) then SynchronizationContext.SetSynchronizationContext(this.sc)
        if isNull this.ec then this.continuation.Invoke(this.state)
        else ExecutionContext.Run(this.ec, ContextCallback (this.continuation.Invoke), this.state)
    
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    member private this.AssertToken(token) =
        if this.token <> token then invalidOp ("Lock awaiter executed with wrong token")
        
    member this.Complete() =
        if this.cancellationToken.IsCancellationRequested then
            this.status <- ValueTaskSourceStatus.Canceled
        else
            this.status <- ValueTaskSourceStatus.Succeeded
        this.Finish()
        
    member this.Fail() =
        this.status <- ValueTaskSourceStatus.Faulted
        this.Finish()
    
    interface IValueTaskSource<'h> with
        member this.GetResult(token) =
            this.AssertToken(token)
            let status = this.status
            let handle = this.handle
            let ct = this.cancellationToken
            LockAwaiter.Return(this)
            match status with
            | ValueTaskSourceStatus.Succeeded -> handle
            | ValueTaskSourceStatus.Canceled -> raise (OperationCanceledException ct)
            | ValueTaskSourceStatus.Faulted -> raise LockUtils.disposedException
            | other -> failwithf "Bug: LockAwaiter.GetResult is in invalid status %O" other
        member this.GetStatus(token) =
            this.AssertToken(token)
            this.status
        member this.OnCompleted(continuation, state, token, flags) =
            this.AssertToken(token)
            this.continuation <- continuation
            this.state <- state
            if flags &&& ValueTaskSourceOnCompletedFlags.FlowExecutionContext <> ValueTaskSourceOnCompletedFlags.FlowExecutionContext then
                this.ec <- ExecutionContext.Capture()
            if flags &&& ValueTaskSourceOnCompletedFlags.UseSchedulingContext <> ValueTaskSourceOnCompletedFlags.UseSchedulingContext then
                this.sc <- SynchronizationContext.Current
        
type private State<'a> =
    { Value: 'a
      LockState: LockState
      AwaitingReaders: ImmutableQueue<LockAwaiter<ReadHandle<'a>>>
      AwaitingWriters: ImmutableQueue<LockAwaiter<WriteHandle<'a>>> }
    static member Create(initialValue: 'a) =
        { Value = initialValue
          LockState = LockState.Default
          AwaitingReaders = ImmutableQueue.Empty
          AwaitingWriters = ImmutableQueue.Empty }

/// A user-space, reentrant read/write lock. Multiple readers may read the corresponding value
/// obtained via `lock.Read()` handles at the same time. Only one task is allowed
/// to have a write access at the same time (obtained either via `lock.Write()`)
/// or by upgrading the read handle. In this case all readers mult be disposed first.
and [<Sealed; NoComparison; NoEquality>] RWLock<'a>(initialValue: 'a) =
    let state = AtomicRef<_>(State<_>.Create initialValue)
    
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let throwIfDisposed() : unit =
        if obj.ReferenceEquals(null, !state) then raise LockUtils.disposedException
    
    member internal this.GetValue() =
        let s = !state
        if obj.ReferenceEquals(null, s) then raise LockUtils.disposedException
        else s.Value
    member internal this.SetValue(value: 'a) =
        let rec loop state value =
            let old = !state
            if obj.ReferenceEquals(null, old) then raise LockUtils.disposedException
            elif Atomic.cas old { old with Value = value } state then ()
            else loop state value
        loop state value
    member internal this.AcquireRead(id: int, ct: CancellationToken): LockAwaiter<_> voption =
        let rec attempt (state: AtomicRef<_>) (id: int) (ct: CancellationToken) =
            if ct.IsCancellationRequested then raise (OperationCanceledException(ct))
            else
                let old = !state
                let updated = old.LockState.AdjustRead(id, 1)
                if obj.ReferenceEquals(old.LockState, updated) then
                    // another task holds a write lock
                    let token = LockUtils.asToken id
                    let handle = new ReadHandle<'a>(this, id)          
                    let awaiter = LockAwaiter<ReadHandle<'a>>.Rent()
                    awaiter.Reset(handle, token, ct)
                    let queued = old.AwaitingReaders.Enqueue awaiter
                    if Atomic.cas old { old with AwaitingReaders = queued } state then ValueSome awaiter
                    else
                        LockAwaiter<_>.Return awaiter
                        attempt state id ct
                elif Atomic.cas old { old with LockState = updated } state then ValueNone
                else attempt state id ct
        attempt state id ct
    member internal this.AcquireWrite(id: int, ct: CancellationToken): LockAwaiter<_> voption =
        let rec attempt (state: AtomicRef<_>) (id: int) (ct: CancellationToken) =
            if ct.IsCancellationRequested then raise (OperationCanceledException(ct))
            else
                let old = !state
                if old.LockState.IsOnlyHolder id then
                    let updated =
                        match old.LockState with
                        | Read map ->
                            let reads =
                                let (ok, r) = map.TryGetValue(id)
                                if ok then r else 0
                            Write(1, reads, id)
                        | Write(w, r, id) -> Write(w+1, r, id)
                    if Atomic.cas old { old with LockState = updated } state then
                        ValueNone
                    else attempt state id ct
                else 
                    // another task holds a lock
                    let token = LockUtils.asToken id
                    let handle = new WriteHandle<'a>(this, id)          
                    let awaiter = LockAwaiter<WriteHandle<'a>>.Rent()
                    awaiter.Reset(handle, token, ct)
                    let queued = old.AwaitingWriters.Enqueue awaiter
                    if Atomic.cas old { old with AwaitingWriters = queued } state then ValueSome awaiter
                    else
                        LockAwaiter<_>.Return awaiter
                        attempt state id ct
        attempt state id ct
    member internal this.ReleaseRead(id: int) =
        let rec attempt (state: AtomicRef<_>) (id: int) =
            let old = !state
            let mutable updated = old.LockState.AdjustRead(id, -1)
            if updated.IsReadLocked then
                if Atomic.cas old { old with LockState = updated } state then ()
                else attempt state id
            else
                // read and write locks are released, we can try to acquire write now
                if old.AwaitingWriters.IsEmpty then
                    // no awaiting writers, try release all readers
                    let readers = old.AwaitingReaders
                    for awaiter in readers do
                        updated <- updated.AdjustRead(awaiter.handle.LockId, 1)
                    if Atomic.cas old { old with LockState = updated; AwaitingReaders = ImmutableQueue.Empty } state then
                        for awaiter in readers do awaiter.Complete()
                    else attempt state id
                else
                    let (modified, awaiter) = old.AwaitingWriters.Dequeue()
                    updated <- Write(1, 0, awaiter.handle.LockId)
                    if Atomic.cas old { old with LockState = updated; AwaitingWriters = modified } state then
                        awaiter.Complete()
                    else attempt state id
        attempt state id
    member internal this.ReleaseWrite(id: int) =
        let rec attempt (state: AtomicRef<_>) (id: int) =
            let old = !state
            let mutable updated =
                match old.LockState with
                | Write(w, r, ownerId) when ownerId = id ->
                    let writes = w - 1
                    if writes < 0 then failwithf "Bug: task %i released write lock that was not held." id
                    elif writes = 0 then
                        Read (if r = 0 then Map.empty else Map.add id r Map.empty)
                    else Write(writes, r, ownerId)
                | other -> other
            if updated.IsReadLocked || old.AwaitingWriters.IsEmpty then
                // no awaiting writers, try release all readers
                let readers = old.AwaitingReaders
                for awaiter in readers do
                    updated <- updated.AdjustRead(awaiter.handle.LockId, 1)
                if Atomic.cas old { old with LockState = updated; AwaitingReaders = ImmutableQueue.Empty } state then
                    for awaiter in readers do awaiter.Complete()
                else attempt state id
            else
                // all locks have been released, try complete next awaiting writer writer
                let (modified, awaiter) = old.AwaitingWriters.Dequeue()
                updated <- Write(1, 0, awaiter.handle.LockId)
                if Atomic.cas old { old with LockState = updated; AwaitingWriters = modified } state then
                    awaiter.Complete()
                else attempt state id
        attempt state id
    
    /// Obtain a read-only lock handle. In case when it's not immediately possible eg.
    /// because another task is holding write lock, returned value task will allow to
    /// asynchronously await for a lock to be obtained.
    member this.Read(?cancellationToken: CancellationToken): ValueTask<ReadHandle<'a>> =
        throwIfDisposed()
        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
        let taskId = LockUtils.lockId()
        match this.AcquireRead(taskId, cancellationToken) with
        | ValueNone -> ValueTask<_>(new ReadHandle<'a>(this, taskId))
        | ValueSome awaiter -> ValueTask<_>(awaiter, awaiter.token)
        
    /// Obtain a read-write lock handle. In case when it's not immediately possible eg.
    /// because another task is holding either read or write lock, returned value task
    /// will allow to asynchronously await for a lock to be obtained.
    member this.Write(?cancellationToken: CancellationToken): ValueTask<WriteHandle<'a>> =
        throwIfDisposed()
        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
        let taskId = LockUtils.lockId()
        match this.AcquireWrite(LockUtils.lockId(), cancellationToken) with
        | ValueNone -> ValueTask<_>(new WriteHandle<'a>(this, taskId))
        | ValueSome awaiter -> ValueTask<_>(awaiter, awaiter.token)
    member this.IsDisposed = obj.ReferenceEquals(null, !state)
    member this.Dispose() =
        let old = state := Unchecked.defaultof<_>
        for awaiter in old.AwaitingReaders do awaiter.Fail()
        for awaiter in old.AwaitingWriters do awaiter.Fail()
    interface IDisposable with member this.Dispose() = this.Dispose()

and [<Struct; NoComparison; NoEquality>] WriteHandle<'a> internal(lock: RWLock<'a>, lockId: int) =
    member this.LockId: int = lockId
    member this.Value
        with [<MethodImpl(MethodImplOptions.AggressiveInlining)>] get () = lock.GetValue()
        and [<MethodImpl(MethodImplOptions.AggressiveInlining)>] set value = lock.SetValue value
    member this.Dispose() = lock.ReleaseWrite(lockId)
    interface IDisposable with member this.Dispose() = this.Dispose()
    
and [<Struct; IsReadOnly; NoComparison; NoEquality>] ReadHandle<'a> internal(lock: RWLock<'a>, lockId: int) =
    member this.LockId: int = lockId
    member this.Value with [<MethodImpl(MethodImplOptions.AggressiveInlining)>] get () = lock.GetValue()
    
    /// Upgrades current read lock into write lock, returning new (writable) lock handle.
    /// This lock still can be used for reads until it's disposed.
    member this.Upgrade(?cancellationToken: CancellationToken): ValueTask<WriteHandle<'a>> =
        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
        match lock.AcquireWrite(LockUtils.lockId(), cancellationToken) with
        | ValueNone -> ValueTask<_>(new WriteHandle<'a>(lock, lockId))
        | ValueSome awaiter -> ValueTask<_>(awaiter, awaiter.token)
    member this.Dispose() = lock.ReleaseRead(lockId)
    interface IDisposable with member this.Dispose() = this.Dispose()
    
[<RequireQualifiedAccess>]
module RWLock =
    
    /// Create a new instance of user-spec, asynchronous, read/write lock over specified
    /// value. Access to this value will be guarded within that lock.
    let inline reentrant (initialValue: 'a) = new RWLock<'a>(initialValue)
    
    /// Acquire read lock.
    let inline read (lock: RWLock<'a>) = lock.Read()
    
    /// Acquire write lock.
    let inline write (lock: RWLock<'a>) = lock.Write()
    
    /// Returns a lock identifier used to track continuity of current task's scope.
    let currentLockId () = LockUtils.lockId ()
