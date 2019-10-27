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

module FSharp.Core.Extensions.Tests.Enum

open FSharp.Core
open FsCheck
open Expecto

[<NoEquality;NoComparison>]
type Point = { x: int; y: int }

[<Tests>]
let tests =
    testList "Enum" [

        testProperty "should be able to iterate over enumerator" <| fun (array: ResizeArray<int>) ->
            let e = array.GetEnumerator()
            let mutable i = 0
            e |> Enum.iter (fun j -> i <- i + j)
            let expected = array |> Seq.sum
            Expect.equal i expected "result should be the same as of sequential sum"
            
        testProperty "filter should be composable" <| fun (array: ResizeArray<int>) ->
            let e = array.GetEnumerator()
            let sum =
                e
                |> Enum.filter (fun i -> i % 2 = 0)
                |> Enum.fold (+) 0
            Expect.isTrue (sum % 2 = 0) "filter should work"
    ]
