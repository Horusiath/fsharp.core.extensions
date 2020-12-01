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

module FSharp.Core.Extensions.Tests.ScheduleTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FSharp.Core
open FSharp.Core.Atomic.Operators
open FsCheck
open Expecto
open FSharp.Control.Tasks.Affine.Unsafe
open MBrace.FsPickler

let private eval (x: ValueTask<'a>) = x.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testSequenced <| testList "Schedule" [
        testCase "now should execute immediately" <| fun _ ->
            let mutable s = Schedule.now
            // run this in loop to ensure that the behavior doesn't change over multiple calls
            for i=0 to 10 do
                Expect.equal s.Delay (ValueSome TimeSpan.Zero) "Schedule.now delay should be instantaneous"
                s <- s.Advance()
                
        testCase "never should return infinite timespan" <| fun _ ->
            let mutable s = Schedule.never
            // run this in loop to ensure that the behavior doesn't change over multiple calls
            for i=0 to 10 do
                Expect.equal s.Delay (ValueSome Timeout.InfiniteTimeSpan) "Schedule.never delay should be infinite"
                s <- s.Advance()
                
        testCase "after should continuously return the same value" <| fun _ ->
            let delay = TimeSpan.FromSeconds 1.
            let mutable s = Schedule.after delay
            // run this in loop to ensure that the behavior doesn't change over multiple calls
            for i=0 to 10 do
                Expect.equal s.Delay (ValueSome delay) "Schedule.after delay should be constant"
                s <- s.Advance()
                
        testCase "completed should complete immediately" <| fun _ ->
            let s = Schedule.completed
            Expect.isTrue (s.Delay.IsNone) "Schedule.completed should complete immediately"
              
        testCase "once should only execute once" <| fun _ ->
            let expected = TimeSpan.FromSeconds 1.
            let s = Schedule.once expected 
            
            Expect.equal s.Delay (ValueSome expected) "Schedule.once delay should be using underlying scheduler"
            Expect.isTrue (s.Advance().Delay.IsNone) "Schedule.once should complete 2nd time"
            
        testCase "times should execute specified number of times" <| fun _ ->
            let expected = TimeSpan.FromSeconds 1.
            let count = 3
            let mutable s = Schedule.after expected |> Schedule.times count
            // run this in loop to ensure that the behavior doesn't change over multiple calls
            for i=1 to count do
                Expect.equal s.Delay (ValueSome expected) <| sprintf "Schedule.time should use underlying scheduler delay (%i time)" i
                s <- s.Advance()
             
            Expect.isTrue (s.Advance().Delay.IsNone) <| sprintf "Schedule.time should complete after %i executions" count
            
        testCase "max completes once either of its components complete" <| fun _ ->
            let a = Schedule.after (TimeSpan.FromSeconds 10.) |> Schedule.times 2
            let b = Schedule.after (TimeSpan.FromSeconds 5.) |> Schedule.times 5
            let mutable s = Schedule.max a b
            
            Expect.equal s.Delay (ValueSome (TimeSpan.FromSeconds 10.)) "Schedule.max should pick higher value"            
            s <- s.Advance()
            Expect.equal s.Delay (ValueSome (TimeSpan.FromSeconds 10.)) "Schedule.max should pick higher value"            
            Expect.isTrue (s.Advance().Delay.IsNone) "Schedule.max should complete 3rd time"
            
        testCase "min completes once both of its components complete" <| fun _ ->
            let a = Schedule.after (TimeSpan.FromSeconds 10.) |> Schedule.times 5
            let b = Schedule.after (TimeSpan.FromSeconds 5.) |> Schedule.times 2
            let mutable s = Schedule.min a b
            
            for i=1 to 2 do
                Expect.equal s.Delay (ValueSome (TimeSpan.FromSeconds 5.)) <| sprintf "Schedule.min should use underlying scheduler delay (%i time)" i
                s <- s.Advance()
                
            for i=3 to 5 do
                Expect.equal s.Delay (ValueSome (TimeSpan.FromSeconds 10.)) <| sprintf "Schedule.min should use underlying scheduler delay (%i time)" i
                s <- s.Advance()

            Expect.isTrue (s.Advance().Delay.IsNone) "Schedule.max should complete after 5th time"
            
        testCase "andThen uses 1st scheduler then 2nd one" <| fun _ ->
            let a = Schedule.after (TimeSpan.FromSeconds 10.) |> Schedule.times 5
            let b = Schedule.after (TimeSpan.FromSeconds 5.) |> Schedule.times 2
            let mutable s = a |> Schedule.andThen b
            
            for i=1 to 5 do
                Expect.equal s.Delay (ValueSome (TimeSpan.FromSeconds 10.)) <| sprintf "Schedule.andThen should use first scheduler delay (%i time)" i
                s <- s.Advance()
                
            for i=6 to 7 do
                Expect.equal s.Delay (ValueSome (TimeSpan.FromSeconds 5.)) <| sprintf "Schedule.andThen should use second scheduler delay (%i time)" i
                s <- s.Advance()

            Expect.isTrue (s.Advance().Delay.IsNone) "Schedule.andThen should complete after 7th time"
            
        testCase "jittered should operate within provided bounds" <| fun _ ->
            let min, max = TimeSpan.FromSeconds 5., TimeSpan.FromSeconds 20.
            let mutable s =
                Schedule.after (TimeSpan.FromSeconds 10.)
                |> Schedule.jittered 0.5 2.0
                
            for i=1 to 10 do
                Expect.isGreaterThan s.Delay (ValueSome min) <| sprintf "Schedule.jittered keep up with the lower bound (%i time)" i
                Expect.isLessThan s.Delay (ValueSome max) <| sprintf "Schedule.jittered keep up with the upper bound (%i time)" i
                s <- s.Advance()
                
        testCase "spaced should return result immediately for empty schedule" <| fun _ ->
            let value = Schedule.spaced (fun () -> ValueTask<_>(1)) CancellationToken.None Schedule.completed
            Expect.isTrue value.IsCompletedSuccessfully "spaced for complete schedule should never delay"
            let actual = value |> eval |> Seq.toList
            Expect.equal actual [1] "spaced should return computed result"
        
        testCase "spaced should execute N+1 number of times" <| fun _ ->
            let sw = Stopwatch.StartNew()
            let results =
                Schedule.after (TimeSpan.FromMilliseconds 100.)
                |> Schedule.times 3
                |> Schedule.spaced (fun () -> ValueTask<_>(1)) CancellationToken.None
                |> eval
            sw.Stop()
            Expect.isLessThan sw.ElapsedMilliseconds 400L "schedule delay should run N times"
            Expect.isGreaterThan sw.ElapsedMilliseconds 300L "schedule delay should run N times"
            Expect.equal (List.ofSeq results) [1;1;1;1] "spaced action should execute N+1 times"
            
        testCase "retry should execute N+1 times (1 for first action + for each retry tick)" <| fun _ ->
            let (result, errors) =
                Schedule.now
                |> Schedule.times 3
                |> Schedule.retry (fun prev -> uvtask {
                    let msg = "A" + (prev |> Option.map (fun e -> e.Message) |> Option.defaultValue "")
                    failwith msg
                }) CancellationToken.None
                |> eval
            Expect.equal result None "retry should never return value if action never succeed"
            let msgs = errors |> List.map (fun e -> e.Message)
            Expect.equal msgs ["A";"AA";"AAA";"AAAA"] "retry should return all errors, that happened during execution"
            
        testCase "retry should execute return successful value" <| fun _ ->
            let (result, errors) =
                Schedule.now
                |> Schedule.retry (fun prev -> uvtask {
                    match prev with
                    | Some _ -> return "ok"
                    | None -> return failwith "BOOM!"
                }) CancellationToken.None
                |> eval
            Expect.equal result (Some "ok") "retry should return value"
            let msgs = errors |> List.map (fun e -> e.Message)
            Expect.equal msgs ["BOOM!"] "retry should return all errors, even for successful case"
            
        testCase "retry should delay retry steps" <| fun _ ->
            let sw = Stopwatch.StartNew()
            let (result, errors) =
                Schedule.after (TimeSpan.FromMilliseconds 100.)
                |> Schedule.times 3
                |> Schedule.retry (fun _ -> uvtask { failwith "A"}) CancellationToken.None
                |> eval
            sw.Stop()
            Expect.isLessThan sw.ElapsedMilliseconds 400L "retry should not run more delays than scheduler ticks"
            Expect.isGreaterThan sw.ElapsedMilliseconds 300L "retry should not run less delays than scheduler ticks for failure case"
            Expect.equal result None "retry should never return value if action never succeed"
            let msgs = errors |> List.map (fun e -> e.Message)
            Expect.equal msgs ["A";"A";"A";"A"] "retry should return all errors"
            
        testCase "is serializable" <| fun _ ->
            let a = Schedule.exponential 2.0 (TimeSpan.FromMilliseconds 100.) |> Schedule.times 3
            let b = Schedule.after (TimeSpan.FromMilliseconds 150.) |> Schedule.times 2
            let policy = a |> Schedule.andThen b 
            
            let serializer = FsPickler.CreateBinarySerializer()
            let payload = serializer.Pickle policy // size: 104B
            let deserialized : Schedule = serializer.UnPickle payload
            let actual =
                deserialized
                |> Schedule.toSeq
                |> Seq.map (fun t -> t.TotalMilliseconds)
                |> Seq.toList
            
            Expect.equal actual [100.;200.;400.;150.;150.] "Schedule should be able to serialize and deserialize payload"
    ]