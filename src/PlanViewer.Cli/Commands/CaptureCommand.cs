using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

/// <summary>
/// Runs a query/proc, captures the actual execution plan (same path as the GUI's
/// "Save .sqlplan" flow), and reports start/end timestamps — no analysis, no parsing.
/// Built for fast scripted capture loops (run → save .sqlplan.gz → hand to Claude).
/// </summary>
public static class CaptureCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Command Create(ICredentialService? credentialService = null)
    {
        var fileArg = new Argument<string?>(
            "file",
            description: "Path to a .sql file to execute") { Arity = ArgumentArity.ZeroOrOne };

        var queryOption = new Option<string?>("--query", "Inline SQL text to execute (proc call, etc.)");
        queryOption.AddAlias("-q");

        var serverOption = new Option<string?>(
            "--server", "SQL Server name (matches credential store key; or set PLANVIEW_SERVER in .env)");
        serverOption.AddAlias("-s");

        var databaseOption = new Option<string?>(
            "--database", "Database context for execution (or set PLANVIEW_DATABASE in .env)");
        databaseOption.AddAlias("-d");

        var outOption = new Option<string?>(
            "--out", "Save the captured plan, gzipped, to this path (e.g. capture.sqlplan.gz). Omit to just report timing.");
        outOption.AddAlias("-o");

        var isolationOption = new Option<string?>(
            "--isolation-level", "Transaction isolation level for the capture (default: connection default)");

        var authOption = new Option<string?>("--auth", "Authentication type: windows, sql, or entra (default: auto-detect)");
        var trustCertOption = new Option<bool>("--trust-cert", "Trust server certificate (or set PLANVIEW_TRUST_CERT=true in .env)");
        var timeoutOption = new Option<int>("--timeout", getDefaultValue: () => 60, description: "Query timeout in seconds");
        var loginOption = new Option<string?>("--login", "SQL Server login — bypasses credential store (or set PLANVIEW_LOGIN in .env)");
        var passwordOption = new Option<string?>("--password", "SQL Server password — bypasses credential store (or set PLANVIEW_PASSWORD in .env)");

        var cmd = new Command("capture", "Run a query/proc and capture the actual execution plan")
        {
            fileArg, queryOption, serverOption, databaseOption, outOption, isolationOption,
            authOption, trustCertOption, timeoutOption, loginOption, passwordOption
        };

        cmd.SetHandler(async (ctx) =>
        {
            var filePath   = ctx.ParseResult.GetValueForArgument(fileArg);
            var query      = ctx.ParseResult.GetValueForOption(queryOption);
            var server     = ctx.ParseResult.GetValueForOption(serverOption);
            var database   = ctx.ParseResult.GetValueForOption(databaseOption);
            var outPath    = ctx.ParseResult.GetValueForOption(outOption);
            var isolation  = ctx.ParseResult.GetValueForOption(isolationOption);
            var auth       = ctx.ParseResult.GetValueForOption(authOption);
            var trustCert  = ctx.ParseResult.GetValueForOption(trustCertOption);
            var timeout    = ctx.ParseResult.GetValueForOption(timeoutOption);
            var login      = ctx.ParseResult.GetValueForOption(loginOption);
            var password   = ctx.ParseResult.GetValueForOption(passwordOption);

            var env = AnalyzeCommand.LoadEnvFile();
            server   ??= env.GetValueOrDefault("PLANVIEW_SERVER");
            database ??= env.GetValueOrDefault("PLANVIEW_DATABASE");
            login    ??= env.GetValueOrDefault("PLANVIEW_LOGIN");
            password ??= env.GetValueOrDefault("PLANVIEW_PASSWORD");
            if (!trustCert && env.GetValueOrDefault("PLANVIEW_TRUST_CERT")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                trustCert = true;

            if (string.IsNullOrEmpty(server))
            {
                Console.Error.WriteLine("No server specified. Use --server or set PLANVIEW_SERVER in .env.");
                Environment.ExitCode = 1;
                return;
            }
            if (string.IsNullOrEmpty(database))
            {
                Console.Error.WriteLine("--database is required (or set PLANVIEW_DATABASE in .env).");
                Environment.ExitCode = 1;
                return;
            }

            string sqlText;
            if (!string.IsNullOrEmpty(query))
            {
                sqlText = query;
            }
            else if (!string.IsNullOrEmpty(filePath))
            {
                if (!File.Exists(filePath))
                {
                    Console.Error.WriteLine($"File not found: {filePath}");
                    Environment.ExitCode = 1;
                    return;
                }
                sqlText = await File.ReadAllTextAsync(filePath);
            }
            else if (Console.IsInputRedirected)
            {
                sqlText = await Console.In.ReadToEndAsync();
            }
            else
            {
                Console.Error.WriteLine("Provide a .sql file, --query, or pipe SQL via stdin.");
                Environment.ExitCode = 1;
                return;
            }

            string connectionString;
            if (!string.IsNullOrEmpty(login))
            {
                connectionString = BuildDirectConnectionString(server, database, login, password ?? "", trustCert);
            }
            else if (credentialService != null)
            {
                var authType = auth?.ToLowerInvariant() switch
                {
                    "windows" => AuthenticationTypes.Windows,
                    "sql" => AuthenticationTypes.SqlServer,
                    "entra" => AuthenticationTypes.EntraMFA,
                    null => credentialService.CredentialExists(server)
                        ? AuthenticationTypes.SqlServer
                        : AuthenticationTypes.Windows,
                    _ => throw new ArgumentException($"Unknown auth type: {auth}. Use: windows, sql, entra")
                };

                var conn = new ServerConnection
                {
                    Id = server, ServerName = server, DisplayName = server,
                    AuthenticationType = authType,
                    TrustServerCertificate = trustCert,
                    EncryptMode = trustCert ? "Optional" : "Mandatory"
                };
                connectionString = conn.GetConnectionString(credentialService, database);
            }
            else
            {
                Console.Error.WriteLine("No credentials. Use --login/--password or the credential store.");
                Environment.ExitCode = 1;
                return;
            }

            var isAzure = server.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
                          server.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase);

            var startUtc = DateTime.UtcNow;
            Console.Error.Write($"[{startUtc:HH:mm:ss} UTC] Capturing actual plan on {server}/{database}... ");

            string? planXml;
            string? captureError;
            try
            {
                (planXml, captureError) = await ActualPlanExecutor.ExecuteForActualPlanAsync(
                    connectionString, database, sqlText,
                    planXml: null, isolationLevel: isolation,
                    isAzureSqlDb: isAzure, timeoutSeconds: timeout,
                    CancellationToken.None);
            }
            catch (SqlException ex)
            {
                planXml = null;
                captureError = $"SQL Error: {ex.Message}";
            }

            var endUtc = DateTime.UtcNow;
            var duration = endUtc - startUtc;

            string? savedTo = null;
            if (!string.IsNullOrEmpty(planXml))
            {
                Console.Error.WriteLine($"OK ({duration.TotalSeconds:F1}s)");

                if (!string.IsNullOrEmpty(outPath))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    await using var stream = File.Create(outPath);
                    await using var gzipStream = new GZipStream(stream, CompressionLevel.Optimal);
                    await using var writer = new StreamWriter(gzipStream);
                    await writer.WriteAsync(planXml);
                    savedTo = Path.GetFullPath(outPath);
                }
            }
            else
            {
                Console.Error.WriteLine($"FAILED ({duration.TotalSeconds:F1}s) — {captureError ?? "no plan captured"}");
            }

            var report = new CaptureReport
            {
                Server = server,
                Database = database,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationMs = duration.TotalMilliseconds,
                Success = !string.IsNullOrEmpty(planXml),
                Error = captureError,
                OutputFile = savedTo
            };
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));

            if (string.IsNullOrEmpty(planXml))
                Environment.ExitCode = 1;
        });

        return cmd;
    }

    private sealed class CaptureReport
    {
        public required string Server { get; init; }
        public required string Database { get; init; }
        public required DateTime StartTimeUtc { get; init; }
        public required DateTime EndTimeUtc { get; init; }
        public required double DurationMs { get; init; }
        public required bool Success { get; init; }
        public string? Error { get; init; }
        public string? OutputFile { get; init; }
    }

    private static string BuildDirectConnectionString(
        string server, string database, string login, string password, bool trustCert)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            UserID = login,
            Password = password,
            ApplicationName = "PlanViewer",
            ConnectTimeout = 15,
            MultipleActiveResultSets = true,
            TrustServerCertificate = trustCert,
            Encrypt = trustCert ? SqlConnectionEncryptOption.Optional : SqlConnectionEncryptOption.Mandatory
        };
        return builder.ConnectionString;
    }
}
