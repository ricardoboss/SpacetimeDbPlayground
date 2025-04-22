using System.Collections.Concurrent;
using SpacetimeDB;
using SpacetimeDB.Types;
// ReSharper disable InvalidXmlDocComment

/// The URI of the SpacetimeDB instance hosting our chat module.
const string host = "http://localhost:3000";

/// The module name we chose when we published our module.
const string dbname = "quickstart-chat";

// our local client SpacetimeDB identity
Identity? localIdentity = null;

// declare a thread safe queue to store commands
var inputQueue = new ConcurrentQueue<(string Command, string Args)>();

Main();

return;

void Main()
{
    // Initialize the `AuthToken` module
    AuthToken.Init(".spacetime_csharp_quickstart");
    // Builds and connects to the database
    var conn = ConnectToDb();
    // Registers to run in response to database events.
    RegisterCallbacks(conn);
    // Declare a threadsafe cancel token to cancel the process loop
    var cancellationTokenSource = new CancellationTokenSource();
    // Spawn a thread to call process updates and process commands
    var thread = new Thread(() => ProcessThread(conn, cancellationTokenSource.Token));
    thread.Start();
    // Handles CLI input
    InputLoop();
    // This signals the ProcessThread to stop
    cancellationTokenSource.Cancel();
    thread.Join();
}

/// Load credentials from a file and connect to the database.
DbConnection ConnectToDb()
{
    return DbConnection.Builder()
        .WithUri(host)
        .WithModuleName(dbname)
        .WithToken(AuthToken.Token)
        .OnConnect(OnConnected)
        .OnConnectError(OnConnectError)
        .OnDisconnect(OnDisconnected)
        .Build();
}

/// Our `OnConnectError` callback: print the error, then exit the process.
void OnConnectError(Exception e)
{
    Console.Write($"Error while connecting: {e}");
}
/// Our `OnDisconnect` callback: print a note, then exit the process.
void OnDisconnected(DbConnection conn, Exception? e)
{
    Console.Write(e == null ? "Disconnected normally." : $"Disconnected abnormally: {e}");
}

/// Register all the callbacks our app will use to respond to database events.
void RegisterCallbacks(DbConnection conn)
{
    conn.Db.User.OnInsert += UserOnInsert;
    conn.Db.User.OnUpdate += UserOnUpdate;

    conn.Db.Message.OnInsert += MessageOnInsert;

    conn.Reducers.OnSetName += ReducerOnSetNameEvent;
    conn.Reducers.OnSendMessage += ReducerOnSendMessageEvent;
}

/// If the user has no set name, use the first 8 characters from their identity.
string UserNameOrIdentity(User user) => user.Name ?? user.Identity.ToString()[..8];

/// Our `User.OnInsert` callback: if the user is online, print a notification.
void UserOnInsert(EventContext ctx, User insertedValue)
{
    if (insertedValue.Online)
    {
        Console.WriteLine($"{UserNameOrIdentity(insertedValue)} is online");
    }
}

/// Our `User.OnUpdate` callback:
/// print a notification about name and status changes.
void UserOnUpdate(EventContext ctx, User oldValue, User newValue)
{
    if (oldValue.Name != newValue.Name)
    {
        Console.WriteLine($"{UserNameOrIdentity(oldValue)} renamed to {newValue.Name}");
    }
    if (oldValue.Online != newValue.Online)
    {
        Console.WriteLine(newValue.Online
            ? $"{UserNameOrIdentity(newValue)} connected."
            : $"{UserNameOrIdentity(newValue)} disconnected.");
    }
}

/// Our `Message.OnInsert` callback: print new messages.
void MessageOnInsert(EventContext ctx, Message insertedValue)
{
    // We are filtering out messages inserted during the subscription being applied,
    // since we will be printing those in the OnSubscriptionApplied callback,
    // where we will be able to first sort the messages before printing.
    if (ctx.Event is not Event<Reducer>.SubscribeApplied)
    {
        PrintMessage(ctx.Db, insertedValue);
    }
}

void PrintMessage(RemoteTables tables, Message message)
{
    var sender = tables.User.Identity.Find(message.Sender);
    var senderName = "unknown";
    if (sender != null)
    {
        senderName = UserNameOrIdentity(sender);
    }

    Console.WriteLine($"{senderName}: {message.Text}");
}

/// Our `OnSetNameEvent` callback: print a warning if the reducer failed.
void ReducerOnSetNameEvent(ReducerEventContext ctx, string name)
{
    var e = ctx.Event;
    if (e.CallerIdentity == localIdentity && e.Status is Status.Failed(var error))
    {
        Console.Write($"Failed to change name to {name}: {error}");
    }
}

/// Our `OnSendMessageEvent` callback: print a warning if the reducer failed.
void ReducerOnSendMessageEvent(ReducerEventContext ctx, string text)
{
    var e = ctx.Event;
    if (e.CallerIdentity == localIdentity && e.Status is Status.Failed(var error))
    {
        Console.Write($"Failed to send message {text}: {error}");
    }
}

/// Our `OnConnect` callback: save our credentials to a file.
void OnConnected(DbConnection conn, Identity identity, string authToken)
{
    localIdentity = identity;
    AuthToken.SaveToken(authToken);

    conn.SubscriptionBuilder()
        .OnApplied(OnSubscriptionApplied)
        .SubscribeToAllTables();
}

/// Our `OnSubscriptionApplied` callback:
/// sort all past messages and print them in timestamp order.
void OnSubscriptionApplied(SubscriptionEventContext ctx)
{
    Console.WriteLine("Connected");
    PrintMessagesInOrder(ctx.Db);
}

void PrintMessagesInOrder(RemoteTables tables)
{
    foreach (var message in tables.Message.Iter().OrderBy(item => item.Sent))
    {
        PrintMessage(tables, message);
    }
}

/// Our separate thread from main, where we can call process updates and process commands without blocking the main thread.
void ProcessThread(DbConnection conn, CancellationToken ct)
{
    try
    {
        // loop until cancellation token
        while (!ct.IsCancellationRequested)
        {
            conn.FrameTick();

            ProcessCommands(conn.Reducers);

            Thread.Sleep(100);
        }
    }
    finally
    {
        conn.Disconnect();
    }
}

/// Read each line of standard input, and either set our name or send a message as appropriate.
void InputLoop()
{
    while (true)
    {
        var input = Console.ReadLine();
        if (input == null)
        {
            break;
        }

        if (input.StartsWith("/name "))
        {
            inputQueue.Enqueue(("name", input[6..]));

            continue;
        }

        inputQueue.Enqueue(("message", input));
    }
}

void ProcessCommands(RemoteReducers reducers)
{
    // process input queue commands
    while (inputQueue.TryDequeue(out var command))
    {
        switch (command.Command)
        {
            case "message":
                reducers.SendMessage(command.Args);
                break;
            case "name":
                reducers.SetName(command.Args);
                break;
        }
    }
}
