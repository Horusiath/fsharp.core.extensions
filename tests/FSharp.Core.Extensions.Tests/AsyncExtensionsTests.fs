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

type User =
    { Id: int
      Name: string
      OrderIds: int[] }

type Order =
    { Id: int
      BuyerId: int
      Name: string }
    
type UserWithOrders =
    { User: User
      Orders: Order[] }

[<Tests>]
let tests = ftestList "AsyncBatch" [
    testAsync "should reuse once resolved values" {
        let! ctx = AsyncBatch.context
        let mutable counter = 0
        let source = AsyncBatch (ctx, fun ids -> async {
            counter <- counter + 1
            do! Async.Sleep 10
            return ids |> Seq.map (fun i -> (i, i+1)) |> Map.ofSeq
        })
        let! results =
            [|1..5|]
            |> Array.map source.GetAsync
            |> Async.Parallel
            
        Expect.equal results [|2..6|] "results should be properly assigned"
        Expect.equal counter 1 "batch should be processed once"
        
        let! results =
            [|1..5|]
            |> Array.map source.GetAsync
            |> Async.Parallel
            
        Expect.equal results [|2..6|] "results should be properly assigned"
        Expect.equal counter 1 "batch should be processed once"
    }
    
    testAsync "resolve function should be called only once per parallel request" {
        let! ctx = AsyncBatch.context
        let mutable counter = 0
        let source = AsyncBatch (ctx, fun ids -> async {
            counter <- counter + 1
            do! Async.Sleep 10
            return ids |> Seq.map (fun i -> (i, i+1)) |> Map.ofSeq
        })
        let! results =
            [|1..5|]
            |> Array.map source.GetAsync
            |> Async.Parallel
            
        Expect.equal results [|2..6|] "results should be properly assigned"
        Expect.equal counter 1 "batch should be processed once"
        
        //do! Async.SwitchToContext ctx
        
        let! results =
            [|6..10|]
            |> Array.map source.GetAsync
            |> Async.Parallel
            
        Expect.equal results [|7..11|] "results should be properly assigned"
        Expect.equal counter 2 "batch should be processed once"
    }
    
    testAsync "can be nested" {
        // contents of our "database" indexes
        let usersIndex = Map.ofList [
            1, { Id = 1; Name = "Alice"; OrderIds = [|1|] }
            2, { Id = 2; Name = "Bob"; OrderIds = [|2;3|] }
        ]
        let ordersIndex = Map.ofList [
            1, { Id = 1; BuyerId = 1; Name = "Headphones" }
            2, { Id = 2; BuyerId = 2; Name = "Keyboard" }
            3, { Id = 3; BuyerId = 2; Name = "Vuvuzela" }
        ]
        
        let ctx1 = AsyncBatchContext() // context used by users data loader
        let ctx2 = AsyncBatchContext() // context used by orders data loader
        let mutable c1 = 0
        let mutable c2 = 0
        let users = AsyncBatch (ctx1, fun ids -> async {
            c1 <- c1 + 1
            return ids |> Seq.map (fun i -> i, Map.find i usersIndex) |> Map.ofSeq
        })
        let orders = AsyncBatch (ctx2, fun ids -> async {
            c2 <- c2 + 1
            return ids |> Seq.map (fun i -> i, Map.find i ordersIndex) |> Map.ofSeq
        })
        let getUserWithOrders (userId: int) = async {
            let! user = users.GetAsync userId
            let! userOrders =
                user.OrderIds
                |> Array.map orders.GetAsync
                |> Async.Parallel
            return { User = user; Orders = userOrders }
        }
        
        do! Async.SwitchToContext ctx1
        let! results =
            [|1..2|]
            |> Array.map getUserWithOrders
            |> Async.Parallel
            
        Expect.equal c1 1 "users loader should be hit once"
        Expect.equal c2 1 "orders loader should be hit once"
        
        let expected = [|
            { User = Map.find 1 usersIndex; Orders = [| Map.find 1 ordersIndex |] }
            { User = Map.find 2 usersIndex; Orders = [| Map.find 2 ordersIndex; Map.find 3 ordersIndex |] }
        |]
        Expect.equal results expected "all values should be retrieved"
    }
]