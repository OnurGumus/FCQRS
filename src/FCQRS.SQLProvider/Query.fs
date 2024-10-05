module FCQRS.SQLProvider.Query
open FSharp.Data.Sql.Common
open System.Linq
let sortByEval column =
    <@@ fun (x: SqlEntity) -> x.GetColumn<System.IComparable>(column) @@>

let augment filter eval orderby orderbydesc thenby thenbydesc (take:int option) skip db =
    let db =
        match filter with
        | Some filter ->
            <@
                query {
                    for c in (%db) do
                        where ((%%eval filter) c)
                        select c
                }
            @>
        | None -> db

    let db =
        match orderby with
        | Some orderby ->
            <@
                query {
                    for c in (%db) do
                        sortBy ((%%sortByEval orderby) c)
                        select c
                }
            @>
        | None ->
            <@
                query {
                    for c in (%db) do
                        select c
                }
            @>

    let db =
        match orderbydesc with
        | Some orderbydesc ->
            <@
                query {
                    for c in (%db) do
                        sortByDescending ((%%sortByEval orderbydesc) c)
                        select c
                }
            @>
        | None -> db

    let db =
        match thenby with
        | Some thenby ->
            <@
                query {
                    for c in (%db) do
                        thenBy ((%%sortByEval thenby) c)
                        select c
                }
            @>
        | None -> db

    let db =
        match thenbydesc with
        | Some thenbydesc ->
            <@
                query {
                    for c in (%db) do
                        thenByDescending ((%%sortByEval thenbydesc) c)
                        select c
                }
            @>
        | None -> db

    let db =
        match take with
        | Some take -> <@ (%db).Take(take) @>
        | None -> db

    let db =
        match skip with
        | Some skip -> <@ (%db).Skip(skip) @>
        | None -> db

    query {
        for u in (%db) do
            select u
    }