module EventStore =
    type EventProducer<'Event> =
        'Event list -> 'Event list

    type EventStore<'Event> =
        {
            Get : unit -> 'Event list
            Append : 'Event list -> unit
            Evolve : EventProducer<'Event> -> unit
        }

    // operações que o MailBox pode processar
    type Msg<'Event> =
        | Append of 'Event list
        | Get of AsyncReplyChannel<'Event list> // que tipo de resposta esperamos
        | Evolve of EventProducer<'Event>

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

                        | Evolve eventProducer ->
                            let newEvents =
                                eventProducer history

                            return! loop(history @ newEvents)
                    }

                loop []
            )
    
        let append events =
            agent.Post (Append events)

        let get () =
            agent.PostAndReply Get

        let evolve eventProducer =     
            agent.Post (Evolve eventProducer)

        {
            Get = get
            Append = append
            Evolve = evolve
        }

module Domain =
    type Flavour = 
        | Strawberry
        | Vanilla

    type Event =
        | FlavourSold of Flavour
        | FlavourRestocked of Flavour * int
        | FlavourWentOutOfStock of Flavour
        | FlavourWasNotInStock of Flavour


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

    let restock flavour number stock =
        stock
        |> Map.tryFind flavour
        |> Option.defaultValue 0
        |> fun portions -> stock |> Map.add flavour (portions + number)

    let updateFlavoursInSotck stock event =
        match event with
        | FlavourSold flavour ->
            stock |> restock flavour -1

        | FlavourRestocked (flavour, number) ->
            stock |> restock flavour number

        | _ -> stock

    let flavoursInStock : Projection<Map<Flavour, int>, Event> =
       {
           Init = Map.empty
           Update = updateFlavoursInSotck
       }

    let stockOf flavour stock =
        stock
        |> Map.tryFind flavour
        |> Option.defaultValue 0

module Behaviour = 
    open Domain
    open Projections

    let sellFlavour flavour (events : Event list) =
        // obtem o estoque para um sabor específico
        let stock = 
            events
            |> project flavoursInStock
            |> stockOf flavour
        // verifique constraints para FlavourSold
        match stock with
        | 0 -> [FlavourWasNotInStock flavour]
        | 1 -> [FlavourSold flavour; FlavourWentOutOfStock flavour]
        | _ -> [FlavourSold flavour]

    let restock flavour number (events : Event list) =
        [FlavourRestocked (flavour, number)]

module Tests =
    open Expecto
    open Expecto.Expect
    open Domain

    // Given events
    // When action
    // Then events

    let Given = id

    let When eventProducer events =
        eventProducer events

    let Then expectedEvents actualEvents =
        equal actualEvents expectedEvents "novos eventos devem ser igual ao eventos esperados"

    let tests = 
        testList "SellFlavour"
            [
                test "FlavourSold caminho feliz" {
                    Given 
                        [
                            FlavourRestocked (Vanilla, 3)
                        ]
                    |> When (Behaviour.sellFlavour Vanilla)
                    |> Then 
                            [
                                FlavourSold Vanilla
                            ]
                }

                test "FlavourSold, FlavourWentOutoOfStock" {
                    Given 
                        [
                            FlavourRestocked (Vanilla, 3)
                            FlavourSold Vanilla
                            FlavourSold Vanilla
                        ]
                    |> When (Behaviour.sellFlavour Vanilla)
                    |> Then 
                        [
                            FlavourSold Vanilla
                            FlavourWentOutOfStock Vanilla
                        ]
                }

                test "FlavourWasNotInStock" {
                    Given 
                        [
                            FlavourRestocked (Vanilla, 3)
                            FlavourSold Vanilla
                            FlavourSold Vanilla
                            FlavourSold Vanilla
                        ]
                    |> When (Behaviour.sellFlavour Vanilla)
                    |> Then 
                        [
                            FlavourWasNotInStock Vanilla
                        ]                        
                }                
            ]       

module Helper =
    open Projections
    open Expecto

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

    let printStockOfFlavour flavour state =
        state
        |> stockOf flavour
        |> printfn "Stock %A: %i" flavour
  
    let runTests () =
        runTests defaultConfig Tests.tests |> ignore 

open EventStore
open Domain
open Helper
open Projections

[<EntryPoint>]
let main _ =
    runTests ()

    let eventStore : EventStore<Event> = initialize()

    eventStore.Evolve (Behaviour.sellFlavour Vanilla)
    eventStore.Evolve (Behaviour.sellFlavour Strawberry)

    eventStore.Evolve (Behaviour.restock Vanilla 3)
    eventStore.Evolve (Behaviour.sellFlavour Vanilla)

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

    let stock =
        events |> project  flavoursInStock

    printStockOfFlavour Vanilla stock        

    0 // return an integer exit code
