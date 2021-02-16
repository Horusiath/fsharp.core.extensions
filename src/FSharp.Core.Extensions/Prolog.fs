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

module FSharp.Core.Operators

open System
open System.Collections.Generic
open System.Runtime.ExceptionServices

/// An operator over an implicit cast operation betwen two types.
let inline (!%) (x:^a) : ^b = ((^a or ^b) : (static member op_Implicit : ^a -> ^b) x)  

/// Creates a `KeyValuePair<k,v>` out of provided `key` and `value` arguments.
let inline (=>) (key: 'k) (value: 'v): KeyValuePair<'k,'v> = KeyValuePair(key, value)

#nowarn "1215"
    
type System.TimeSpan with

    /// Multiplies given time span by a given number of `times` eg. 2.sec * 2.5 => 5.sec.
    static member (*) (time: TimeSpan, times: float): TimeSpan =
        TimeSpan(int64 (float time.Ticks * times))
        
    /// Multiplies given time span by a given number of `times` eg. 2.sec * 2 => 4.sec.
    static member (*) (time: TimeSpan, times: int): TimeSpan =
        TimeSpan(int64 (time.Ticks * int64 times))
        
[<RequireQualifiedAccess>]
module Map =
    
    /// Inserts or updates value under provided `key` (if it existed before) using function `fn`.
    /// Upsert function receives `Some` containing existing value, if such has been found,
    /// or `None` otherwise.
    let upsert (key: 'k) (fn: 'v option -> 'v) (map: Map<'k,'v>) : Map<'k,'v> =
        let nval = map |> Map.tryFind key |> fn
        Map.add key nval map
        
    /// Builds a union of two maps. In case when both maps have entries with the same given key,
    /// function `fn` will be used to reconcile the result value in output map. Function arguments:
    /// 1. Key of reconciled entires matched between both maps `a` and `b`.
    /// 2. Value of entry in first map `a`.
    /// 3. Value of entry in second map `b`.
    let union (fn: 'k -> 'v -> 'v -> 'v) (a: Map<'k,'v>) (b: Map<'k,'v>): Map<'k,'v> =
        let mutable m = a
        for e in b do
            let key = e.Key
            let bval = e.Value
            let ok, aval = a.TryGetValue(key)
            m <- Map.add key (if ok then fn key aval bval else bval) m
        m
        
    /// Builds an intersection of two maps using function `fn` to produce an output value. Function arguments:
    /// 1. Key of reconciled entires matched between both maps `a` and `b`.
    /// 2. Value of entry in first map `a`.
    /// 3. Value of entry in second map `b`.
    let intersect (fn: 'k -> 'v -> 'v -> 'v2) (a: Map<'k,'v>) (b: Map<'k,'v>): Map<'k,'v2> =
        let mutable m = Map.empty
        for e in b do
            let key = e.Key
            let bval = e.Value
            let ok, aval = a.TryGetValue(key)
            if ok then
                m <- Map.add key (fn key aval bval) m
        m

[<RequireQualifiedAccess>]
module Result =
        
    /// Unwraps value from the Result if it was Ok. In case of Error,
    /// the underlying exception is being rethrown with preserved stack trace.
    let inline unwrap (r: Result<'t, #exn>) =
        match r with
        | Ok value  -> value
        | Error err ->
            ExceptionDispatchInfo.Capture(err).Throw()
            Unchecked.defaultof<'t>