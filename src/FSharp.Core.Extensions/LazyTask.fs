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

[<RequireQualifiedAccess>]
module Task =
    
    let private complete (promise: TaskCompletionSource<'a>) (t: Task<'a>) =
        if t.IsCompletedSuccessfully then promise.TrySetResult(t.Result)
        elif t.IsCanceled then promise.TrySetCanceled()
        else promise.TrySetException(t.Exception)
        |> ignore
    
    /// Redirects the result of provided `task` execution into given TaskCompletionSource,
    /// completing it, cancelling or rejecting depending on a given task output.
    let fulfill (promise: TaskCompletionSource<'a>) (task: Task<'a>) =
        if task.IsCompleted then complete promise task // short path for immediately completed tasks
        else task.ContinueWith(Action<_>(complete promise), TaskContinuationOptions.ExecuteSynchronously|||TaskContinuationOptions.AttachedToParent) |> ignore

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
    member this.HasValue =
        let value = Volatile.Read(&result)
        value.Task.IsCompletedSuccessfully
    member this.Value =
        let old = Volatile.Read(&result)
        if isNull old then getValue() else old.Task
        