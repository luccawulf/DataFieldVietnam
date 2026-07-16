using System.Text;

namespace DataField42.Core.Tests;

/// <summary>
/// Reading the Battlefield Vietnam server list off bflist.
/// </summary>
public class ServerLobbyTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name), Encoding.UTF8);

    [Fact]
    public void Bflist_server_list_parses()
    {
        var entries = Bf1942ServerLobby.ParseServerList(Fixture("bflist_bfvietnam_servers.json"));

        Assert.Equal(8, entries.Count);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Ip)));
        Assert.All(entries, e => Assert.InRange(e.QueryPort, 1, 65535));
        Assert.Contains(entries, e => e.Ip == "108.61.236.22" && e.QueryPort == 23000);
    }

    /// <summary>
    /// Query ports are not uniform, so they must come from the list rather than being assumed.
    /// </summary>
    [Fact]
    public void Bflist_reports_non_default_query_ports()
    {
        var entries = Bf1942ServerLobby.ParseServerList(Fixture("bflist_bfvietnam_servers.json"));

        Assert.Contains(entries, e => e.QueryPort == 23009);
        Assert.Contains(entries, e => e.QueryPort == 23010);
        Assert.Contains(entries, e => e.QueryPort == 28001);
    }

    [Fact]
    public void Empty_page_parses_to_nothing()
    {
        Assert.Empty(Bf1942ServerLobby.ParseServerList("[]"));
    }
}
