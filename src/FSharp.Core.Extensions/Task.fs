namespace FSharp.Core

open FSharp.Control.Tasks.Builders
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open System.Threading
open System
open System.Threading.Channels

type AsyncSeq<'a> = IAsyncEnumerable<'a>

[<RequireQualifiedAccess>]
module AsyncSeq =
    
    let inline private rethrow (e: exn) =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<_>
        
    /// Modifies provided async sequence into the one which will apply
    /// a provided cancellation token upon async enumerator creation.
    let withCancellation (cancel: CancellationToken) (aseq: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator _ = aseq.GetAsyncEnumerator(cancel) }
       
    /// Returns an empty async sequence.
    let empty<'a> : AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                { new IAsyncEnumerator<'a> with
                    member __.Current = failwith "AsyncEnumerator is empty"
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() = ValueTask<_>(false) } }
        
    /// Returns a task, which will pull all incoming elements from a given sequence
    /// or until token has cancelled, and pushes them to a given channel writer.
    let into (chan: ChannelWriter<'a>) (aseq: AsyncSeq<'a>) = vtask {
        let e = aseq.GetAsyncEnumerator()
        try
            let! hasNext = e.MoveNextAsync()
            let mutable hasNext' = hasNext
            while hasNext' do
                do! chan.WriteAsync(e.Current)
                let! hasNext = e.MoveNextAsync()
                hasNext' <- hasNext
            do! e.DisposeAsync()
            do chan.Complete()
        with ex ->
            do! e.DisposeAsync()
            return rethrow ex 
    }
            
    /// Tries to return the first element of the async sequence,
    /// returning None if sequence is empty or cancellation was called.
    let tryHead (aseq: AsyncSeq<'a>) = vtask {
        let e = aseq.GetAsyncEnumerator()
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
    let tryLast (aseq: AsyncSeq<'a>) = vtask {
        let e = aseq.GetAsyncEnumerator()
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
    let collect (aseq: AsyncSeq<'a>): ValueTask<'a seq> = vtask {
        let e = aseq.GetAsyncEnumerator()
        try
            let! hasNext = e.MoveNextAsync()
            let mutable hasNext' = hasNext
            let mutable result = ResizeArray<_>()
            while hasNext' do
                result.Add (e.Current)
                let! hasNext = e.MoveNextAsync()
                hasNext' <- hasNext
            do! e.DisposeAsync()
            return upcast result
        with ex ->
            do! e.DisposeAsync()
            return rethrow ex
    }
    
    /// Asynchronously folds over incoming elements, continuously constructing
    /// the output state (starting from provided seed) and returning it eventually
    /// when a sequence will complete. 
    let fold (f: 's -> 'a -> ValueTask<'s>) (seed: 's) (aseq: AsyncSeq<'a>) = vtask {
        let e = aseq.GetAsyncEnumerator()
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
    
    /// Asynchronously folds over incoming elements, continuously constructing
    /// the output state (or returning with none if sequence had no elements)
    /// and returning it eventually when a sequence will complete. 
    let reduce (f: 'a -> 'a -> ValueTask<'a>) (aseq: AsyncSeq<'a>) = vtask {
        let e = aseq.GetAsyncEnumerator()
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
    let iteri (f: int64 -> 'a -> ValueTask) (aseq: AsyncSeq<'a>) = unitVtask {
        let e = aseq.GetAsyncEnumerator()
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
    let inline iter (f: 'a -> ValueTask) aseq = iteri (fun _ e -> f e) aseq
    
    [<Sealed>]
    type private MapParallelEnumerator<'a, 'b>(parallelism: int, e: IAsyncEnumerator<'a>, cancel:CancellationToken, map: 'a -> ValueTask<'b>) =
        let input = Channel.CreateBounded<'a>(parallelism)
        let output = Channel.CreateBounded<'b>(parallelism)
        let mutable current = Unchecked.defaultof<_>
        let mutable remaining = parallelism
        let workers = Array.init parallelism (fun _ -> unitTask {
            while not (cancel.IsCancellationRequested || input.Reader.Completion.IsCompleted) do
                let! next = input.Reader.ReadAsync(cancel)
                let! o = map next
                do! output.Writer.WriteAsync(o, cancel)
            Interlocked.Decrement(&remaining) 
        })
        let reader = Task.Run(Func<Task>(fun () -> unitTask {
            let mutable cont = not cancel.IsCancellationRequested 
            while cont do
                let! hasNext = e.MoveNextAsync()
                if hasNext then
                    do! input.Writer.WriteAsync(e.Current, cancel)
                cont <- hasNext
            input.Writer.TryComplete() |> ignore
        }))
        interface IAsyncEnumerator<'b> with
            member __.Current = current
            member __.DisposeAsync() = unitVtask {
                try reader.Dispose() with _ -> ()
                for worker in workers do
                    try worker.Dispose() with _ -> ()
                input.Writer.TryComplete() |> ignore
            }
            member __.MoveNextAsync() = vtask {
                if cancel.IsCancellationRequested then return false
                elif output.Reader.TryRead(&current) then return true
                elif remaining = 0 then return false
                else
                    let! ready = output.Reader.WaitToReadAsync(cancel)
                    if ready && output.Reader.TryRead(&current) then 
                        return true
                    else                      
                        return false
            }
    
    /// Iterates over all elements of provided async sequence in parallel.
    let mapParallel (parallelism: int) (f: 'a -> ValueTask<'b>) (aseq: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                upcast MapParallelEnumerator<'a, 'b>(parallelism, aseq.GetAsyncEnumerator(cancel), cancel, f) }
            
    /// Returns a new async sequence constructed from provided one transformed by applying
    /// mapping function (with monotonically increasing counter) over all input elements.
    let mapAsynci (f: int64 -> 'a -> ValueTask<'b>) (aseq: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
                let current = ref Unchecked.defaultof<_>
                let mutable counter = 0L
                { new IAsyncEnumerator<'b> with
                    member __.Current = !current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        if cancel.IsCancellationRequested then return false
                        else
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
    let inline mapAsync (f: 'a -> ValueTask<'b>) aseq = mapAsynci (fun _ a -> f a) aseq
    
    /// Returns a new async sequence constructed from provided one transformed by applying
    /// mapping function (with monotonically increasing counter) over all input elements.
    let mapi (f: int64 -> 'a -> 'b) (aseq: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
                let mutable current = Unchecked.defaultof<_>
                let mutable counter = 0L
                { new IAsyncEnumerator<'b> with
                    member __.Current = current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        if cancel.IsCancellationRequested then return false
                        else
                            let! cont = inner.MoveNextAsync()
                            if cont then
                                current <- f counter inner.Current
                                counter <- counter + 1L
                                return true
                            else return false
                    } }}
      
    /// Returns a new async sequence constructed from provided one transformed by applying
    /// mapping function over all input elements.  
    let inline map (f: 'a -> 'b) (aseq: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
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
    let choose (f: 'a -> ValueTask<'b option>) (aseq: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
                let mutable current = None
                { new IAsyncEnumerator<'b> with
                    member __.Current = current.Value
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        let! hasNext = inner.MoveNextAsync()
                        let mutable cont = hasNext
                        while cont do
                            let! result = f inner.Current
                            current <- result
                            match result with
                            | Some _ ->
                                cont <- false
                            | None ->
                                let! hasNext = inner.MoveNextAsync()
                                cont <- hasNext
                        return cont && Option.isSome current      
                    } }}
    
    /// Returns an async sequence which filters out the input async sequence elements,
    /// which have not satisfied given predicate.
    let filter (f: 'a -> bool) (aseq: AsyncSeq<'a>): AsyncSeq<'a> =
        choose (fun a -> if f a then ValueTask<_>(Some a) else ValueTask<_> None) aseq

    [<Sealed>]
    type private MergeParallelEnumerator<'a>(enums: ResizeArray<IAsyncEnumerator<'a>>, cancel: CancellationToken) =
        let input = Channel.CreateBounded<'a>(1)
        let mutable current = Unchecked.defaultof<_>
        let tasks =
            enums
            |> Seq.map (fun e -> Task.Run(Func<Task>(fun () -> unitTask {
                let! hasNext = e.MoveNextAsync()
                let mutable hasNext' = hasNext
                while not cancel.IsCancellationRequested && hasNext' do
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
                for e in enums do
                    do! e.DisposeAsync()
                for t in tasks do
                    t.Dispose()
                input.Writer.TryComplete() |> ignore
            }
            member this.MoveNextAsync() = vtask {
                if cancel.IsCancellationRequested || enums.Count = 0 then return false
                else
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
     
    /// Given an async sequence which for a given input will produce the next sub-sequence
    /// and then returns an async sequence which flattens the results produced by sub-sequences
    /// until all sub-sequences produces will be completed.
    let bind (f: 'a -> AsyncSeq<'b>) (aseq: AsyncSeq<'a>): AsyncSeq<'b> =
        { new AsyncSeq<'b> with
            member __.GetAsyncEnumerator (cancel) =
                let outer = aseq.GetAsyncEnumerator(cancel)
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
    let skipWhile (f: 'a -> bool) (aseq: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
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
        
    /// Modifies given async sequence into new one, which will skip first `n` elements.
    let skip (n: int64) (aseq: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
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
    let takeWhile (f: 'a -> bool) (aseq: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        let! hasNext = inner.MoveNextAsync()
                        if hasNext && f inner.Current then return true
                        else return false
                    } }}
        
    /// Modifies given async sequence into new one, which will only emit up to a given number of elements. 
    let take (n: int64) (aseq: AsyncSeq<'a>) = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
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
        
    /// Similar to fold, but it's not a terminal operation.
    /// An updated state is produced continuously as an output element.
    let scan (f: 's -> 'a -> ValueTask<'s>) (seed: 's) (aseq: AsyncSeq<'a>): AsyncSeq<'s> = 
        { new AsyncSeq<'s> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
                let mutable state = seed
                { new IAsyncEnumerator<'s> with
                    member __.Current = state
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        if cancel.IsCancellationRequested then return false
                        else
                            let! hasNext = inner.MoveNextAsync()
                            if hasNext then
                                let! state' = f state inner.Current
                                state <- state'
                                return true
                            else
                                return false
                    } }}
    
    /// Shifts an element emission in time by delaying it by specified timeout.   
    let delay (timeout: TimeSpan) (aseq: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
                        if cancel.IsCancellationRequested then return false
                        else
                            do! Task.Delay(timeout)
                            return! inner.MoveNextAsync()
                    } }}
    
    /// Groups incoming elements into chunks of size `n`, and returns an async sequence of chunks.
    /// Once input sequence completes, it will produce a final chunk (potentially smaller than `n`)
    /// before completing itself. 
    let grouped (n: int) (aseq: AsyncSeq<'a>): AsyncSeq<'a[]> =
        if n <= 0 then raise (ArgumentException "AsyncSeq.buffered buffer size must be positive number")
        else
            { new AsyncSeq<'a[]> with
                member __.GetAsyncEnumerator (cancel) =
                    let inner = aseq.GetAsyncEnumerator(cancel)
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
    
    /// Zips two async sequences together producing an output as transformation of elements of corresponding elements.
    /// Whenever one of the async sequences completes, the result will also complete.
    let zipWith (f: 'a * 'b -> 'c) (aseq: AsyncSeq<'a>) (bseq: AsyncSeq<'b>): AsyncSeq<'c> =
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
                let ae = aseq.GetAsyncEnumerator(cancel)
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
    
    let zip (left: AsyncSeq<'a>) (right: AsyncSeq<'b>): AsyncSeq<'a * 'b> = zipWith id left right
    
    /// Creates an async sequence out of a given synchronous sequence.
    let ofSeq (s: 'a seq): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = s.GetEnumerator()
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.Dispose(); ValueTask()
                    member __.MoveNextAsync() = vtask {
                        if cancel.IsCancellationRequested then return false
                        else return inner.MoveNext()
                    } } }
    
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
                    member __.MoveNextAsync() = vtask {
                        if cancel.IsCancellationRequested then return false
                        else
                            let! next = f i
                            match next with
                            | Some value ->
                                current <- value
                                i <- i + 1L
                                return true
                            | None ->
                                return false
                    } } }
     
    /// Wraps given `task` with an async sequence, which will emit a result of a task and then complete. 
    let ofTask (task: Task<'a>): AsyncSeq<'a> = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable completed = false
                { new IAsyncEnumerator<'a> with
                    member __.Current = task.Result
                    member __.DisposeAsync() = task.Dispose(); ValueTask()
                    member __.MoveNextAsync() = vtask {
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
        
    /// Wraps given Async with an async sequence, which will emit a result of a task and then complete.
    let ofAsync (a: Async<'a>): AsyncSeq<'a> = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let mutable result = None
                { new IAsyncEnumerator<'a> with
                    member __.Current = result.Value
                    member __.DisposeAsync() = ValueTask()
                    member __.MoveNextAsync() = vtask {
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
                    member __.MoveNextAsync() = vtask {
                        match result with
                        | None ->
                            result <- Some value
                            return true
                        | Some _ ->
                            return false
                    } } }
        
    /// Returns an async sequence, that reads elements from a given channel reader.
    let inline ofChannel (ch: ChannelReader<'a>): AsyncSeq<'a> = ch.ReadAllAsync()
        
    /// Attaches a disposal function to a current async sequence, which will be called once a corresponding async
    /// enumerator will be disposed.  
    let onDispose (f: unit -> ValueTask) (aseq: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
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
                            if step < 2 then
                                do! f()
                            return rethrow ex }
                    member __.MoveNextAsync() = inner.MoveNextAsync() } }

    /// Returns an async sequence, that will limit the rate at which the input elements of given
    /// sequence are produces into the output.
    let throttle (count: int) (timeout: TimeSpan) (aseq: AsyncSeq<'a>): AsyncSeq<'a> =
        if count <= 0 then raise (ArgumentException "AsyncSeq.throttle count must be positive")
        else
            { new AsyncSeq<'a> with
                member __.GetAsyncEnumerator (cancel) =
                    let inner = aseq.GetAsyncEnumerator(cancel)
                    let mutable remaining = count
                    let mutable period = Unchecked.defaultof<Task>
                    { new IAsyncEnumerator<'a> with
                        member __.Current = inner.Current
                        member __.DisposeAsync() = inner.DisposeAsync()
                        member __.MoveNextAsync() = vtask {
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
    let keepAlive (timeout: TimeSpan) (inject: 'a -> 'a) (aseq: AsyncSeq<'a>): AsyncSeq<'a> = 
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
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
                    member __.MoveNextAsync() = vtask {
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
    let deduplicate (equal: 'a -> 'a -> bool) (aseq: AsyncSeq<'a>): AsyncSeq<'a> =
        { new AsyncSeq<'a> with
            member __.GetAsyncEnumerator (cancel) =
                let inner = aseq.GetAsyncEnumerator(cancel)
                let mutable last = None
                { new IAsyncEnumerator<'a> with
                    member __.Current = inner.Current
                    member __.DisposeAsync() = inner.DisposeAsync()
                    member __.MoveNextAsync() = vtask {
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