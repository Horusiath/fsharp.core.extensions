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
open System.Collections.Generic
open System.Runtime.CompilerServices

[<Interface>]
type IAllocator<'a> =
    abstract Alloc: unit -> 'a
    abstract Free: 'a -> unit

/// A default implementation of `IAllocator{T}` interface.
/// It simply proxies a creation of new values.
[<Struct>]
type DefaultAllocator<'a when 'a : (new : unit -> 'a)> =
    interface IAllocator<'a> with
        
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Alloc() = new 'a()
        
        [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
        member __.Free _ = ()
        
/// A pooling implementation of `IAllocator{T}` interface.
/// It will collect a freed items, so that they can be reused later.  
[<Sealed>]
type PoolAllocator<'a when 'a : (new : unit -> 'a)>() =
    let pool = System.Collections.Concurrent.ConcurrentStack<'a>()
    static member Global = new PoolAllocator<'a>()
    interface IAllocator<'a> with
        
        member __.Alloc() =
            match pool.TryPop() with
            | true, value -> value
            | false, _    -> new 'a()
        
        member __.Free value =
            pool.Push value
            
module internal VecConstants =
    
    /// The size of a single vector node.
    let [<Literal>] SegmentCapacity = 32

[<Sealed>]   
type VecNode<'a>() =
    member val array: 'a[] = Array.zeroCreate VecConstants.SegmentCapacity
    
[<Struct>]
type VecEnumerator<'a>(node: VecNode<'a>) =
    [<DefaultValue(false)>]
    val mutable private current: VecNode<'a>
    interface IEnumerator<'a> with
        member __.Current: 'a = failwith "not implemented"
        member __.Current: obj = failwith "not implemented"
        member this.Reset() = this.current <- node
        member __.Dispose() = ()
        member this.MoveNext(): bool = failwith "not implemented"
            
/// An immutable implementation of array-like data structure. It's based on
/// Bitmapped Vector Trie implementation and it offers a sane compromise
/// between append/insert and access to any element (which are all ~O(1)).
[<AbstractClass>]
type Vec<'a> internal(count: int, root: VecNode<'a>) =
    static let empty: Vec<'a> = upcast Vec<DefaultAllocator<VecNode<'a>>, 'a>(DefaultAllocator(), 0, VecNode()) 
    abstract Alloc: unit -> VecNode<'a>
    abstract Free: VecNode<'a> -> unit
    static member Empty: Vec<'a> = empty
    static member Create(items: #ICollection<'a>) =
        let count = items.Count
        if count < 32 then
            ()
        elif count = 32 then ()
        else
            ()
    member __.Count = count
    member __.GetEnumerator() = new VecEnumerator<'a>(root)
    member __.ElementAt(index: int): 'a = failwith "not implemented"
    member __.IndexOf(value: 'a): int = failwith "not implemented"
    member __.CopyTo(array: 'a[], arrayIndex: int) = failwith "not implemented"
    interface IEquatable<Vec<'a>> with
        member this.Equals(other: Vec<'a>): bool = failwith "not implemented"
    interface IEnumerable<'a> with
        member this.GetEnumerator(): IEnumerator<'a> = upcast this.GetEnumerator()
        member this.GetEnumerator(): System.Collections.IEnumerator = upcast this.GetEnumerator()
    interface IList<'a> with
        member this.Count = this.Count
        member __.IsReadOnly = true
        member this.Item
            with get (index: int): 'a = this.ElementAt(index)
            and set index value = raise (NotSupportedException "Vec cannot insert elements in mutable fashion.") 
        member __.Add _ = raise (NotSupportedException "Vec cannot add elements in mutable fashion.")
        member __.Clear() = raise (NotSupportedException "Vec cannot be cleared in mutable fashion.")
        member __.Insert(_,_) = raise (NotSupportedException "Vec cannot insert elements in mutable fashion.")
        member __.RemoveAt(_) = raise (NotSupportedException "Vec cannot remove elements in mutable fashion.")
        member __.Remove(_) = raise (NotSupportedException "Vec cannot remove elements in mutable fashion.")
        member this.IndexOf(value) = this.IndexOf(value)
        member this.Contains(value) = this.IndexOf(value) <> -1
        member this.CopyTo(array, arrayIndex) = this.CopyTo(array, arrayIndex)
    interface IReadOnlyList<'a> with
        member this.Count = this.Count
        member this.Item with get index = this.ElementAt(index)
  
and [<Sealed>] Vec<'alloc, 'a when 'alloc :> IAllocator<VecNode<'a>>>(allocator: 'alloc, count: int, root: VecNode<'a>) =
    inherit Vec<'a>(count, root)
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    override __.Alloc() = allocator.Alloc()
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    override __.Free node = allocator.Free node

[<RequireQualifiedAccess>]
module Vec =
    
    /// Returns an empty vector.
    let inline empty<'a> = Vec<_>.Empty
    
    /// Constructs current vector out of the given array.
    [<CompiledName("OfArray")>]
    let ofArray (a: 'a[]): Vec<'a> = failwith "not implemented"
    
    /// Constructs current vector out of the given sequence of elements.
    [<CompiledName("OfEnumerable")>]
    let ofSeq (a: 'a[]): Vec<'a> = failwith "not implemented"
        
    /// Copies contents of current vector into new array and returns it.
    [<CompiledName("ToArray")>]
    let toArray (a: Vec<'a>): 'a[] = failwith "not implemented"
    
    /// Checks if current vector contains no elements. Complexity: O(1).
    [<CompiledName("IsEmpty")>]
    let isEmpty (v: Vec<'a>): bool = failwith "not implemented"
    
    /// Returns a length of the current vector.
    /// Complexity: O(1).
    [<CompiledName("Length")>]
    let length (v: Vec<'a>): int = failwith "not implemented"
    
    /// Inserts an element at the end of vector, returning new vector in the result.
    /// Complexity: O(log32(n)) - most of the time it's close to O(1).
    [<CompiledName("Append")>]
    let append (item: 'a) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"
    
    /// Inserts an element at the beginning of the vector, returning new vector in the result.
    /// Complexity: O(n) - requires copying entire vector.
    [<CompiledName("Prepend")>]
    let prepend (item: 'a) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"
    
    /// Inserts an element at the given position of the vector, returning new vector in the result.
    /// Complexity: O(m + log32(n-m)) where i is the number of elements after given index.
    /// The closer to the end of the vector, the less expensive it is.
    [<CompiledName("Insert")>]
    let insert (index: int) (item: 'a) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"

    [<CompiledName("InsertRange")>]
    let insertRange (index: int) (items: #seq<'a>) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"
    
    [<CompiledName("Update")>]
    let update (index: int) (item: 'a) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"
    
    /// Returns first element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("First")>]
    let head (v: Vec<'a>) : 'a voption = failwith "not implemented"
    
    /// Returns the last element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("Last")>]
    let last (v: Vec<'a>): 'a voption = failwith "not implemented"
    
    /// Returns vector element at provided index, or None if index is out of bound of a vector.
    [<CompiledName("ElementAt")>]
    let nth (index: int) (v: Vec<'a>): 'a voption = failwith "not implemented"
    
    /// Concatenates two vectors, returning new vector with elements of vector `a` first, then elements of vector `b` second.
    /// Complexity: O(b.length) - requires copying elements of b.
    [<CompiledName("Concat")>]
    let concat (a: Vec<'a>) (b: Vec<'a>): Vec<'a> = failwith "not implemented"
    
    [<CompiledName("IndexOf")>]
    let indexOf (item: 'a) (v: Vec<'a>): int voption = failwith "not implemented"
    
    [<CompiledName("Contains")>]
    let contains (item: 'a) (v: Vec<'a>): bool = failwith "not implemented"
    
    [<CompiledName("FirstOrDefault")>]
    let find (f: 'a -> bool) (v: Vec<'a>): 'a voption = failwith "not implemented"
    
    [<CompiledName("Map")>]
    let map (f: 'a -> 'b) (v: Vec<'a>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("MapWithIndex")>]
    let mapi (f: int -> 'a -> 'b) (v: Vec<'a>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("Filter")>]
    let filter (f: 'a -> bool) (v: Vec<'a>): Vec<'a> = failwith "not implemented"
    
    [<CompiledName("Exists")>]
    let exists (f: 'a -> bool) (v: Vec<'a>): bool = failwith "not implemented"
    
    [<CompiledName("Choose")>]
    let choose (f: 'a -> 'b option) (v: Vec<'a>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("Slice")>]
    let slice (low: int) (high: int) (v: Vec<'a>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("Fold")>]
    let fold (f: 'a -> 'b -> 'a) (init: 'a) (v: Vec<'a>): 'b = failwith "not implemented"
    
    [<CompiledName("Reduce")>]
    let reduce (f: 'a -> 'a -> 'a) (v: Vec<'a>): 'a voption = failwith "not implemented"
    
    [<CompiledName("Sum")>]
    let sum (v: Vec<int>): int = failwith "not implemented" 