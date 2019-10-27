module FSharp.Core.Extensions.Tests.Program

open System
open Expecto

[<EntryPoint>]
let main argv =
    Tests.runTestsInAssembly defaultConfig argv
