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

module FSharp.Core.Extensions.Tests.ActorTests

open System
open System.Threading
open System.Threading.Tasks
open FSharp.Core
open FSharp.Core.Atomic.Operators
open FsCheck
open Expecto
open FSharp.Control.Tasks.Builders.Unsafe

type Message =
    | Add of int
    | Done

[<Tests>]
let tests =
    testList "Actor" [
        
        testTask "should process incoming messages" {
            do! uunitTask {
                use actor = Actor.stateful 0 (fun ctx msg -> uvtask {
                    match msg with
                    | Add v -> return ctx.State + v
                    | Done ->
                        ctx.Complete()
                        return ctx.State
                })
                do! actor.Send (Add 1)
                do! actor.Send (Add 2)
                do! actor.Send (Add 3)
                do! actor.Send Done
                
                do! actor.Terminated
                
                Expect.equal actor.State 6 "actor should process all messages"                
            }
        }
        
        testTask "should cancel depending actions" {
            do! uunitTask {
                let flag = atom false
                use actor = Actor.stateful 0 (fun ctx msg -> uvtask {
                    if ctx.State = 0 then
                        ctx.CancellationToken.Register (System.Action(fun () -> (flag := true) |> ignore)) |> ignore
                    match msg with
                    | Add v -> return ctx.State + v
                    | Done ->
                        ctx.Complete()
                        return ctx.State
                })
                
                do! actor.Send Done
                do! actor.Terminated
                
                Expect.isTrue !flag "actor trigger cancellation on completion"                
            }
        }
        
        testTask "should work like channel writer" {
            do! uunitTask {
                use actor = Actor.stateful 0 (fun ctx msg -> uvtask {
                    match msg with
                    | Add v -> return ctx.State + v
                    | Done ->
                        ctx.Complete()
                        return ctx.State
                })
                
                do! AsyncSeq.ofSeq [Add 1; Add 2; Add 3] |> AsyncSeq.into true actor
                do! actor.Terminated
                
                Expect.equal actor.State 6 "actor should process all pending messages before complete"
            }
        }
        
        testTask "DisposeAsync(false) should wait for pending message to complete first" {
            do! uunitTask {
                use actor = Actor.stateful 0 (fun ctx delta -> uvtask {
                    do! Task.Delay(100)
                    return ctx.State + delta
                })
                
                do! actor.Send 1
                do! actor.Send 2
                
                do! actor.DisposeAsync false
                
                Expect.isFalse (actor.TryWrite 3) "upon disposal actor should no longer accept incoming messages"
                
                do! actor.Terminated
                
                Expect.isTrue actor.CancellationToken.IsCancellationRequested "after dispose actor's cancellation token should be triggered"
                Expect.equal actor.State 3 "actor should process all messages" 
            }
        }
        
        testTask "DisposeAsync(true) should terminate actor immediately" {
            do! uunitTask {
                use actor = Actor.stateful 0 (fun ctx delta -> uvtask {
                    do! Task.Delay(100)
                    return ctx.State + delta
                })
                
                do! actor.Send 1
                do! actor.Send 2
                
                do! actor.DisposeAsync true
                
                Expect.isFalse (actor.TryWrite 3) "upon disposal actor should no longer accept incoming messages"
                
                do! actor.Terminated
                
                Expect.isTrue actor.CancellationToken.IsCancellationRequested "after dispose actor's cancellation token should be triggered"
                Expect.notEqual actor.State 3 "actor should finish before processing all messages" 
            }
        }
    ]