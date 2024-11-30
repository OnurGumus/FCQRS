module rec FCQRS.Data


open System
open ModelValidation
open FCQRS.Aether

type ValueLens<'T, 'U, 'K when 'T: (static member Value_: (('T -> 'U) * ('U -> 'T -> Result<'T, 'K>)))> = 'T
//type ValueLensModelError<'T,'U when 'T: (static member Value_ : (('T->'U)*('U->'T->Result<'T,ModelError>)))> = 'T

let inline value<'T, 'U, 'K when ValueLens<'T, 'U, 'K>> (this: 'T) = fst 'T.Value_ this

let inline toString<'T, 'U, 'K when ValueLens<'T, 'U, 'K>> (this: 'T) =
    (value this).ToString() |> Unchecked.nonNull

let inline isValid<'T, 'U, 'K when ValueLens<'T, 'U, 'K>> (this: 'T) =
    (Optic.set 'T.Value_ (value this) this).IsOk

let inline tryCreate<'T, 'U, 'K when ValueLens<'T, 'U, 'K>> (s: 'U) = snd 'T.Value_ s

[<RequireQualifiedAccessAttribute>]
module Result =
    let inline valueOrErrorList e =
        match e with
        | Ok x -> x
        | Error x ->
            let errors =
                x |> List.map (fun x -> x.ToString() |> Unchecked.nonNull) |> String.concat ", "

            invalidOp errors

    let inline value e =
        match e with
        | Ok x -> x
        | Error x -> invalidOp (x.ToString() |> Unchecked.nonNull)

type Predicate =
    | Greater of string * IComparable
    | GreaterOrEqual of string * IComparable
    | Smaller of string * IComparable
    | SmallerOrEqual of string * IComparable
    | Equal of string * obj
    | NotEqual of string * obj
    | And of Predicate * Predicate
    | Or of Predicate * Predicate
    | Not of Predicate

type ModelError =
    | EmptyString
    | TooLongString
    | InvalidEmail
    | InvalidUrl
    | InvalidGuid
    | MustBeNonNegative

type Version =
    private
    | Version of int64

    static member Value_ =
        (fun (Version v) -> v), (fun v _ -> if v >= 0L then Ok(Version v) else Error MustBeNonNegative)

    static member Zero = Version 0L

type ShortString =
    private
    | ShortString of string

    static member Value_ =
        (fun (ShortString s) -> s),
        (fun (s: string) _ ->
            single (fun t ->
                t.TestOne s
                |> t.NotBlank EmptyString
                |> t.MaxLen 255 TooLongString
                |> t.Map ShortString
                |> t.End))

type LongString =
    private
    | LongString of string

    static member Value_ =
        (fun (LongString s) -> s),
        (fun (s: string) _ -> single (fun t -> t.TestOne s |> t.NotBlank EmptyString |> t.Map LongString |> t.End))

type CID =
    private
    | CID of ShortString

    static member Value_ = (fun (CID v) -> v), (fun v _ -> Ok(CID v))

// let cid: CID = Unchecked.defaultof<CID>
// let s2 = cid |> toString
// member this.Value: string = let (CID v) = this in v |> ShortString.Value

// static member CreateNew() =
//     Guid.NewGuid().ToString() |> ShortString.TryCreate |> Result.value |> CID

// static member Create(s: string) =
//     let s = if (s.Contains "~") then s.Split("~")[1] else s
//     s |> ShortString.TryCreate |> Result.value |> CID

// let v1 =   (01L ^= Version.Value_)  Version.Zero
// let v2 =v1 |> Result.value
// let v3 = Optic.get Version.Value_ v2
