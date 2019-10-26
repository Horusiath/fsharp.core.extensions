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
open System.Collections.Generic

type PrefixMap<'a>() =
    static member Empty: PrefixMap<'a> = Unchecked.defaultof<_>

[<RequireQualifiedAccess>]
module PrefixMap =
    
    let empty<'a> = PrefixMap<'a>.Empty
    
    let ofSeq (seq: seq<string * 'a>): PrefixMap<'a> = failwith "not implemented"
    
    let ofMap (map: #seq<KeyValuePair<string, 'a>>): PrefixMap<'a> = failwith "not implemented"
    
    let add (key: string) (value: 'a) (map: PrefixMap<'a>): PrefixMap<'a> = failwith "not implemented"
    
    let tryFind (key: string) (map: PrefixMap<'a>): 'a voption = failwith "not implemented"
    
    let prefixed (prefix: string) (map: PrefixMap<'a>) = failwith "not implemented"
    
    let map (fn: string -> 'a -> 'b) (map: PrefixMap<'a>) = failwith "not implemented"
    
    let filter (fn: string -> 'a -> bool) (map: PrefixMap<'a>) = failwith "not implemented"
    