module FCQRS.Aether

open System

//  Optics

/// Lens from 'a -> 'b.
type Lens<'a,'b> =
    ('a -> 'b) * ('b -> 'a -> 'a)

/// Prism from 'a -> 'b.
type Prism<'a,'b> =
    ('a -> 'b option) * ('b -> 'a -> 'a)

//  Morphisms

/// Isomorphism between 'a <> 'b.
type Isomorphism<'a,'b> =
    ('a -> 'b) * ('b -> 'a)

/// Epimorphism between 'a <> 'b.
type Epimorphism<'a,'b> =
    ('a -> 'b option) * ('b -> 'a)

/// Validated Lens from 'a -> 'b with error type 'e.
type ValidatedLens<'a,'b,'e> =
    ('a -> 'b) * ('b -> 'a -> Result<'a,'e>)

/// Validated Prism from 'a -> 'b with error type 'e.
type ValidatedPrism<'a,'b,'e> =
    ('a -> 'b option) * ('b -> 'a -> Result<'a,'e>)

[<RequireQualifiedAccess>]
module Compose =

    /// Static overloads of the composition function for lenses (>->).
    type Lens =
        | Lens with

        static member (>->) (Lens, (g2, s2): Lens<'b,'c>) =
            fun ((g1, s1): Lens<'a,'b>) ->
                (fun a -> g2 (g1 a)),
                (fun c a -> s1 (s2 c (g1 a)) a) : Lens<'a,'c>

        static member (>->) (Lens, (g2, s2): Prism<'b,'c>) =
            fun ((g1, s1): Lens<'a,'b>) ->
                (fun a -> g2 (g1 a)),
                (fun c a -> s1 (s2 c (g1 a)) a) : Prism<'a,'c>

        static member (>->) (Lens, (f, t): Isomorphism<'b,'c>) =
            fun ((g, s): Lens<'a,'b>) ->
                (fun a -> f (g a)),
                (fun c a -> s (t c) a) : Lens<'a,'c>

        static member (>->) (Lens, (f, t): Epimorphism<'b,'c>) =
            fun ((g, s): Lens<'a,'b>) ->
                (fun a -> f (g a)),
                (fun c a -> s (t c) a) : Prism<'a,'c>

        static member (>->) (Lens, validatedLens: ValidatedLens<'b,'c,'e>) =
                    fun (vLens: ValidatedLens<'a,'b,'e>) ->
                        let (g1, s1) = vLens
                        let (g2, s2) = validatedLens
                        let get a = g2 (g1 a)
                        let set c a =
                            s2 c (g1 a)
                            |> Result.bind (fun b' -> s1 b' a)
                        (get, set) : ValidatedLens<'a,'c,'e>
    /// Compose a lens with an optic or morphism.
    let inline lens l o =
        (Lens >-> o) l

    /// Static overloads of the composition function for prisms (>?>).
    type Prism =
        | Prism with

        static member (>?>) (Prism, (g2, s2): Lens<'b,'c>) =
            fun ((g1, s1): Prism<'a,'b>) ->
                (fun a -> Option.map g2 (g1 a)),
                (fun c a ->
                    Option.map (s2 c) (g1 a)
                    |> function
                    | Some b -> s1 b a
                    | _ -> a
                ) : Prism<'a,'c>

        static member (>?>) (Prism, (g2, s2): Prism<'b,'c>) =
            fun ((g1, s1): Prism<'a,'b>) ->
                (fun a -> Option.bind g2 (g1 a)),
                (fun c a ->
                    Option.map (s2 c) (g1 a)
                    |> function
                    | Some b -> s1 b a
                    | _ -> a
                ) : Prism<'a,'c>

        static member (>?>) (Prism, (f, t): Isomorphism<'b,'c>) =
            fun ((g, s): Prism<'a,'b>) ->
                (fun a -> Option.map f (g a)),
                (fun c a -> s (t c) a) : Prism<'a,'c>

        static member (>?>) (Prism, (f, t): Epimorphism<'b,'c>) =
            fun ((g, s): Prism<'a,'b>) ->
                (fun a -> Option.bind f (g a)),
                (fun c a -> s (t c) a) : Prism<'a,'c>

        static member (>?>) (Prism, validatedPrism: ValidatedPrism<'b,'c,'e>) =
            fun (prism: Prism<'a,'b>) ->
                let (g1, s1) = prism
                let (g2, s2) = validatedPrism
                let get a =
                    Option.bind g2 (g1 a)
                let set c a =
                    match g1 a with
                    | Some b ->
                        s2 c b
                        |> Result.map (fun b' -> s1 b' a)
                    | None ->
                        // Return an error indicating the path is invalid.
                        Error (unbox<'e> "Invalid path")
                (get, set) : ValidatedPrism<'a,'c,'e>

    /// Compose a prism with an optic or morphism.
    let inline prism p o =
        (Prism >?> o) p

    module ValidatedLens =

        /// Compose two ValidatedLens instances.
        let compose (lens1: ValidatedLens<'a,'b,'e>) (lens2: ValidatedLens<'b,'c,'e>) : ValidatedLens<'a,'c,'e> =
            let (get1, set1) = lens1
            let (get2, set2) = lens2
            let get a = get2 (get1 a)
            let set c a =
                set2 c (get1 a)
                |> Result.bind (fun b' -> set1 b' a)
            (get, set)

        /// Compose a ValidatedLens with a standard Lens.
        let composeValidatedLens (vLens: ValidatedLens<'a,'b,'e>) (lens: Lens<'b,'c>) : ValidatedLens<'a,'c,'e> =
            let (get1, set1) = vLens
            let (get2, set2) = lens
            let get a = get2 (get1 a)
            let set c a =
                let b = get1 a
                let b' = set2 c b
                set1 b' a
            (get, set)

        /// Compose a standard Lens with a ValidatedLens.
        let composeLensValidated (lens: Lens<'a,'b>) (vLens: ValidatedLens<'b,'c,'e>) : ValidatedLens<'a,'c,'e> =
            let (get1, set1) = lens
            let (get2, set2) = vLens
            let get a = get2 (get1 a)
            let set c a =
                set2 c (get1 a)
                |> Result.map (fun b' -> set1 b' a)
            (get, set)


        let composeMapErrors
            (lens1: ValidatedLens<'a,'b,'e1>)
            (lens2: ValidatedLens<'b,'c,'e2>)
            (mapError1: 'e1 -> string)
            (mapError2: 'e2 -> string)
            : ValidatedLens<'a,'c,string> =
            let (get1, set1) = lens1
            let (get2, set2) = lens2
            let get a = get2 (get1 a)
            let set c a =
                match set2 c (get1 a) with
                | Ok b' ->
                    match set1 b' a with
                    | Ok a' -> Ok a'
                    | Error e1 -> Error (mapError1 e1)
                | Error e2 -> Error (mapError2 e2)
            (get, set)
        let composeLensPrismMapErrors
            (lens: ValidatedLens<'a,'b,'e1>)
            (prism: ValidatedPrism<'b,'c,'e2>)
            (mapError1: 'e1 -> 'e)
            (mapError2: 'e2 -> 'e)
            : ValidatedPrism<'a,'c,'e> =
            let (get1, set1) = lens
            let (get2, set2) = prism
            let get a =
                get2 (get1 a)
            let set c a =
                match set2 c (get1 a) with
                | Ok b' ->
                    match set1 b' a with
                    | Ok a' -> Ok a'
                    | Error e1 -> Error (mapError1 e1)
                | Error e2 -> Error (mapError2 e2)
            (get, set)

    module ValidatedPrism =

        /// Compose two ValidatedPrism instances.
        let compose (prism1: ValidatedPrism<'a,'b,'e>) (prism2: ValidatedPrism<'b,'c,'e>) : ValidatedPrism<'a,'c,'e> =
            let (get1, set1) = prism1
            let (get2, set2) = prism2
            let get a =
                Option.bind get2 (get1 a)
            let set c a =
                match get1 a with
                | Some b ->
                    set2 c b
                    |> Result.bind (fun b' -> set1 b' a)
                | None ->
                    // Return an error indicating the path is invalid.
                    Error (unbox<'e> "Invalid path")
            (get, set)

        /// Compose a ValidatedPrism with a standard Prism.
        let composeValidatedPrism (vPrism: ValidatedPrism<'a,'b,'e>) (prism: Prism<'b,'c>) : ValidatedPrism<'a,'c,'e> =
            let (get1, set1) = vPrism
            let (get2, set2) = prism
            let get a =
                Option.bind get2 (get1 a)
            let set c a =
                match get1 a with
                | Some b ->
                    let b' = set2 c b
                    set1 b' a
                | None ->
                    // Return an error indicating the path is invalid.
                    Error (unbox<'e> "Invalid path")
            (get, set)

        /// Compose a standard Prism with a ValidatedPrism.
        let composePrismValidated (prism: Prism<'a,'b>) (vPrism: ValidatedPrism<'b,'c,'e>) : ValidatedPrism<'a,'c,'e> =
            let (get1, set1) = prism
            let (get2, set2) = vPrism
            let get a =
                Option.bind get2 (get1 a)
            let set c a =
                match get1 a with
                | Some b ->
                    set2 c b
                    |> Result.map (fun b' -> set1 b' a)
                | None ->
                    // Return an error indicating the path is invalid.
                    Error (unbox<'e> "Invalid path")
            (get, set)

        let composeMapErrors
            (prism1: ValidatedPrism<'a,'b,'e1>)
            (prism2: ValidatedPrism<'b,'c,'e2>)
            (mapError1: 'e1 -> 'e)
            (mapError2: 'e2 -> 'e)
            : ValidatedPrism<'a,'c,'e> =
            let (get1, set1) = prism1
            let (get2, set2) = prism2
            let get a =
                match get1 a with
                | Some b ->
                    match get2 b with
                    | Some c -> Some c
                    | None -> None
                | None -> None
            let set c a =
                match get1 a with
                | Some b ->
                    match set2 c b with
                    | Ok b' ->
                        match set1 b' a with
                        | Ok a' -> Ok a'
                        | Error e1 -> Error (mapError1 e1)
                    | Error e2 -> Error (mapError2 e2)
                | None ->
                    // Return an error indicating the path is invalid.
                    Error (unbox<'e> "Invalid path")
            (get, set)
        let composePrismLensMapErrors
            (prism: ValidatedPrism<'a,'b,'e1>)
            (lens: ValidatedLens<'b,'c,'e2>)
            (mapError1: 'e1 -> 'e)
            (mapError2: 'e2 -> 'e)
            : ValidatedPrism<'a,'c,'e> =
            let (get1, set1) = prism
            let (get2, set2) = lens
            let get a =
                match get1 a with
                | Some b -> Some (get2 b)
                | None -> None
            let set c a =
                match get1 a with
                | Some b ->
                    match set2 c b with
                    | Ok b' ->
                        match set1 b' a with
                        | Ok a' -> Ok a'
                        | Error e1 -> Error (mapError1 e1)
                    | Error e2 -> Error (mapError2 e2)
                | None ->
                    Error (unbox<'e> "Invalid path")
            (get, set)




[<RequireQualifiedAccess>]
module Optic =

    /// Static overloads of the optic get function (^.).
    type Get =
        | Get with

        static member (^.) (Get, (g, _): Lens<'a,'b>) =
            fun (a: 'a) ->
                g a : 'b

        static member (^.) (Get, (g, _): Prism<'a,'b>) =
            fun (a: 'a) ->
                g a : 'b option

        static member (^.) (Get, (g, _): ValidatedLens<'a,'b,'e>) =
            fun (a: 'a) ->
                g a : 'b

        static member (^.) (Get, (g, _): ValidatedPrism<'a,'b,'e>) =
            fun (a: 'a) ->
                g a : 'b option

    /// Get a value using an optic.
    let inline get optic target =
        (Get ^. optic) target

    /// Static overloads of the optic set function (^=).
    type Set =
        | Set with

        static member (^=) (Set, (_, s): Lens<'a,'b>) =
            fun (b: 'b) ->
                s b : 'a -> 'a

        static member (^=) (Set, (_, s): Prism<'a,'b>) =
            fun (b: 'b) ->
                s b : 'a -> 'a

        static member (^=) (Set, (_, s): ValidatedLens<'a,'b,'e>) =
            fun (b: 'b) ->
                s b : 'a -> Result<'a,'e>

        static member (^=) (Set, (_, s): ValidatedPrism<'a,'b,'e>) =
            fun (b: 'b) ->
                s b : 'a -> Result<'a,'e>

    /// Set a value using an optic.
    let inline set optic value =
        (Set ^= optic) value

    /// Static overloads of the optic map function (%=).
    type Map =
        | Map with

        static member (^%) (Map, (g, s): Lens<'a,'b>) =
            fun (f: 'b -> 'b) ->
                (fun a -> s (f (g a)) a) : 'a -> 'a

        static member (^%) (Map, (g, s): Prism<'a,'b>) =
            fun (f: 'b -> 'b) ->
                (fun a -> Option.map f (g a) |> function | Some b -> s b a
                                                         | _ -> a) : 'a -> 'a

        static member (^%) (Map, (g, s): ValidatedLens<'a,'b,'e>) =
            fun (f: 'b -> 'b) ->
                (fun a ->
                    s (f (g a)) a
                ) : 'a -> Result<'a,'e>

        static member (^%) (Map, (g, s): ValidatedPrism<'a,'b,'e>) =
            fun (f: 'b -> 'b) ->
                (fun a ->
                    match g a with
                    | Some b -> s (f b) a
                    | None -> Ok a
                ) : 'a -> Result<'a,'e>

    /// Modify a value using an optic.
    let inline map optic f =
        (Map ^% optic) f

/// Functions for creating or using lenses.
[<RequireQualifiedAccess>]
module Lens =

    /// Converts an isomorphism into a lens.
    let ofIsomorphism ((f, t): Isomorphism<'a,'b>) : Lens<'a,'b> =
        f, (fun b _ -> t b)

    /// Lift a standard Lens into a ValidatedLens with no validation.
    let toValidated (lens: Lens<'a,'b>) : ValidatedLens<'a,'b,'e> =
        let (get, set) = lens
        get, (fun b a -> Ok (set b a))

/// Functions for creating or using prisms.
[<RequireQualifiedAccess>]
module Prism =

    /// Converts an epimorphism into a prism.
    let ofEpimorphism ((f, t): Epimorphism<'a,'b>) : Prism<'a,'b> =
        f, (fun b _ -> t b)

    /// Lift a standard Prism into a ValidatedPrism with no validation.
    let toValidated (prism: Prism<'a,'b>) : ValidatedPrism<'a,'b,'e> =
        let (get, set) = prism
        get, (fun b a -> Ok (set b a))

[<AutoOpen>]
module Optics =

    let id_ : Lens<'a,'a> =
        (fun x -> x),
        (fun x _ -> x)

    let fst_ : Lens<('a * 'b),'a> =
        fst,
        (fun a t -> a, snd t)

    let snd_ : Lens<('a * 'b),'b> =
        snd,
        (fun b t -> fst t, b)

/// Operators for working with optics, including validated optics.
module Operators =

    /// Compose a lens with an optic or morphism.
    let inline (>->) l o =
        Compose.lens l o

    /// Compose a prism with an optic or morphism.
    let inline (>?>) p o =
        Compose.prism p o

    /// Get a value using an optic.
    let inline (^.) target optic =
        Optic.get optic target

    /// Set a value using an optic.
    let inline (^=) value optic =
        Optic.set optic value

    /// Modify a value using an optic.
    let inline (^%) f optic =
        Optic.map optic f