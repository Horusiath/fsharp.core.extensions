# FSharp.Core.Extensions

This library contains a set of utilities to support building efficient, concurrent programs using functional paradigm in F#, like:

- [x] `atom` - an equivalent of F# `ref` cells, which major difference is that all operations (reads and updates) are **thread-safe**, yet executed without expensive OS-level locks, relying fully on lock-free data structures instead.
- [x] `Vec` - an immutable efficient array-like data structure, with fast random reads, element traversal and append to the tail operations. Operations like `Vec.add` and `Vec.item` are *O(log32(n))*, which for in-memory data structure goes close to *O(1)*. Usually a performance is better than that of F# list or System.Collection.Immutable data structures for supported operations. 
- [x] `HybridTime` which is an implementation of [hybrid-logical time protocol](http://users.ece.utexas.edu/~garg/pdslab/david/hybrid-time-tech-report-01.pdf), that works as a middle-ground between wall-clock UTC time (with precision up to millisecond) and monotonicity guarantees (originally `DateTime.UtcNow` doesn't guarantee, that returned value will always be greater than the one previously obtained due to nature of NTP and phenomenas like leap seconds).
- [x] `Random` which provides a **thread-safe** module for generating random operations. It also contains few extra functions not supplied originally by `System.Random` class.
- [x] `LazyTask` which is going to work like `lazy (factory)` data structure, but the factory method can return a `Task`. In order to make this data structure non-blocking, the `lazyTask.Value` call itself returns a task.
  - [ ] *TODO: make a `lazyTask { ... }` computation expression.*
- [ ] *In progress: an implementation of [Persistent Adaptative Radix Tree](https://ankurdave.com/dl/part-tr.pdf).*
- [ ] *TODO: Last-Recently Used cache implementation.*
- [x] `Enum` module, which provides a `Seq`-like module functions (`map`, `filter` etc.), that are optimized to work directly on enumerators. The difference is that, Enum is aware of generic nature of enumerators, so it can inline and devirtualize certain calls.
- [x] Bunch of utility functions:
  - [x] `Array` and `Array.Simd` which provide vectorized variants of some operations (like `Array.Simd.contains`) that can be much faster.
  - [x] Utility functions in form of `Span`, `ReadOnlySpan`, `Memory`, `ReadOnlyMemory` and `ReadOnlySequence` modules.
  - [x] Other functions I missed from time to time:
    - `~%` operator as equivalent of `op_Implicit` cast.
    - `=>` which creates a KeyValuePair out of its parameters.
    - `*` mutliply operator for `TimeSpan`.  

## API

### Atomic

`atom` is meant to be an almost drop-in replacement of F# `ref` cells. It offers a read/update operations that use efficient interlocked semantics to perform a thread-safe read/modify access on all supported operations.

```fsharp
open FSharp.Core
open FSharp.Core.Atomic.Operators
// Create an atomic cell. Currently it's limited to work only with:
//
// - Reference types (classes, non-struct records, discriminated unions, tuples or anonymous objects)
// - 32- and 64-bit integers (NOTE: interlocked 64-bit operations on 32-bit OSes are downgraded by .NET runtime)
// - booleans
// - 32- and 64-bit floating point numbers
// - booleans
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
// NOTE: for reference types it uses a reference equality (pointer check) instead of structural equality
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

#### Atomic update performance

Here's the performance check of given operations - the operation is about getting reading a stored value, modifying it, storing and returning updated result. All of these operations must happen in thread-safe manner. We're comparing:

- *InterlockedLoop* as a baseline which uses an `Interlocked.CompareExchange` done in a loop until value has been safelly updated.
- *AtomicUpdate* which is a variant above wrapped into a generic functionality of `Atomic.update` function.
- *ObjectLock* which uses `lock` method over object sync root.
- *Mutex* which uses `Mutex` as a tool of control.
- *SemaphoreSlim* which uses `SemaphoreSlim` - an optimized variant of mutex/semaphore.

|          Method |         Mean |      Error |     StdDev |  Ratio | RatioSD |  Gen 0 | Gen 1 | Gen 2 | Allocated |
|---------------- |-------------:|-----------:|-----------:|-------:|--------:|-------:|------:|------:|----------:|
| InterlockedLoop |     6.639 ns |  0.0821 ns |  0.0768 ns |   1.00 |    0.00 |      - |     - |     - |         - |
|    AtomicUpdate |     9.373 ns |  0.2935 ns |  0.4302 ns |   1.43 |    0.09 | 0.0115 |     - |     - |      24 B |
|      ObjectLock |    15.550 ns |  0.3465 ns |  0.4382 ns |   2.36 |    0.09 |      - |     - |     - |         - |
|           Mutex | 1,423.705 ns | 59.8898 ns | 98.4006 ns | 223.52 |   18.64 |      - |     - |     - |         - |
|   SemaphoreSlim |    45.281 ns |  0.9881 ns |  1.1763 ns |   6.86 |    0.19 |      - |     - |     - |         - |


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

#### `Vec<'a>` benchmarks

Belows are some of the benchmarks for this implementation, all of them are running on the same machine configuration:

```ini
BenchmarkDotNet=v0.12.0, OS=Windows 10.0.18362
Intel Core i7-7660U CPU 2.50GHz (Kaby Lake), 1 CPU, 4 logical and 2 physical cores
.NET Core SDK=3.0.100
  [Host]     : .NET Core 3.0.0 (CoreCLR 4.700.19.46205, CoreFX 4.700.19.46214), X64 RyuJIT DEBUG
  DefaultJob : .NET Core 3.0.0 (CoreCLR 4.700.19.46205, CoreFX 4.700.19.46214), X64 RyuJIT
```

#### Appending

Here, we're appending elements one by one in a loop of 1, 100, 1000 and 10 000 items (starting with an empty collection). Compared implementations are:

- *MutableListAppend* used as a baseline - it represents `System.Collections.Generic.List<'a>.Add(x)`.
- *ImmutableListAppend* represents `System.Collections.Immutable.ImmutableList<'a>.Add(x)`.
- *ImmutableArrayAppend* represents `System.Collections.Immutable.ImmutableArray<'a>.Add(x)`.
- *VecAppend* which is a immutable vector as implemented by this library. 
- *FSharpxVectorAppend* represents a `PersistentVector<'a>` from [FSharpx.Collections](https://github.com/fsprojects/FSharpx.Collections), which is another implementation of the same data structure as the one implemented here. 

|               Method | to_append |             Mean |          Error |         StdDev |           Median |  Ratio | RatioSD |       Gen 0 |    Gen 1 |   Gen 2 |   Allocated |
|--------------------- |---------- |-----------------:|---------------:|---------------:|-----------------:|-------:|--------:|------------:|---------:|--------:|------------:|
|    MutableListAppend |         1 |         26.95 ns |       1.407 ns |       4.038 ns |         25.33 ns |   1.00 |    0.00 |      0.0421 |        - |       - |        88 B |
|  ImmutableListAppend |         1 |         45.65 ns |       0.992 ns |       1.018 ns |         45.61 ns |   1.96 |    0.13 |      0.0344 |        - |       - |        72 B |
| ImmutableArrayAppend |         1 |         14.25 ns |       0.319 ns |       0.425 ns |         14.11 ns |   0.58 |    0.08 |      0.0153 |        - |       - |        32 B |
|  FSharpxVectorAppend |         1 |        177.47 ns |       5.538 ns |      16.328 ns |        174.50 ns |   6.79 |    1.35 |      0.2561 |        - |       - |       536 B |
|            VecAppend |         1 |         38.02 ns |       0.848 ns |       1.712 ns |         37.23 ns |   1.54 |    0.17 |      0.0343 |        - |       - |        72 B |
|                      |           |                  |                |                |                  |        |         |             |          |         |             |
|    MutableListAppend |       100 |        689.72 ns |       4.335 ns |       4.055 ns |        690.22 ns |   1.00 |    0.00 |      1.0481 |        - |       - |      2192 B |
|  ImmutableListAppend |       100 |     17,839.33 ns |     115.082 ns |      96.099 ns |     17,837.45 ns |  25.86 |    0.16 |     16.5710 |        - |       - |     34704 B |
| ImmutableArrayAppend |       100 |      5,690.08 ns |     112.959 ns |     150.797 ns |      5,615.92 ns |   8.32 |    0.29 |     20.4620 |        - |       - |     42800 B |
|  FSharpxVectorAppend |       100 |      6,491.06 ns |     129.398 ns |     108.053 ns |      6,476.84 ns |   9.41 |    0.17 |     12.1002 |        - |       - |     25304 B |
|            VecAppend |       100 |      5,610.23 ns |      91.306 ns |      85.407 ns |      5,577.25 ns |   8.13 |    0.13 |      9.6283 |        - |       - |     20136 B |
|                      |           |                  |                |                |                  |        |         |             |          |         |             |
|    MutableListAppend |      1000 |      4,653.65 ns |      68.112 ns |      63.712 ns |      4,649.60 ns |   1.00 |    0.00 |      7.9346 |        - |       - |     16600 B |
|  ImmutableListAppend |      1000 |    341,368.64 ns |   6,736.781 ns |   8,759.719 ns |    340,537.92 ns |  74.03 |    1.67 |    240.2344 |        - |       - |    502896 B |
| ImmutableArrayAppend |      1000 |    380,422.74 ns |   8,284.896 ns |  23,637.275 ns |    368,581.81 ns |  83.57 |    5.28 |   1922.8516 |        - |       - |   4028000 B |
|  FSharpxVectorAppend |      1000 |     70,497.83 ns |     964.505 ns |     902.198 ns |     70,390.37 ns |  15.15 |    0.22 |    121.0938 |        - |       - |    253320 B |
|            VecAppend |      1000 |     61,596.14 ns |   1,686.168 ns |   2,523.776 ns |     60,990.33 ns |  13.28 |    0.63 |     98.1445 |        - |       - |    205400 B |
|                      |           |                  |                |                |                  |        |         |             |          |         |             |
|    MutableListAppend |     10000 |    144,939.65 ns |   3,632.198 ns |   3,219.849 ns |    143,854.16 ns |   1.00 |    0.00 |     82.5195 |  41.5039 | 41.5039 |    262456 B |
|  ImmutableListAppend |     10000 |  5,822,711.41 ns |  51,205.254 ns |  47,897.426 ns |  5,831,860.16 ns |  40.16 |    0.92 |   1070.3125 | 281.2500 | 39.0625 |   6653616 B |
| ImmutableArrayAppend |     10000 | 40,742,758.37 ns | 762,077.943 ns | 782,597.940 ns | 40,635,738.46 ns | 281.91 |    9.14 | 188538.4615 |        - |       - | 400280000 B |
|  FSharpxVectorAppend |     10000 |  1,124,129.70 ns |  15,587.402 ns |  14,580.466 ns |  1,119,198.63 ns |   7.75 |    0.19 |   1087.8906 | 220.7031 |  1.9531 |   2624120 B |
|            VecAppend |     10000 |    962,364.54 ns |  18,915.218 ns |  15,795.064 ns |    963,077.54 ns |   6.64 |    0.22 |    881.8359 | 164.0625 |  3.9063 |   2146433 B |

Note: since FSharpx implementation internally represents stored elements as `obj`, in case of value types there's an additional cost related to boxing.

#### Iterating

Here, we're iterating over 10, 1000 and 200 000 elements of the collections in a `for item in collection do` loop to check the data structure overhead over that operation:

- *MutableListEnumerate* used as a baseline - it represents `System.Collections.Generic.List<'a>.Add(x)`.
- *ImmutableListEnumerate* represents `System.Collections.Immutable.ImmutableList<'a>.Add(x)`.
- *ImmutableArrayEnumerate* represents `System.Collections.Immutable.ImmutableArray<'a>.Add(x)`.
- *VecEnumerate* which is a immutable vector as implemented by this library. 
- *FSharpxVectorEnumerate* represents a `PersistentVector<'a>` from [FSharpx.Collections](https://github.com/fsprojects/FSharpx.Collections), which is another implementation of the same data structure as the one implemented here.

|                  Method |  count |             Mean |          Error |         StdDev |           Median | Ratio | RatioSD |  Gen 0 | Gen 1 | Gen 2 | Allocated |
|------------------------ |------- |-----------------:|---------------:|---------------:|-----------------:|------:|--------:|-------:|------:|------:|----------:|
|    MutableListEnumerate |     10 |         52.33 ns |       0.536 ns |       0.501 ns |         52.34 ns |  1.00 |    0.00 |      - |     - |     - |         - |
|  ImmutableListEnumerate |     10 |        876.14 ns |      20.904 ns |      29.980 ns |        873.56 ns | 16.77 |    0.74 |      - |     - |     - |         - |
| ImmutableArrayEnumerate |     10 |         16.33 ns |       0.391 ns |       0.327 ns |         16.32 ns |  0.31 |    0.01 |      - |     - |     - |         - |
|  FSharpxVectorEnumerate |     10 |        315.96 ns |      19.858 ns |      58.552 ns |        331.66 ns |  4.82 |    0.31 | 0.1109 |     - |     - |     232 B |
|            VecEnumerate |     10 |         71.13 ns |       2.137 ns |       1.999 ns |         70.47 ns |  1.36 |    0.04 |      - |     - |     - |         - |
|                         |        |                  |                |                |                  |       |         |        |       |       |           |
|    MutableListEnumerate |   1000 |      3,651.23 ns |     128.698 ns |     375.417 ns |      3,488.58 ns |  1.00 |    0.00 |      - |     - |     - |         - |
|  ImmutableListEnumerate |   1000 |     67,096.30 ns |   1,327.362 ns |   3,076.364 ns |     66,223.86 ns | 17.70 |    1.48 |      - |     - |     - |         - |
| ImmutableArrayEnumerate |   1000 |      2,047.00 ns |      13.241 ns |      12.385 ns |      2,043.86 ns |  0.50 |    0.04 |      - |     - |     - |         - |
|  FSharpxVectorEnumerate |   1000 |     15,226.12 ns |     296.629 ns |     329.702 ns |     15,059.95 ns |  3.73 |    0.33 | 0.0916 |     - |     - |     232 B |
|            VecEnumerate |   1000 |      4,625.08 ns |      45.560 ns |      38.045 ns |      4,614.30 ns |  1.12 |    0.09 |      - |     - |     - |         - |
|                         |        |                  |                |                |                  |       |         |        |       |       |           |
|    MutableListEnumerate | 200000 |    689,594.71 ns |  16,050.917 ns |  15,014.038 ns |    685,333.79 ns |  1.00 |    0.00 |      - |     - |     - |         - |
|  ImmutableListEnumerate | 200000 | 18,099,508.62 ns | 359,991.898 ns | 774,920.537 ns | 18,345,503.91 ns | 25.27 |    1.66 |      - |     - |     - |         - |
| ImmutableArrayEnumerate | 200000 |    482,744.71 ns |  11,278.890 ns |  31,253.750 ns |    478,121.04 ns |  0.75 |    0.05 |      - |     - |     - |         - |
|  FSharpxVectorEnumerate | 200000 |  5,035,035.54 ns |  99,934.161 ns | 250,715.271 ns |  4,915,667.19 ns |  7.32 |    0.40 |      - |     - |     - |     232 B |
|            VecEnumerate | 200000 |  1,114,727.52 ns |  20,929.033 ns |  23,262.565 ns |  1,115,355.66 ns |  1.61 |    0.05 |      - |     - |     - |         - |

#### Building a collection out of array

Here, we are converting an array of 1000 elements into a final representation of a given collection:

- *ResizeArrayOfArray* represents construction of `new System.Collections.Generic.List<'a>(items)`.
- *ImmutableListOfArray* represents construction via `ImmutableList.Create<User>(items)`.
- *FSharpListOfArray* represents `List.ofArray items`
- *VecOfArray* represents `Vec.ofArray items` - an implementation of immutable vector made in this repository.
- *FSharpxVecOfArray* represents a `PersistentVector.ofSeq items` from [FSharpx.Collections](https://github.com/fsprojects/FSharpx.Collections), which is another implementation of the same data structure as the one implemented here.

|               Method |        Mean |       Error |      StdDev |   Gen 0 | Gen 1 | Gen 2 | Allocated |
|--------------------- |------------:|------------:|------------:|--------:|------:|------:|----------:|
|   ResizeArrayOfArray |    683.0 ns |    13.62 ns |    30.75 ns |  3.8452 |     - |     - |   7.87 KB |
| ImmutableListOfArray | 34,739.8 ns | 1,736.68 ns | 4,954.84 ns | 22.9492 |     - |     - |  46.92 KB |
|    FSharpListOfArray | 12,979.5 ns |   211.91 ns |   176.95 ns | 15.2893 |     - |     - |  31.25 KB |
|    FSharpxVecOfArray | 50,078.3 ns | 1,414.83 ns | 1,737.54 ns |  5.0049 |     - |     - |  10.26 KB |
|           VecOfArray | 11,784.9 ns |   304.26 ns |   897.11 ns |  9.3536 |     - |     - |   19.1 KB |

Note: since FSharpx implementation internally represents stored elements as `obj`, in case of value types there's an additional cost related to boxing.