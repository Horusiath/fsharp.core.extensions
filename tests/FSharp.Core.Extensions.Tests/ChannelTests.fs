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

module FSharp.Core.Extensions.Tests.ChannelTask

open System
open System.Threading
open System.Threading.Tasks
open FSharp.Core
open FsCheck
open Expecto
open FSharp.Control.Tasks.Builders

let private testChannel items =
    let w, r = Channel.unboundedMpsc ()
    for i in items do
        w.WriteAsync(i) |> ignore
    w, r
    
let inline private eval (vt: ValueTask<'a>): 'a = vt.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList "Channel" [

        testCase "readTo can return less elements than the buffer size" <| fun _ ->
            let _, r = testChannel [| 1..3 |]                
            let buf = Array.zeroCreate 5
            let span = Span buf
            let read = Channel.readTo span r
            Expect.equal read 3 "channel had 3 elements inside"
            Expect.equal buf [|1;2;3;0;0|] "output buffer should not be fully filled"
            
        testCase "readTo won't return more elements than the buffer size" <| fun _ ->
            let _, r = testChannel [| 1..10 |]
            let buf = Array.zeroCreate 5
            let span = Span buf
            let read = Channel.readTo span r
            Expect.equal read 5 "channel had 10 elements inside, but buffer size was 5"
            Expect.equal buf [|1;2;3;4;5|] "output buffer was fully filled"
            
        testCase "readTo works with empty channel" <| fun _ ->
            let _, r = testChannel [||]
            let buf = Array.zeroCreate 3
            let span = Span buf
            let read = Channel.readTo span r
            Expect.equal read 0 "channel was empty"
            Expect.equal buf [|0;0;0|] "nothing was written to the buffer"
                        
        testCase "select should not consume elements from other queues if they were not returned" <| fun _ ->
            let _, r1 = testChannel [| 1..3 |]
            let _, r2 = testChannel [| 11..13 |]
            let _, r3 = testChannel [| 21..23 |]
            let v1 = Channel.select [| r1; r2; r3 |] |> eval
            Expect.equal v1 1 "the first item from the first channel should be returned"
            let v2 = r2.ReadAsync() |> eval
            Expect.equal v2 11 "second (loosing) queue should be intact after select"
            let v3 = r3.ReadAsync() |> eval
            Expect.equal v3 21 "third (loosing) queue should be intact after select"
         
        testCase "select should consume elements from the queue, that has received element first" <| fun _ ->
            let w1, r1 = testChannel [||]
            let w2, r2 = testChannel [||]
            let selectVt = Channel.select [| r1; r2 |] // it's pending here
            // spawn two task
            // first will write to 1st queue after 200ms,
            // second will write to 2nd queue after 100ms
            let t1 = Task.run (fun () -> task {
                do! Task.Delay(200)
                do! w1.WriteAsync(1)
            })
            let t2 = Task.run (fun () -> task {
                do! Task.Delay(100)
                do! w2.WriteAsync(2)
            })
            Task.WaitAll(t1, t2) |> ignore
            let value = selectVt |> eval
            Expect.equal value 2 "second queue finished before first one"
   
        testCase "map should modify elements coming from source channel" <| fun _ ->
            let _, r1 = testChannel [| 1..3 |]
            let wrap = r1 |> Channel.map string
            let v1 = wrap.ReadAsync() |> eval
            Expect.equal v1 "1" "Channel.map maps upstream elements"
            let v2 = r1.ReadAsync() |> eval
            Expect.equal v2 2 "original channel is left intact"

    ]