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

module FSharp.Core.Extensions.Tests.Atomic

open FSharp.Core
open FSharp.Core.Atomic.Operators
open Expecto

[<Tests>]
let tests =
    testList "Atomic" [
        
        (* AtomicRef *)
        
        testCase "Atomic ref returns init value" <| fun _ ->
            let a = atom "hello"
            Expect.equal !a "hello" "Atomic ref should return its init value"

        testCase "Atomic ref swap returns old value" <| fun _ ->
            let a = atom "hello"
            let old = a := "world"
            Expect.equal old "hello" "Atomic assignment should return previous value"
            Expect.equal !a "world" "Atomic ref should return updated value"

        testCase "Atomic ref cas swaps value if comparand is equal" <| fun _ ->
            let value = "hello"
            let a = atom value
            let success = a |> Atomic.cas value "world"
            Expect.isTrue success  "Atomic compare and swap should succeed to update value given previous value"
            Expect.equal !a "world" "Atomic ref should return updated value"
    
        testCase "Atomic ref cas doesn't swap value if comparand is not equal" <| fun _ ->
            let a = atom "hello"
            let success = a |> Atomic.cas "hi" "world"
            Expect.isFalse success  "Atomic compare and swap should fail to update value if previous value differs"
            Expect.equal !a "hello" "Atomic ref should not update value"

        testCase "Atomic ref update replaces old value with modified one" <| fun _ ->
            let a = atom "hello"
            let old = a |> Atomic.update (fun o -> o + o)
            Expect.equal old "hello"  "Atomic update should return previously stored value"
            Expect.equal !a "hellohello" "Atomic update should return updated value"

        (* AtomicInt *)
        
        testCase "Atomic int returns init value" <| fun _ ->
            let a = atom 1
            Expect.equal !a 1 "Atomic int should return its init value"

        testCase "Atomic int swap returns old value" <| fun _ ->
            let a = atom 1
            let old = a := 2
            Expect.equal old 1 "Atomic assignment should return previous value"
            Expect.equal !a 2 "Atomic int should return updated value"

        testCase "Atomic int cas swaps value if comparand is equal" <| fun _ ->
            let value = 1
            let a = atom value
            let success = a |> Atomic.cas value 2
            Expect.isTrue success  "Atomic compare and swap should succeed to update value given previous value"
            Expect.equal !a 2 "Atomic int should return updated value"
    
        testCase "Atomic int cas doesn't swap value if comparand is not equal" <| fun _ ->
            let a = atom 1
            let success = a |> Atomic.cas 3 2
            Expect.isFalse success  "Atomic compare and swap should fail to update value if previous value differs"
            Expect.equal !a 1 "Atomic int should not update value"

        testCase "Atomic int update replaces old value with modified one" <| fun _ ->
            let a = atom 1
            let old = a |> Atomic.update (fun o -> o + o)
            Expect.equal old 1  "Atomic update should return previously stored value"
            Expect.equal !a 2 "Atomic update should return updated value"

        testCase "Atomic int increments the value" <| fun _ ->
            let a = atom 1
            let old = a |> Atomic.inc
            Expect.equal old 2  "Atomic increment should return updated value"
            Expect.equal !a 2 "Atomic increment should store updated value"
            
        testCase "Atomic int decrements the value" <| fun _ ->
            let a = atom 1
            let old = a |> Atomic.dec
            Expect.equal old 0  "Atomic decrement should return updated value"
            Expect.equal !a 0 "Atomic decrement should store updated value"
            
        (* AtomicInt64 *)
        
        testCase "Atomic int64 returns init value" <| fun _ ->
            let a = atom 1L
            Expect.equal !a 1L "Atomic int64 should return its init value"

        testCase "Atomic int64 swap returns old value" <| fun _ ->
            let a = atom 1L
            let old = a := 2L
            Expect.equal old 1L "Atomic assignment should return previous value"
            Expect.equal !a 2L "Atomic int64 should return updated value"

        testCase "Atomic int64 cas swaps value if comparand is equal" <| fun _ ->
            let value = 1L
            let a = atom value
            let success = a |> Atomic.cas value 2L
            Expect.isTrue success  "Atomic compare and swap should succeed to update value given previous value"
            Expect.equal !a 2L "Atomic int64 should return updated value"
    
        testCase "Atomic int64 cas doesn't swap value if comparand is not equal" <| fun _ ->
            let a = atom 1L
            let success = a |> Atomic.cas 3L 2L
            Expect.isFalse success  "Atomic compare and swap should fail to update value if previous value differs"
            Expect.equal !a 1L "Atomic int64 should not update value"

        testCase "Atomic int64 update replaces old value with modified one" <| fun _ ->
            let a = atom 1L
            let old = a |> Atomic.update (fun o -> o + o)
            Expect.equal old 1L  "Atomic update should return previously stored value"
            Expect.equal !a 2L "Atomic update should return updated value"

        testCase "Atomic int64 increments the value" <| fun _ ->
            let a = atom 1L
            let old = a |> Atomic.inc
            Expect.equal old 2L "Atomic increment should return updated value"
            Expect.equal !a 2L "Atomic increment should store updated value"
            
        testCase "Atomic int64 decrements the value" <| fun _ ->
            let a = atom 1L
            let old = a |> Atomic.dec
            Expect.equal old 0L  "Atomic decrement should return updated value"
            Expect.equal !a 0L "Atomic decrement should store updated value"
            
        (* AtomicFloat *)
        
        testCase "Atomic float returns init value" <| fun _ ->
            let a = atom 1.0
            Expect.equal !a 1.0 "Atomic float should return its init value"

        testCase "Atomic float swap returns old value" <| fun _ ->
            let a = atom 1.0
            let old = a := 2.0
            Expect.equal old 1.0 "Atomic assignment should return previous value"
            Expect.equal !a 2.0 "Atomic float should return updated value"

        testCase "Atomic float cas swaps value if comparand is equal" <| fun _ ->
            let value = 1.0
            let a = atom value
            let success = a |> Atomic.cas value 2.0
            Expect.isTrue success  "Atomic compare and swap should succeed to update value given previous value"
            Expect.equal !a 2.0 "Atomic float should return updated value"
    
        testCase "Atomic float cas doesn't swap value if comparand is not equal" <| fun _ ->
            let a = atom 1.0
            let success = a |> Atomic.cas 3.0 2.0
            Expect.isFalse success  "Atomic compare and swap should fail to update value if previous value differs"
            Expect.equal !a 1.0 "Atomic float should not update value"

        testCase "Atomic float update replaces old value with modified one" <| fun _ ->
            let a = atom 1.0
            let old = a |> Atomic.update (fun o -> o + o)
            Expect.equal old 1.0  "Atomic update should return previously stored value"
            Expect.equal !a 2.0 "Atomic update should return updated value"
 
        (* AtomicFloat32 *)
        
        testCase "Atomic float32 returns init value" <| fun _ ->
            let a = atom 1.0f
            Expect.equal !a 1.0f "Atomic float32 should return its init value"

        testCase "Atomic float32 swap returns old value" <| fun _ ->
            let a = atom 1.0f
            let old = a := 2.0f
            Expect.equal old 1.0f "Atomic assignment should return previous value"
            Expect.equal !a 2.0f "Atomic float32 should return updated value"

        testCase "Atomic float32 cas swaps value if comparand is equal" <| fun _ ->
            let value = 1.0f
            let a = atom value
            let success = a |> Atomic.cas value 2.0f
            Expect.isTrue success  "Atomic compare and swap should succeed to update value given previous value"
            Expect.equal !a 2.0f "Atomic float32 should return updated value"
    
        testCase "Atomic float32 cas doesn't swap value if comparand is not equal" <| fun _ ->
            let a = atom 1.0f
            let success = a |> Atomic.cas 3.0f 2.0f
            Expect.isFalse success  "Atomic compare and swap should fail to update value if previous value differs"
            Expect.equal !a 1.0f "Atomic float32 should not update value"

        testCase "Atomic float32 update replaces old value with modified one" <| fun _ ->
            let a = atom 1.0f
            let old = a |> Atomic.update (fun o -> o + o)
            Expect.equal old 1.0f  "Atomic update should return previously stored value"
            Expect.equal !a 2.0f "Atomic update should return updated value"

        (* AtomicBool *)
        
        testCase "Atomic bool returns init value" <| fun _ ->
            let a = atom 1.0f
            Expect.equal !a 1.0f "Atomic float32 should return its init value"

        testCase "Atomic bool swap returns old value" <| fun _ ->
            let a = atom true
            let old = a := false
            Expect.equal old true "Atomic assignment should return previous value"
            Expect.equal !a false "Atomic bool should return updated value"

        testCase "Atomic bool cas swaps value if comparand is equal" <| fun _ ->
            let value = true
            let a = atom value
            let success = a |> Atomic.cas value false
            Expect.isTrue success  "Atomic compare and swap should succeed to update value given previous value"
            Expect.equal !a false "Atomic bool should return updated value"
    
        testCase "Atomic bool cas doesn't swap value if comparand is not equal" <| fun _ ->
            let a = atom true
            let success = a |> Atomic.cas false false
            Expect.isFalse success  "Atomic compare and swap should fail to update value if previous value differs"
            Expect.equal !a true "Atomic bool should not update value"

        testCase "Atomic bool update replaces old value with modified one" <| fun _ ->
            let a = atom true
            let old = a |> Atomic.update (not)
            Expect.equal old true  "Atomic update should return previously stored value"
            Expect.equal !a false "Atomic update should return updated value"       
    ]