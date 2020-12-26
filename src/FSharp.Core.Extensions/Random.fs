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

[<Sealed>]
type internal ThreadSafeRandom() =
    static let mutable seed = System.Security.Cryptography.RandomNumberGenerator.Create()

    [<ThreadStatic; DefaultValue>]
    static val mutable private current: Random
    static member Current
        with get () = 
            if isNull ThreadSafeRandom.current
            then
                let span = Array.zeroCreate 4
                seed.GetBytes span
                ThreadSafeRandom.current <- Random(BitConverter.ToInt32(span, 0))
                
            ThreadSafeRandom.current

/// A module which allows to produce a random data in thread safe manner.
[<RequireQualifiedAccess>]
module Random =
    
    type internal R = ThreadSafeRandom

    /// Returns a Random instance for current OS thread. Sharing this value between threads is NOT safe operation and
    /// may cause unexpected behavior.
    let current: Random = R.Current
    
    /// Mutates provided `array` by filling it with random bytes.
    /// This is a thread safe operation, given that input array access is exclusive for current thread.
    let fill (array: byte[]) = R.Current.NextBytes(array)
    
    /// Returns an array of randomized bytes of a given `size`. This is a thread safe operation.
    let bytes (size: int) =
        let buf = Array.zeroCreate size
        R.Current.NextBytes buf
        buf
    
    /// Returns a random byte. This is a thread safe operation.
    let byte (): byte = byte (abs (R.Current.Next()))
        
    /// Returns a random signed byte. This is a thread safe operation.
    let sbyte (): sbyte = sbyte (R.Current.Next())
    
    /// Returns a random 16bit integer. This is a thread safe operation.
    let int16 (): int16 = int16 (R.Current.Next())
    
    /// Returns a random 32bit integer. This is a thread safe operation.
    let int32 (): int = R.Current.Next()

    /// Returns a random 64bit integer. This is a thread safe operation.
    let int64 (): int64 =
        let hi = R.Current.Next()
        let lo = R.Current.Next()
        ((int64 hi) <<< 32) ||| (int64 lo)
    
    /// Returns a random unsigned 16bit integer. This is a thread safe operation.
    let uint16 (): uint16 = uint16 (abs (R.Current.Next()))
    
    /// Returns a random unsigned 32bit integer. This is a thread safe operation.
    let uint32 (): uint32 =
        // since random.Next() is limited to int32 with sign, we must use different approach
        let s = Span.stackalloc sizeof<uint32>
        R.Current.NextBytes(s)
        BitConverter.ToUInt32(Span.op_Implicit s)

    /// Returns a random unsigned 64bit integer. This is a thread safe operation.
    let uint64 (): uint64 =
        // since random.Next() is limited to int32 with sign, we must use different approach
        let s = Span.stackalloc sizeof<uint64>
        R.Current.NextBytes(s)
        BitConverter.ToUInt64(Span.op_Implicit s)
        
    /// Returns a random 64bit floating point number. This is a thread safe operation.
    let float () : float = R.Current.NextDouble()
        
    /// Returns a random 32bit integer in [min, max) range. This is a thread safe operation.
    let between (min: int) (max: int): int = R.Current.Next(min, max)
    
    /// Returns a random TimeSpan fitting in between [min, max) range. This is a thread safe operation.
    let time (min: TimeSpan) (max: TimeSpan): TimeSpan =
        let value = abs (int64())
        TimeSpan ((value + min.Ticks) % max.Ticks)
        
    /// Picks a random element from given list. This is a thread safe operation.
    let pick (items: #IReadOnlyList<_>) =
        let length = items.Count
        items.[between 0 length]
    
    /// Returns items from a given sequence shuffled in a random order. This is a thread safe operation.
    let shuffle<'t> = Seq.sortWith<'t> (fun _ _ -> sign (int32 ()))