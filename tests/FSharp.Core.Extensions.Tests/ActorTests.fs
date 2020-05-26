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
    ftestList "Actor" [
        
        testTask "should process incoming messages" {
            do! uunitTask {
                let mutable state = 0
                use actor =
                    { new UnboundedActor<_>() with
                        override ctx.Receive msg = uunitVtask {
                            match msg with
                            | Add v -> state <- state + v
                            | Done -> ctx.Complete()                        
                        } }
                do! actor.Send (Add 1)
                do! actor.Send (Add 2)
                do! actor.Send (Add 3)
                do! actor.Send Done
                
                do! actor.Terminated
                
                Expect.equal state 6 "actor should process all messages"                
            }
        }
        
        testTask "should cancel depending actions" {
            do! uunitTask {
                let flag = atom false
                let mutable state = 0
                use actor =
                    { new UnboundedActor<_>() with
                        override ctx.Receive msg = uunitVtask {
                            if state = 0 then
                                ctx.CancellationToken.Register (System.Action(fun () -> (flag := true) |> ignore)) |> ignore
                            match msg with
                            | Add v -> state <- state + v
                            | Done -> ctx.Complete()
                        } }
                
                do! actor.Send Done
                do! actor.Terminated
                
                Expect.isTrue !flag "actor trigger cancellation on completion"                
            }
        }
        
        testTask "should work like channel writer" {
            do! uunitTask {
                let mutable state = 0
                use actor = 
                    { new UnboundedActor<_>() with
                        override ctx.Receive msg = uunitVtask {
                            match msg with
                            | Add v -> state <- state + v
                            | Done -> ctx.Complete()
                        } }
                
                do! AsyncSeq.ofSeq [Add 1; Add 2; Add 3] |> AsyncSeq.into true actor
                do! actor.Terminated
                
                Expect.equal state 6 "actor should process all pending messages before complete"
            }
        }
        
        testTask "DisposeAsync(false) should wait for pending message to complete first" {
            do! uunitTask {
                let mutable state = 0
                use actor =
                    { new UnboundedActor<_>() with
                        override ctx.Receive msg = uunitVtask {
                            do! Task.Delay(100)
                            state <- state + msg
                        } }
                
                do! actor.Send 1
                do! actor.Send 2
                
                do! actor.DisposeAsync false
                
                Expect.isFalse (actor.TryWrite 3) "upon disposal actor should no longer accept incoming messages"
                
                do! actor.Terminated
                
                Expect.isTrue actor.CancellationToken.IsCancellationRequested "after dispose actor's cancellation token should be triggered"
                Expect.equal state 3 "actor should process all messages" 
            }
        }
        
        testTask "DisposeAsync(true) should terminate actor immediately" {
            do! uunitTask {
                let mutable state = 0
                use actor = 
                    { new UnboundedActor<_>() with
                        override ctx.Receive msg = uunitVtask {
                            do! Task.Delay(100)
                            state <- state + msg
                        } }
                
                do! actor.Send 1
                do! actor.Send 2
                
                do! actor.DisposeAsync true
                
                Expect.isFalse (actor.TryWrite 3) "upon disposal actor should no longer accept incoming messages"
                
                do! actor.Terminated
                
                Expect.isTrue actor.CancellationToken.IsCancellationRequested "after dispose actor's cancellation token should be triggered"
                Expect.notEqual state 3 "actor should finish before processing all messages" 
            }
        }
        
        testTask "failure inside of actor is propagated to terminated task" {
            do! uunitTask {
                let mutable state = 0
                let actor = 
                    { new UnboundedActor<_>() with
                        override ctx.Receive msg = uunitVtask {
                            if msg = 2 then failwith "BOOM"
                            else state <- msg
                        } }
                
                do! actor.Send 1
                do! actor.Send 2
                
                Expect.throwsT<Exception> (fun () -> actor.Terminated.GetAwaiter().GetResult()) "terminated should complete with failure"
                
                Expect.isTrue actor.CancellationToken.IsCancellationRequested "after terminated actor's cancellation token should be triggered"
                Expect.equal state 1 "actor should process unfailable messages" 
            }
        }
    ]