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
open System.Collections.Generic
open System.Collections.Immutable
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open System.Threading.Tasks.Sources
open FSharp.Core
open FSharp.Core.Atomic.Operators

type LockId = int

module private LockUtils =
    let counter = atom 0
    let current: AsyncLocal<LockId> = AsyncLocal()
    let inline lockId () =
        if current.Value = 0 then
            current.Value <- Atomic.incr counter 
        current.Value
        
    let inline dispose (d: #IDisposable) = d.Dispose()
        
    let disposedException = ObjectDisposedException "RWLock has been disposed"
    
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let raiseDisposedException () = raise disposedException

type private LockState =
    | Write of writeLocks:int * readLocks:int * ownerId:LockId
    | Read of Map<LockId, int>
    static member Default = Read Map.empty
    member this.ReadLocks(id: LockId) =
        match this with
        | Read map ->
            match map.TryGetValue id with
            | true, i -> i
            | false, _ -> 0
        | Write(_, readLocks, ownerId) when ownerId = id -> readLocks
        | _ -> 0
    member this.WriteLocks(id: LockId) =
        match this with
        | Write(writeLocks, _, ownerId) when ownerId = id -> writeLocks
        | _ -> 0
    member this.IsOnlyHolder(id: LockId) =
        match this with
        | Read map when Map.isEmpty map || (map.Count = 1 && Map.containsKey id map) -> true
        | Write(_,_, ownerId) when ownerId = id -> true
        | _ -> false
    member this.AdjustRead(id: LockId, delta: int) =
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

[<Struct>]
type private Pending<'t> =
    { id: LockId
      awaiter: TaskCompletionSource<'t>
      cancel: CancellationTokenRegistration option }
    member this.Dispose() =
        this.cancel |> Option.iter LockUtils.dispose
        this.awaiter.TrySetException(LockUtils.disposedException) |> ignore

type private State<'value> =
    { value: 'value
      stm: LockState
      readerQ: ImmutableQueue<Pending<ReadLockAcquisition<'value>>>
      writerQ: ImmutableQueue<Pending<WriteLockAcquisition<'value>>> }
    static member Create(initialValue: 'value) =
        { value = initialValue
          stm = LockState.Default
          readerQ = ImmutableQueue.Empty
          writerQ = ImmutableQueue.Empty }

/// A user-space, reentrant read/write lock. Multiple readers may read the corresponding value
/// obtained via `lock.Read()` handles at the same time. Only one task is allowed
/// to have a write access at the same time (obtained either via `lock.Write()`)
/// or by upgrading the read handle. In this case all readers mult be disposed first.
and [<Sealed; NoComparison; NoEquality>] RWLock<'a>(initialValue: 'a) =
    static let cancelWrite: Action<obj, CancellationToken> = Action<_,_>(fun o ct ->
        let promise = o :?> TaskCompletionSource<WriteLockAcquisition<'a>>
        promise.TrySetCanceled(ct) |> ignore)
    
    static let cancelRead: Action<obj, CancellationToken> = Action<_,_>(fun o ct ->
        let promise = o :?> TaskCompletionSource<ReadLockAcquisition<'a>>
        promise.TrySetCanceled(ct) |> ignore)
    
    let state = AtomicRef<_>(State.Create initialValue)
        
    static let rec pollNonCancelled (q: ImmutableQueue<Pending<_>> byref, v: _ outref) : bool =
        if q.IsEmpty then false
        else
            let mutable pending = Unchecked.defaultof<_>
            let updatedQ = q.Dequeue &pending
            q <- updatedQ
            match pending.cancel with
            | Some c when c.Token.IsCancellationRequested -> pollNonCancelled(&q, &v)
            | _ ->
                v <- pending
                true

    member internal this.GetValue() =
        let s = !state
        if obj.ReferenceEquals(null, s) then LockUtils.raiseDisposedException ()
        else s.value
        
    member internal this.SetValue(value: 'a) =
        let rec loop state value =
            let old = !state
            if obj.ReferenceEquals(null, old) then LockUtils.raiseDisposedException ()
            elif Atomic.cas old { old with value = value } state then ()
            else loop state value
        loop state value
    member internal this.AcquireRead(id: LockId, cancellationToken: CancellationToken): TaskCompletionSource<_> voption =
        let rec attempt (state: AtomicRef<_>) (id: LockId) (ct: CancellationToken) =
            if ct.IsCancellationRequested then raise (OperationCanceledException(ct))
            else
                let old = !state
                let updated = old.stm.AdjustRead(id, 1)
                if obj.ReferenceEquals(old.stm, updated) then
                    // another task holds a write lock
                    let awaiter = TaskCompletionSource<_>()
                    let reg =
                        if cancellationToken.CanBeCanceled then
                            Some (cancellationToken.Register(cancelRead, box awaiter))
                        else None
                    let readerQ = old.readerQ.Enqueue { id = id; awaiter = awaiter; cancel = reg }
                    if Atomic.cas old { old with readerQ = readerQ } state
                    then ValueSome awaiter
                    else
                        reg |> Option.iter LockUtils.dispose
                        attempt state id cancellationToken
                elif Atomic.cas old { old with stm = updated } state then ValueNone
                else attempt state id ct
        attempt state id cancellationToken
    member internal this.AcquireWrite(id: LockId, cancellationToken: CancellationToken): TaskCompletionSource<_> voption =
        let rec attempt (state: AtomicRef<_>) (id: LockId) (ct: CancellationToken) =
            if ct.IsCancellationRequested then raise (OperationCanceledException(ct))
            else
                let old = !state
                if old.stm.IsOnlyHolder id then
                    let updated =
                        match old.stm with
                        | Read map ->
                            let reads =
                                let (ok, r) = map.TryGetValue(id)
                                if ok then r else 0
                            Write(1, reads, id)
                        | Write(w, r, id) -> Write(w+1, r, id)
                    if Atomic.cas old { old with stm = updated } state then
                        ValueNone
                    else attempt state id ct
                else 
                    // another task holds a lock
                    let awaiter = TaskCompletionSource<_>()
                    let reg =
                        if cancellationToken.CanBeCanceled then
                            Some (cancellationToken.Register(cancelWrite, box awaiter))
                        else None
                    let writerQ = old.writerQ.Enqueue { id = id; awaiter = awaiter; cancel = reg }
                    if Atomic.cas old { old with writerQ = writerQ } state
                    then ValueSome awaiter
                    else
                        reg |> Option.iter LockUtils.dispose
                        attempt state id cancellationToken
        attempt state id cancellationToken
    member internal this.ReleaseRead(id: LockId) =
        let rec attempt (state: AtomicRef<_>) (id: LockId) =
            let old = !state
            let mutable updated = old.stm.AdjustRead(id, -1)
            if updated.IsReadLocked then
                if Atomic.cas old { old with stm = updated } state then ()
                else attempt state id
            else
                let mutable writers = old.writerQ
                let mutable pending = Unchecked.defaultof<_>
                // read and write locks are released, we can try to acquire write now
                if not (pollNonCancelled(&writers, &pending)) then
                    // no awaiting writers, try release all readers
                    this.ReleaseAllReaders ()
                else
                    // we have awaiting writer
                    if Atomic.cas old { old with stm = Write(1, 0, pending.id); writerQ = writers } state then
                        if pending.awaiter.TrySetResult(new WriteLockAcquisition<_>(this, pending.id)) then
                            // task was successfully completed
                            pending.cancel |> Option.iter LockUtils.dispose
                        else
                            // task was cancelled before we managed to complete it
                            // we need to revert write acquisition
                            this.ReleaseWrite(pending.id)
                    else attempt state id
        attempt state id            
    member internal this.ReleaseAllReaders () =
        let rec loop (state: AtomicRef<_>) =
            let old = !state
            if not old.readerQ.IsEmpty && not old.stm.IsWriteLocked then
                let mutable pending = Unchecked.defaultof<_>
                let readerQ = old.readerQ.Dequeue &pending
                let updated = old.stm.AdjustRead(pending.id, 1)
                if not (obj.ReferenceEquals(old.stm, updated)) then
                    // we successfully acquired read
                    if Atomic.cas old { old with readerQ = readerQ; stm = updated } state then
                        if pending.awaiter.TrySetResult(new ReadLockAcquisition<'a>(this, pending.id)) then
                            // successfully completed reader lock task acquisition, try to complete others
                            pending.cancel |> Option.iter LockUtils.dispose
                            loop state
                        else
                            // reader lock acquisition task failed (probably task was already cancelled)
                            // revert read adjustment
                            state |> Atomic.update (fun s -> { s with stm = s.stm.AdjustRead(pending.id, -1) }) |> ignore
                            loop state
                    else
                        // we failed to update - retry
                        loop state
        loop state
        
    member internal this.ReleaseWrite(id: LockId) =
        let rec attempt (state: AtomicRef<_>) (id: LockId) =
            let old = !state
            let mutable updated =
                match old.stm with
                | Write(w, r, ownerId) when ownerId = id ->
                    let writes = w - 1
                    if writes < 0 then failwithf "Bug: task %i released write lock that was not held." id
                    elif writes = 0 then
                        Read (if r = 0 then Map.empty else Map.add id r Map.empty)
                    else Write(writes, r, ownerId)
                | other -> other
            // read lock is not held, so try to find awaiting writer than can be completed
            let mutable writers = old.writerQ
            let mutable pending = Unchecked.defaultof<_>
            if not updated.IsReadLocked && pollNonCancelled(&writers, &pending) then
                // all locks have been released, try complete next awaiting writer
                updated <- Write(1, 0, pending.id)
                if Atomic.cas old { old with stm = updated; writerQ = writers } state then
                    if pending.awaiter.TrySetResult(new WriteLockAcquisition<_>(this, pending.id)) then
                        // task was successfully completed
                        pending.cancel |> Option.iter LockUtils.dispose
                    else
                        // task was cancelled before we managed to complete it
                        // we need to revert write acquisition
                        attempt state pending.id       
                else attempt state id
            elif Atomic.cas old { old with stm = updated } state then
                this.ReleaseAllReaders()
            else
                attempt state id
        attempt state id
    
    /// Obtain a read-only lock handle. In case when it's not immediately possible eg.
    /// because another task is holding write lock, returned value task will allow to
    /// asynchronously await for a lock to be obtained.
    member this.Read(?cancellationToken: CancellationToken): ValueTask<ReadLockAcquisition<'a>> =
        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
        let taskId = LockUtils.lockId()
        match this.AcquireRead(taskId, cancellationToken) with
        | ValueNone -> ValueTask<ReadLockAcquisition<'a>>(new ReadLockAcquisition<'a>(this, taskId))
        | ValueSome awaiter -> ValueTask<ReadLockAcquisition<'a>>(awaiter.Task)
        
    /// Obtain a read-write lock handle. In case when it's not immediately possible eg.
    /// because another task is holding either read or write lock, returned value task
    /// will allow to asynchronously await for a lock to be obtained.
    member this.Write(?cancellationToken: CancellationToken): ValueTask<WriteLockAcquisition<'a>> =
        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
        let taskId = LockUtils.lockId()
        match this.AcquireWrite(LockUtils.lockId(), cancellationToken) with
        | ValueNone -> ValueTask<WriteLockAcquisition<'a>>(new WriteLockAcquisition<'a>(this, taskId))
        | ValueSome awaiter -> ValueTask<WriteLockAcquisition<'a>>(awaiter.Task)
    member this.IsDisposed = obj.ReferenceEquals(null, !state)
    member this.Dispose() =
        let old = state := Unchecked.defaultof<_>
        for pending in old.readerQ do pending.Dispose()
        for pending in old.writerQ do pending.Dispose()
    interface IDisposable with member this.Dispose() = this.Dispose()

and [<Struct; NoComparison; NoEquality>] WriteLockAcquisition<'a> internal(lock: RWLock<'a>, lockId: LockId) =
    member this.LockId: LockId = lockId
    member this.Value
        with [<MethodImpl(MethodImplOptions.AggressiveInlining)>] get () = lock.GetValue()
        and [<MethodImpl(MethodImplOptions.AggressiveInlining)>] set value = lock.SetValue value
    member this.Dispose() = lock.ReleaseWrite(lockId)
    interface IDisposable with member this.Dispose() = this.Dispose()
    
and [<Struct; IsReadOnly; NoComparison; NoEquality>] ReadLockAcquisition<'a> internal(lock: RWLock<'a>, lockId: LockId) =
    member this.LockId: LockId = lockId
    //TODO: maybe Value should return inref, to prevent it from being closure-captured between tasks (outside of lock jurisdiction)?  
    member this.Value with [<MethodImpl(MethodImplOptions.AggressiveInlining)>] get () = lock.GetValue()
    
    /// Upgrades current read lock into write lock, returning new (writable) lock handle.
    /// This lock still can be used for reads until it's disposed.
    member this.Upgrade(?cancellationToken: CancellationToken): ValueTask<WriteLockAcquisition<'a>> =
        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
        match lock.AcquireWrite(LockUtils.lockId(), cancellationToken) with
        | ValueNone -> ValueTask<_>(new WriteLockAcquisition<'a>(lock, lockId))
        | ValueSome awaiter -> ValueTask<WriteLockAcquisition<'a>>(awaiter.Task)
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
