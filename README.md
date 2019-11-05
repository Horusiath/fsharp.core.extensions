# FSharp.Core.Extensions

This library contains a set of utilities to support building efficient, concurrent programs using functional paradigm in F#.

## API

### Atomic

`atom` is meant to be an almost drop-in replacement of F# `ref` cells. It offers a read/update operations that use efficient interlocked semantics to perform a thread-safe read/modify access on all supported operations.

```fsharp
open FSharp.Core
open FSharp.Core.Atomic.Operators
// Create an atomic cell. Currently it's limited to work only with:
//
// - Reference types (classes, non-struct records, discriminated unions, tuples or anonymous objects)
// - 32- and 64-bit integers (keep in mind that interlocked 64-bit operations on 32-bit OSes are downgraded by .NET runtime)
// - booleans
// - 32- and 64-bit floating point numbers
let a = atom "hello"

// Receive stored value
!a  // returns: "hello"

// Replace stored value with a new one and return the previously stored one
a := "Hello" // returns: "hello"

// Those operations can be joined together to receive swap mechanics with no temporary variables necessary
let a = atom 1
let b = atom 2
a := b := !a |> ignore // swap contents of `a` and `b`. Now !a = 2 and !b = 1

// You can also increment/decrement atomic values, effectivelly turning it into thread-safe counter
let a = atom 1
Atomic.inc a // returns 2

// You can also perform a conditional updates a.k.a. Compare-And-Swap or Compare-Exchange.
// NOTE: keep in mind that for reference types it uses a reference equality (pointer check) instead of structural equality
let value = "hello"
let a = atom value
if a |> Atomic.cas value "new-value" then
    printfn "Successfully updated value of `a` to %s" !a
else
    printfn "Value of `a` has been updated in the meantime to %s" !a

// If you want to perform a thread safe update no matter if 
// atomic cell is concurrently modified by other threads you can still do that:
let users = atom Set.empty
// keep in mind, that update function should be pure and lightweight, as it may be called multiple times
users |> Atomic.update (fun s -> Set.add "Alice" s) //returns: a state of Set prior to update
```

### Vec

`Vec<'a>` or `'a vec` is a persistent immutable data type, which provides a fairly fast add/remove operations (which append value to the tail of the collection as oposed to list), which are *O(log32(n))* (which for an in-memory data structure is effectivelly close to *O(1)*). It's also optimized for `for` loop traversals which are close in speed to native array.

```fsharp
// Create an empty vector.
let a = Vec.empty
// Initialize vector with function mapping indexes (0-based) to elements.
let a = Vec.init 100 (fun i -> i * i) 

// ... or initialize it from other collection
let a = Vec.ofArray [|1;2;3|]
let b = Vec.ofSeq [1;2;3]

// Check if it's empty
if Vec.isEmpty a then printfn "Vector is empty"
// ... or get number of stored elements
let len = Vec.length a

// Add new element to the tail
let a = Vec.ofArray [|1;2|]
let b = a |> Vec.add 3 // result: b = [|1;2;3|]
// ... or add entire collection of elements at once
let c = a |> Vec.append [3;4;5] // result: c = [|1;2;3;4;5|]

// You can also replace an element at given index (without changing original structure)
let a = Vec.ofSeq ["Hello"; "world"; "!"]
let d = c |> Vec.replace 1 "F#" // result d = [|"Hello"; "F#"; "!"|]

// You can treat Vec as immutable stack
let a = Vec.ofSeq [1;2;3]
let (b, last) = Vec.pop a //result: b = [|1;2|], last = 3
// ... or just drop the last value
let b = Vec.initial a

// Vectors offer fast random access by index
let a = Vec.init 100 (fun i -> i * i)
let b = a |> Vec.item 51 // returns: ValueSome 2500
// You can also try to find an index of a value (if it exists), starting from either beginning or end of a vector
let x = a |> Vec.indexOf -199 // in case when value is not found -1 is returned
let y = a |> Vec.lastIndexOf 100

// There are also helper methods for a first and last element
let (ValueSome first) = a |> Vec.head
let (ValueSome last) =  a |> Vec.last

// Just like array, vector also has `contains` function
if Vec.contains 100 a then printfn "Value 100 exists within a vector"

// ...or you can use more generic checkers
let exists = a |> Vec.exists (fun x -> x % 2 = 0)
let (ValueSome x) = a |> Vec.find (fun x -> x % 2 = 0)
```

... and other common collection operations like: `iter`, `iteri`, `map`, `mapi`, `filter`, `choose`, `forall`, `fold`, `foldBack`, `reduce` and `rev` (reverse). 