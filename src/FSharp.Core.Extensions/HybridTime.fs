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
open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading

/// Implementation of Hybrid-Logical Time, which aims to provide a consistent always increasing timestamps,
/// which is not always guaranteed by a `System.DateTime.UtcNow` property. In order to achieve that, when
/// a new timestamp is coming from remote system, a `HybridTime.update` should be called.
///
/// Implementation uses UTC-compatible 64bit date time representation, where top 48bits are used as a basis
/// for physical time - this allows to keep time consistency up to milliseconds. The lower 16bits are used
/// for logical component, which is used to guarantee consistency through monotonic increment if necessary.
/// 
/// See: http://users.ece.utexas.edu/~garg/pdslab/david/hybrid-time-tech-report-01.pdf
[<IsReadOnly;Struct>]
type HybridTime =
    { Ticks: int64 }
    override this.ToString() = DateTime(this.Ticks, DateTimeKind.Utc).ToString("O")

[<RequireQualifiedAccess>]
module HybridTime =
    
    let [<Literal>] private mask = 0xffL
    
    let mutable private osTime = fun () -> DateTime.UtcNow.Ticks &&& ~~~mask

    let mutable private latestTicks = atom (osTime())
     
    /// Converts current `DateTime` to a `HybridTime`
    let inline ofDateTime (date: DateTime): HybridTime =
        let ticks = date.ToUniversalTime().Ticks &&& ~~~mask
        { Ticks = ticks }
        
    /// Converts current `HybridTime` to a `DateTime`.
    let inline toDateTime (time: HybridTime): DateTime = DateTime(time.Ticks, DateTimeKind.Utc)
         
    /// Returns a hybrid time, which can be used to represent a UTC-compatible time.
    let now (): HybridTime =
        let ticks = 
            latestTicks
            |> Atomic.update (fun t -> max (osTime ()) (t+1L))
        { Ticks = ticks }
        
    /// Returns the ticks (close equivalent to DateTime.Ticks) stored inside `HybridTime`.
    let inline ticks (time: HybridTime): int64 = time.Ticks
    
    /// Updates a current hybrid clock with the `time` incoming from remote replica.
    let update (time: HybridTime): HybridTime =
        let ticks = latestTicks |> Atomic.update (max time.Ticks)
        { Ticks = ticks }
        
    module Unsafe =
        
        /// Executes given `thunk` of code within the mocked environment, where a OS wall clock is replaced
        /// with a given `clock` function (expected to return ticks of current time).
        let mocked (clock: unit -> int64) (thunk: unit -> unit) =
            let oldClock = Interlocked.Exchange(&osTime, clock)
            try
                thunk()
            finally
                Volatile.Write(&osTime, oldClock)