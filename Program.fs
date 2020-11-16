module EventStore =
    type EventProducer<'Event> =
        'Event list -> 'Event list

    type Aggregate = System.Guid

    type EventStore<'Event> =
        {
            Get : unit -> Map<Aggregate, 'Event list>
            GetStream : Aggregate -> 'Event list
            Append : Aggregate -> 'Event list -> unit
            Evolve : Aggregate -> EventProducer<'Event> -> unit
        }

    // operações que o MailBox pode processar
    type Msg<'Event> =
        | Append of Aggregate * 'Event list
        | GetStream of Aggregate * AsyncReplyChannel<'Event list>
        | Get of AsyncReplyChannel<Map<Aggregate, 'Event list>> // que tipo de resposta esperamos
        | Evolve of Aggregate * EventProducer<'Event>

    let eventsforAggregate aggregate history =
        history
        |> Map.tryFind aggregate
        |> Option.defaultValue []

    let initialize () : EventStore<'Event> =
        let agent =
            MailboxProcessor.Start(fun inbox ->
                let rec loop history =
                    async {
                        // aguarda nova mensagem (let! igual o await no C#)
                        let! msg = inbox.Receive()

                        match msg with 
                        | Append (aggregate,  events) ->
                            let streamEvents = history |> eventsforAggregate aggregate

                            let newState =
                                history
                                |> Map.add aggregate (streamEvents @ events)

                            // chama a função recursiva para manter o agente escutando
                            return! loop newState
                        
                        | Get reply -> 
                            // responde no canal determinado
                            reply.Reply history

                            // chama a função recursiva para manter o agente escutando
                            return! loop history

                        | GetStream (aggregate, reply) ->
                            reply.Reply (history |> eventsforAggregate aggregate)

                            return! loop history

                        | Evolve (aggregate, eventProducer) ->
                            let streamEvents = 
                                history |> eventsforAggregate aggregate

                            let newEvents =
                                eventProducer streamEvents

                            let newHistory =
                                history
                                |> Map.add aggregate (streamEvents @ newEvents)                                

                            return! loop newHistory
                    }

                loop Map.empty
            )
    
        let append aggregate events =
            agent.Post (Append (aggregate, events))

        let get () =
            agent.PostAndReply Get

        let evolve aggregate eventProducer =     
            agent.Post (Evolve (aggregate, eventProducer))

        let getStream aggregate = 
            agent.PostAndReply (fun reply -> GetStream (aggregate, reply))

        {
            Get = get
            Append = append
            Evolve = evolve
            GetStream = getStream
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

    let printEvents header events =
        events 
        |> List.length
        |> printfn "History for %s (Length: %i)" header

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

    let truck1 = System.Guid.NewGuid()
    let truck2 = System.Guid.NewGuid()

    let eventStore : EventStore<Event> = initialize()

    eventStore.Evolve truck1 (Behaviour.sellFlavour Vanilla)
    eventStore.Evolve truck1 (Behaviour.sellFlavour Strawberry)
    eventStore.Evolve truck1 (Behaviour.restock Vanilla 3)
    eventStore.Evolve truck1 (Behaviour.sellFlavour Vanilla)

    eventStore.Evolve truck2 (Behaviour.sellFlavour Vanilla)

    let eventsTruck1 = eventStore.GetStream truck1 
    let eventsTruck2 = eventStore.GetStream truck2
    
    eventsTruck1 |> printEvents " Truck1"
    eventsTruck2 |> printEvents " Truck2"

    (*
        Map {
            Vanilla => 3
            Strawberry => 0
        }
    *)

    let sold : Map<Flavour, int> =
        eventsTruck1
        |> project soldFlavours

    printSoldFlavour Vanilla sold
    printSoldFlavour Strawberry sold

    let stock =
        eventsTruck1 |> project  flavoursInStock

    printStockOfFlavour Vanilla stock        

    0 // return an integer exit code
