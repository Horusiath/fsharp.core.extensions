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
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Threading
open FSharp.Control.Tasks
open FSharp.Control.Tasks.Affine.Unsafe
open System.Threading.Channels
open System.Threading.Tasks

type AsyncSeq<'a> = IAsyncEnumerable<'a>

[<RequireQualifiedAccess>]
module AsyncSeq =
    
    let inline private rethrow (e: exn) =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<_>
        
    let cancelled (cancel: CancellationToken) = ValueTask<bool>(Task.FromCanceled<bool>(cancel))
        
    /// Modifies provided async sequence into the one which will apply
    /// a provided cancellation token upon async enumerator creation.
    let withCancellation (cancel: CancellationToken) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator _ = upstream.GetAsyncEnumerator(cancel) }
       
    /// Returns an empty async sequence.
    let empty<'a> : AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                { new IAsyncEnumerator<'a> with
                    member __.Current = failwith "AsyncEnumerator is empty"
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() =
                        if cancel.IsCancellationRequested then cancelled cancel else ValueTask<_>(false) } }
        
    /// Returns the same `value` over and over until a cancellation token triggers.
    let repeat (value: 'a): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                { new IAsyncEnumerator<'a> with
                    member __.Current = value
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() =
                        if cancel.IsCancellationRequested then cancelled cancel
                        else ValueTask<_>(true) } }
        
    let private timerCallback = TimerCallback(fun state ->
        let promise = state :?> TaskCompletionSource<unit> ref
        (!promise).TrySetResult(()) |> ignore
    )
        
    /// Returns the stream, that will produce new element in given time intervals.
    /// Produced value is the time that has passed since previous completion of MoveNextAsync enumerator call.
    let timer (interval: TimeSpan): AsyncSeq<TimeSpan> =
        { new AsyncSeq<TimeSpan> with
            member __.GetAsyncEnumerator (cancel) =
                let stopwatch = System.Diagnostics.Stopwatch.StartNew()
                let promise = ref (TaskCompletionSource<unit>())
                let timer = new Timer(timerCallback, promise, interval, interval)
                let mutable current = Unchecked.defaultof<_>
                { new IAsyncEnumerator<TimeSpan> with
                    member __.Current = current
                    member __.DisposeAsync() =
                        stopwatch.Stop()
                        timer.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            do! (!promise).Task
                            promise := TaskCompletionSource<unit>()
                            current <- stopwatch.Elapsed
                            stopwatch.Restart()
                            return true
                        } } }
        
    /// Returns a task, which will pull all incoming elements from a given sequence
    /// or until token has cancelled, and pushes them to a given channel writer.
    let into (closeChannelOnComplete: bool) (chan: ChannelWriter<'a>) (upstream: AsyncSeq<'a>) = vtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! hasNext = e.MoveNextAsync()
            let mutable hasNext' = hasNext
            while hasNext' do
                do! chan.WriteAsync(e.Current)
                let! hasNext = e.MoveNextAsync()
                hasNext' <- hasNext
            do! e.DisposeAsync()
            if closeChannelOnComplete then
                chan.Complete()
        with ex ->
            do! e.DisposeAsync()
            if closeChannelOnComplete then
                chan.TryComplete(ex) |> ignore
            return rethrow ex
    }
            
    /// Tries to return the first element of the async sequence,
    /// returning None if sequence is empty or cancellation was called.
    let tryHead (upstream: AsyncSeq<'a>) = vtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! hasNext = e.MoveNextAsync()
            if hasNext then
                do! e.DisposeAsync()
                return Some e.Current
            else
                do! e.DisposeAsync()
                return None
        with ex ->
            do! e.DisposeAsync()
            return rethrow ex
    }
    
    /// Tries to return the last element of the async sequence,
    /// returning None if sequence is empty or cancellation was called.
    let tryLast (upstream: AsyncSeq<'a>) = vtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! hasNext = e.MoveNextAsync()
            let mutable hasNext' = hasNext
            if not hasNext' then
                do! e.DisposeAsync()
                return None
            else
                let mutable last = e.Current
                while hasNext' do
                    last <- e.Current
                    let! hasNext = e.MoveNextAsync()
                    hasNext' <- hasNext
                do! e.DisposeAsync()
                return Some last
        with ex ->
            do! e.DisposeAsync()
            return rethrow ex
    } 

    /// Collects all of the incoming events into a single sequence, which is then
    /// returned, once async sequence completes.
    let collect (upstream: AsyncSeq<'a>): ValueTask<'a[]> = vtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! hasNext = e.MoveNextAsync()
            let mutable hasNext' = hasNext
            let mutable result = ResizeArray<_>()
            while hasNext' do
                result.Add (e.Current)
                let! hasNext = e.MoveNextAsync()
                hasNext' <- hasNext
            do! e.DisposeAsync()
            return result.ToArray()
        with ex ->
            do! e.DisposeAsync()
            return rethrow ex
    }
    
    /// Asynchronously folds over incoming elements, continuously constructing
    /// the output state (starting from provided seed) and returning it eventually
    /// when a sequence will complete. 
    let fold (f: 's -> 'a -> ValueTask<'s>) (seed: 's) (upstream: AsyncSeq<'a>) = vtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! cont = e.MoveNextAsync()
            let mutable state = seed
            let mutable hasNext = cont
            while hasNext do
                let! state' = f state e.Current
                state <- state'
                let! cont = e.MoveNextAsync()
                hasNext <- cont
            return state
        with ex ->
            do! e.DisposeAsync()
            return rethrow ex
    }
    
    /// Stream the result as long as unfolding function `f` returns Some.
    let unfold (f: 's -> ValueTask<ValueTuple<'s,'a> voption>) (seed: 's) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable current = Unchecked.defaultof<_>
                let mutable state = seed
                { new IAsyncEnumerator<'a> with
                    member __.Current = current
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() = vtask {
                        match! f state with
                        | ValueNone -> return false
                        | ValueSome(struct(state', item)) ->
                            state <- state'
                            current <- item
                            return true
                    } }}
    
    /// Asynchronously folds over incoming elements, continuously constructing
    /// the output state (or returning with none if sequence had no elements)
    /// and returning it eventually when a sequence will complete. 
    let reduce (f: 'a -> 'a -> ValueTask<'a>) (upstream: AsyncSeq<'a>) = vtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! cont = e.MoveNextAsync()
            if not cont then return None
            else
                let mutable state = e.Current
                let! cont = e.MoveNextAsync()
                let mutable hasNext = cont
                while hasNext do
                    let! state' = f state e.Current
                    state <- state'
                    let! cont = e.MoveNextAsync()
                    hasNext <- cont
                return Some state
        with ex ->
            do! e.DisposeAsync()
            return rethrow ex
    }
    
    /// Iterates over all elements of provided async sequence - or until a
    /// cancellation is called - and executes given action over every one
    /// of them with an incrementally growing counter.
    let iteri (f: int64 -> 'a -> ValueTask) (upstream: AsyncSeq<'a>) = unitVtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! cont = e.MoveNextAsync()
            let mutable hasNext = cont
            let mutable i = 0L
            while hasNext do
                do! f i e.Current
                i <- i + 1L
                let! cont = e.MoveNextAsync()
                hasNext <- cont
            do! e.DisposeAsync()
        with ex ->
            do! e.DisposeAsync()
            rethrow ex
    }
    
    /// Iterates over all elements of provided async sequence - or until a cancellation
    /// is called - and executes given action over every one of them.
    let inline iter (f: 'a -> ValueTask) upstream = iteri (fun _ e -> f e) upstream
        
    [<Sealed>]
    type private MapParallelEnumerator<'a, 'b>(parallelism: int, e: IAsyncEnumerator<'a>, cancel:CancellationToken, map: 'a -> ValueTask<'b>) =
        let (iwriter, ireader) = Channel.boundedSpmc<'a>(parallelism)
        let (owriter, oreader) = Channel.boundedMpmc<'b>(parallelism)
        let mutable current = Unchecked.defaultof<_>
        let mutable remaining = parallelism
        let workers = Array.init parallelism (fun i -> unitTask {
            try
                let mutable contRead = not cancel.IsCancellationRequested
                while contRead do
                    let ok, next = ireader.TryRead()
                    if ok then
                        let! o = map next
                        let mutable contWrite = not cancel.IsCancellationRequested
                        while contWrite do
                            if owriter.TryWrite(o) then 
                                contWrite <- false
                            else
                                let! canWrite = owriter.WaitToWriteAsync(cancel)
                                contWrite <- not cancel.IsCancellationRequested && canWrite
                    else
                        let! canRead = ireader.WaitToReadAsync(cancel)
                        contRead <- not cancel.IsCancellationRequested && canRead
                if Interlocked.Decrement(&remaining) = 0 then
                    owriter.TryComplete() |> ignore
            with err ->
                owriter.TryComplete(err) |> ignore
        })
        let dispatcher = Task.Run(Func<Task>(fun () -> unitTask {
            try
                let mutable cont = not cancel.IsCancellationRequested 
                while cont do
                    let! hasNext = e.MoveNextAsync()
                    if hasNext then
                        do! iwriter.WriteAsync(e.Current, cancel)
                    cont <- hasNext
                iwriter.TryComplete() |> ignore
            with err ->
               iwriter.TryComplete(err) |> ignore 
        }))
        interface IAsyncEnumerator<'b> with
            member __.Current = current
            member __.DisposeAsync() = unitVtask {
                try dispatcher.Dispose() with _ -> ()
                for worker in workers do
                    try worker.Dispose() with _ -> ()
                iwriter.TryComplete() |> ignore
            }
            member __.MoveNextAsync() = 
                if cancel.IsCancellationRequested then cancelled cancel
                elif oreader.TryRead(&current) then ValueTask<_>(true)
                elif Volatile.Read(&remaining) = 0 then ValueTask<_>(false)
                else uvtask {
                    let! ready = oreader.WaitToReadAsync(cancel)
                    if ready && oreader.TryRead(&current) then 
                        return true
                    else                      
                        return false
                }
    
    /// Iterates over all elements of provided async sequence in parallel.
    let mapParallel (parallelism: int) (f: 'a -> ValueTask<'b>) (upstream: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                upcast MapParallelEnumerator<'a, 'b>(parallelism, upstream.GetAsyncEnumerator(cancel), cancel, f) }
            
    /// Returns a new async sequence constructed from provided one transformed by applying
    /// mapping function (with monotonically increasing counter) over all input elements.
    let mapAsynci (f: int64 -> 'a -> ValueTask<'b>) (upstream: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let current = ref Unchecked.defaultof<_>
                let mutable counter = 0L
                { new IAsyncEnumerator<'b> with
                    member __.Current = !current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            let! cont = inner.MoveNextAsync()
                            if cont then
                                let! b = f counter inner.Current
                                current := b
                                counter <- counter + 1L
                                return true
                            else return false
                        } }}
      
    /// Returns a new async sequence constructed from provided one transformed by applying
    /// mapping function over all input elements.  
    let inline mapAsync (f: 'a -> ValueTask<'b>) upstream = mapAsynci (fun _ a -> f a) upstream
    
    /// Returns a new async sequence constructed from provided one transformed by applying
    /// mapping function (with monotonically increasing counter) over all input elements.
    let mapi (f: int64 -> 'a -> 'b) (upstream: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable current = Unchecked.defaultof<_>
                let mutable counter = 0L
                { new IAsyncEnumerator<'b> with
                    member __.Current = current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            let! cont = inner.MoveNextAsync()
                            if cont then
                                current <- f counter inner.Current
                                counter <- counter + 1L
                                return true
                            else return false
                        } }}
      
    /// Returns a new async sequence constructed from provided one transformed by applying
    /// mapping function over all input elements.  
    let inline map (f: 'a -> 'b) (upstream: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable current = Unchecked.defaultof<_>
                { new IAsyncEnumerator<'b> with
                    member __.Current = current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        let! hasNext = inner.MoveNextAsync()
                        if hasNext then
                            current <- f inner.Current
                        return hasNext
                    } }}
    
    /// Creates an async sequence, that transforms provided inputs into outputs, potentially
    /// asynchronously, if the mapping function output will be some (none results will be skipped). 
    let choose (f: 'a -> ValueTask<'b option>) (upstream: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable current = None
                { new IAsyncEnumerator<'b> with
                    member __.Current = current.Value
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        let! hasNext = inner.MoveNextAsync()
                        let mutable innerHasMore = hasNext
                        let mutable cont = hasNext
                        while cont do
                            let! result = f inner.Current
                            current <- result
                            match result with
                            | Some _ ->
                                cont <- false
                            | None ->
                                let! hasNext = inner.MoveNextAsync()
                                innerHasMore <- hasNext
                                cont <- hasNext
                        return innerHasMore      
                    } }}
    
    /// Returns an async sequence which filters out the input async sequence elements,
    /// which have not satisfied given predicate.
    let filter (f: 'a -> bool) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        choose (fun a -> if f a then ValueTask<_>(Some a) else ValueTask<_> None) upstream
        
    /// Catches all errors that happened upstream and tries to map them into new output element.
    /// If function `f` returned None, stream will be completed gracefully, if that function fails,
    /// then stream will be finished abruptly.
    let recover (f: exn -> ValueTask<'a voption>) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable current = Unchecked.defaultof<_>
                { new IAsyncEnumerator<'a> with
                    member __.Current = current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        try
                            let! hasNext = inner.MoveNextAsync()
                            if hasNext then
                                current <- inner.Current
                                return true
                            else return false
                        with e ->
                            match! f e with
                            | ValueNone -> return false
                            | ValueSome v ->
                                current <- v
                                return true
                    } }}
        
    /// Creates an asynchronous sequence, that will forward elements comming from `upstream` until an exception
    /// occurs. In that case it will create a new AsyncSeq using function `f`, that will be used from now on
    /// to produce subsequent elements. If subsequent sequence will fail, function `f` will be used again to
    /// create new one, and so on recursively. If function `f` itself will throw an exception, this stage will fail as
    /// well.
    let onError (f: exn -> AsyncSeq<'a>) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable inner = upstream.GetAsyncEnumerator(cancel)
                let rec moveAsync () = vtask {
                    try
                        return! inner.MoveNextAsync()
                    with e ->
                        do! inner.DisposeAsync()
                        let next = f e
                        inner <- next.GetAsyncEnumerator(cancel)
                        return! moveAsync()
                }
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = moveAsync () }}

    [<Sealed>]
    type private MergeParallelEnumerator<'a>(enums: ResizeArray<IAsyncEnumerator<'a>>, cancel: CancellationToken) =
        let input = Channel.CreateBounded<'a>(1)
        let mutable current = Unchecked.defaultof<_>
        let active = atom true
        let tasks =
            enums
            |> Seq.map (fun e -> Task.Run(Func<Task>(fun () -> unitTask {
                let! hasNext = e.MoveNextAsync()
                let mutable hasNext' = hasNext
                while active.Value() && not cancel.IsCancellationRequested && hasNext' do
                    let current = e.Current
                    do! input.Writer.WriteAsync(current, cancel)
                    let! hasNext = e.MoveNextAsync()
                    hasNext' <- hasNext
                enums.Remove e |> ignore
                if enums.Count = 0 then input.Writer.TryComplete() |> ignore
            })))
            |> Seq.toArray
        interface IAsyncEnumerator<'a> with
            member __.Current = current
            member this.DisposeAsync() = unitVtask {
                if active.Swap false then
                    for e in enums do
                        do! e.DisposeAsync()
                    input.Writer.TryComplete() |> ignore
            }
            member this.MoveNextAsync() = 
                if cancel.IsCancellationRequested then cancelled cancel
                elif enums.Count = 0 then ValueTask<_>(false)
                else uvtask {
                    let! ready = input.Reader.WaitToReadAsync(cancel)
                    if ready then
                        let (has, next) = input.Reader.TryRead()
                        if has then
                            current <- next
                            return true
                        else return false
                    else return false
                }
    
    /// Merges a collection of async sequences together, emitting output of each one of them
    /// (obtained in parallel) as an input of result async sequence. Returned sequence will
    /// complete once all of the merged sequences will complete, and fail when any of the 
    /// underlying sequences fails.
    let mergeParallel (sequences: AsyncSeq<'a>[]): AsyncSeq<'a> = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let enums = sequences |> Array.map (fun s -> s.GetAsyncEnumerator(cancel)) |> ResizeArray
                upcast MergeParallelEnumerator<'a>(enums, cancel)
        }
            
    /// Merges together multiple async `sequences`, consuming one after another until they complete.
    let merge (sequences: AsyncSeq<'a> seq): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable seqs = sequences |> List.ofSeq
                let mutable head = Unchecked.defaultof<IAsyncEnumerator<'a>>
                let rec consume () = vtask {
                    match head with
                    | null ->
                        match seqs with
                        | [] -> return false
                        | h::t ->
                            head <- h.GetAsyncEnumerator(cancel)
                            seqs <- t
                            return! consume ()
                    | e ->
                        let! hasNext = e.MoveNextAsync()
                        if hasNext then return true
                        else
                            do! e.DisposeAsync()
                            head <- null
                            return! consume() }     
                { new IAsyncEnumerator<'a> with
                    member __.Current = head.Current
                    member __.DisposeAsync() = unitVtask {
                        if not (isNull head) then
                            do! head.DisposeAsync()
                    }
                    member __.MoveNextAsync() = consume()
                } }
     
    /// Given an async sequence which for a given input will produce the next sub-sequence
    /// and then returns an async sequence which flattens the results produced by sub-sequences
    /// until all sub-sequences produces will be completed.
    let bind (f: 'a -> AsyncSeq<'b>) (upstream: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let outer = upstream.GetAsyncEnumerator(cancel)
                let mutable inner = Unchecked.defaultof<IAsyncEnumerator<'b>>
                let rec next () = vtask {
                    let! hasNext = inner.MoveNextAsync()
                    if hasNext then return true
                    else
                        let! hasNextSeq = outer.MoveNextAsync()
                        if hasNextSeq then
                            inner <- (f outer.Current).GetAsyncEnumerator(cancel)
                            return! next()
                        else return false }
                { new IAsyncEnumerator<'b> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = unitVtask {
                        let mutable step = 0
                        try
                            do! outer.DisposeAsync()
                            step <- 1
                            if not (isNull inner) then
                                do! inner.DisposeAsync()
                            step <- 2
                        with ex ->
                            if step < 2 then
                                do! inner.DisposeAsync()
                            return rethrow ex
                    }
                    member __.MoveNextAsync() = vtask {
                        if isNull inner then                            
                            let! nextSeq = outer.MoveNextAsync()
                            if nextSeq then
                                inner <- (f outer.Current).GetAsyncEnumerator(cancel)
                                return! next ()
                            else return false
                        else return! next ()
                    } }}
    
    /// Modifies given async sequence into new one, which will skip incoming elements as long as predicate is satisfied.
    let skipWhile (f: 'a -> bool) (upstream: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable skip = true
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        let! hasNext = inner.MoveNextAsync()
                        let mutable hasNext' = hasNext
                        skip <- skip && f inner.Current
                        while hasNext' && skip do
                            let! hasNext = inner.MoveNextAsync()
                            hasNext' <- hasNext
                            if hasNext' then
                                skip <- skip && f inner.Current
                        return hasNext'
                    } }}
        
        
    let private skipWithinCallback = TimerCallback(fun state ->
        let c = state :?> AtomicBool
        (c.Swap false) |> ignore
    )
    
    /// Modifies given async sequence into new one, which will skip incoming elements until a given time has passed.
    let skipWithin (timeout: TimeSpan) (upstream: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let skip = atom (timeout > TimeSpan.Zero)
                let timer = new Timer(skipWithinCallback, skip, timeout, TimeSpan.Zero)
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() =
                        timer.Dispose()
                        inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        let mutable hasNext = not cancel.IsCancellationRequested
                        while hasNext && skip.Value() do
                            let! next = inner.MoveNextAsync()
                            hasNext <- next
                            
                        return! inner.MoveNextAsync()                        
                    } }}
        
    /// Modifies given async sequence into new one, which will skip first `n` elements.
    let skip (n: int64) (upstream: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable counter = n
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        if counter = 0L then return! inner.MoveNextAsync()
                        else
                            let mutable cont = not cancel.IsCancellationRequested
                            let mutable found = false
                            while not found do
                                let! hasNext = inner.MoveNextAsync()
                                found <- hasNext
                                counter <- counter - 1L
                                cont <- hasNext && counter > 0L
                            return found
                    } }}
        
    /// Modifies given async sequence into new one, which will only emit elements as long as predicate is satisfied. 
    let takeWhile (f: 'a -> bool) (upstream: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        let! hasNext = inner.MoveNextAsync()
                        if hasNext && f inner.Current then return true
                        else return false
                    } }}
        
    let private takeWithinCallback = TimerCallback(fun state ->
        let c = state :?> CancellationTokenSource
        c.Cancel()
    )
        
    /// Modifies given async sequence into new one, which will produce elements until given timeout, then will complete.
    let takeWithin (timeout: TimeSpan) (upstream: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let complete = CancellationTokenSource.CreateLinkedTokenSource(cancel)
                let inner = upstream.GetAsyncEnumerator(complete.Token)
                let timer = new Timer(takeWithinCallback, complete, timeout, TimeSpan.Zero)
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() =
                        complete.Dispose()
                        timer.Dispose()
                        inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        if complete.IsCancellationRequested then return false
                        else return! inner.MoveNextAsync()
                    } }}
        
    /// Modifies given async sequence into new one, which will only emit up to a given number of elements. 
    let take (n: int64) (upstream: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable counter = n
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        if counter > 0L then
                            counter <- counter - 1L
                            return! inner.MoveNextAsync()
                        else return false
                    } }}
        
    /// Splits upstream sequence into two, first producing items from an upstream as long as given `condition`
    /// function is not satisfied. Once condition has been satisfied, current and all next elements will be
    /// redirected into second sequence, while the first one will be completed.
    let split (condition: 'a -> bool) (upstream: AsyncSeq<'a>) : (AsyncSeq<'a> * AsyncSeq<'a>) =
        let fullfiled = TaskCompletionSource<bool>()
        let before =
            { new AsyncSeq<'a> with
                member __.GetAsyncEnumerator (cancel) =
                    let inner = upstream.GetAsyncEnumerator(cancel)
                    let mutable current = Unchecked.defaultof<_>
                    { new IAsyncEnumerator<'a> with
                        member __.Current = current
                        member __.DisposeAsync() = inner.DisposeAsync()
                        member __.MoveNextAsync() = 
                            if cancel.IsCancellationRequested then cancelled cancel
                            elif fullfiled.Task.IsCompleted then ValueTask<_>(false)
                            else uvtask {
                                let! hasNext = inner.MoveNextAsync()
                                if hasNext then
                                    current <- inner.Current
                                    let finish = condition current
                                    if finish then
                                        fullfiled.SetResult(hasNext)
                                        return false
                                    else return true
                                else return false
                            } }}
        let after =
            { new AsyncSeq<'a> with
                member __.GetAsyncEnumerator (cancel) =
                    let inner = upstream.GetAsyncEnumerator(cancel)
                    let mutable awaiter = fullfiled
                    { new IAsyncEnumerator<'a> with
                        member __.Current = inner.Current
                        member __.DisposeAsync() = inner.DisposeAsync()
                        member __.MoveNextAsync() = 
                            if cancel.IsCancellationRequested then cancelled cancel
                            else uvtask {
                                if not (isNull awaiter) then
                                    let a = awaiter
                                    awaiter <- null
                                    return! a.Task
                                else
                                    return! inner.MoveNextAsync()
                            } }}
        (before, after)
        
    /// Similar to fold, but it's not a terminal operation.
    /// An updated state is produced continuously as an output element.
    let scan (f: 's -> 'a -> ValueTask<'s>) (seed: 's) (upstream: AsyncSeq<'a>): AsyncSeq<'s> = 
        { new AsyncSeq<'s> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable state = seed
                { new IAsyncEnumerator<'s> with
                    member __.Current = state
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            let! hasNext = inner.MoveNextAsync()
                            if hasNext then
                                let! state' = f state inner.Current
                                state <- state'
                                return true
                            else
                                return false
                        } }}
    
    /// Shifts an element emission in time by delaying it by timeout generated by `timeoutFn` for that specific element.
    /// Since delay uses a function, it's very easy to implement things like eg. exponential backoff.
    let delay (timeoutFn: 'a -> TimeSpan) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            let! hasNext = inner.MoveNextAsync()
                            if not hasNext then return false
                            else
                                let timeout = timeoutFn inner.Current
                                do! Task.Delay(timeout, cancel)
                                return true
                        } } }
        
    
    /// Groups incoming elements into chunks of size `n`, and returns an async sequence of chunks.
    /// Once input sequence completes, it will produce a final chunk (potentially smaller than `n`)
    /// before completing itself. 
    let grouped (n: int) (upstream: AsyncSeq<'a>): AsyncSeq<'a[]> =
        if n <= 0 then raise (ArgumentException "AsyncSeq.grouped buffer size must be positive number")
        else
            { new AsyncSeq<'a[]> with
                member __.GetAsyncEnumerator (cancel) =
                    let inner = upstream.GetAsyncEnumerator(cancel)
                    let mutable buffer = Array.zeroCreate n
                    let mutable current = null
                    let mutable idx = 0 
                    { new IAsyncEnumerator<'a[]> with
                        member __.Current = current
                        member __.DisposeAsync() = inner.DisposeAsync()
                        member __.MoveNextAsync() = vtask {
                            if isNull buffer then return false
                            else
                                let! hasNext = inner.MoveNextAsync()
                                let mutable hasNext' = hasNext
                                let mutable ready = false
                                while hasNext' && not ready do
                                    buffer.[idx] <- inner.Current
                                    idx <- idx + 1
                                    if idx = n then
                                        current <- buffer
                                        buffer <- Array.zeroCreate n
                                        idx <- 0
                                        ready <- true
                                    else
                                        let! hasNext = inner.MoveNextAsync()
                                        hasNext' <- hasNext
                                        
                                if not hasNext' && idx > 0 then
                                    // when stream ended before buffer was full, emit remaining buffer
                                    current <- Array.zeroCreate idx
                                    Array.blit buffer 0 current 0 idx
                                    buffer <- null
                                    ready <- true
                                return ready
                        } }}

    /// Pulls upstream asynchronously from elements produced downstream, backpressuring when upstream has been pulled
    /// up to `maxBufferSize` times before a single pull from downstream arrived. This can be useful when downstream
    /// pulls at slower rate than upstream is ready to produce its elements.
    let buffered (maxBufferSize: int) (upstream: AsyncSeq<'a>): AsyncSeq<'a[]> =
        if maxBufferSize <= 0 then raise (ArgumentException "AsyncSeq.buffered buffer size must be positive number")
        else
            { new AsyncSeq<'a[]> with
                member __.GetAsyncEnumerator (cancel) =
                    let inner = upstream.GetAsyncEnumerator(cancel)
                    let (writer, reader) =
                        let ch = Channel.CreateBounded<'a>(BoundedChannelOptions(maxBufferSize, SingleReader=true, SingleWriter=true))
                        ch.Writer, ch.Reader
                    let pullWorker = Task.runCancellable cancel (fun () -> task {
                        let mutable hasNext = not cancel.IsCancellationRequested
                        while hasNext && not cancel.IsCancellationRequested do
                            let! next = inner.MoveNextAsync()
                            hasNext <- next
                            if hasNext && not (writer.TryWrite(inner.Current)) then
                                let! next = writer.WaitToWriteAsync(cancel)
                                hasNext <- hasNext && next && writer.TryWrite(inner.Current)
                        writer.Complete()
                    })
                    let mutable current = Unchecked.defaultof<_>
                    let buf = Array.zeroCreate maxBufferSize
                    { new IAsyncEnumerator<'a[]> with
                        member __.Current = current
                        member __.DisposeAsync() =
                            pullWorker.Dispose() 
                            inner.DisposeAsync()
                        member __.MoveNextAsync() = vtask {
                            let! hasItems = reader.WaitToReadAsync(cancel)
                            if hasItems then
                                let span = Span.ofArray buf
                                let read = Channel.readTo span reader
                                current <- span.Slice(0, read).ToArray()
                                return true
                            else
                                current <- Unchecked.defaultof<_>
                                return false
                        } }}
        
    
    /// Zips two async sequences together producing an output as transformation of elements of corresponding elements.
    /// Whenever one of the async sequences completes, the result will also complete.
    let zipWith (f: 'a * 'b -> 'c) (upstream: AsyncSeq<'a>) (bseq: AsyncSeq<'b>): AsyncSeq<'c> =
        let next (ae: IAsyncEnumerator<'a>) (be: IAsyncEnumerator<'b>) = vtask {
            let x = ae.MoveNextAsync()
            let y = be.MoveNextAsync()
            match x.IsCompletedSuccessfully, y.IsCompletedSuccessfully with
            | false, false ->
                let! [|xready; yready|] = Task.WhenAll(x.AsTask(), y.AsTask())
                return xready && yready
            | true, false ->
                let! yready = y
                return x.Result && yready
            | false, true ->
                let! xready = x
                return y.Result && xready
            | true, true -> return x.Result && y.Result }
        
        { new AsyncSeq<'c> with
            member __.GetAsyncEnumerator (cancel) =
                let ae = upstream.GetAsyncEnumerator(cancel)
                let be = bseq.GetAsyncEnumerator(cancel)
                let mutable current = Unchecked.defaultof<_>
                { new IAsyncEnumerator<'c> with
                    member __.Current = current
                    member __.DisposeAsync() = unitVtask {
                        let mutable step = 0
                        try
                            do! ae.DisposeAsync()
                            step <- 1
                            if not (isNull be) then
                                do! be.DisposeAsync()
                            step <- 2
                        with ex ->
                            if step < 2 then
                                do! be.DisposeAsync()
                            return rethrow ex
                    }
                    member __.MoveNextAsync() = vtask {
                        let! hasNext = next ae be
                        if hasNext then
                            current <- f (ae.Current, be.Current)
                            return true
                        else return false
                    }
                }
        }
    
    let inline zip (left: AsyncSeq<'a>) (right: AsyncSeq<'b>): AsyncSeq<'a * 'b> = zipWith id left right
    
    /// Creates an async sequence out of a given synchronous sequence.
    let ofSeq (s: 'a seq): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = s.GetEnumerator()
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.Dispose(); ValueTask()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else ValueTask<_>(inner.MoveNext())
                    } }
    
    /// Creates an async sequence out of the provided function. Function takes as an input every subsequent call counter
    /// (starting from 0L) and outputs as value task with optional value. Once a None value will be emitted,
    /// a corresponding async enumerator will be closed. 
    let ofFunc (f: int64 -> ValueTask<'a option>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable current = Unchecked.defaultof<_>
                let mutable i = 0L
                { new IAsyncEnumerator<'a> with
                    member __.Current = current
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() =
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            let! next = f i
                            match next with
                            | Some value ->
                                current <- value
                                i <- i + 1L
                                return true
                            | None ->
                                return false
                        } } }
        
    /// Wraps given Task producing function with an async sequence, which will emit a result of a task and then complete.
    /// A cancellation token passed from async enumerator can be used within task produced function to interrupt it.
    let ofTaskCancellable (taskFn: CancellationToken -> Task<'a>): AsyncSeq<'a> = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable completed = false
                let task = taskFn cancel
                { new IAsyncEnumerator<'a> with
                    member __.Current = task.Result
                    member __.DisposeAsync() = task.Dispose(); ValueTask()
                    member __.MoveNextAsync() =
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            if completed then return false
                            else
                                if task.IsCompletedSuccessfully then
                                    completed <- true
                                    return true
                                elif task.IsCanceled then return false
                                else
                                    let! _ = task
                                    completed <- true
                                    return true
                        } } }
     
    /// Wraps given `task` with an async sequence, which will emit a result of a task and then complete. 
    let ofTask (task: Task<'a>): AsyncSeq<'a> = ofTaskCancellable (fun _ -> task)
        
    /// Wraps given Async with an async sequence, which will emit a result of a task and then complete.
    let ofAsync (a: Async<'a>): AsyncSeq<'a> = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable result = None
                { new IAsyncEnumerator<'a> with
                    member __.Current = result.Value
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            match result with
                            | None ->
                                let! r = a
                                result <- Some r
                                return true
                            | Some _ ->
                                return false
                        } } }
    
    /// Returns an async sequence, that produces only a one element and then closes.    
    let singleton (value: 'a): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable result = None
                { new IAsyncEnumerator<'a> with
                    member __.Current = result.Value
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            match result with
                            | None ->
                                result <- Some value
                                return true
                            | Some _ ->
                                return false
                        } } }
        
    /// Returns an async sequence, that produces element if provided value was Some. Otherwise it will be completed.    
    let ofOption (value: 'a option): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable pulled = Option.isNone value
                { new IAsyncEnumerator<'a> with
                    member __.Current = value.Value
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() =
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            if pulled then return false
                            else
                                pulled <- true
                                return true
                        } } }
        
    /// Returns an async sequence, that reads elements from a given channel reader.
    let inline ofChannel (ch: ChannelReader<'a>): AsyncSeq<'a> = ch.ReadAllAsync()
        
    /// Attaches a deferred function to a current async sequence, which will be called once a corresponding async
    /// enumerator will be completed.  
    let onDispose (f: unit -> ValueTask) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = unitVtask {
                        let mutable step = 0
                        try
                            do! inner.DisposeAsync()
                            step <- 1
                            do! f()
                            step <- 2
                        with ex ->
                            // ensure that we call defer function even if dispose async failed, but not call it twice
                            if step < 2 then
                                do! f() 
                            return rethrow ex }
                    member __.MoveNextAsync() = inner.MoveNextAsync() } }

    /// Returns an async sequence, that will limit the rate at which the input elements of given
    /// sequence are produces into the output.
    let throttle (count: int) (timeout: TimeSpan) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        if count <= 0 then raise (ArgumentException "AsyncSeq.throttle count must be positive")
        else
            { new AsyncSeq<'a> with
                member __.GetAsyncEnumerator (cancel) =
                    let inner = upstream.GetAsyncEnumerator(cancel)
                    let mutable remaining = count
                    let mutable period = Unchecked.defaultof<Task>
                    { new IAsyncEnumerator<'a> with
                        member __.Current = inner.Current
                        member __.DisposeAsync() = inner.DisposeAsync()
                        member __.MoveNextAsync() = 
                            if cancel.IsCancellationRequested then cancelled cancel
                            else uvtask {
                                if isNull period || period.IsCompleted then
                                    period <- Task.Delay(timeout)
                                    remaining <- count
                                    let! hasNext = inner.MoveNextAsync()
                                    if hasNext then
                                       remaining <- remaining - 1
                                    return hasNext                                
                                elif remaining = 0 then
                                    do! period // trigger throttling
                                    return! inner.MoveNextAsync()
                                else
                                    remaining <- remaining - 1
                                    return! inner.MoveNextAsync()
                            } } }
            
    /// Returns an async sequence, that will monitor the rate at which input elements are
    /// incoming, and in case when no element what produce before a given `timeout` has passed
    /// it will generate a new element using given `inject` function. That function will receive
    /// a last element that was produced prior the timeout.
    let keepAlive (timeout: TimeSpan) (inject: 'a -> 'a) (upstream: AsyncSeq<'a>): AsyncSeq<'a> = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable pending = Unchecked.defaultof<Task<bool>>
                let mutable current = Unchecked.defaultof<'a>
                let handleAsync () = vtask {
                    let delay = Task.Delay(timeout, cancel)
                    let! first = Task.WhenAny(delay, pending)
                    if obj.ReferenceEquals(first, delay) then
                        current <- inject current
                        return true
                    else
                        delay.Dispose()
                        let r = pending
                        pending <- null
                        if r.Result then
                            current <- inner.Current
                            return true
                        else
                            return false }
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            if isNull pending then
                                let t = inner.MoveNextAsync()
                                if t.IsCompletedSuccessfully then
                                    current <- inner.Current
                                    return t.Result
                                else
                                    pending <- t.AsTask()
                                    return! handleAsync()                                
                            else return! handleAsync()
                        } } }
    
    /// Deduplicates a consecutive input elements, skipping all elements,
    /// that were considered equal using given equality comparer function. 
    let deduplicate (equal: 'a -> 'a -> bool) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable last = None
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            let! hasNext = inner.MoveNextAsync()
                            let mutable hasNext' = hasNext
                            let mutable skipped' = true
                            while hasNext' && skipped' do
                                match last with
                                | Some l when equal l inner.Current ->
                                    let! hasNext = inner.MoveNextAsync()
                                    hasNext' <- hasNext
                                    skipped' <- true
                                | _ ->
                                    last <- Some inner.Current
                                    skipped' <- false
                            return hasNext'
                        } } }
      
    type private InterleavingState =
        | None = 0
        | Transitive = 1
        | Interleaved = 2
      
    /// Interleaves the elements produced by a given asynchronous sequence, by producing an interleaved
    /// value in between elements, based on the previously produced element.
    /// Example:
    /// <code>
    /// AsyncSeq.ofSeq ["a";"b";"c"]
    /// |> AsyncSeq.interleave (fun p n -> p + "-" + n) // "a"; "a-b"; "b"; "b-c"; "c"
    /// </code>
    let interleave (fn: 'a -> 'a -> 'a) (upstream: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = upstream.GetAsyncEnumerator(cancel)
                let mutable state = InterleavingState.None
                let mutable prev = Unchecked.defaultof<'a>
                let mutable current = Unchecked.defaultof<'a>
                let mutable next = Unchecked.defaultof<'a>
                { new IAsyncEnumerator<'a> with
                    member __.Current = current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else uvtask {
                            match state with
                            | InterleavingState.Interleaved ->
                                current <- next
                                prev <- next
                                state <- InterleavingState.Transitive
                                return true
                            | InterleavingState.Transitive ->
                                let! hasNext = inner.MoveNextAsync()
                                if hasNext then
                                    next <- inner.Current
                                    current <- fn prev next
                                    state <- InterleavingState.Interleaved
                                return hasNext
                            | InterleavingState.None ->
                                state <- InterleavingState.Transitive
                                let! hasNext = inner.MoveNextAsync()
                                if hasNext then
                                    prev <- inner.Current
                                    current <- inner.Current
                                return hasNext
                        } } }

    /// Groups incoming events by provided key selector, returning an async sequence
    /// which return pair of produced key and newly produced output. To avoid pulling
    /// unbounded load, `maxActiveGroups` can be specified to limit the max number of
    /// active children sequences. Each group-sequence will also allow to pull up to a
    /// `capacityPerGroup` elements, before triggering backpressure in upstream.
    let groupBy (maxActiveGroups: int) (capacityPerGroup: int) (fn: 'a -> 'b) (upstream: AsyncSeq<'a>): AsyncSeq<('b * AsyncSeq<'a>)> =
        { new AsyncSeq<_> with
            member __.GetAsyncEnumerator (cancel) =
                let up = upstream.GetAsyncEnumerator(cancel)
                let children = Dictionary<'b, ChannelWriter<'a>>()
                let inp, out = if maxActiveGroups = 0 then Channel.unboundedSpsc () else Channel.boundedSpsc maxActiveGroups
                let complete (e: exn) =
                    inp.TryComplete e |> ignore
                    for kv in children do
                        kv.Value.TryComplete e |> ignore
                    up.DisposeAsync()
                let mutable disposed = false
                let receiver = Task.runCancellable cancel <| fun () -> task {
                    try
                        let mutable cont = not disposed
                        while not cancel.IsCancellationRequested && not disposed && cont do
                            let! hasNext = up.MoveNextAsync()
                            if hasNext then
                                let key = fn up.Current
                                match children.TryGetValue(key) with
                                | true, writer ->
                                    do! writer.WriteAsync(up.Current, cancel)
                                | false, _     ->
                                    let w, r = if capacityPerGroup = 0 then Channel.unboundedSpsc () else Channel.boundedSpsc capacityPerGroup
                                    children.Add(key, w)
                                    do! w.WriteAsync(up.Current, cancel)
                                    do! inp.WriteAsync((key, r.ReadAllAsync()), cancel)
                            else cont <- false
                        do! complete null
                    with e ->
                        do! complete e                
                }
                let mutable current = Unchecked.defaultof<_>
                { new IAsyncEnumerator<('b * AsyncSeq<'a>)> with
                    member __.Current = current
                    member __.DisposeAsync() =
                        disposed <- true
                        ValueTask(receiver :> Task)
                    member __.MoveNextAsync() =
                        if cancel.IsCancellationRequested then cancelled cancel
                        elif out.TryRead(&current) then ValueTask<_> true
                        else uvtask {
                            let! hasNext = out.WaitToReadAsync()
                            if hasNext && out.TryRead(&current)
                            then return true
                            else return false                                
                        }
                }
        }
        
    /// Returns an asynchronous sequence of elements, that will immediately fail once consumer will try to pull
    /// first element. 
    let failed<'a> (e: exn) : AsyncSeq<'a> =
        { new AsyncSeq<_> with
            member __.GetAsyncEnumerator (cancel) =
                { new IAsyncEnumerator<_> with
                    member __.Current = Unchecked.defaultof<_>
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() = 
                        if cancel.IsCancellationRequested then cancelled cancel
                        else ValueTask<bool>(Task.FromException<bool>(e))
                    } }

    /// Waits until all elements of the upstream sequence arrive, but discards them.
    let ignore (upstream: AsyncSeq<'a>) = unitVtask {
        let e = upstream.GetAsyncEnumerator()
        try
            let! cont = e.MoveNextAsync()
            let mutable hasNext = cont
            while hasNext do
                let! cont = e.MoveNextAsync()
                hasNext <- cont
            do! e.DisposeAsync()
        with ex ->
            do! e.DisposeAsync()
            rethrow ex
    }
