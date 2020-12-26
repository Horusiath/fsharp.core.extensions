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

module FSharp.Core.Extensions.Tests.RWLockTests

open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.Core
open FSharp.Control.Tasks.Builders

[<Tests>]
let testsUnbounded = testList "Reentrant lock" [
    testTask "read lock handle: reads" {
        do! Task.run (fun () -> task {
            use lock = RWLock.reentrant 123
            use! reader = lock.Read()
            Expect.equal reader.Value 123 "read lock should return actual value"
        })
    }
    
    testTask "write lock handle: reads" {
        do! Task.run (fun () -> task {
            use lock = RWLock.reentrant 123
            use! writer = lock.Write()
            Expect.equal writer.Value 123 "read lock should return actual value"
        })
    }
    
    testTask "write lock handle: writes" {
        do! Task.run (fun () -> task {
            use lock = RWLock.reentrant 100
            use! writer = lock.Write()
            let mutable w = writer 
            w.Value <- w.Value + 20
            Expect.equal writer.Value 120 "write lock should return actual value"
        })
    }
    
    testTask "read lock handle: upgrades" {
        do! Task.run (fun () -> task {
            use lock = RWLock.reentrant 100
            use! reader = lock.Read()
            let v = reader.Value
            use! writer = reader.Upgrade()
            let mutable w = writer
            w.Value <- v + 20
            Expect.equal reader.Value 120 "read lock should return actual value after upgrade"
        })
    }
    
    testTask "concurrent read then write" {
        use lock = RWLock.reentrant 100
        let t1 = Task.run (fun () -> task {
            use! reader = lock.Read()   // obtain read lock first
            do! Task.Delay 500          // wait to force writ lock into awaiters queue 
            Expect.equal reader.Value 100 "read lock should return actual value"
        })
        let t2 = Task.run (fun () -> task {
            printfn "%O" Thread.CurrentThread.ManagedThreadId
            do! Task.Delay 100          // force read lock to be obtained first
            printfn "%O" Thread.CurrentThread.ManagedThreadId
            use! writer = lock.Write()
            Expect.equal writer.Value 100 "write lock should return actual value"
        })
        do! Task.WhenAll(t1, t2)
    }
]