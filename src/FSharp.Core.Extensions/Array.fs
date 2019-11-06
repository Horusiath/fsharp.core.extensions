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

module FSharp.Core.Array

open System
open System
open System.Numerics
open System.Runtime.Intrinsics

/// Inserts a `value` at the given `index` of an `array`,
/// returning new array in the result with all contents copied and expanded by inserted item.
let insert (index: int) (value: 'a) (array: 'a[]): 'a[] =
    let count = array.Length
    if index < 0 || index > count then raise (IndexOutOfRangeException (sprintf "Cannot insert value at index %i of array of size %i" index count))
    else
        let copy = Array.zeroCreate (count+1)
        Array.blit array 0 copy 0 index
        copy.[index] <- value
        Array.blit array index copy (index+1) (count-index)
        copy

let removeAt (index: int) (array: 'a[]): 'a[] =
    let count = array.Length
    if index < 0 || index > count then raise (IndexOutOfRangeException (sprintf "Cannot insert value at index %i of array of size %i" index count))
    else
        let copy = Array.zeroCreate (count-1)
        Array.blit array 0 copy 0 (index-1)
        Array.blit array index copy (index+1) (count-index)
        copy
        
[<RequireQualifiedAccess>]
module Simd =

    /// Checks if two arrays are equal to each other using vectorized operations.           
    let equal (a: 'a[]) (b: 'a[]): bool =
        if a.Length <> b.Length then false
        else
            let mutable result = true
            let mutable i = 0
            let vectorizedLen = a.Length - Vector<'a>.Count
            while result && i < vectorizedLen do
                let va = Vector<'a>(a, i)
                let vb = Vector<'a>(b, i)
                result <- Vector.EqualsAll(va, vb)
                i <- i + Vector<'a>.Count
            if result then    
                while i < a.Length do
                    result <- a.[i] = b.[i]
                    i <- i + 1
            result
    
    /// Checks if given `item` can be found inside a window of an array
    /// (starting at given `offset` for up to `count` items), using vectorized operations.
    let containsWithin offset count (item: 'a) (a: 'a[]): bool =
        if offset + count > a.Length then raise (ArgumentException <| sprintf "Provided range window (offset: %i, count: %i) doesn't fit inside array of length: %i" offset count a.Length)
        let finish = offset + count
        if count < Vector<'a>.Count then
            let mutable i = offset
            let mutable found = false
            while not found && i < finish do
                found <- a.[i] = item
            found
        else
            let mutable found = true
            let mutable i = offset
            let vi = Vector<'a>(item)
            let vectorizedLen = finish - Vector<'a>.Count
            while not found && i < vectorizedLen do
                let va = Vector<'a>(a, i)
                found <- Vector.EqualsAny(vi, va)
                i <- i + Vector<'a>.Count
            if not found then     
                while i < finish do
                    found <- a.[i] = item
                    i <- i + 1
            found
            
    /// Checks if given `item` can be found inside a window of an array, using vectorized operations.
    let inline contains item a = containsWithin 0 (Array.length a) item a
    