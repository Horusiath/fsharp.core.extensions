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
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks.Builders.Unsafe
open FSharp.Core

[<Sealed>]
type private ScheduleExponentialEnumerator(init: TimeSpan, factor: double) =
    let mutable current = init
    let mutable next = current
    interface IEnumerator<TimeSpan> with
        member _.Current with get () : TimeSpan = current
        member _.Current with get () : obj = box current
        member _.MoveNext() =
            current <- next
            next <- TimeSpan (int64(double next.Ticks * factor))
            true
        member _.Reset() =
            current <- init
            next <- current
        member _.Dispose() = ()
        
[<Sealed>]
type private ScheduleOnceEnumerator(inner: IEnumerator<TimeSpan>) =
    let mutable finished = false
    interface IEnumerator<TimeSpan> with
        member _.Current with get () : TimeSpan = inner.Current
        member _.Current with get () : obj = box inner.Current
        member _.MoveNext() =
            if finished then false
            else
                let ok = inner.MoveNext()
                finished <- true
                ok
        member _.Reset() =
            inner.Reset()
            finished <- false
        member _.Dispose() = inner.Dispose()
        
[<Sealed>]
type private ScheduleTimesEnumerator(inner: IEnumerator<TimeSpan>, times: int) =
    let mutable remaining = times
    interface IEnumerator<TimeSpan> with
        member _.Current with get () : TimeSpan = inner.Current
        member _.Current with get () : obj = box inner.Current
        member _.MoveNext() =
            if remaining > 0 then
                let ok = inner.MoveNext()
                remaining <- remaining - 1
                ok
            else false
        member _.Reset() =
            inner.Reset()
            remaining <- times
        member _.Dispose() = inner.Dispose()
        
[<Sealed>]
type private ScheduleMaxEnumerator(left: IEnumerator<TimeSpan>, right: IEnumerator<TimeSpan>) =
    let mutable current = Unchecked.defaultof<_>
    interface IEnumerator<TimeSpan> with
        member _.Current with get () : TimeSpan = current
        member _.Current with get () : obj = box current
        member _.MoveNext() =
            if left.MoveNext() && right.MoveNext() then
                current <- max left.Current right.Current
                true
            else false
        member _.Reset() =
            current <- Unchecked.defaultof<_>
            left.Reset()
            right.Reset()
        member _.Dispose() =
            left.Dispose()
            right.Dispose()
            
[<Sealed>]
type private ScheduleMinEnumerator(left: IEnumerator<TimeSpan>, right: IEnumerator<TimeSpan>) =
    let mutable current = Unchecked.defaultof<_>
    interface IEnumerator<TimeSpan> with
        member _.Current with get () : TimeSpan = current
        member _.Current with get () : obj = box current
        member _.MoveNext() =
            if left.MoveNext() then
                if right.MoveNext() then
                    current <- min left.Current right.Current
                else
                    current <- left.Current
                true
            elif right.MoveNext() then
                current <- right.Current
                true
            else false
        member _.Reset() =
            current <- Unchecked.defaultof<_>
            left.Reset()
            right.Reset()
        member _.Dispose() =
            left.Dispose()
            right.Dispose()
            
[<Sealed>]
type private ScheduleAndThenEnumerator(first: IEnumerator<TimeSpan>, second: IEnumerator<TimeSpan>) =
    let mutable active = first
    interface IEnumerator<TimeSpan> with
        member _.Current with get () : TimeSpan = active.Current
        member _.Current with get () : obj = box active.Current
        member _.MoveNext() =
            if active.MoveNext() then true
            elif obj.ReferenceEquals(active, second) then false
            else
                active <- second
                active.MoveNext()
        member _.Reset() =
            first.Reset()
            second.Reset()
            active <- first
        member _.Dispose() =
            first.Dispose()
            second.Dispose()
            
[<Sealed>]
type private ScheduleJitteredEnumerator(inner: IEnumerator<TimeSpan>, min: double, max: double) =
    let mutable current = Unchecked.defaultof<_>
    interface IEnumerator<TimeSpan> with
        member _.Current with get () : TimeSpan = current
        member _.Current with get () : obj = box current
        member _.MoveNext() =
            if inner.MoveNext() then
                let ticks = (double inner.Current.Ticks)
                let random = Random.float ()
                let jittered = ticks * min * (1.0 - random) + ticks * max * random
                current <- TimeSpan(int64 jittered)
                true
            else false
        member _.Reset() =
            current <- Unchecked.defaultof<_>
            inner.Reset()
        member _.Dispose() = inner.Dispose()

