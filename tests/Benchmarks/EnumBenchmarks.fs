namespace Benchmarks

open BenchmarkDotNet.Attributes
open FSharp.Core
open System.Linq

[<MemoryDiagnoser>]
type EnumBenchmarks() =

    [<DefaultValue; Params(10, 100, 10_000)>]
    val mutable to_append: int
    
    [<DefaultValue>]
    val mutable items: ResizeArray<User>

    [<GlobalSetup>]
    member this.Setup() =
        this.items <- ResizeArray<_>(this.to_append)
        for i=0 to this.to_append - 1 do
            let s = string i
            this.items.Add { FirstName = "Alex" + s; LastName = "McCragh" + s; Age = i }
        
    [<GlobalCleanup>]
    member this.Cleanup() =
        this.items <- null

    [<Benchmark(Baseline=true)>]
    member this.ForLoop() =
        let mutable sum = 0
        for i = 5 to this.items.Count - 1 do
            let item = this.items.[i]
            if item.Age % 2 = 0 then
                sum <- sum + item.Age
        sum
        
    [<Benchmark>]
    member this.FSharpPipes() =
         this.items
         |> Seq.skip 5
         |> Seq.filter (fun i -> i.Age % 2 = 0)
         |> Seq.fold (fun sum i -> sum + i.Age) 0         
        
    [<Benchmark>]
    member this.Linq() =
        this.items
            .Skip(5)
            .Where(System.Func<_, _>(fun i -> i.Age % 2 = 0))
            .Aggregate(0, System.Func<_,_,_>(fun sum i -> sum + i.Age))
        
    [<Benchmark>]
    member this.FSharpEnum() =
        Enum.from this.items
        |> Enum.skip 2L
        |> Enum.filter (fun i -> i.Age % 2 = 0)
        |> Enum.fold (fun sum i -> sum + i.Age) 0