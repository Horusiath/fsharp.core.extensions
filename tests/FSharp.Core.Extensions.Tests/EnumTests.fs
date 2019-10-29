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
open FSharp.Core.Atomic.Operators
open FsCheck
open Expecto

[<NoEquality;NoComparison>]
type Point = { x: int; y: int }

let private disposableEnumerator (atom) =
    { new  System.Collections.Generic.IEnumerator<int> with
        member this.Current: int = 0
        member this.Current: obj = null
        member this.Reset() = ()
        member this.Dispose() = (atom := true) |> ignore
        member this.MoveNext(): bool = false }

[<Tests>]
let tests =
    testList "Enum" [

        testProperty "should be able to iterate over enumerator" <| fun (array: ResizeArray<int>) ->
            let i = atom 0
            Enum.from array |> Enum.iter (fun j -> (i := !i + j) |> ignore)
            let expected = array |> Seq.sum
            Expect.equal !i expected "result should be the same as of sequential sum"
            
        testCase "iter should dispose enumerator at the end of process" <| fun _ ->
            let disposed = atom false
            let e = disposableEnumerator disposed
            e |> Enum.iter ignore
            Expect.isTrue !disposed "enumerator dispose should be called"
            
        testCase "fold should dispose enumerator at the end of process" <| fun _ ->
            let disposed = atom false
            let e = disposableEnumerator disposed
            let _ = e |> Enum.fold (+) 0
            Expect.isTrue !disposed "enumerator dispose should be called"
            
        testCase "reduce should dispose enumerator at the end of process" <| fun _ ->
            let disposed = atom false
            let e = disposableEnumerator disposed
            let _ = e |> Enum.reduce (+)
            Expect.isTrue !disposed "enumerator dispose should be called"
            
        testCase "tryHead should dispose enumerator at the end of process" <| fun _ ->
            let disposed = atom false
            let e = disposableEnumerator disposed
            let _ = e |> Enum.tryHead
            Expect.isTrue !disposed "enumerator dispose should be called"
            
        testCase "tryLast should dispose enumerator at the end of process" <| fun _ ->
            let disposed = atom false
            let e = disposableEnumerator disposed
            let _ = e |> Enum.tryLast
            Expect.isTrue !disposed "enumerator dispose should be called"

        testProperty "fold should work over all elements" <| fun (array: ResizeArray<int>) ->
            let actual = array |> Enum.from |> Enum.fold (+) 0
            let expected = array |> Seq.sum
            Expect.equal actual expected "result should be the same as of sequential sum"
            
        testProperty "reduce should work for non-empty list" <| fun (NonEmptyArray array) ->
            let e = (ResizeArray array).GetEnumerator()
            let actual = e |> Enum.reduce (+)
            let expected = array |> Array.sum
            Expect.equal actual (ValueSome expected) "result should be the same as of sequential sum"
            
        testCase "reduce should not fail for empty list" <| fun _ ->
            let sum = ResizeArray<int>().GetEnumerator() |> Enum.reduce (+)
            Expect.equal sum ValueNone "result should be None"
            
        testProperty "count should properly count the number of elements" <| fun array ->
            let expected = array |> Array.length
            let actual = (array :> int seq).GetEnumerator() |> Enum.count
            Expect.equal actual (int64 expected) "unexpected count value"
                        
        testProperty "should be composable" <| fun (array: ResizeArray<int>) ->
            let sum =
                Enum.from array
                |> Enum.filter (fun i -> i % 2 = 0)
                |> Enum.skip 2L
                |> Enum.take 2L
                |> Enum.fold (+) 0
            Expect.isTrue (sum % 2 = 0) "filter should work"
            
        testCase "tryHead should return first element" <| fun _ ->
            let list = [1; 2; 3]
            let head =
                Enum.ofSeq list
                |> Enum.tryHead
            Expect.equal head (ValueSome 1) "first element should be returned"
            
        testCase "tryHead should not fail for empty collection" <| fun _ ->
            let list = []
            let head =
                Enum.ofSeq list
                |> Enum.tryHead
            Expect.equal head ValueNone "tryHead should return none"
            
        testProperty "tryLast should return last element" <| fun (NonEmptyArray array) ->
            let list = array
            let head =
                Enum.ofSeq list
                |> Enum.tryLast
            let expected = array.[array.Length - 1]
            Expect.equal head (ValueSome expected) "last element should be returned"
            
        testCase "tryLast should not fail for empty collection" <| fun _ ->
            let list = []
            let head =
                Enum.ofSeq list
                |> Enum.tryLast
            Expect.equal head ValueNone "tryLast should return none"
            
        testProperty "filter should return only filtered elements" <| fun (array: ResizeArray<int>) ->
            let predicate = fun i -> i % 2 = 0
            let result =
                Enum.from array
                |> Enum.filter predicate
                |> Enum.fold (fun tail head -> head::tail) []
            let ok = result |> List.forall predicate
            Expect.isTrue ok "filter should obey predicate"
            
        testProperty "skip should never return first N elements" <| fun (array: ResizeArray<int>, n:uint16) ->
            let m = min array.Count (int n)
            let expected =
                array
                |> Seq.skip m
                |> Seq.toList
                |> List.rev
            let actual =
                Enum.from array
                |> Enum.skip (int64 n)
                |> Enum.fold (fun tail head -> head::tail) []
            Expect.equal actual expected "skip should work like Seq.skip"
            
        testProperty "take should return first N elements" <| fun (array: ResizeArray<int>, n:uint16) ->
            let m = min array.Count (int n)
            let actual =
                Enum.from array
                |> Enum.take (int64 m)
                |> Enum.count
            Expect.isLessThanOrEqual actual (int64 n) (sprintf "take should return up to %i elements" n)
            
        testProperty "map should return mapped elements" <| fun (NonEmptyArray array) ->
            let actual =
                Enum.ofSeq array
                |> Enum.map ((+) 1)
                |> Enum.fold (+) 0
            let expected = Array.sum array + array.Length
            Expect.equal actual expected "map should apply value to every element"
            
        testProperty "choose should return only elements for which mapping function returned a value" <| fun (array: ResizeArray<uint32>) ->
            let result =
                Enum.ofSeq array
                |> Enum.choose (fun i -> if i % 2u = 0u then Some (i+1u) else None)
                |> Enum.fold (fun t h -> h::t) []
            let ok = result |> List.forall (fun i -> i % 2u = 1u)
            Expect.isTrue ok "choose should apply both predicate test and mapping"
    ]
