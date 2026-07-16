using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

public class Bf1942ServerLobby
{
    public List<Bf1942Server> Servers { get; private set; } = new();

    /// <summary>
    /// Battlefield Vietnam's servers are not on BF1942's master (master.bf1942.org), so the list
    /// comes from bflist instead. Paginated, 100 per page, and small in practice -- the whole BFV
    /// population fits on one page.
    /// </summary>
    private const string ServerListApi = "https://api.bflist.io/bfvietnam/v1/servers";
    private const int ServersPerPage = 100;

    private readonly ILogger<Bf1942ServerLobby> _logger;

    public Bf1942ServerLobby(ILogger<Bf1942ServerLobby> logger)
    {
        _logger = logger;
    }

    public async Task QueryAllServers()
    {
        _logger.LogDebug($"Querying all {Servers.Count} servers.");
        var queryTasks = new List<Task>();
        foreach (var server in Servers)
            queryTasks.Add(server.QueryServer(TimeSpan.FromSeconds(3)));
        try
        {
            await Task.WhenAll(queryTasks);
            _logger.LogDebug("All server queries completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "One or more server queries failed during QueryAllServers.");
        }
    }

    public Bf1942Server GetOrCreate(IPAddress ip, int port)
    {
        var existing = Servers.FirstOrDefault(s => s.Ip.Equals(ip) && s.QueryPort == port);
        if (existing != null)
            return existing;
        var server = new Bf1942Server(ip, port);
        Servers.Add(server);
        return server;
    }

    public async Task GetServerListFromHttpApi()
    {
        _logger.LogDebug($"Fetching server list from {ServerListApi}.");
        try
        {
            using HttpClient client = new();
            int added = 0;

            // Servers are listed a page at a time; a short page means we have reached the end.
            for (int page = 1; ; page++)
            {
                var entries = await GetServerListPage(client, page);
                added += AddServers(entries);

                if (entries.Count < ServersPerPage)
                    break;
            }

            if (Servers.Count == 0)
                throw new Exception("Server list API returned no servers.");

            _logger.LogInformation($"Server list fetched: {Servers.Count} total servers, {added} newly added.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve data from Server list Api. An error occurred: {ex.Message}");
        }
    }

    private static async Task<List<BflistServerEntry>> GetServerListPage(HttpClient client, int page)
    {
        var response = await client.GetAsync($"{ServerListApi}/{page}?perPage={ServersPerPage}");

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Status code: {response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync();
        return ParseServerList(json);
    }

    private int AddServers(List<BflistServerEntry> entries)
    {
        int added = 0;
        foreach (var entry in entries)
        {
            try
            {
                var server = new Bf1942Server(IPAddress.Parse(entry.Ip), entry.QueryPort);
                if (!Servers.Contains(server))
                {
                    Servers.Add(server);
                    added++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to parse server entry {entry.Ip}:{entry.QueryPort}.");
            }
        }
        return added;
    }

    /// <summary>
    /// Pulls the address and query port out of a bflist server-list page.
    /// </summary>
    /// <remarks>
    /// The query port is read from the response rather than assumed: most BFV servers answer on
    /// 23000, but live ones were observed on 23009, 23010 and 28001 too.
    /// </remarks>
    public static List<BflistServerEntry> ParseServerList(string json)
    {
        // Read the two fields by hand rather than deserializing into a type: the client is published
        // trimmed, which turns reflection-based serialization off and strips members, so a
        // Deserialize<T> here throws in the published build while working fine in tests and debug
        // runs. JsonDocument needs neither reflection nor the type to survive trimming.
        var entries = new List<BflistServerEntry>();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return entries;

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("ip", out var ip)
                && element.TryGetProperty("queryPort", out var queryPort)
                && ip.ValueKind == JsonValueKind.String
                && queryPort.TryGetInt32(out var port))
                entries.Add(new BflistServerEntry(ip.GetString() ?? "", port));
        }
        return entries;
    }
}

public record BflistServerEntry(string Ip, int QueryPort);
