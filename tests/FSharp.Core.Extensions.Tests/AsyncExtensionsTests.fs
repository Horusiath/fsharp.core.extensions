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

module FSharp.Core.Extensions.Tests.AsyncExtensionsTests

open Expecto
open FSharp.Core

[<Tests>]
let testIVar = testList "IVar" [
        testAsync "write works for past and future requests" {
            let v = IVar.empty ()
            let read = async {
                return! v.Value
            }
            let assign = async {
                v.Write 1
                return! v.Value
            }
            let! results = [| read; assign |] |> Async.Parallel
            Expect.equal results [|1;1|] "IVar value should return updated value"
            
            let! value = v.Value
            Expect.equal value 1 "IVar value should return for subsequent reads"
        }
        
        testAsync "write works only once" {
            let v = IVar.empty ()
            Expect.isTrue (v.TryWrite 1) "IVar should accept first write"
            Expect.isFalse (v.TryWrite 2) "IVar should not allow to write multiple times"
            
            let! value = v.Value
            Expect.equal value 1 "IVar should resolve to first written value"
        }
    ]

[<Tests>]
let testMVar = testList "MVar" [
        testAsync "write works for past and future requests" {
            let v = MVar.empty ()
            let read = async {
                return! v.Value
            }
            let assign = async {
                v.Swap 1 |> ignore
                return! v.Value
            }
            let! results = [| read; assign |] |> Async.Parallel
            Expect.equal results [|1;1|] "MVar value should return updated value"
            
            let! value = v.Value
            Expect.equal value 1 "MVar value should return for subsequent reads"
        }
        
        testAsync "write works multiple times" {
            let v = MVar.empty ()
            Expect.isNone (v.Swap 1) "MVar.Swap should return None on first assignment"
            let! value = v.Value
            Expect.equal value 1 "MVar should resolve to last written value"
            
            Expect.equal (v.Swap 2) (Some 1) "MVar.Swap should return previous value on second assignment"
            let! value = v.Value
            Expect.equal value 2 "MVar should resolve to last written value"
        }
        
        testAsync "clear works" {
            let v = MVar.empty ()
            Expect.isNone (v.Swap 1) "MVar.Swap should return None on first assignment"
            let! value = v.Value
            Expect.equal value 1 "MVar should resolve to last written value"
            Expect.isTrue v.HasValue "MVar should have value inside after swap"
            
            Expect.equal (MVar.clear v) (Some 1) "MVar.Clear should return last stored value"
            Expect.isFalse v.HasValue "After clear, MVar should not have any value"
            
            let read = async {
                return! v.Value
            }
            let assign = async {
                do! Async.Sleep 100
                v.Swap 2 |> ignore
                return! v.Value
            }
            let! results = [| read; assign |] |> Async.Parallel
            Expect.equal results [|2;2|] "MVar value should return updated value"
        }
    ]