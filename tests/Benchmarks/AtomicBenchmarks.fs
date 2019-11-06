namespace Benchmarks

open BenchmarkDotNet.Attributes
open FSharp.Core
open System.Linq
open System.Threading

[<MemoryDiagnoser>]
type AtomicUpdateBenchmarks() =

    [<DefaultValue>] val mutable a: AtomicInt64
    [<DefaultValue>] val mutable c1: int64
    [<DefaultValue>] val mutable syncRoot: obj
    [<DefaultValue>] val mutable c2: int64
    [<DefaultValue>] val mutable mut: Mutex
    [<DefaultValue>] val mutable c3: int64
    [<DefaultValue>] val mutable mutSlim: SemaphoreSlim
    [<DefaultValue>] val mutable c4: int64
    
    [<GlobalSetup>]
    member this.Setup() =
        this.a <- atom 0L
        this.syncRoot <- obj()
        this.mut <- new Mutex()
        this.mutSlim <- new SemaphoreSlim(1)
        
    [<GlobalCleanup>]
    member this.Cleanup() =
        this.a <- Unchecked.defaultof<_>
        this.syncRoot <- Unchecked.defaultof<_>
        this.mut.Dispose()
        this.mutSlim.Dispose()

    [<Benchmark(Baseline=true)>]
    member this.InterlockedLoop() =
        let rec loop () =
            let old = Volatile.Read &this.c1
            let new' = old + 1L
            if Interlocked.CompareExchange(&this.c1, new', old) = old then new' 
            else loop ()
        loop ()
        
    [<Benchmark>]
    member this.AtomicUpdate() =
        this.a |> Atomic.update (fun x -> x + 1L)
        
    [<Benchmark>]
    member this.ObjectLock() =
        lock this.syncRoot <| fun () ->
            this.c2 <- this.c2 + 1L
            this.c2
                        
    [<Benchmark>]
    member this.Mutex() =
        try
            this.mut.WaitOne() |> ignore
            this.c3 <- this.c3 + 1L
            this.c3            
        finally
            this.mut.ReleaseMutex()
        
    [<Benchmark>]
    member this.SemaphoreSlim() =
        try
            this.mutSlim.Wait()
            this.c4 <- this.c4 + 1L
            this.c4            
        finally
            this.mutSlim.Release() |> ignore
