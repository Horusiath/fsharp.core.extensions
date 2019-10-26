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
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

module internal VecConst =
    
    [<Literal>] 
    let off = 5
    
    [<Literal>]
    let capacity = 32 // 1 << off
    
    [<Literal>]
    let mask = 0x1f // capacity - 1
    
    let inline tailoff count = if count < capacity then 0 else ((count - 1) >>> off) <<< off
    
open VecConst

type internal VecNode<'a> =
    | Leaf   of array:'a[]
    | Branch of children: VecNode<'a>[] 
    member this.Copy() =
        match this with
        | Leaf array -> Leaf (array.Clone() :?> 'a[])
        | Branch array -> Branch (array.Clone() :?> VecNode<'a>[])
    
[<IsByRefLike;Struct>]
type VecEnumerator<'a>(vector: Vec<'a>) =
    [<DefaultValue(false)>]val mutable private array: 'a[]
    [<DefaultValue(false)>]val mutable private current: 'a
    [<DefaultValue(false)>]val mutable private index: int
    [<DefaultValue(false)>]val mutable private offset: int
    member e.Current = e.current
    member e.MoveNext() =
        let i = e.index
        if i < vector.Count then
            if isNull e.array then e.array <- vector.FindArrayForIndex(i)
            elif i - e.offset = capacity then
                e.array <- vector.FindArrayForIndex(i)
                e.offset <- e.offset + capacity
            e.current <- e.array.[e.index &&& mask]
            e.index <- i + 1
            true
        else false
    interface IEnumerator<'a> with
        member this.Current: 'a = this.current
        member this.Current: obj = upcast this.current
        member this.Reset() =
            this.array <- null
            this.current <- Unchecked.defaultof<_>
            this.index <- 0
        member __.Dispose() = ()
        member this.MoveNext(): bool = this.MoveNext()
        
and [<Sealed>] Vec<'a> internal(count: int, shift: int, root: VecNode<'a>, tail: 'a[]) =
        
    let rec pathFor level node =
        if level = 0 then node
        else
            let result = Array.zeroCreate capacity
            result.[0] <- pathFor (level - off) node
            Branch result
    
    let rec appendTail (level: int) (Branch children) (tailNode: VecNode<'a>) =
        let subIdx = ((count - 1) >>> level) &&& mask            
        let result = Array.copy children
        let nodeToInsert =
            if level = off then tailNode
            else
                let child = children.[subIdx]
                if obj.ReferenceEquals(child, null) then appendTail (level-off) child tailNode
                else pathFor (level-off) tailNode
        result.[subIdx] <- nodeToInsert
        Branch result
    
    static let emptyNode: VecNode<'a> = Branch (Array.zeroCreate capacity)
    static let empty = Vec<_>(0, off, emptyNode, [||])
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Empty(): Vec<'a> = empty
    
    static member From(items: 'a[]): Vec<'a> = 
        let count = Array.length items
        if count > capacity then
            let mutable v = empty
            for item in items do
                v <- v.Append(item)
            v
        else
            Vec<_>(count, off, emptyNode, Array.copy items)
            
    member __.Count: int = count
    member this.GetEnumerator(): VecEnumerator<_> = new VecEnumerator<_>(this)
    
    member internal __.FindArrayForIndex(index: int): 'a[] =
        if index >= tailoff count then tail
        else
            let mutable node = root
            let mutable level = shift
            while level > 0 do
                match node with
                | Branch array ->
                    node <- array.[(index >>> level) &&& mask]
                | _ -> ()
                level <- level - shift
            let (Leaf array) = node
            array
    
    member __.Append(value) =
        if count - tailoff count < capacity
        then
            let tail' = Array.zeroCreate (tail.Length + 1)
            tail.CopyTo(tail', 0)
            tail'.[tail.Length] <- value
            Vec<_>(count + 1, shift, root, tail')
        else
            // tail is full
            let tailNode = Leaf tail
            let mutable shift' = shift
            let root' =
                if (count >>> off) > (1 <<< shift)
                then
                    // overflow root
                    let arr = Array.zeroCreate capacity
                    arr.[0] <- root
                    arr.[1] <- pathFor shift tailNode
                    shift' <- shift' + 5
                    Branch arr
                else
                    appendTail shift root tailNode
            Vec<_>(count + 1, shift', root', [| value |])
            
    member this.ElementAt(index: int): 'a =
        if index >= 0 && index < count then
            let n = this.FindArrayForIndex index
            n.[index &&& mask]
        else raise (IndexOutOfRangeException(sprintf "Index [%i] out of range of vector (size: %i)" index count))
        
    member inline this.Item with get index = this.ElementAt index

    member this.IndexOf(value: 'a): int =
        let eq = EqualityComparer<'a>.Default
        let mutable e = new VecEnumerator<'a>(this)
        let mutable found = -1
        let mutable i = 0
        while found = -1 && e.MoveNext() do
            if eq.Equals(e.Current, value) then
                found <- i
            i <- i + 1
        found            

    member this.CopyTo(array: 'a[], arrayIndex: int) =
        let mutable offset = 0
        let mutable i = arrayIndex
        while offset < count do
            let src = this.FindArrayForIndex(offset)
            Array.Copy(src, 0, array, i, src.Length)
            offset <- offset + capacity
            i <- i + capacity

    interface IEquatable<Vec<'a>> with
        member this.Equals(other: Vec<_>): bool =
            if obj.ReferenceEquals(other, null) then false
            elif obj.ReferenceEquals(this, other) then false
            elif count <> other.Count then false
            else
                let eq = EqualityComparer<'a>.Default
                let mutable e1 = new VecEnumerator<_>(this)
                let mutable e2 = new VecEnumerator<_>(other)
                let mutable result = false
                while not result && e1.MoveNext() && e2.MoveNext() do
                    result <- eq.Equals(e1.Current, e2.Current)
                result
                
    interface IEnumerable<'a> with
        member this.GetEnumerator(): IEnumerator<'a> = 
            let mutable array: 'a[] = null
            let mutable index = 0
            let mutable offset = 0
            { new IEnumerator<'a> with
                member __.Dispose () = ()
                member __.Reset() =
                    array <- null
                    index <- 0
                member __.Current: 'a = array.[index &&& mask]
                member __.Current: obj = upcast array.[index &&& mask]
                member __.MoveNext() =
                    let i = index
                    if i < this.Count then
                        if isNull array then array <- this.FindArrayForIndex(i)
                        else
                            if i - offset = capacity then
                                array <- this.FindArrayForIndex(i)
                                offset <- offset + capacity
                            index <- i + 1
                        true
                    else false }
        member this.GetEnumerator(): System.Collections.IEnumerator =
            upcast (this :> IEnumerable<'a>).GetEnumerator()
    interface IList<'a> with
        member this.Count = this.Count
        member __.IsReadOnly = true
        member this.IndexOf(value) = this.IndexOf(value)
        member this.Contains(value) = this.IndexOf(value) <> -1
        member this.CopyTo(array, arrayIndex) = this.CopyTo(array, arrayIndex)
        member this.Item
            with get (index: int): 'a = this.ElementAt(index)
            and set index value = raise (NotSupportedException "Vec cannot insert elements in mutable fashion.") 
        member __.Add _ = raise (NotSupportedException "Vec cannot add elements in mutable fashion.")
        member __.Clear() = raise (NotSupportedException "Vec cannot be cleared in mutable fashion.")
        member __.Insert(_,_) = raise (NotSupportedException "Vec cannot insert elements in mutable fashion.")
        member __.RemoveAt(_) = raise (NotSupportedException "Vec cannot remove elements in mutable fashion.")
        member __.Remove(_) = raise (NotSupportedException "Vec cannot remove elements in mutable fashion.")
    interface IReadOnlyList<'a> with
        member this.Count = this.Count
        member this.Item with get index = this.ElementAt(index)

type vec<'a> = Vec<'a>

type VecRangedEnumerator<'a> =
    struct
        val private vector: Vec<'a>
        val mutable private array: 'a[]
        val mutable private index: int
        val mutable private offset: int
        val private stop: int
        val private start: int
        new (v: Vec<_>, start: int, finish: int) =
            { vector = v;
              stop = finish;
              start = start;
              index = start;
              offset = (start - (start % capacity));
              array = null }
        member e.Current = e.array.[e.index &&& mask]
        member e.MoveNext() =
            let i = e.index
            if i < e.stop then
                if isNull e.array then e.array <- e.vector.FindArrayForIndex(i)
                else
                    if i - e.offset = capacity then
                        e.array <- e.vector.FindArrayForIndex(i)
                        e.offset <- e.offset + capacity
                    e.index <- i + 1
                true
            else false
    end
    interface IEnumerator<'a> with
        member this.Current: 'a = this.Current
        member this.Current: obj = upcast this.Current
        member this.Reset() =
            this.array <- null
            this.index <- this.start
        member __.Dispose() = ()
        member this.MoveNext(): bool = this.MoveNext()


[<RequireQualifiedAccess>]
module Vec =
    
    /// Returns an empty vector.
    let inline empty<'a> = Vec<'a>.Empty()
    
    /// Constructs current vector out of the given array.
    [<CompiledName("OfArray")>]
    let ofArray (items): Vec<_> = Vec<_>.From items
    
    /// Constructs current vector out of the given sequence of elements.
    [<CompiledName("OfEnumerable")>]
    let inline ofSeq (items: 'a seq): Vec<'a> =
        let mutable vector = Vec<'a>.Empty()
        for item in items do
            vector <- vector.Append(item)
        vector
        
    /// Copies contents of current vector into new array and returns it.
    [<CompiledName("ToArray")>]
    let toArray (a: Vec<_>): 'a[] = 
        let array = Array.zeroCreate a.Count
        a.CopyTo(array, 0)
        array
    
    /// Checks if current vector contains no elements. Complexity: O(1).
    [<CompiledName("IsEmpty")>]
    let inline isEmpty (v: Vec<_>): bool = v.Count = 0
    
    /// Returns a length of the current vector.
    /// Complexity: O(1).
    [<CompiledName("Length")>]
    let inline length (v: Vec<_>): int = v.Count
    
    /// Inserts an element at the end of vector, returning new vector in the result.
    /// Complexity: O(log32(n)) - most of the time it's close to O(1).
    [<CompiledName("Append")>]
    let inline append (item: 'a) (v: Vec<_>) : Vec<_> = v.Append(item)
    
    /// Inserts an element at the beginning of the vector, returning new vector in the result.
    /// Complexity: O(n) - requires copying entire vector.
    [<CompiledName("Prepend")>]
    let prepend (item: 'a) (v: Vec<_>) : Vec<_> = failwith "not implemented"
    
    /// Inserts an element at the given position of the vector, returning new vector in the result.
    /// Complexity: O(m + log32(n-m)) where i is the number of elements after given index.
    /// The closer to the end of the vector, the less expensive it is.
    [<CompiledName("Insert")>]
    let insert (index: int) (item: 'a) (v: Vec<_>) : Vec<_> = failwith "not implemented"

    [<CompiledName("InsertRange")>]
    let insertRange (index: int) (items: #seq<'a>) (v: Vec<_>) : Vec<_> = failwith "not implemented"
    
    /// Replaces element an given `index` with provided `item`. Returns an updated vector and replaced element.
    [<CompiledName("Update")>]
    let replace (index: int) (item: 'a) (v: Vec<_>) : (Vec<_> * 'a) = failwith "not implemented"
    
    /// Returns first element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("First")>]
    let head (v: Vec<_>) : 'a voption = if isEmpty v then ValueNone else ValueSome(v.ElementAt 0)
    
    /// Returns the last element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("Last")>]
    let last (v: Vec<_>): 'a voption = if isEmpty v then ValueNone else ValueSome(v.ElementAt (v.Count - 1))
    
    /// Returns vector element at provided index, or None if index is out of bound of a vector.
    [<CompiledName("ElementAt")>]
    let nth (index: int) (v: Vec<_>): 'a voption = 
        if index >= 0 && index < v.Count then
            ValueSome(v.ElementAt index)
        else ValueNone
    
    /// Concatenates two vectors, returning new vector with elements of vector `a` first, then elements of vector `b` second.
    /// Complexity: O(b.length) - requires copying elements of b.
    [<CompiledName("Concat")>]
    let concat (a: Vec<_>) (b: Vec<_>): Vec<_> = failwith "not implemented"
    
    [<CompiledName("IndexOf")>]
    let inline indexOf (item: 'a) (v: Vec<_>): int = v.IndexOf item
    
    [<CompiledName("Contains")>]
    let inline contains (item: 'a) (v: Vec<_>): bool = v.IndexOf item <> -1
    
    [<CompiledName("ForEach")>]
    let inline iter (f: 'a -> unit) (v: Vec<_>): unit =
        let mutable e = new VecEnumerator<'a>(v)
        while e.MoveNext() do
            f e.Current
            
    [<CompiledName("ForEach")>]
    let inline iteri (f: int -> 'a -> unit) (v: Vec<_>): unit =
        let mutable i = 0
        let mutable e = new VecEnumerator<'a>(v)
        while e.MoveNext() do
            f i e.Current
            i <- i + 1
    
    [<CompiledName("FirstOrDefault")>]
    let find (fn: 'a -> bool) (v: Vec<_>): 'a voption =
        let mutable e = new VecEnumerator<'a>(v)
        let mutable found = ValueNone
        while ValueOption.isNone found && e.MoveNext() do
            let c = e.Current
            if fn c then
                found <- ValueSome c
        found
                
    
    [<CompiledName("Map")>]
    let map (f: 'a -> 'b) (v: Vec<_>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("MapWithIndex")>]
    let mapi (f: int -> 'a -> 'b) (v: Vec<_>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("Filter")>]
    let filter (f: 'a -> bool) (v: Vec<_>): Vec<_> = failwith "not implemented"
    
    [<CompiledName("Exists")>]
    let exists (fn: 'a -> bool) (v: Vec<_>): bool =
        let mutable e = new VecEnumerator<'a>(v)
        let mutable found = false
        while not found && e.MoveNext() do
            found <- fn e.Current
        found
    
    [<CompiledName("Choose")>]
    let choose (f: 'a -> 'b option) (v: Vec<_>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("Slice")>]
    let slice (low: int) (high: int) (v: Vec<_>): VecRangedEnumerator<'a> = new VecRangedEnumerator<'a>(v, low, high)
    
    [<CompiledName("Fold")>]
    let fold (f: 'b -> 'a -> 'b) (init: 'b) (v: Vec<_>): 'b =
        let mutable e = new VecEnumerator<'a>(v) 
        let mutable state = init
        while e.MoveNext() do
            state <- f state e.Current
        state

    [<CompiledName("Reduce")>]
    let reduce (f: 'a -> 'a -> 'a) (v: Vec<_>): 'a voption =
        let mutable e = new VecEnumerator<'a>(v)
        if not (e.MoveNext()) then ValueNone
        else
            let mutable state = e.Current
            while e.MoveNext() do
                state <- f state e.Current
            ValueSome state 