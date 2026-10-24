using Microsoft.Data.Sqlite;

namespace AskYourReports.Data;

/// <summary>
/// Owns the SQLite connection string, schema and fake seed data.
/// </summary>
public sealed class SalesDatabase
{
    private readonly string _connectionString;

    public SalesDatabase(string connectionString)
    {
        _connectionString = connectionString;
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Creates the schema and inserts fake data if the database is empty.
    /// Uses a fixed random seed so every run produces the same data,
    /// with dates relative to <paramref name="today"/> so "last month" always has rows.
    /// </summary>
    public SeedResult EnsureSeeded(DateOnly today)
    {
        using var connection = Open();
        Execute(connection, Schema);

        var existing = Scalar(connection, "SELECT COUNT(*) FROM products");
        if (existing > 0)
            return new SeedResult(existing, Scalar(connection, "SELECT COUNT(*) FROM sales"), Seeded: false);

        using var tx = connection.BeginTransaction();

        foreach (var p in Products)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO products (id, name, category, unit_price) VALUES ($id, $name, $category, $price)";
            cmd.Parameters.AddWithValue("$id", p.Id);
            cmd.Parameters.AddWithValue("$name", p.Name);
            cmd.Parameters.AddWithValue("$category", p.Category);
            cmd.Parameters.AddWithValue("$price", p.UnitPrice);
            cmd.ExecuteNonQuery();
        }

        using var insertSale = connection.CreateCommand();
        insertSale.Transaction = tx;
        insertSale.CommandText =
            "INSERT INTO sales (product_id, sale_date, quantity, unit_price, region) VALUES ($product, $date, $qty, $price, $region)";
        var pProduct = insertSale.Parameters.Add("$product", SqliteType.Integer);
        var pDate = insertSale.Parameters.Add("$date", SqliteType.Text);
        var pQty = insertSale.Parameters.Add("$qty", SqliteType.Integer);
        var pPrice = insertSale.Parameters.Add("$price", SqliteType.Real);
        var pRegion = insertSale.Parameters.Add("$region", SqliteType.Text);

        var random = new Random(42);
        var totalWeight = Products.Sum(p => p.Popularity);
        var start = today.AddDays(-SeedDays);

        for (var day = start; day < today; day = day.AddDays(1))
        {
            var salesToday = random.Next(8, 20);
            for (var i = 0; i < salesToday; i++)
            {
                var product = PickWeighted(random, totalWeight);
                pProduct.Value = product.Id;
                pDate.Value = day.ToString("yyyy-MM-dd");
                pQty.Value = random.Next(1, 6);
                pPrice.Value = product.UnitPrice;
                pRegion.Value = Regions[random.Next(Regions.Length)];
                insertSale.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return new SeedResult(Products.Length, Scalar(connection, "SELECT COUNT(*) FROM sales"), Seeded: true);
    }

    private static Product PickWeighted(Random random, int totalWeight)
    {
        var roll = random.Next(totalWeight);
        foreach (var p in Products)
        {
            roll -= p.Popularity;
            if (roll < 0)
                return p;
        }
        return Products[^1];
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private const int SeedDays = 180;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS products (
            id          INTEGER PRIMARY KEY,
            name        TEXT    NOT NULL,
            category    TEXT    NOT NULL,
            unit_price  REAL    NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sales (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            product_id  INTEGER NOT NULL REFERENCES products(id),
            sale_date   TEXT    NOT NULL,  -- yyyy-MM-dd
            quantity    INTEGER NOT NULL,
            unit_price  REAL    NOT NULL,
            region      TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sales_date ON sales(sale_date);
        """;

    private static readonly string[] Regions = ["North", "South", "East", "West"];

    // Popularity is a relative weight: higher = sold more often. A few items are deliberately weak sellers.
    private static readonly Product[] Products =
    [
        new(1, "Espresso Beans 1kg", "Coffee", 24.00, Popularity: 30),
        new(2, "Decaf Beans 500g", "Coffee", 14.50, Popularity: 6),
        new(3, "Cold Brew Concentrate", "Coffee", 11.00, Popularity: 18),
        new(4, "Green Tea Tin", "Tea", 9.50, Popularity: 12),
        new(5, "Chai Spice Blend", "Tea", 8.00, Popularity: 4),
        new(6, "Ceramic Mug", "Merch", 12.00, Popularity: 20),
        new(7, "Travel Tumbler", "Merch", 22.00, Popularity: 10),
        new(8, "Logo T-Shirt", "Merch", 18.00, Popularity: 2),
        new(9, "Pour-Over Kit", "Equipment", 39.00, Popularity: 8),
        new(10, "Burr Grinder", "Equipment", 89.00, Popularity: 5),
        new(11, "Milk Frother", "Equipment", 29.00, Popularity: 9),
        new(12, "Paper Filters (100)", "Equipment", 5.50, Popularity: 25),
    ];

    private sealed record Product(int Id, string Name, string Category, double UnitPrice, int Popularity);
}

public sealed record SeedResult(long Products, long Sales, bool Seeded);
