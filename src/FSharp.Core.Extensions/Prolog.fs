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

module FSharp.Core.ExtOperators

open System
open System.Collections.Generic
open System.Collections.Generic

/// An operator over a implicit cast operation.
let inline (~%) (x:^a) : ^b = ((^a or ^b) : (static member op_Implicit : ^a -> ^b) x)  

/// Creates a key value pair out of provided `key` and `value` arguments.
let inline (=>) (key: 'k) (value: 'v): KeyValuePair<'k,'v> = KeyValuePair(key, value)

type System.TimeSpan with

    /// Multiplies given time span by a given number of `times` eg. 2.sec * 2.5 => 5.sec.
    static member (*) (time: TimeSpan, times: float): TimeSpan =
        TimeSpan(int64 (float time.Ticks * times))
        
    /// Multiplies given time span by a given number of `times` eg. 2.sec * 2 => 4.sec.
    static member (*) (time: TimeSpan, times: int): TimeSpan =
        TimeSpan(int64 (time.Ticks * int64 times))