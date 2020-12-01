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

module FSharp.Core.Extensions.Tests.LazyTask

open System.Threading
open System.Threading.Tasks
open FSharp.Core
open FsCheck
open Expecto
open FSharp.Control.Tasks

[<Tests>]
let tests =
    testList "LazyTask" [

        testCase "should be called only once for multiple calls" <| fun _ ->
            let mutable i = 0
            let l: LazyTask<int> = LazyTask<_>(fun () -> task {
                do! Task.Yield()
                return Interlocked.Increment &i                
            })
            let call () = l.Value
            let calls = Array.init 100 (fun _ -> call ())
            let task = Task.WhenAll(calls)
            let results = task.GetAwaiter().GetResult()
            let ok = results |> Array.forall ((=) 1)
            Expect.isTrue ok "All calls should return the same value"
            
    ]