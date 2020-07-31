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

open FSharp.Control.Tasks.Builders
open System.Threading.Tasks
open System.Threading
open System

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

    /// Converts a `Task<'a>` into `Task` (untyped, with no result type).
    let inline ignore (t: Task<'a>) : Task = upcast t
    
    /// Maps result value produced by given task, returning new task in the result.
    let map (f: 'a -> 'b) (t: Task<'a>) : Task<'b> =
        if not t.IsCompleted then
            t.ContinueWith(Func<Task<'a>, 'b>(fun t -> f t.Result), TaskContinuationOptions.ExecuteSynchronously|||TaskContinuationOptions.NotOnCanceled|||TaskContinuationOptions.NotOnFaulted)
        elif t.IsCompletedSuccessfully then Task.FromResult(f t.Result)
        else Task.FromException<'b>(if isNull t.Exception then TaskCanceledException(t) :> exn else upcast t.Exception)
        
    /// Converts task's exception channel into Error case of returned Result type.
    let secure (t: Task<'a>) : Task<Result<'a,exn>> = task {
        try
            let! result = t
            return Ok result
        with e ->
            return Error e
    }
    
    /// Runs two tasks in parallel, returning a result of the one which completed first
    /// while disposing the other.
    let race (left: Task<'a>) (right: Task<'b>) : Task<Choice<'a,'b>> = task {
        use t1 = left :> Task
        use t2 = right :> Task
        let! finished = Task.WhenAny(t1, t2)
        if obj.ReferenceEquals(finished, t1) then
            return Choice1Of2 left.Result
        else
            return Choice2Of2 right.Result
    }
        
    let inline run (f: unit -> Task<'a>) : Task<'a> = Task.Run<'a>(Func<_>(f))
    
    let inline runCancellable (c: CancellationToken) (f: unit -> Task<'a>) : Task<'a> = Task.Run<'a>(Func<_>(f), c)

type Promise<'a> = TaskCompletionSource<'a>