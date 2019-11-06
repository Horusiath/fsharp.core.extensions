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

module FSharp.Core.Extensions.Tests.HybridTime


open System
open System
open FSharp.Core
open Expecto
open Expecto.Logging
open FSharp.Core
open FSharp.Core
open FSharp.Core
open FSharp.Core

[<Tests>]
let tests =
    testList "HybridTime" [

        testCase "should never decrease (real clock)" <| fun _ ->
            // this is not really a test as we'd need to run into NTP stepping, which is hard to do
            let mutable last = HybridTime.now ()
            for i=0 to 1_000_000 do
                let now = HybridTime.now ()
                Expect.isGreaterThan now last "Hybrid time must never step back"
                last <- now

        testCase "should never decrease (virtual clock)" <| fun _ ->
            let clock = fun () ->
                let jitter = int64 (Random.int32 ())
                if jitter % 2L = 0L
                then DateTime.UtcNow.Ticks - jitter
                else DateTime.UtcNow.Ticks + jitter
                
            HybridTime.Unsafe.mocked clock <| fun _ ->                    
                // this is not really a test as we'd need to run into NTP stepping, which is hard to do
                let mutable last = HybridTime.now ()
                for i=0 to 1_000_000 do
                    let now = HybridTime.now ()
                    Expect.isGreaterThan now last "Hybrid time must never step back"
                    last <- now

        testCase "should update value if greater" <| fun _ ->
            let remoteTime = HybridTime.ofDateTime (DateTime.UtcNow.AddDays 1.)
            let localTime = HybridTime.now ()
            Expect.isLessThan localTime remoteTime "at first local time should be less than remote"
            let winner = HybridTime.update remoteTime
            let localTime = HybridTime.now ()
            Expect.equal winner remoteTime "update should detect remote time as the most recent one"
            Expect.isGreaterThan localTime remoteTime "after update, local time should be greater than remote"
            
        testCase "should not update value if lesser" <| fun _ ->
            let remoteTime = HybridTime.ofDateTime (DateTime.UtcNow.AddDays -1.)
            let localTime = HybridTime.now ()
            Expect.isGreaterThan localTime remoteTime "local time should be greater than remote"
            let winner = HybridTime.update remoteTime
            let localTime = HybridTime.now ()
            Expect.notEqual winner remoteTime "update should detect remote time as the outdated one"
            Expect.isGreaterThan localTime remoteTime "after update, local time should be greater than remote"
            
        testCase "should be comparable to DateTime.UtcNow with millisecond precision" <| fun _ ->
            let utc = DateTime.UtcNow
            let ht = utc |> HybridTime.ofDateTime
            let hi = (utc.AddMilliseconds 1.)
            let lo = (utc.AddMilliseconds -1.)
            
            Expect.isGreaterThanOrEqual ht.Ticks lo.Ticks <| sprintf "HybridTime(%s) should not differ more than 1ms from UTC time low: (%s)" (string ht) (lo.ToString("O"))
            Expect.isLessThanOrEqual ht.Ticks hi.Ticks <| sprintf "HybridTime(%s) should not differ more than 1ms from UTC time high: (%s)" (string ht) (hi.ToString("O")) 
    ]
