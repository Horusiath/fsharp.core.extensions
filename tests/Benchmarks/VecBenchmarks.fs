namespace Benchmarks

open System.Collections.Immutable
open BenchmarkDotNet.Attributes
open FSharp.Core

type User = { FirstName: string; LastName: string; Age: int }

[<MemoryDiagnoser>]
type VecAppendBenchmarks() =

    [<DefaultValue; Params(1, 100, 1000, 200_000)>]
    val mutable count: int
    
    [<DefaultValue>]
    val mutable items: User[]

    [<GlobalSetup>]
    member this.Setup() =
        this.items <- Array.zeroCreate this.count
        for i=0 to this.count - 1 do
            let s = string i
            this.items.[i] <- { FirstName = "Alex" + s; LastName = "McCragh" + s; Age = i }
        
    [<GlobalCleanup>]
    member this.Cleanup() =
        this.items <- null

    [<Benchmark(Baseline=true)>]
    member this.MutableListAppend() =
        let list = ResizeArray()
        for item in this.items do
            list.Add item
        list
        
    [<Benchmark>]
    member this.ImmutableListAppend() =
        let mutable list = ImmutableList.Empty
        for item in this.items do
            list <- list.Add item
        list
        
    [<Benchmark>]
    member this.ImmutableArrayAppend() =
        let mutable array = ImmutableArray.Empty
        for item in this.items do
            array <- array.Add item
        array
        
    [<Benchmark>]
    member this.VecAppend() =
        let mutable array = Vec.empty
        for item in this.items do
            array <- Vec.append item array
        array

[<MemoryDiagnoser>]
type VecInsertBenchmarks() =

    [<DefaultValue; Params(1, 100, 1000, 200_000)>]
    val mutable count: int
    
    [<DefaultValue>]
    val mutable items: User[]

    [<GlobalSetup>]
    member this.Setup() =
        this.items <- Array.zeroCreate this.count
        for i=0 to this.count - 1 do
            let s = string i
            this.items.[i] <- { FirstName = "Alex" + s; LastName = "McCragh" + s; Age = i }
        
    [<GlobalCleanup>]
    member this.Cleanup() =
        this.items <- null

    [<Benchmark(Baseline=true)>]
    member this.MutableListInsert() =
        let list = ResizeArray()
        for item in this.items do
            let i = list.Count / 2
            list.Insert(i, item)
        list
        
    [<Benchmark>]
    member this.ImmutableListInsert() =
        let mutable list = ImmutableList.Empty
        for item in this.items do
            let i = list.Count / 2
            list <- list.Insert(i, item)
        list
        
    [<Benchmark>]
    member this.ImmutableArrayInsert() =
        let mutable array = ImmutableArray.Empty
        for item in this.items do
            let i = array.Length / 2
            array <- array.Insert(i, item)
        array
        
    [<Benchmark>]
    member this.VecInsert() =
        let mutable array = Vec.empty
        for item in this.items do
            let i = array.Count / 2
            array <- Vec.insert i item array
        array
