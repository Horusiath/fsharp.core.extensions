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

module FSharp.Core.Extensions.Tests.AsyncSeq

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.Control.Tasks.Builders
open FSharp.Core

let private eval (x: ValueTask<'a>) = x.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList "AsyncSeq" [

        testCase "tryHead should pick first element" <| fun _ ->
            let actual =
                seq {
                    let mutable i = 0
                    while true do
                        i <- i + 1
                        yield i
                }
                |> AsyncSeq.ofSeq
                |> AsyncSeq.tryHead
                |> eval
                
            Expect.equal actual (Some 1) "a first element should be returned immediately"
            
        testCase "tryHead should not fail for empty sequence" <| fun _ ->
            let actual =
                []
                |> AsyncSeq.ofSeq
                |> AsyncSeq.tryHead
                |> eval
                
            Expect.equal actual None "returns None for empty sequence"
            
        testCase "tryLast should return last element" <| fun _ ->
            let actual =
                [1;2;3]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.tryHead
                |> eval
                
            Expect.equal actual (Some 3) "a first element should be returned immediately"
            
        testCase "tryLast should not fail for empty sequence" <| fun _ ->
            let actual =
                []
                |> AsyncSeq.ofSeq
                |> AsyncSeq.tryLast
                |> eval
                
            Expect.equal actual None "returns None for empty sequence"
            
        testProperty "fold should work over consecutive elements" <| fun (input: int[]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.fold (fun s a -> vtask {
                    do! Task.Yield()
                    return s + a
                }) 1
                |> eval
            let expected = 1 + (Array.sum input) 
            Expect.equal actual expected "returns None for empty sequence"
            
        testProperty "reduce should work over consecutive elements" <| fun (input: int[]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.reduce (fun s a -> vtask {
                    do! Task.Yield()
                    return s + a
                })
                |> eval
            let expected = if Array.isEmpty input then None else Some (Array.sum input)
            Expect.equal actual expected "returns None for empty sequence"
            
        testProperty "iteri should work over consecutive elements" <| fun (input: int[]) ->
            let expected = ref 0
            let t =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.iteri (fun i e -> unitVtask {
                    do! Task.Yield()
                    Expect.equal i (int64 !expected) "iteri should produce consecutive numbers"
                    Expect.equal e input.[int i] "iteri elements should be equivalent to incoming input"
                    incr expected })
            t.GetAwaiter().GetResult()
            
        testProperty "iteri should work over consecutive elements" <| fun (input: int[]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.mapi (fun i e -> vtask {
                    do! Task.Yield()
                    return e + (int i)
                })
                |> AsyncSeq.iteri (fun i e -> unitVtask {
                    let expected = input.[int i] + (int i)
                    Expect.equal e expected "mapi should map elements with their indexes"
                })
            actual.GetAwaiter().GetResult
            
        testProperty "collect should gather all incoming elements" <| fun (input: int[]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.map (fun e -> vtask {
                    do! Task.Yield()
                    return e
                })
                |> AsyncSeq.collect
                |> eval
                |> Array.ofSeq
            Expect.equal actual input "all inputs should be collected in the same order as origin"
            
        testProperty "choose should only pick correct elements" <| fun (input: int[]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.choose (fun e -> vtask {
                    do! Task.Yield()
                    if e % 2 = 0 then return Some (e+1)
                    else return None
                })
                |> AsyncSeq.collect
                |> eval
            
            for i in actual do
                Expect.isTrue (i % 2 <> 0) "choose should apply both mapping and filter"
                
        testProperty "filter should only pick correct elements" <| fun (input: int[]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.filter (fun e -> e % 2 = 0)
                |> AsyncSeq.collect
                |> eval
            
            for i in actual do
                Expect.isTrue (i % 2 = 0) "filter should not pass incorrect elements"
                
        testCase "bind should execute all sub-sequences untill completion" <| fun _ ->
            let actual =
                [1;3;3;2]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.bind (fun i ->
                    AsyncSeq.ofSeq (seq { for j = 1 to i do yield j }))
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [1; 1;2;3; 1;2;3; 1;2] "bound elements should be picked one after another until the end"
            
        testCase "skipWhile should omit all elements until the first one appears" <| fun _ ->
            let actual =
                [1;3;3;1;5; 2;1;3;4;1]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.skipWhile (fun e -> e % 2 = 1)
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [2;1;3;4;1] "should omit all elements until first even have been found"
            
        testCase "takeWhile should pick all elements until the first one appears" <| fun _ ->
            let actual =
                [1;3;3;1;5; 2;1;3;4;1]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.takeWhile (fun e -> e % 2 = 1)
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [1;3;3;1;5;] "should omit all elements after first even have been found"
        
        testCase "scan emits accumulating value" <| fun _ ->
            let actual =
                [1..5]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.scan (fun s e -> vtask { 
                    return s + e
                }) 1
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [2;4;7;11;16] "scan should return sequence of accumulated values"
            
        testCase "delay waits before producing next value" <| fun _ ->
            let sw = System.Diagnostics.Stopwatch()
            sw.Start()
            let t =
                [1..5]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.delay (TimeSpan.FromMilliseconds 100.)
                |> AsyncSeq.iter (fun i -> unitVtask {
                    let elapsed = sw.ElapsedMilliseconds
                    sw.Reset()
                    Expect.isGreaterThanOrEqual elapsed 100L "delayed element should comply to delay lower bound"
                })
            t.GetAwaiter().GetResult()
            
        testCase "withCancellation should be applied to underlying async sequence" <| fun _ ->
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let actual =
                [1..5]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.withCancellation cts.Token
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [] "cancellation should work on the underlying data type"
            
        testCase "grouped should chop incoming element into even batches" <| fun _ ->
            let actual =
                [1..20]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.grouped 6
                |> AsyncSeq.map (fun b -> ValueTask<_>(List.ofArray b))
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            let expected = [
                [1;2;3;4;5;6]
                [7;8;9;10;11;12]
                [13;14;15;16;17;18]
                [19;20] ]
            Expect.equal actual expected "grouped should return even-sized elements and the reminder"
            
        testCase "zip should combine both sequences" <| fun _ ->
            let left = AsyncSeq.ofSeq [1;2;3;4;5]
            let right = AsyncSeq.ofSeq [11;12;13;14;15;16]
            let actual = 
                AsyncSeq.zip left right
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            let expected = [(1,11); (2,12); (3,13); (4,14); (5,15)]
            Expect.equal actual expected "zip should combine elements together until first one closes"
            
        testProperty "singleton should return a single element" <| fun (i: int) ->
            let actual = 
                AsyncSeq.singleton i
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [i] "should pick first element and then close"
            
        testCase "deduplicate should remove consecutive duplicates" <| fun _ ->
            let actual = 
                AsyncSeq.ofSeq [1;1;1;2;3;3;1;1;2]
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [1;2;3;1;2] "deduplicate must remove consecutive duplicates, but not total uniqueness"
            
        testProperty "ofTask should return from successful task" <| fun (i: int) ->
            let actual = 
                AsyncSeq.ofTask (task {
                    do! Task.Yield()
                    return i
                })
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [i] "for successful task a result should be collected"
            
        testCase "ofTask should fail for failed task" <| fun _ ->
            let actual = 
                AsyncSeq.ofTask (Task.FromException<int> (Exception "Boom"))
                |> AsyncSeq.collect
            Expect.throws (fun () -> eval actual |> ignore) "for failed task a result should fail as well"
            
        testCase "ofTask should not return for cancelled task" <| fun _ ->
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let actual = 
                AsyncSeq.ofTask (Task.FromCanceled<int> (cts.Token))
                |> AsyncSeq.collect
                |> eval
            Expect.isEmpty actual "cancelled task should produce no result"
            
        testCase "ofFunc should produce results on demand" <| fun _ ->
            let list = [|Some 1; Some 2; Some 3; None; Some 4|]
            let actual = 
                AsyncSeq.ofFunc (fun i -> vtask {
                    return list.[int i]
                })
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [1;2;3] "func should produce elements until first None was provided"
        
        testCase "mergeParallel should return all combined results" <| fun _ ->
            let seqs = [|
                AsyncSeq.ofSeq [1;1] |> AsyncSeq.delay (TimeSpan.FromMilliseconds 100.)
                AsyncSeq.ofSeq [2;2;2] |> AsyncSeq.delay (TimeSpan.FromMilliseconds 100.)
                AsyncSeq.ofSeq [3;3;3;3] |> AsyncSeq.delay (TimeSpan.FromMilliseconds 100.)
            |]
            let sw = System.Diagnostics.Stopwatch()
            sw.Start()
            let actual = 
                AsyncSeq.mergeParallel seqs
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
                |> List.groupBy id
            let elapsed = sw.ElapsedMilliseconds
            sw.Stop()
            let expected = [
                (1, [1;1])
                (2, [2;2;2])
                (3, [3;3;3;3])
            ]
            Expect.equal actual expected "mergeParallel should return combined results"
            Expect.isLessThan elapsed 800L "mergeParallel should not run sub-sequent sequences one by one"
    ]
