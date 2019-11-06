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
open System.Runtime.CompilerServices

[<Interface>]
type IAtomic<'a> =
    abstract Value: unit -> 'a
    abstract Swap: 'a -> 'a
    abstract CompareAndSwap: 'a * 'a -> bool
    abstract Update: ('a -> 'a) -> 'a
        
[<Sealed>]
type AtomicBool(initialValue: bool) =
    let mutable value: int = if initialValue then 1 else 0
    interface IAtomic<bool> with
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Value () = Volatile.Read(&value) = 1

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Swap (nval: bool): bool = Interlocked.Exchange(&value, if nval then 1 else 0) = 1

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.CompareAndSwap (compared: bool, nval: bool): bool = 
            let v = if compared then 1 else 0
            Interlocked.CompareExchange(&value, (if nval then 1 else 0), v) = v
            
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Update (modify) =
            let rec loop modify=
                let old = Volatile.Read(&value)
                let nval = if modify (old = 1) then 1 else 0
                if Interlocked.CompareExchange(&value, nval, old) = old
                then nval = 1
                else loop modify
            loop modify

[<Sealed>]
type AtomicInt(initialValue: int) =
    let mutable value: int = initialValue
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member __.Increment() = Interlocked.Increment(&value)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member __.Decrement() = Interlocked.Decrement(&value)

    interface IAtomic<int> with
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Value () = Volatile.Read(&value)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Swap (nval: int): int = Interlocked.Exchange(&value, nval)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.CompareAndSwap (compared: int, nval: int): bool = Interlocked.CompareExchange(&value, nval, compared) = compared

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Update (modify) =
            let rec loop modify=
                let old = Volatile.Read(&value)
                let nval = modify old
                if Interlocked.CompareExchange(&value, nval, old) = old
                then nval
                else loop modify
            loop modify
            
[<Sealed>]
type AtomicInt64(initialValue: int64) =
    let mutable value: int64 = initialValue

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member __.Increment() = Interlocked.Increment(&value)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member __.Decrement() = Interlocked.Decrement(&value)
    
    interface IAtomic<int64> with
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Value () = Volatile.Read(&value)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Swap (nval: int64): int64 = Interlocked.Exchange(&value, nval)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.CompareAndSwap (compared: int64, nval: int64): bool = Interlocked.CompareExchange(&value, nval, compared) = compared
        
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Update (modify) =
            let rec loop modify=
                let old = Volatile.Read(&value)
                let nval = modify old
                if Interlocked.CompareExchange(&value, nval, old) = old
                then nval
                else loop modify
            loop modify
            
[<Sealed>]
type AtomicFloat(initialValue: float) =
    let mutable value: float = initialValue
    interface IAtomic<float> with
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Value () = Volatile.Read(&value)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Swap (nval: float): float = Interlocked.Exchange(&value, nval)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.CompareAndSwap (compared: float, nval: float): bool = Interlocked.CompareExchange(&value, nval, compared) = compared
        
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Update (modify) =
            let rec loop modify=
                let old = Volatile.Read(&value)
                let nval = modify old
                if Interlocked.CompareExchange(&value, nval, old) = old
                then nval
                else loop modify
            loop modify
[<Sealed>]
type AtomicFloat32(initialValue: float32) =
    let mutable value: float32 = initialValue
    interface IAtomic<float32> with
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Value () = Volatile.Read(&value)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Swap (nval: float32): float32 = Interlocked.Exchange(&value, nval)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.CompareAndSwap (compared: float32, nval: float32): bool = Interlocked.CompareExchange(&value, nval, compared) = compared

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Update (modify) =
            let rec loop modify=
                let old = Volatile.Read(&value)
                let nval = modify old
                if Interlocked.CompareExchange(&value, nval, old) = old
                then nval
                else loop modify
            loop modify
[<Sealed>]
type AtomicRef<'a when 'a: not struct>(initialValue: 'a) =
    let mutable value: 'a = initialValue
    interface IAtomic<'a> with
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Value () = Volatile.Read(&value)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Swap (nval: 'a): 'a = Interlocked.Exchange<'a>(&value, nval)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.CompareAndSwap (compared: 'a, nval: 'a): bool = Object.ReferenceEquals(Interlocked.CompareExchange<'a>(&value, nval, compared), compared)

        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Update (modify) =
            let rec loop modify=
                let old = Volatile.Read(&value)
                let nval = modify old
                if obj.ReferenceEquals(Interlocked.CompareExchange(&value, nval, old), old)
                then nval
                else loop modify
            loop modify
            
[<Struct>]
type Atom =
    static member inline ($) (_: Atom, value: bool) = AtomicBool value
    static member inline ($) (_: Atom, value: int) = AtomicInt value
    static member inline ($) (_: Atom, value: int64) = AtomicInt64 value
    static member inline ($) (_: Atom, value: float) = AtomicFloat value
    static member inline ($) (_: Atom, value: float32) = AtomicFloat32 value
    static member inline ($) (_: Atom, value: 'a) = AtomicRef value

[<AutoOpen>]
module Atom =

    /// Create a new reference cell with atomic access semantics.
    let inline atom value = Unchecked.defaultof<Atom> $ value

/// Atomic module can be used to work with atomic reference cells. They are 
/// expected to look and work like standard F# ref cells with the difference 
/// that they work using thread-safe atomic operations for reads and updates.
[<RequireQualifiedAccess>]
module Atomic =

    /// Atomically replaces old value stored inside an atom with a new one,
    /// but only if previously stored value is (referentially) equal to the
    /// expected value. Returns true if managed to successfully replace the
    /// stored value, false otherwise.
    let inline cas (expected: 'a) (nval: 'a) (atom: #IAtomic<'a>) =
        atom.CompareAndSwap(expected, nval)

    /// Atomically tries to update value stored inside an atom, by passing
    /// current atom's value to modify function to get new result, which will
    /// be stored instead. Returns an updated value.
    let inline update (modify: 'a -> 'a) (atom: #IAtomic<'a>): 'a =
        atom.Update modify

    /// Atomically increments counter stored internally inside of an atom.
    /// Returns an incremented value.
    let inline inc (atom: ^a ): ^b when ^a : (member Increment: unit -> ^b) =
        ( ^a : (member Increment: unit -> ^b) (atom))
        
    /// Atomically decrements counter stored internally inside of an atom.
    /// Returns an incremented value.
    let inline dec (atom: ^a ): ^b when ^a : (member Decrement: unit -> ^b) =
        ( ^a : (member Decrement: unit -> ^b) (atom))

    module Operators =

        /// Unwraps the value stored inside of an atom.
        let inline (!) (atom: #IAtomic<'a>): 'a = atom.Value ()

        /// Atomically swaps the value stored inside of an atom with provided one.
        /// Returns previously stored value.
        let inline (:=) (atom: #IAtomic<'a>) (value: 'a): 'a = atom.Swap value