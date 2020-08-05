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
open FSharp.Core

let private eval (x: ValueTask<'a>) = x.GetAwaiter().GetResult()
let private ueval (x: ValueTask) = x.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testSequenced <| testList "AsyncSeq" [

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
                |> AsyncSeq.tryLast
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
            
        testProperty "mapAsynci should work over consecutive elements" <| fun (input: int[]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.mapAsynci (fun i e -> vtask {
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
                |> AsyncSeq.mapAsync (fun e -> vtask {
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
                
        testProperty "bind should execute all sub-sequences until completion" <| fun (input: int[][]) ->
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.bind AsyncSeq.ofSeq
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            let expected = Array.concat input |> Array.toList
            Expect.equal actual expected "bound elements should be picked one after another until the end"
            
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
                [1L..5L]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.delay (fun _ -> TimeSpan.FromMilliseconds 100.)
                |> AsyncSeq.iter (fun i -> unitVtask {
                    let elapsed = sw.ElapsedMilliseconds
                    Expect.isGreaterThanOrEqual elapsed (i*100L) (sprintf "delayed element [%i] should comply to delay lower bound" i)
                })
            t.GetAwaiter().GetResult()
            sw.Stop()
            
        testCase "withCancellation should be applied to underlying async sequence" <| fun _ ->
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let actual =
                [1..5]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.withCancellation cts.Token
                |> AsyncSeq.collect
                |> Task.ofValueTask
                |> Task.secure
            Expect.isError (actual.GetAwaiter().GetResult()) "cancellation should work on the underlying data type"
            
        testCase "grouped should chop incoming element into even batches" <| fun _ ->
            let actual =
                [1..20]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.grouped 6
                |> AsyncSeq.mapAsync (fun b -> ValueTask<_>(List.ofArray b))
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
                |> AsyncSeq.deduplicate (=)
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
            
        testCase "mapParallel should pick elements in parallel" <| fun _ ->
            let actual =
                [|1..40|]
                |> AsyncSeq.ofSeq
                |> AsyncSeq.mapParallel 4 (fun i -> vtask {
                    do! Task.Delay(15)
                    return i + 1
                })
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.containsAll actual [|2..41|] "result should contain necessary elements"
            
        testCase "repeat should be composable into range sequence" <| fun _ ->
            let actual =
                AsyncSeq.repeat 1
                |> AsyncSeq.scan (fun x y -> ValueTask<_>(x + y)) 0
                |> AsyncSeq.take 5L
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual [1;2;3;4;5] "repeat should work"
        
        testCase "mergeParallel should return all combined results" <| fun _ ->
            let seqs = [|
                AsyncSeq.ofSeq [1;1] |> AsyncSeq.delay (fun _ -> TimeSpan.FromMilliseconds 100.)
                AsyncSeq.ofSeq [2;2;2] |> AsyncSeq.delay (fun _ -> TimeSpan.FromMilliseconds 100.)
                AsyncSeq.ofSeq [3;3;3;3] |> AsyncSeq.delay (fun _ -> TimeSpan.FromMilliseconds 100.)
            |]
            let actual = 
                AsyncSeq.mergeParallel seqs
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
                |> List.groupBy id
                |> Map.ofList
            let expected = Map.ofList [
                (1, [1;1])
                (2, [2;2;2])
                (3, [3;3;3;3])
            ]
            Expect.equal actual expected "mergeParallel should return combined results"
            
        testProperty "merge should return all combined results in order" <| fun (a: int[][]) ->
            let expected = Array.concat a |> Array.toList
            let actual =
                a
                |> Array.map AsyncSeq.ofSeq
                |> AsyncSeq.merge
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual expected "merge should return ordered result"
            
        testProperty "groupBy should work" <| fun (input: int list) ->
            let expected =
                input
                |> Seq.groupBy (fun i -> i % 100)
                |> Map.ofSeq
                |> Map.map (fun k v -> Set.ofSeq v)
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.groupBy 100 10 (fun i -> i % 100)
                |> AsyncSeq.mapParallel 10 (fun (k, s) -> vtask {
                    let! items = s |> AsyncSeq.collect
                    return (k, Set.ofSeq items)
                })
                |> AsyncSeq.collect
                |> eval
                |> Map.ofSeq
                
            Expect.equal actual expected "groupBy should return correct groups"
            for (KeyValue(k, v)) in actual do
                Expect.all v (fun i -> i % 100 = k) "groups should be created correctly"
                
        testCase "interleave should produce value between input elements" <| fun _ ->
            let input = ["a";"b";"c";"d"]
            let expected = ["a";"a-b";"b";"b-c";"c";"c-d";"d"]
            let actual =
                input
                |> AsyncSeq.ofSeq
                |> AsyncSeq.interleave (fun prev next -> prev + "-" + next)
                |> AsyncSeq.collect
                |> eval
                |> List.ofSeq
            Expect.equal actual expected "interleave should produce values between elements from upstream"
            
        testCase "ignore should wait for all elements to complete" <| fun _ ->
            let input = [|1..10|]
            let expected = input |> Array.sum
            let mutable sum = 0
            input
            |> AsyncSeq.ofSeq
            |> AsyncSeq.map (fun i -> sum <- sum + i)
            |> AsyncSeq.ignore
            |> ueval
            Expect.equal sum expected "ignore value task should complete after iterating over all elements"
            
        testCase "split should partition upstream after first matching case" <| fun _ ->
            let writer, reader = Channel.unboundedMpsc ()
            let input = [| 1; 3; 3; 5; 2; 4; 1; 7; 6 |]
            for i in input do writer.WriteAsync(i) |> ueval
            writer.Complete()
            let expected = [| 1; 3; 3; 5; |], [| 2; 4; 1; 7; 6 |]
            let left, right =
                reader
                |> AsyncSeq.ofChannel
                |> AsyncSeq.split (fun x -> x % 2 = 0)
            
            let a = left |> AsyncSeq.collect |> eval
            Expect.equal a (fst expected) "left part should collect up to first even number"
            let b = right |> AsyncSeq.collect |> eval
            Expect.equal b (snd expected) "right part should collect up after first even number (inclusive)"
            
        testCase "unfold should produce values until None is returned" <| fun _ ->
            let expected = [| 1..5 |] |> Array.rev
            let actual =
                AsyncSeq.unfold (fun state -> vtask {
                    return if state = 0 then ValueNone else ValueSome (state-1, state) 
                }) 5
                |> AsyncSeq.collect
                |> eval
            Expect.equal actual expected "unfold should produce 5 elements"
            
        testCase "buffered should not wait for buffer to be full" <| fun _ ->
            let writer, reader = Channel.unboundedMpsc()
            let aseq =
                reader
                |> AsyncSeq.ofChannel
                |> AsyncSeq.buffered 3
            let enum = aseq.GetAsyncEnumerator()
            
            // pulling of empty upstream should not complete immediatelly, but rather wait for at least one element to appear
            let vt = enum.MoveNextAsync()
            Expect.isFalse vt.IsCompleted "buffered enumerator should wait for at least 1 item to arrive"
            writer.WriteAsync(1) |> ueval
            Expect.isTrue (eval vt) "1st (eager) pull should return true"
            Expect.equal enum.Current [| 1 |] "element written upstream should appear downstream immediately"
            
            // now we push more than buffered operator capacity allowed to, we shouldn't overflow buffered size
            for i in 2..6 do writer.WriteAsync(i) |> ueval
            Expect.isTrue (eval <| enum.MoveNextAsync()) "2nd pull should not fail"
            Expect.equal enum.Current [| 2; 3; 4 |] "pulled buffer should not overflow configured capacity"
            
            // now pull rest of the elements
            writer.Complete()
            Expect.isTrue (eval <| enum.MoveNextAsync()) "3rd pull should not fail"
            Expect.equal enum.Current [| 5; 6 |] "should fetch remaining elements"
            Expect.isFalse (eval <| enum.MoveNextAsync()) "4rd pull should notice closed upstream"
            enum.DisposeAsync() |> ueval
            
        testCase "timer should not produce events before expected interval" <| fun _ -> 
            // Scenario: we configure timer to tick every 100ms but... downstream we wait for 500ms
            // what we DON'T want to see, is a "cumulative" ticking (so after 500ms, we don't want to yield 4-5 ticks in a row)
            let aseq = AsyncSeq.timer (TimeSpan.FromMilliseconds 100.)
            let enum = aseq.GetAsyncEnumerator()
            try (unitTask {
                let! hasNext = enum.MoveNextAsync()
                Expect.isTrue hasNext "1st pull"
                Expect.isGreaterThanOrEqual enum.Current (TimeSpan.FromMilliseconds 85.) "1st pull is delayed by 100ms"
                
                do! Task.Delay 500

                let! hasNext = enum.MoveNextAsync()
                Expect.isTrue hasNext "2nd pull"
                Expect.isGreaterThanOrEqual enum.Current (TimeSpan.FromMilliseconds 485.) "2nd pull is delayed by 500ms because of sleep in current thread"

                let! hasNext = enum.MoveNextAsync()
                Expect.isTrue hasNext "3rd pull"
                // since timer is not super precise use 85ms to give an error margin
                Expect.isGreaterThanOrEqual enum.Current (TimeSpan.FromMilliseconds 85.) "3rd pull shouldn't be immediate even though 2nd was delayed" }).GetAwaiter().GetResult()
            finally
                (enum :> IAsyncDisposable).DisposeAsync() |> ueval
            
        testCase "into(true) should push items into channel" <| fun _ ->
            let (writer, reader) = Channel.unboundedSpsc ()
            AsyncSeq.ofSeq [1;2;3]
            |> AsyncSeq.into true writer
            |> eval
            
            let mutable item = Unchecked.defaultof<_>
            Expect.isTrue (reader.TryRead(&item)) "reader should read 1st element"
            Expect.equal item 1 "1st element is 1"
            Expect.isTrue (reader.TryRead(&item)) "reader should read 2nd element"
            Expect.equal item 2 "2nd element is 2"
            Expect.isTrue (reader.TryRead(&item)) "reader should read 3rd element"
            Expect.equal item 3 "3rd element is 3"
            Expect.isFalse (reader.TryRead(&item)) "only 3 elements where pushed before completion"
            
            Expect.isTrue reader.Completion.IsCompletedSuccessfully "AsyncSeq.into(true) should close writer upon completion"
            
        testCase "into(false) should leave the channel open" <| fun _ ->
            let (writer, reader) = Channel.unboundedSpsc ()
            AsyncSeq.ofSeq [1]
            |> AsyncSeq.into false writer
            |> eval
            
            let mutable item = Unchecked.defaultof<_>
            Expect.isTrue (reader.TryRead(&item)) "reader should read 1st element"
            Expect.equal item 1 "1st element is 1"
            Expect.isFalse (reader.TryRead(&item)) "only 1 element was pushed"
            
            Expect.isFalse reader.Completion.IsCompleted "AsyncSeq.into(false) should keep open channel upon completion"
            
            Expect.isTrue (writer.TryWrite(2)) "it should be possible to write to channel again"
            Expect.isTrue (reader.TryRead(&item)) "reader should read 2nd element"
            Expect.equal item 2 "2nd element is 2"
            
            writer.Complete()
    ]
