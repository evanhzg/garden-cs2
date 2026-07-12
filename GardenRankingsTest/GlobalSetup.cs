using GardenRankingsCore.Config;
using GardenRankingsCore.Db;

namespace GardenRankingsTest;

[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public void Setup()
    {
        // Start from a clean file so schema changes never leave a stale database behind.
        if (File.Exists("test_rankings.db"))
        {
            File.Delete("test_rankings.db");
        }

        Configs.OverrideConfigDataForTests(new ConfigData
        {
            DatabaseConnectionString = "Data Source=test_rankings.db; Pooling=False",
        });
        Queries.Initialize();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        Queries.Disconnect();
    }
}

public abstract class BaseTestFixture
{
    [SetUp]
    public void PerTestSetup()
    {
        Configs.OverrideConfigDataForTests(new ConfigData
        {
            DatabaseConnectionString = "Data Source=test_rankings.db; Pooling=False",
        });
        Queries.Wipe();
        Queries.EnsureActiveSeason();
    }
}
