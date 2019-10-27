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

/// Utility functions that enable traverse function helpers over IEnumerator-like elements.
[<RequireQualifiedAccess>]
module FSharp.Core.Enum

open System.Collections.Generic
open System.Runtime.CompilerServices

let inline iter<'e, 'a when 'e :> IEnumerator<'a>> (fn: 'a -> unit) (e: 'e) =
    let mutable e2 = e
    while e2.MoveNext() do
        fn e.Current
        
let inline fold<'e, 'a, 'b when 'e :> IEnumerator<'a>> (fn: 'b -> 'a -> 'b) (init: 'b) (e: 'e) =
    let mutable e2 = e
    let mutable state = init
    while e2.MoveNext() do
        state <- fn state e.Current
    state
    
let reduce<'e, 'a when 'e :> IEnumerator<'a>> (fn: 'a -> 'a -> 'a) (e: 'e) =
    let mutable e2 = e
    if e2.MoveNext() then
        let mutable state = e2.Current
        while e2.MoveNext() do
            state <- fn state e.Current
        ValueSome state
    else ValueNone
    
[<Struct>]
type FilterEnumerator<'e, 'a when 'e :> IEnumerator<'a>> =
    val mutable private enumerator: 'e
    val private predicate: 'a -> bool
    new(enumerator: 'e, predicate: 'a -> bool) =
        { enumerator = enumerator; predicate = predicate }
    member this.Current = this.enumerator.Current
    member this.MoveNext() =
        let mutable satisfied = false
        while not satisfied && this.enumerator.MoveNext() do
            satisfied <- this.predicate this.enumerator.Current
        satisfied
    interface IEnumerator<'a> with
        member this.Current: 'a = this.Current
        member this.Current: obj = upcast this.Current
        member this.Reset() = this.enumerator.Reset()
        member this.Dispose() = this.enumerator.Dispose()
        member this.MoveNext(): bool = this.MoveNext()
    
    
let filter<'e, 'a when 'e :> IEnumerator<'a>> (fn: 'a -> bool) (e: 'e) = new FilterEnumerator<_,_>(e, fn)