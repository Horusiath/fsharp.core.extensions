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

/// Immutable, atomically swapped state of `AsyncPromise`.
type internal VarState<'t> =
    /// Promise was not completed yet. We gather Async callbacks to complete/reject them after promise is completed.
    | Empty of ('t->unit) list //TODO: we could implement list of awaiters in terms of VarState itself
    | Value of 't
    override this.ToString() =
        match this with
        | Empty awaiters -> sprintf "Empty(awaiters: %i)" (List.length awaiters)
        | Value value    -> sprintf "Value(%O)" value
    
type NotEmptyVarException(msg) =
    inherit Exception(msg)

/// An value cell, that can be subscribed to in order to asynchronously await for its content to be filled.
/// Unlike `MVar` its value can be set only once. When this is done, all subsequent reads will be immediately resolved.
[<Sealed; NoComparison; NoEquality>]
type IVar<'t>(?value: 't) =
    let mutable state =
        match value with
        | None -> Empty []
        | Some value -> Value value 
    let accept (resolve, _reject, _cancel) =
        let rec update resolve =
            let old = Volatile.Read &state
            match old with
            | Empty list ->
                let nval = Empty (resolve::list)
                if not (obj.ReferenceEquals(old, Interlocked.CompareExchange(&state, nval, old))) then
                    update resolve
            | Value value -> resolve value
        update resolve
    let async = Async.FromContinuations accept
    /// Asynchronously read a value from this cell. If cell has not value,
    /// this async will await until the value will be written.
    member this.Value: Async<'t> = async
    /// Check if `IVar` value has been resolved. If so, all `Value` awaiter will be completed immediately.
    member this.HasValue : bool = match Volatile.Read &state with Value _ -> true | _ -> false
    /// Try to write a value, resolving all awaiting value readers as well as future incoming ones.
    /// Returns true if value was successfully written, false if another value already resided there.
    member this.TryWrite (value: 't): bool =
        let rec update value =
            let old = Volatile.Read &state
            match old with
            | Value _ -> false
            | Empty awaiters ->
                if obj.ReferenceEquals(Interlocked.CompareExchange(&state, Value value, old), old) then
                    for resolve in awaiters do
                        resolve value
                    true
                else update value               
        update value
    /// Try to write a value, resolving all awaiting value readers as well as future incoming ones.
    /// If value was already written, throws `NotEmptyVarException`.
    member this.Write (value: 't): unit =
        if not (this.TryWrite value) then
            raise (NotEmptyVarException "Cannot write value to IVar, as it was already written to.")
    override this.ToString() = sprintf "IVar<%O>(%O)" typeof<'t>.Name state
    
/// An value cell, that can be subscribed to in order to asynchronously await for its content to be filled.
/// Unlike `MVar` its value can be set only once. When this is done, all subsequent reads will be immediately resolved.
[<RequireQualifiedAccess>]
module IVar =
    
    /// Create a new empty `IVar`.
    let inline empty (): IVar<'t> = IVar<'t>()
    
    /// Create a new `IVar` with value already being resolved.
    let inline init (value: 't): IVar<'t> = IVar<'t>(value)
    
    /// Asynchronously read a value from this cell. If cell has not value,
    /// this async will await until the value will be written.
    let inline read (v: IVar<'t>) : Async<'t> = v.Value
    
    /// Check if `IVar` value has been resolved. If so, all `Value` awaiter will be completed immediately.
    let inline hasValue (v: IVar<'t>) : bool = v.HasValue
    
    /// Try to write a value, resolving all awaiting value readers as well as future incoming ones.
    /// If value was already written, throws `NotEmptyVarException`.
    let inline write (value: 't) (v: IVar<'t>) = v.Write(value)
    
    /// Try to write a value, resolving all awaiting value readers as well as future incoming ones.
    /// Returns true if value was successfully written, false if another value already resided there.
    let inline tryWrite (value: 't) (v: IVar<'t>) = v.TryWrite(value)

/// An value cell, that can be subscribed to in order to asynchronously await for its content to be filled.
/// Unlike `IVar` its value can be set only multiple times. When this is done, all subsequent reads will be
/// immediately resolved.
[<Sealed; NoComparison; NoEquality>]
type MVar<'t>(?value: 't) =
    let mutable state =
        match value with
        | None -> Empty []
        | Some value -> Value value
    let accept (resolve, _reject, _cancel) =
        let rec update resolve =
            let old = Volatile.Read &state
            match old with
            | Empty list ->
                let nval = Empty (resolve::list)
                if not (obj.ReferenceEquals(old, Interlocked.CompareExchange(&state, nval, old))) then
                    update resolve
            | Value value -> resolve value
        update resolve
    let async = Async.FromContinuations accept
    /// Asynchronously read a value from this cell. If cell has not value,
    /// this async will await until the value will be written.
    member this.Value: Async<'t> = async
    /// Check if `MVar` value has been resolved. If so, all `Value` awaiter will be completed immediately.
    member this.HasValue : bool = match Volatile.Read &state with Value _ -> true | _ -> false
    /// Write a value, resolving all awaiting value readers as well as future incoming ones.
    /// Returns previously stored value or None, if there was none.
    member this.Swap (value: 't): 't option =
        let rec update value =
            let old = Volatile.Read &state
            match old with
            | Value previousValue -> 
                if obj.ReferenceEquals(Interlocked.CompareExchange(&state, Value value, old), old)
                then Some previousValue                    
                else update value 
            | Empty awaiters ->
                if obj.ReferenceEquals(Interlocked.CompareExchange(&state, Value value, old), old) then
                    for resolve in awaiters do
                        resolve value
                    None
                else update value               
        update value
    /// Clears the last resolved value and returns it. All incoming reads will be blocked asynchronously
    /// and awaited until a new value will be written again.
    member this.Clear () : 't option =
        let rec update value =
            let old = Volatile.Read &state
            match old with
            | Value previousValue -> 
                if obj.ReferenceEquals(Interlocked.CompareExchange(&state, Empty [], old), old)
                then Some previousValue                    
                else update value 
            | Empty _ -> None            
        update value
    override this.ToString() = sprintf "MVar<%O>(%O)" typeof<'t>.Name state

/// An value cell, that can be subscribed to in order to asynchronously await for its content to be filled.
/// Unlike `IVar` its value can be set only multiple times. When this is done, all subsequent reads will be
/// immediately resolved.
[<RequireQualifiedAccess>]
module MVar =

    /// Create a new empty instance of `MVar`.
    let inline empty () : MVar<'t> = MVar<'t>()
    
    /// Create a new instance of `MVar` with initialized value.
    let inline init (value: 't) : MVar<'t> = MVar<'t>(value)
    
    /// Asynchronously read a value from this cell. If cell has not value,
    /// this async will await until the value will be written.
    let inline read (v: MVar<'t>) : Async<'t> = v.Value
    
    /// Check if `MVar` value has been resolved. If so, all `Value` awaiter will be completed immediately.
    let inline hasValue (v: MVar<'t>) : bool = v.HasValue
    
    /// Write a value, resolving all awaiting value readers as well as future incoming ones.
    /// Returns previously stored value or None, if there was none.
    let inline swap (value: 't) (v: MVar<'t>) : 't option = v.Swap(value)
    
    /// Clears the last resolved value and returns it. All incoming reads will be blocked asynchronously
    /// and awaited until a new value will be written again.
    let inline clear (v: MVar<'t>) : 't option = v.Clear()