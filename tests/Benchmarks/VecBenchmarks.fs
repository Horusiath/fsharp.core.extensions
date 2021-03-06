namespace Benchmarks

open System.Collections.Immutable
open BenchmarkDotNet.Attributes
open FSharp.Core
open FSharpx.Collections
open FSharpx.Collections

type User = { FirstName: string; LastName: string; Age: int }

[<MemoryDiagnoser>]
type VecAppendBenchmarks() =

    [<DefaultValue; Params(1, 100, 1000, 10_000)>]
    val mutable to_append: int
    
    [<DefaultValue>]
    val mutable items: User[]

    [<GlobalSetup>]
    member this.Setup() =
        this.items <- Array.zeroCreate this.to_append
        for i=0 to this.to_append - 1 do
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
    member this.FSharpxVectorAppend() =
        let mutable array = PersistentVector.empty
        for item in this.items do
            array <- PersistentVector.conj item array
        array
        
    [<Benchmark>]
    member this.VecAppend() =
        let mutable array = Vec.empty
        for item in this.items do
            array <- Vec.add item array
        array
        
[<MemoryDiagnoser>]
type VecAppendByteBenchmarks() =

    [<DefaultValue; Params(500, 4000)>]
    val mutable to_append: int
    
    [<DefaultValue>]
    val mutable items: byte[]

    [<GlobalSetup>]
    member this.Setup() =
        this.items <- Random.bytes this.to_append
        
    [<GlobalCleanup>]
    member this.Cleanup() =
        this.items <- null

    [<Benchmark(Baseline=true)>]
    member this.MutableListAppend() =
        let list = ResizeArray()
        list.AddRange this.items
        list
        
    [<Benchmark>]
    member this.ImmutableListAppend() =
        let mutable list = ImmutableList.Empty
        list <- list.AddRange this.items
        list
        
    [<Benchmark>]
    member this.ImmutableArrayAppend() =
        let mutable array = ImmutableArray.Empty
        array <- array.AddRange this.items
        array
        
    [<Benchmark>]
    member this.FSharpxVectorAppend() =
        let mutable array = PersistentVector.empty
        array <- PersistentVector.append array (PersistentVector.ofSeq this.items)
        array
        
    [<Benchmark>]
    member this.VecAppend() =
        let mutable array = Vec.empty
        array <- Vec.append array this.items
        array

[<MemoryDiagnoser>]
type VecEnumeratorBenchmarks() =

    [<DefaultValue; Params(10, 1000, 200_000)>]
    val mutable count: int
    
    [<DefaultValue>] val mutable list: ResizeArray<User>
    [<DefaultValue>] val mutable immList: ImmutableList<User>
    [<DefaultValue>] val mutable immArray: ImmutableArray<User>
    [<DefaultValue>] val mutable fsxVector: PersistentVector<User>
    [<DefaultValue>] val mutable vector: User vec
    
    [<GlobalSetup>]
    member this.Setup() =
        let items = Array.zeroCreate this.count
        for i=0 to this.count - 1 do
            let s = string i
            items.[i] <- { FirstName = "Alex" + s; LastName = "McCragh" + s; Age = i }
        
        this.list <- ResizeArray(items)
        this.immList <- ImmutableList.Create<User>(items)
        this.immArray <- ImmutableArray.Create<User>(items)
        this.fsxVector <- PersistentVector.ofSeq items
        this.vector <- Vec.ofArray items
            
    [<GlobalCleanup>]
    member this.Cleanup() =
        this.list <- Unchecked.defaultof<_>
        this.immList <- Unchecked.defaultof<_>
        this.immArray <- Unchecked.defaultof<_>
        this.vector <- Unchecked.defaultof<_>

    [<Benchmark(Baseline=true)>]
    member this.MutableListEnumerate() =
        let mutable last = Unchecked.defaultof<_>
        for u in this.list do
            last <- u
        last
        
    [<Benchmark>]
    member this.ImmutableListEnumerate() =
        let mutable last = Unchecked.defaultof<_>
        for u in this.immList do
            last <- u
        last
        
    [<Benchmark>]
    member this.ImmutableArrayEnumerate() =
        let mutable last = Unchecked.defaultof<_>
        for u in this.immArray do
            last <- u
        last
        
    [<Benchmark>]
    member this.FSharpxVectorEnumerate() =
        let mutable last = Unchecked.defaultof<_>
        for u in this.fsxVector do
            last <- u
        last
        
    [<Benchmark>]
    member this.VecEnumerate() =
        let mutable last = Unchecked.defaultof<_>
        for u in this.vector do
            last <- u
        last
    
[<MemoryDiagnoser>]
type VecOfArrayBenchmarks() =

    [<DefaultValue>]
    val mutable items: User[]

    [<GlobalSetup>]
    member this.Setup() =
        this.items <- Array.zeroCreate 1000
        for i=0 to 999 do
            let s = string i
            this.items.[i] <- { FirstName = "Alex" + s; LastName = "McCragh" + s; Age = i }
        
    [<GlobalCleanup>]
    member this.Cleanup() =
        this.items <- null
        
    [<Benchmark>] member this.ResizeArrayOfArray() = ResizeArray<_>(this.items)
    [<Benchmark>] member this.ImmutableListOfArray() = ImmutableList.Create<User>(this.items)
    [<Benchmark>] member this.FSharpListOfArray() = List.ofArray this.items
    [<Benchmark>] member this.FSharpxVecOfArray() = PersistentVector.ofSeq this.items
    [<Benchmark>] member this.VecOfArray() = Vec.ofArray this.items