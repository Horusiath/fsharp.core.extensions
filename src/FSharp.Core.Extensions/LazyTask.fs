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

namespace FSharp.Core

open System
open System.Threading
open System.Threading.Tasks

/// An object, which is supposed to work like a lazy wrapper,
/// but for functions that are returning results asynchronously.
[<Sealed>]
type LazyTask<'a>(fn: unit -> Task<'a>) = 
    let mutable result: TaskCompletionSource<'a> = null
    let getValue() =
        let promise = TaskCompletionSource<'a>()
        let old = Interlocked.CompareExchange(&result, promise, null)
        if isNull old then
            (Task.Run<'a>(Func<Task<'a>>(fn)) |> Task.fulfill promise)
            promise.Task
        else old.Task
    
    /// Checks if current lazy cell contains a computed (initialized) value.
    member this.HasValue =
        let value = Volatile.Read(&result)
        not (isNull value) && value.Task.IsCompletedSuccessfully
        
    /// Returns a task, which holds a result of initializing function passed to this LazyTask.
    /// Once computed, the same value will be returned in all subsequent calls.
    member this.Value =
        let old = Volatile.Read(&result)
        if isNull old
        then getValue() // since `getValue()` allocates, we only call it if current lazy task was never called (in causal past)
        else old.Task // > 99% of the time value is already computed so no need to reallocate
        