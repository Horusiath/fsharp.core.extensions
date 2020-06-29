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

/// Schedule is an immutable data type, that allows to apply iterator pipeline over non-blocking delays and timeouts.
/// Schedule can put current Task/ValueTask to sleep via `Schedule.sleep` function. It can also be persisted and resumed
/// afterwards.
///
/// Core concepts involve:
///
/// - `Delay` property, which returns a next Delay computed by this schedule, or ValueNone in case when current schedule has finished.
/// - `Advance` method which returns an updated schedule with next computed value in timeline progression.
type Schedule =
    | Done
    | Now
    | Never
    | Once of delay:TimeSpan
    | After of delay:TimeSpan
    | Exp of init:TimeSpan * factor:double
    | OfArray of delays:TimeSpan[] * current:int
    | Times of Schedule * times:int
    | Max of Schedule * Schedule
    | Min of Schedule * Schedule
    | AndThen of Schedule * Schedule
    | Jit of delay:TimeSpan * Schedule * min:double * max:double
    with
        member this.Delay : TimeSpan voption =
            match this with
            | Done -> ValueNone
            | Now -> ValueSome TimeSpan.Zero
            | Never -> ValueSome Timeout.InfiniteTimeSpan
            | Once delay -> ValueSome delay
            | After delay -> ValueSome delay
            | Exp(delay, _) -> ValueSome delay
            | Jit(delay, _, _, _) -> ValueSome delay
            | Times(schedule, _) -> schedule.Delay
            | OfArray(delays, current) -> if current >= delays.Length then ValueNone else ValueSome delays.[current]
            | Max(left, right) ->
                match left.Delay, right.Delay with
                | ValueSome a, ValueSome b -> ValueSome (max a b)
                | ValueNone, ValueNone -> ValueNone
                | ValueNone, other
                | other, ValueNone -> other
            | Min(left, right) ->
                match left.Delay, right.Delay with
                | ValueSome a, ValueSome b -> ValueSome (min a b)
                | ValueNone, ValueNone -> ValueNone
                | ValueNone, other
                | other, ValueNone -> other
            | AndThen(first, second) -> first.Delay |> ValueOption.orElse second.Delay
        member this.Advance() : Schedule =
            match this with
            | Done -> this
            | Now -> this
            | Never -> this
            | After _ -> this
            | Once _ -> Done
            | Exp(delay, fac) -> Exp(TimeSpan(int64(double delay.Ticks * fac)), fac)
            | OfArray(delays, current) -> if current + 1 >= delays.Length then Done else OfArray(delays, current + 1)
            | Times(schedule, times) -> if times = 1 then Done else Times(schedule.Advance(), times-1) 
            | Max(left, right) ->
                let left = left.Advance()
                let right = right.Advance()
                match left.Delay, right.Delay with
                | ValueNone, _
                | _, ValueNone -> Done
                | _ -> Max(left, right)
            | Min(left, right) ->
                let left = left.Advance()
                let right = right.Advance()
                match left.Delay, right.Delay with
                | ValueNone, ValueNone -> Done
                | _ -> Min(left, right)
            | AndThen(first, second) ->
                let first = first.Advance()
                match first.Delay with
                | ValueNone -> second
                | _ -> AndThen(first, second)
            | Jit(_, schedule, min, max) ->
                let schedule = schedule.Advance()
                match schedule.Delay with
                | ValueNone -> Done
                | ValueSome d ->
                    let ticks = (double d.Ticks)
                    let random = Random.float ()
                    let jittered = ticks * min * (1.0 - random) + ticks * max * random
                    Jit(TimeSpan(int64 jittered), schedule, min, max)

