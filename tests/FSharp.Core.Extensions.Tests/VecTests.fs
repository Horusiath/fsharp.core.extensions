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

module FSharp.Core.Extensions.Tests.Vec

open FSharp.Core
open FsCheck
open Expecto
open Expecto.ExpectoFsCheck

[<Tests>]
let tests =
    testList "Vec" [

        testCase "empty vector should be empty" <| fun _ ->
            let v = Vec.empty
            Expect.isTrue (Vec.isEmpty v) "vector defined as empty should be empty"

        testProperty "should create vector from array" <| fun (a: int[]) ->
            let v = Vec.ofArray a
            Expect.equal (Vec.length v) (Array.length a) "vector constructed from array should have the same length"
            for i=0 to a.Length - 1 do
                let x = a.[i]
                let y = v.[i]
                Expect.equal x y (sprintf "vector's element at index %i should be the same as array's" i)

        testProperty "should create array back from vector" <| fun (a: int[]) ->
            let a' = a |> Vec.ofArray |> Vec.toArray
            Expect.equal a' a "array constructed from the vector should be the same as the vector origin"

        testProperty "should be able to iterate over vector elements" <| fun (a: int[]) ->
            let v = Vec.ofArray a
            let mutable i = 0
            for item in v do
                let item' = a.[i]
                Expect.equal item item' (sprintf "vector iterator should work for element at position %i" i)
                i <- i + 1
        
        testProperty "should be able to find a correct element by using indexOf" <| fun (NonEmptyArray a) ->
            let v: Vec<int> = Vec.ofArray a
            let expected = Random.between 0 a.Length
            let item = a.[expected]
            let actual = v |> Vec.indexOf item
            Expect.equal actual expected "vector should find the item expected at the same position as originating array"
            
        testProperty "should be able to fold over the elements" <| fun (a: int[]) ->
            let v = Vec.ofArray a
            let sum1 = a |> Array.fold max 0
            let sum2 = v |> Vec.fold max 0
            Expect.equal sum1 sum2 "vector fold should work like array fold"
            
        testProperty "should be able to reduce over the elements in non-empty case" <| fun (NonEmptyArray a) ->
            let v: Vec<int> = Vec.ofArray a
            let sum1 = a |> Array.reduce max
            let sum2 = v |> Vec.reduce max 
            Expect.equal (ValueSome sum1) sum2 "vector reduce should return the same result as array reduce"
            
        testCase "should be able to reduce over the elements in empty case" <| fun _ ->
            let v = Vec.empty
            let value = v |> Vec.reduce max
            Expect.equal value ValueNone "Vec.reduce should not fail in empty case"
        
        testProperty "should be able to find an element in non-empty case" <| fun (NonEmptyArray a) ->
            let v: Vec<int> = Vec.ofArray a
            let expected = Random.pick a
            let actual = v |> Vec.find ((=) expected)
            Expect.equal actual (ValueSome expected) "should be able to find an expected element"
            
        testCase "find should not fail in an empty case" <| fun _ ->
            let v = Vec.empty
            let value = v |> Vec.find ((=) 123)
            Expect.equal value ValueNone "Vec.find should not fail in empty case"
    ]
