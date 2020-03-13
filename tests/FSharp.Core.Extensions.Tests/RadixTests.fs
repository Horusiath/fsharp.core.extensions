(*

Copyright 2019 BRadixosz Sypytkowski

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

module FSharp.Core.Extensions.Tests.Radix

open FSharp.Core
open Expecto
open System
open System.Text
open System.Collections.Generic

[<Tests>]
let tests =
    testList "Radix Tree" [

        testCase "empty should return an empty map" <| fun _ ->
            let a = Radix.empty
            Expect.isTrue (Radix.isEmpty a) "Radix.empty should correctly be identified as empty"
            Expect.equal (Radix.count a) 0 "Radix.count should be zero for empty map"
            
        testProperty "should be able to add multiple entries" <| fun (entries: (string * int) []) ->
            let mutable a = Radix.empty
            for (k, v) in entries do
                a <- Radix.add k v a
                Expect.isFalse (Radix.isEmpty a) "after adding an element, Radix should no longer be empty"
                Expect.isTrue (Radix.find k a |> ValueOption.isSome) "Radix should be able to retrieve inserted element"
                
        testProperty "should be able to construct map from sequence of elements" <| fun (entries: (string * int) []) ->
            let a = Radix.ofSeq entries
            let distinct = entries |> Array.distinctBy fst
            Expect.equal a.Count distinct.Length "Radix map should contain all unique elements of provided array"
            
        testProperty "should be able to remove inserted elements" <| fun (entries: (string * int) []) ->
            let mutable a = Radix.ofSeq entries
            for (KeyValue (k, v)) in a do
                a <- Radix.remove k a
                Expect.equal (Radix.find k a) ValueNone <| sprintf "Value for key %A should no longer be present" (k)
                
        ftestCase "should be able to read all elements with given prefix" <| fun _ ->
            let input = [
                ("romane", 1)
                ("romanus", 2)
                ("romulus", 3)
                ("rubens", 4)
                ("ruber", 5)
                ("rubicon", 6)
                ("rubicundus", 7) ]
            let a = Radix.ofSeq input
            let actual = a |> Radix.prefixed "rube" |> List.ofSeq
            let expected = [
                KeyValuePair<_,_>("rubens", 4)
                KeyValuePair<_,_>("ruber", 5) ]
            Expect.equal actual expected "Radix map should return all entries with keys starting with 'rube'"
    ]

