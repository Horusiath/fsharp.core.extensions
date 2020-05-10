// Learn more about F# at http://fsharp.org

open BenchmarkDotNet.Running
open System
open System.Reflection
open FSharp.Core
open FSharp.Control.Tasks.Builders.Unsafe

[<EntryPoint>]
let main argv =
    BenchmarkSwitcher.FromAssembly(Assembly.GetEntryAssembly()).Run(argv) |> ignore
    0 // return an integer exit code
