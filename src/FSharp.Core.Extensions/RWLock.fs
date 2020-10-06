namespace FSharp.Core

open System
open System.Threading
open System.Threading.Tasks

module internal LockUtils =
    
    let currentTaskId () =
        let taskId = Task.CurrentId
        if taskId.HasValue then taskId.Value
        else failwith "RWLock operation must be executed in context of wrapping task"

[<NoComparison;ReferenceEquality>]
type internal RWLockState =
    | ReadLock of holders:Map<int, int> * waitQ:Promise<unit> list
    | WriteLock of writeHold:int * readHold:int * holder:int * waitQ:Promise<unit> list
    static member Default = ReadLock(Map.empty,[])
    member this.NoOtherHolder(taskId: int) : bool =
        match this with
        | ReadLock(holders, _) -> Map.isEmpty holders || (holders.Count = 1 && Map.containsKey taskId holders)
        | WriteLock(_,_,holder, _) -> holder = taskId
        
    member this.AdjustReaders(taskId: int, count: int, promise: Promise<unit> outref) =
        match this with
        | ReadLock(holders, waitQ) ->
            let readLocks =
                let ok, x = holders.TryGetValue taskId
                if ok then x else 0
            let readLocks' = readLocks + count
            if readLocks' < 0 then failwithf "Defect: Task %i is trying to release a lock it doesn't hold." taskId
            elif readLocks' = 0 then ReadLock(Map.remove taskId holders, waitQ)
            else ReadLock(Map.add taskId readLocks' holders, waitQ)
        | WriteLock(writers,readers,holder, waitQ) when holder = taskId ->
            let readers' = readers + count
            if readers' < 0 then failwithf "Defect: Task %i is trying to release a lock it doesn't hold." taskId
            elif readers' = 0 then
                match waitQ with
                | [] -> RWLockState.Default
                | h::t ->
                    h.SetResult(())
                    ReadLock(Map.empty, t)                    
            else WriteLock (writers, readers', holder, waitQ)
        | WriteLock(writers,readers,holder,waitQ) ->
            promise <- Promise()
            WriteLock(writers, readers, holder, promise::waitQ)
            
    member this.AcquireWrite(taskId: int, promise: Promise<unit> outref) =
        match this with
        | ReadLock(holders, waitQ) when this.NoOtherHolder(taskId) ->
            let readLocks =
                let ok, count = holders.TryGetValue taskId
                if ok then count else 0
            WriteLock(1, readLocks, taskId, waitQ)
        | ReadLock(holders, waitQ) ->
            promise <- Promise()
            ReadLock(holders, promise::waitQ)
        | WriteLock(writers, readers, holder, waitQ) when holder = taskId ->
            WriteLock(writers + 1, readers, holder, waitQ)
        | WriteLock(writers, readers, holder, waitQ) ->
            promise <- Promise()
            WriteLock(writers, readers, holder, promise::waitQ)

[<Sealed>]
type RWLock<'a> internal(value: 'a ref) =
    let mutable state = RWLockState.Default
    member this.Dispose() = failwith "not impl"
    member this.Reader(?token: CancellationToken) : ValueTask<ReadLockHandle<'a>> =
        let taskId = LockUtils.currentTaskId ()
        let mutable promise = Unchecked.defaultof<_>
        let old = Volatile.Read &state
        let updated = Interlocked.CompareExchange(&state, old.AdjustReaders(taskId, 1, &promise), old)
        if updated = old then
            match promise with
            | null -> ValueTask<_>(new ReadLockHandle<'a>(this))
            | promise -> uvtask {
                do! promise.Task
                return! this.Reader(token)
            }
        else failwith "TODO"
    member this.Writer(?token: CancellationToken) : ValueTask<WriteLockHandle<'a>> = failwith "not impl"
    interface IDisposable with member this.Dispose() = this.Dispose()
    
and [<Struct>] ReadLockHandle<'a> internal(lock: RWLock<'a>) =
    member this.Value with get () = failwith "not impl"
    member this.Upgrade(?token: CancellationToken) : ValueTask<WriteLockHandle<'a>> = failwith "not impl"
    member this.Dispose() = failwith "not impl"
    interface IDisposable with member this.Dispose() = this.Dispose()
    
and [<Struct>] WriteLockHandle<'a> internal(lock: RWLock<'a>) =
    member this.Value
        with get () = failwith "not impl"
        and set value = failwith "not impl"
    member this.Dispose() = failwith "not impl"
    interface IDisposable with member this.Dispose() = this.Dispose()
    
[<RequireQualifiedAccess>]
module RWLock =
    
    let reentrant (value: 'a) = new RWLock<'a>(ref value)