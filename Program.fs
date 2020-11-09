module Infrastructure =
    type EventStore<'Event> =
        {
            Get : unit -> 'Event list
            Append : 'Event list -> unit
        }

    type Projection<'State, 'Event> =
        {
            Init   : 'State
            Update : 'State -> 'Event -> 'State
        }

    module EventStore =
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

module Helper =
    let printUl list =
        list
        |> List.iteri (fun i item -> printfn " %i: %A" (i+1) item)

    let printEvents events =
        events 
        |> List.length
        |> printfn "History (Length: %i)"

        events |> printUl        

open Infrastructure
open Domain
open Helper

[<EntryPoint>]
let main _ =
    let eventStore : EventStore<Event> = EventStore.initialize()

    eventStore.Append [FlavourRestocked (Vanilla, 3)]
    eventStore.Append [FlavourSold Vanilla]
    eventStore.Append [FlavourSold Vanilla]
    eventStore.Append [FlavourSold Vanilla ; FlavourWentOutOfStock Vanilla]
    eventStore.Append [FlavourRestocked (Strawberry, 1)]

    eventStore.Get() |> printEvents

    (*
        Map {
            
        }
    *)

    0 // return an integer exit code
