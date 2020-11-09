module EventStore =
    type EventStore<'Event> =
        {
            Get : unit -> 'Event list
            Append : 'Event list -> unit
        }

    // operações que o MailBox pode processar
    type Msg<'Event> =
        | Append of 'Event list
        | Get of AsyncReplyChannel<'Event list> // que tipo de resposta esperamos

    let initialize () : EventStore<'Event> =
        let agent =
            MailboxProcessor.Start(fun inbox ->
                let rec loop history =
                    async {
                        // aguarda nova mensagem (let! igual o await no C#)
                        let! msg = inbox.Receive()

                        match msg with 
                        | Append events ->
                            // chama a função recursiva para manter o agente escutando
                            return! loop (history @ events)
                        
                        | Get reply -> 
                            // responde no canal determinado
                            reply.Reply history

                            // chama a função recursiva para manter o agente escutando
                            return! loop history
                    }

                loop []
            )
    
        let append events =
            agent.Post (Append events)

        let get () =
            agent.PostAndReply Get

        {
            Get = get
            Append = append
        }

module Domain =
    type Flavour = 
        | Strawberry
        | Vanilla

    type Event =
        | FlavourSold of Flavour
        | FlavourRestocked of Flavour * int
        | FlavourWentOutOfStock of Flavour
        | FlavorWasNotInStock of Flavour


module Projections =
    open Domain

    type Projection<'State, 'Event> =
        {
            Init   : 'State
            Update : 'State -> 'Event -> 'State
        }

    let project (projection : Projection<_,_>) events =
        events |> List.fold projection.Update projection.Init

    let soldOfFlavour flavour state =
        state
        |> Map.tryFind flavour
        |> Option.defaultValue 0

    let updateSoldFlavours state event =
        match event with
        | FlavourSold flavour -> 
            state
            |> soldOfFlavour flavour 
            |> fun portions -> state |> Map.add flavour (portions + 1)

        | _ -> state 

    let soldFlavours : Projection<Map<Flavour, int>, Event> = 
        {
            Init = Map.empty
            Update = updateSoldFlavours
        }

module Helper =
    open Projections

    let printUl list =
        list
        |> List.iteri (fun i item -> printfn " %i: %A" (i+1) item)

    let printEvents events =
        events 
        |> List.length
        |> printfn "History (Length: %i)"

        events |> printUl    

    let printSoldFlavour flavour state =
        state
        |> soldOfFlavour flavour
        |> printfn "Sold %A: %i" flavour

open EventStore
open Domain
open Helper
open Projections
[<EntryPoint>]
let main _ =
    let eventStore : EventStore<Event> = initialize()

    eventStore.Append [FlavourRestocked (Vanilla, 3)]
    eventStore.Append [FlavourSold Vanilla]
    eventStore.Append [FlavourSold Vanilla]
    eventStore.Append [FlavourSold Vanilla ; FlavourWentOutOfStock Vanilla]
    eventStore.Append [FlavourRestocked (Strawberry, 1)]

    let events = eventStore.Get() 
    
    events |> printEvents

    (*
        Map {
            Vanilla => 3
            Strawberry => 0
        }
    *)

    let sold : Map<Flavour, int> =
        events
        |> project soldFlavours

    printSoldFlavour Vanilla sold
    printSoldFlavour Strawberry sold

    0 // return an integer exit code
