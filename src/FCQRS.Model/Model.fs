module rec FCQRS.Model.Data


open System
open FCQRS.Model.Validation
open FCQRS.Model.Aether

type ValueLensResult<'Wrapped, 'Inner, 'Error
    when 'Wrapped: (static member Value_: (('Wrapped -> 'Inner) * ('Inner -> 'Wrapped -> Result<'Wrapped, 'Error>)))> =
    'Wrapped

type ValueLens<'Wrapped, 'Inner
    when 'Wrapped: (static member Value_: (('Wrapped -> 'Inner) * ('Inner -> 'Wrapped -> 'Wrapped)))> = 'Wrapped

type ValueLens =
    static member inline Value<'Wrapped, 'Inner, 'Error when ValueLensResult<'Wrapped, 'Inner, 'Error>>
        (this: 'Wrapped)
        =
        fst 'Wrapped.Value_ this

    static member inline Value<'Wrapped, 'Inner when ValueLens<'Wrapped, 'Inner>>(this: 'Wrapped) =
        fst 'Wrapped.Value_ this

    static member inline toString<'Wrapped, 'Inner, 'Error when ValueLensResult<'Wrapped, 'Inner, 'Error>>
        (this: 'Wrapped)
        =
        (ValueLens.Value this).ToString() |> Unchecked.nonNull

    static member inline ToString<'Wrapped, 'Inner when ValueLens<'Wrapped, 'Inner>>(this: 'Wrapped) =
        (ValueLens.Value this).ToString() |> Unchecked.nonNull

    static member inline IsValidValue<'Wrapped, 'Inner, 'Error when ValueLensResult<'Wrapped, 'Inner, 'Error>>
        (this: 'Wrapped)
        =
        (Optic.set 'Wrapped.Value_ (ValueLens.Value this) this).IsOk

    static member inline Isvalid this =
        ValueLens.IsValidValue this && (ValueLens.Value this) |> ValueLens.IsValidValue

    static member inline TryCreate<'Wrapped, 'Inner, 'Error when ValueLensResult<'Wrapped, 'Inner, 'Error>>(s: 'Inner) =
        snd 'Wrapped.Value_ s


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
    override this.ToString() = (ValueLens.Value this).ToString()

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

    member this.IsValid = ValueLens.IsValidValue this
    override this.ToString() = (ValueLens.Value this).ToString()

type LongString =
    private
    | LongString of string

    static member Value_ =
        (fun (LongString s) -> s),
        (fun (s: string) _ -> single (fun t -> t.TestOne s |> t.NotBlank EmptyString |> t.Map LongString |> t.End))

type CID =
    private
    | CID of ShortString

    static member Value_ = (fun (CID v) -> v), (fun v _ ->(CID v))
    member this.IsValid =  (ValueLens.Value this).IsValid
    override this.ToString() = (ValueLens.Value this).ToString()


// let cid: CID = Unchecked.defaultof<CID>
// let s1 =  ValueLens.Value cid
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
