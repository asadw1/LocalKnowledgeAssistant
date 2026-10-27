using Assistant.Core.Indexing;

namespace Assistant.Tests;

public class MigrationOrderingTests
{
    private static (string Resource, Func<string> Read) Res(string name, string sql = "") => ("migrations/" + name, () => sql);

    [Fact]
    public void Migrations_are_sorted_by_version_and_carry_their_sql()
    {
        var ordered = MetadataDatabase.Order([Res("010_late.sql", "B"), Res("002_early.sql", "A")]);

        Assert.Equal([(2, "002_early.sql", "A"), (10, "010_late.sql", "B")], ordered);
    }

    [Theory]
    [InlineData("init.sql")]      // no underscore
    [InlineData("_init.sql")]     // nothing before the underscore
    [InlineData("abc_init.sql")]  // prefix is not a number
    public void Misnamed_migration_is_rejected(string name)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MetadataDatabase.Order([Res(name)]));

        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Duplicate_versions_are_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MetadataDatabase.Order([Res("001_a.sql"), Res("001_b.sql")]));

        Assert.Contains("Duplicate", ex.Message);
    }
}