[<RequireQualifiedAccess>]
module Schedule =
    
    /// Creates a schedule made of explicit sequence of consecutive delays.               
    let ofArray (s: TimeSpan[]) : Schedule = OfArray(s, 0) 
    
    /// Creates a schedule with instant execution (no delays) rules.
    let now : Schedule = Now 
    
    /// Creates a schedule that will immediately complete.
    let completed : Schedule = Done
    
    /// Creates a schedule that provides an infinite delay.
    let never : Schedule = Never
    
    /// Creates a schedule that will execute after given delay.
    let after (delay: TimeSpan) : Schedule = After delay
    
    /// Creates a schedule that will execute passed schedule once and then complete.
    let once (delay: TimeSpan) : Schedule = Once delay
            
    /// Creates a new schedule from existing one, which will modify it's delays by a randomly choosen jittered value
    /// within given `min`-`max` bounds, which describe percentage of shift eg.: 0.7 means 70% of original value.
    let jittered (min: double) (max: double) (schedule: Schedule) : Schedule =
        match schedule.Delay with
        | ValueSome d ->
            let ticks = (double d.Ticks)
            let random = Random.float ()
            let jit = ticks * min * (1.0 - random) + ticks * max * random
            Jit(TimeSpan(int64 jit), schedule, min, max)
        | ValueNone -> Done
    
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
            
    /// Puts current task to sleep accordingly to a given `schedule`. Result returns an updated schedule.
    let sleep (cancel: CancellationToken) (schedule: Schedule) : ValueTask<Schedule> =
        if cancel.IsCancellationRequested then ValueTask<Schedule>(Task.FromCanceled<Schedule>(cancel))
        else
            match schedule.Delay with
            | ValueNone -> ValueTask<_> schedule
            | ValueSome t when t = TimeSpan.Zero -> ValueTask<_> (schedule.Advance())
            | ValueSome t when t = Timeout.InfiniteTimeSpan -> uvtask {
                do! sleepInfinite cancel
                return schedule.Advance() }
            | ValueSome d -> uvtask {
                do! Task.Delay d
                return schedule.Advance() }
    
    /// Repeats given action N+1 times (where N is number of `schedule` ticks), spaced by delays provided by a given
    /// `schedule` until that schedule completes: f(), sleep(), f(), sleep(), f().
    let spaced (f: unit -> ValueTask<'a>) (cancel: CancellationToken) (schedule: Schedule) : ValueTask<'a seq> = uvtask {
        if cancel.IsCancellationRequested then return Seq.empty
        else
            let result = ResizeArray()
            let! a = f ()
            result.Add a
            let mutable s = schedule
            while not cancel.IsCancellationRequested && ValueOption.isSome s.Delay do
                do! Task.Delay(s.Delay.Value, cancel)
                let! a = f ()
                result.Add a
                s <- s.Advance()
            return upcast result
    }

    /// Executes given action and retries is until success or until schedule completes. Produced result is a tuple,
    /// which first element is an option with successful result (if any was produced) and a list of failures that caused
    /// retry to trigger along the way (if any where thrown).
    let retry (f: exn option -> ValueTask<'a>) (cancel: CancellationToken) (schedule: Schedule) : ValueTask<('a option * exn list)> = uvtask {
        let mutable cont = true
        let mutable result = None
        let mutable exceptions = []
        let mutable lastExn = None
        let mutable s = schedule
        while not cancel.IsCancellationRequested && cont do
            try
                let! a = f lastExn
                cont <- false
                result <- Some a
            with err ->
                exceptions <- err::exceptions
                lastExn <- Some err
                match s.Delay with
                | ValueSome delay ->
                    do! Task.Delay(delay, cancel)
                    s <- s.Advance()
                | _ -> cont <- false
                    
        return result, List.rev exceptions
    }
        
    /// Converts current schedule into lazily evaluated sequence of delays.
    let toSeq (schedule: Schedule) : TimeSpan seq = seq {
        let mutable s = schedule
        let mutable cont = s.Delay.IsSome
        while cont do
            yield s.Delay.Value
            s <- s.Advance()
            cont <- s.Delay.IsSome
    }