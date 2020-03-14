namespace Benchmarks

open System.Collections.Immutable
open System.IO
open BenchmarkDotNet.Attributes
open FSharp.Core
open FSharp.Core.Operators

[<MemoryDiagnoser>]
type RadixAddSmallBenchmark() =
    
    let mutable small = [||]
    let mutable smallTuples = [||]
    
    [<GlobalSetup>]
    member __.Setup() =
        small <- [|
                "romane"     => 1
                "romanus"    => 2
                "romulus"    => 3
                "rubens"     => 4
                "ruber"      => 5
                "rubicon"    => 6
                "rubicundus" => 7 |]
        smallTuples <- small |> Array.map (fun e -> (e.Key, e.Value))
        
    [<GlobalCleanup>]
    member __.Cleanup() =
        small <- [||]
        smallTuples <- [||]
        
    [<Benchmark>]
    member __.ImmutableSortedDictionaryAddSmall() =
        ImmutableSortedDictionary.CreateRange(small)
        
    [<Benchmark>]
    member __.FSharpMapAddSmall() =
        Map.ofArray smallTuples
        
    [<Benchmark>]
    member __.RadixAddSmall() =
        Radix.ofSeq smallTuples
        
[<MemoryDiagnoser>]
type RadixAddHugeBenchmark() =
    
    let mutable thesaurus = [||]
    
    [<GlobalSetup>]
    member __.Setup() =
        thesaurus <- File.ReadAllText("words.txt").Split(',') |> Array.map (fun w -> w, 1L)
        
    [<GlobalCleanup>]
    member __.Cleanup() =
        thesaurus <- [||]
        
    [<Benchmark>]
    member __.ImmutableSortedDictionaryAddSmall() =
        ImmutableSortedDictionary.CreateRange(thesaurus |> Seq.map (fun (k,v) -> k => v))
        
    [<Benchmark>]
    member __.FSharpMapAddSmall() =
        Map.ofArray thesaurus
        
    [<Benchmark>]
    member __.RadixAddSmall() =
        Radix.ofSeq thesaurus
        
[<MemoryDiagnoser>]
type RadixFindBenchmark() =
    
    let mutable sortedDict = Unchecked.defaultof<_>
    let mutable map = Unchecked.defaultof<_>
    let mutable radix = Unchecked.defaultof<_>
    
    [<GlobalSetup>]
    member __.Setup() =
        let src = File.ReadAllText("words.txt").Split(',')
        sortedDict <- src |> Seq.map (fun w -> w => 1L) |> ImmutableSortedDictionary.CreateRange
        map <- src |> Seq.map (fun w -> (w, 1L)) |> Map.ofSeq
        radix <- src |> Seq.map (fun w -> (w, 1L)) |> Radix.ofSeq
        
    [<GlobalCleanup>]
    member __.Cleanup() =
        sortedDict <- Unchecked.defaultof<_>
        map <- Unchecked.defaultof<_>
        radix <- Unchecked.defaultof<_>

    [<Benchmark>]
    member __.ImmutableSortedDictionaryFind() = sortedDict.TryGetValue("career")
    
    [<Benchmark>]
    member __.MapFind() = Map.tryFind "career" map
    
    [<Benchmark>]
    member __.RadixFind() = Radix.find "career" radix
    
[<MemoryDiagnoser>]
type RadixPrefixBenchmark() =
    
    let mutable sortedDict = Unchecked.defaultof<_>
    let mutable map = Unchecked.defaultof<_>
    let mutable radix = Unchecked.defaultof<_>
    
    [<GlobalSetup>]
    member __.Setup() =
        let src = File.ReadAllText("words.txt").Split(',')
        sortedDict <- src |> Seq.map (fun w -> w => 1L) |> ImmutableSortedDictionary.CreateRange
        map <- src |> Seq.map (fun w -> (w, 1L)) |> Map.ofSeq
        radix <- src |> Seq.map (fun w -> (w, 1L)) |> Radix.ofSeq
        
    [<GlobalCleanup>]
    member __.Cleanup() =
        sortedDict <- Unchecked.defaultof<_>
        map <- Unchecked.defaultof<_>
        radix <- Unchecked.defaultof<_>

    [<Benchmark>]
    member __.ImmutableSortedDictionaryPrefix() =
        sortedDict
        |> Seq.skipWhile (fun e -> not <| e.Key.StartsWith("care"))
        |> Seq.takeWhile (fun e -> e.Key.StartsWith("care"))
        |> Seq.toArray
    
    [<Benchmark>]
    member __.MapPrefix() =
        map
        |> Seq.skipWhile (fun e -> not <| e.Key.StartsWith("care"))
        |> Seq.takeWhile (fun e -> e.Key.StartsWith("care"))
        |> Seq.toArray
    
    [<Benchmark>]
    member __.RadixPrefix() = Radix.prefixed "care" radix |> Seq.toArray