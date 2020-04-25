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

/// Implementation of Hybrid-Logical Time, which aims to provide a consistent always increasing timestamps,
/// which is not always guaranteed by a `System.DateTime.UtcNow` property. In order to achieve that, when
/// a new timestamp is coming from remote system, a `HybridTime.adjust` should be called.
///
/// Implementation uses UTC-compatible 64bit date time representation, where top 48bits are used as a basis
/// for physical time - this allows to keep time consistency up to milliseconds. The lower 16bits are used
/// for logical component, which is used to guarantee consistency through monotonic increment if necessary.
/// 
/// See: http://users.ece.utexas.edu/~garg/pdslab/david/hybrid-time-tech-report-01.pdf
[<RequireQualifiedAccess>]
module Hlc =
    
    let [<Literal>] private MASK = 0xffL    
    let mutable private osTime = fun () -> DateTime.UtcNow.Ticks &&& ~~~MASK
    let mutable private highest = atom (osTime())
    let private getTime = fun t -> Math.Max(osTime (), t+1L)
         
    /// Returns a hybrid time, which can be used to represent a UTC-compatible time.
    /// Unlike `DateTime.UtcNow` this value is guaranteed to be monotonic.
    let now (): DateTime =
        let ticks = highest |> Atomic.update (fun ts -> Math.Max(osTime(), ts) + 1L)
        DateTime(ticks, DateTimeKind.Utc)
    
    /// Updates a current hybrid clock with the `time` incoming from remote replica.
    let sync (remoteDate: DateTime): DateTime =
        let ticks = highest |> Atomic.update (fun ts -> Math.Max(ts, remoteDate.Ticks))
        DateTime(ticks, DateTimeKind.Utc)
        