//TODO: this really conforms to standard iterator/enumerator patter - should we even create a separate interface just for that?
type Schedule =
    | Now
    | Done
    | Never
    | After of delay:TimeSpan
    | Exp of init:TimeSpan * factor:double
    | OfSeq of delays:TimeSpan seq
    | Once of Schedule
    | Times of Schedule * times:int
    | Max of Schedule * Schedule
    | Min of Schedule * Schedule
    | AndThen of Schedule * Schedule
    | Jit of Schedule * min:double * max:double
    with member this.GetEnumerator() : IEnumerator<TimeSpan> =
            match this with
            | Now ->
                { new IEnumerator<TimeSpan> with
                    member _.Current with get () : TimeSpan = Unchecked.defaultof<_>
                    member _.Current with get () : obj = box (Unchecked.defaultof<TimeSpan>)
                    member _.MoveNext() = true
                    member _.Reset() = ()
                    member _.Dispose() = () }
            | Done -> 
                { new IEnumerator<TimeSpan> with
                    member _.Current with get () : TimeSpan = Unchecked.defaultof<_>
                    member _.Current with get () : obj = box (Unchecked.defaultof<TimeSpan>)
                    member _.MoveNext() = false
                    member _.Reset() = ()
                    member _.Dispose() = () }
            | Never ->
                { new IEnumerator<TimeSpan> with
                    member _.Current with get () : TimeSpan = Timeout.InfiniteTimeSpan
                    member _.Current with get () : obj = box (Timeout.InfiniteTimeSpan)
                    member _.MoveNext() = true
                    member _.Reset() = ()
                    member _.Dispose() = () }
            | After(d) ->
                { new IEnumerator<TimeSpan> with
                    member _.Current with get () : TimeSpan = d
                    member _.Current with get () : obj = box (d)
                    member _.MoveNext() = true
                    member _.Reset() = ()
                    member _.Dispose() = () }
            | OfSeq(delays) -> delays.GetEnumerator()
            | Exp(init, factor) -> upcast new ScheduleExponentialEnumerator(init, factor)
            | Once(schedule) -> upcast new ScheduleOnceEnumerator(schedule.GetEnumerator())        
            | Times(schedule, times) -> upcast new ScheduleTimesEnumerator(schedule.GetEnumerator(), times)  
            | Max(left, right) -> upcast new ScheduleMaxEnumerator(left.GetEnumerator(), right.GetEnumerator())
            | Min(left, right) -> upcast new ScheduleMinEnumerator(left.GetEnumerator(), right.GetEnumerator())
            | AndThen(first, second) -> upcast new ScheduleAndThenEnumerator(first.GetEnumerator(), second.GetEnumerator())
            | Jit(schedule, min, max) -> upcast new ScheduleJitteredEnumerator(schedule.GetEnumerator(), min, max)

[<RequireQualifiedAccess>]
module Schedule =
    
    /// Creates a schedule made of explicit sequence of consecutive delays.               
    let ofSeq (s: TimeSpan seq) : Schedule = OfSeq s          
    
    /// Creates a schedule with instant execution (no delays) rules.
    let now : Schedule = Now 
    
    /// Creates a schedule that will immediately complete.
    let completed : Schedule = Done
    
    /// Creates a schedule that provides an infinite delay.
    let never : Schedule = Never
    
    /// Creates a schedule that will execute after given delay.
    let after (delay: TimeSpan) : Schedule = After delay
    
    /// Creates a schedule that will execute passed schedule once and then complete.
    let once (schedule: Schedule) : Schedule = Once schedule
            
    /// Creates a new schedule from existing one, which will modify it's delays by a randomly choosen jittered value
    /// within given `min`-`max` bounds.
    let jittered (min: double) (max: double) (schedule: Schedule) : Schedule = Jit(schedule, min, max)
    
    /// Creates a new schedule from existing one, which will execute it a given number of times before completing.
    let times (count: int) (schedule: Schedule) : Schedule = Times(schedule, count)
    
    /// Creates a schedule which will exponentially increase the provided delay.
    let exponential (factor: double) (initDelay: TimeSpan) : Schedule = Exp(initDelay, factor)
            
    /// Creates a new schedule as a combination of two others, which will execute as long as both of them execute,
    /// taking a maximum delay between the two. 
    let max (a: Schedule) (b: Schedule) : Schedule = Max(a, b)
    
    /// Creates a new schedule as a combination of two others, which will execute as long as either of them execute,
    /// taking a minimum delay between the two.
    let min (a: Schedule) (b: Schedule) : Schedule = Min(a, b)
    
    /// Creates a new schedule as a combination of two, that will take delays for the `prev` until it completes,
    /// and then pick the delays from `next` until it's completion.
    let andThen (next: Schedule) (prev: Schedule) : Schedule = AndThen(prev, next)
    
    let private sleepInfinite (cancel: CancellationToken) : Task =
        if cancel.IsCancellationRequested then Task.FromCanceled(cancel)
        else 
            let promise = TaskCompletionSource<unit>()
            if cancel.CanBeCanceled then
                cancel.Register(System.Action(fun () ->
                    promise.SetCanceled()    
                )) |> ignore
            upcast promise.Task
    
    /// Repeats given action N+1 times (where N is number of `schedule` ticks), spaced by delays provided by a given
    /// `schedule` until that schedule completes: f(), sleep(), f(), sleep(), f().
    let spaced (f: unit -> ValueTask<'a>) (cancel: CancellationToken) (schedule: Schedule) : ValueTask<'a seq> = uvtask {
        if cancel.IsCancellationRequested then return Seq.empty
        else
            let result = ResizeArray()
            let! a = f ()
            result.Add a
            let e = schedule.GetEnumerator()
            while not cancel.IsCancellationRequested && e.MoveNext() do
                do! Task.Delay(e.Current, cancel)
                let! a = f ()
                result.Add a
            return upcast result
    }

    /// Executes given action and retries is until success or until schedule completes.
    let retry (f: exn option -> ValueTask<'a>) (cancel: CancellationToken) (schedule: Schedule) : ValueTask<('a option * exn list)> = uvtask {
        let mutable cont = true
        let mutable result = None
        let mutable exceptions = []
        let mutable lastExn = None
        let e = schedule.GetEnumerator()
        while not cancel.IsCancellationRequested && cont do
            try
                let! a = f lastExn
                cont <- false
                result <- Some a
            with err ->
                exceptions <- err::exceptions
                lastExn <- Some err
                cont <- e.MoveNext()
                if cont then
                    do! Task.Delay(e.Current, cancel)
                    
        return result, List.rev exceptions
    }