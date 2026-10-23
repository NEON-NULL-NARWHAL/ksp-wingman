// KSP Wingman · src/KspWingman.Mcp/KrpcLink.cs
// The connection to kRPC in the agent's KSP: opened on first use, reopened after KSP restarts.

using System.Net;
using KRPC.Client;

namespace KspWingman.Mcp;

/// <summary>
/// kRPC answers only in a game scene (not the main menu), and KSP can restart under us: every call (re)connects as
/// needed and turns kRPC's errors into one clear message for the agent.
/// </summary>
public sealed class KrpcLink(string host, int port) : IDisposable
{
    private readonly object _gate = new();
    private Connection? _connection;

    /// <summary>The open connection, opening it if needed.</summary>
    public Connection Connection
    {
        get
        {
            lock (_gate)
            {
                if (_connection != null)
                    return _connection;
                try
                {
                    var address = IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host)[0];
                    return _connection = new Connection("KSP Wingman", address, port, port + 1);
                }
                catch (Exception e)
                {
                    throw new WingmanException($"Can't reach kRPC at {host}:{port} ({e.Message}). Is the agent's KSP running and in a game (not the main menu)?");
                }
            }
        }
    }

    /// <summary>Runs a call; on a broken connection, drops it so the next call reconnects.</summary>
    public T Run<T>(Func<Connection, T> call)
    {
        try
        {
            return call(Connection);
        }
        catch (WingmanException)
        {
            throw;
        }
        catch (Exception e) when (e is System.IO.IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Reset();
            throw new WingmanException("Lost the connection to kRPC (KSP closed or restarted?). Try again in a moment.");
        }
        catch (Exception e)
        {
            throw new WingmanException(e.GetType().Name == "RPCException" ? "kRPC: " + e.Message.Split('\n')[0] : e.Message);
        }
    }

    /// <summary>Calls one of the Wingman bridge's procedures (the kRPC service "Wingman" in the agent's KSP).</summary>
    public T Wingman<T>(string procedure, params object[] arguments) => Run(connection =>
    {
        var encoded = arguments.Select(a => Encoder.Encode(a, TypeSpec.For(a.GetType()))).ToList();
        var result = connection.Invoke("Wingman", procedure, encoded, typeof(T));
        if (result.Error != null)
            throw new WingmanException($"kRPC: {result.Error.Description}" +
                (result.Error.Description.Contains("service", StringComparison.OrdinalIgnoreCase)
                    ? " Is the KSP Wingman bridge installed in the agent's KSP (GameData/KspWingman)?" : ""));
        return (T)Encoder.Decode(result.Value, TypeSpec.For(typeof(T)), connection)!;
    });

    private void Reset()
    {
        lock (_gate)
        {
            try { _connection?.Dispose(); } catch (Exception) { }
            _connection = null;
        }
    }

    public void Dispose() => Reset();
}

/// <summary>A problem the agent should read as is (shown as the tool's error).</summary>
public sealed class WingmanException(string message) : Exception(message);
