using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Output;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

public static class AnalyzeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create(ICredentialService? credentialService = null)
    {
        var fileArg = new Argument<string?>(
            "file",
            description: "Path to a .sqlplan file, .sql file, directory of .sql files, or glob pattern (e.g. queries/*FULL.sql)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };

        var stdinOption = new Option<bool>(
            "--stdin",
            "Read plan XML from stdin");

        // --output / -o controls both file types (live mode) and stdout format (file/stdin mode).
        // Live mode:      comma-separated types: sqlplan, json, txt, csv  (default: sqlplan,json,txt)
        // File/stdin mode: json (default) or txt — other values are ignored for stdout
        var outputOption = new Option<string>(
            "--output",
            getDefaultValue: () => "sqlplan,json,txt",
            description: "Output types (comma-separated): sqlplan, json, txt, csv. " +
                         "In live mode controls which files are written (default: sqlplan,json,txt). " +
                         "In file/stdin mode controls stdout format: json (default) or txt.");
        outputOption.AddAlias("-o");

        var compactOption = new Option<bool>(
            "--compact",
            "Compact JSON output (no indentation)");

        var warningsOnlyOption = new Option<bool>(
            "--warnings-only",
            "Only output warnings and missing indexes, skip operator tree");

        // Live execution options
        var serverOption = new Option<string?>(
            "--server",
            "SQL Server name (matches credential store key; or set PLANVIEW_SERVER in .env)");
        serverOption.AddAlias("-s");

        var databaseOption = new Option<string?>(
            "--database",
            "Database context for execution (or set PLANVIEW_DATABASE in .env)");
        databaseOption.AddAlias("-d");

        var queryOption = new Option<string?>(
            "--query",
            "Inline SQL text to execute");
        queryOption.AddAlias("-q");

        var outputDirOption = new Option<DirectoryInfo?>(
            "--output-dir",
            "Directory for output files (default: current directory)");

        var estimatedOption = new Option<bool>(
            "--estimated",
            "Estimated plan only (query is NOT executed)");

        var authOption = new Option<string?>(
            "--auth",
            "Authentication type: windows, sql, or entra (default: auto-detect)");

        var trustCertOption = new Option<bool>(
            "--trust-cert",
            "Trust server certificate (or set PLANVIEW_TRUST_CERT=true in .env)");

        var timeoutOption = new Option<int>(
            "--timeout",
            getDefaultValue: () => 60,
            description: "Query timeout in seconds");

        var loginOption = new Option<string?>(
            "--login",
            "SQL Server login — bypasses credential store (or set PLANVIEW_LOGIN in .env)");

        var passwordOption = new Option<string?>(
            "--password",
            "SQL Server password — bypasses credential store (or set PLANVIEW_PASSWORD in .env)");

        var configOption = new Option<string?>(
            "--config",
            "Path to .planview.json config file (overrides auto-discovery)");

        var repeatOption = new Option<int>(
            "--repeat",
            getDefaultValue: () => 1,
            description: "Execute each SQL input N times, saving outputs with a _001, _002 … suffix. " +
                         "Useful for warm-start or best-of-N comparisons. Requires live mode (--server or PLANVIEW_SERVER).");

        var whatIfOption = new Option<bool>(
            "--what-if",
            "Show which files would be analysed and what outputs would be generated, without executing");

        var testConnectionOption = new Option<bool>(
            "--test-connection",
            "Verify connectivity and show where each credential value was sourced from, without running any analysis");

        var cmd = new Command("analyze", "Analyze a SQL Server execution plan")
        {
            fileArg,
            stdinOption,
            outputOption,
            compactOption,
            warningsOnlyOption,
            serverOption,
            databaseOption,
            queryOption,
            outputDirOption,
            estimatedOption,
            authOption,
            trustCertOption,
            timeoutOption,
            loginOption,
            passwordOption,
            configOption,
            repeatOption,
            whatIfOption,
            testConnectionOption
        };

        cmd.SetHandler(async (ctx) =>
        {
            var filePath   = ctx.ParseResult.GetValueForArgument(fileArg);
            var stdin      = ctx.ParseResult.GetValueForOption(stdinOption);
            var outputRaw  = ctx.ParseResult.GetValueForOption(outputOption) ?? "sqlplan,json,txt";
            var compact    = ctx.ParseResult.GetValueForOption(compactOption);
            var warningsOnly = ctx.ParseResult.GetValueForOption(warningsOnlyOption);
            var server     = ctx.ParseResult.GetValueForOption(serverOption);
            var database   = ctx.ParseResult.GetValueForOption(databaseOption);
            var query      = ctx.ParseResult.GetValueForOption(queryOption);
            var outputDir  = ctx.ParseResult.GetValueForOption(outputDirOption);
            var estimated  = ctx.ParseResult.GetValueForOption(estimatedOption);
            var auth       = ctx.ParseResult.GetValueForOption(authOption);
            var trustCert  = ctx.ParseResult.GetValueForOption(trustCertOption);
            var timeout    = ctx.ParseResult.GetValueForOption(timeoutOption);
            var login      = ctx.ParseResult.GetValueForOption(loginOption);
            var password   = ctx.ParseResult.GetValueForOption(passwordOption);
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var repeat     = ctx.ParseResult.GetValueForOption(repeatOption);
            var whatIf     = ctx.ParseResult.GetValueForOption(whatIfOption);
            var testConn   = ctx.ParseResult.GetValueForOption(testConnectionOption);

            // Capture which values were explicitly provided via CLI *before* .env is loaded
            bool serverFromCli   = ctx.ParseResult.FindResultFor(serverOption)   != null;
            bool databaseFromCli = ctx.ParseResult.FindResultFor(databaseOption) != null;
            bool loginFromCli    = ctx.ParseResult.FindResultFor(loginOption)    != null;
            bool passwordFromCli = ctx.ParseResult.FindResultFor(passwordOption) != null;
            bool trustCertFromCli = ctx.ParseResult.FindResultFor(trustCertOption) != null;
            bool timeoutFromCli  = ctx.ParseResult.FindResultFor(timeoutOption)  != null;
            bool authFromCli     = ctx.ParseResult.FindResultFor(authOption)     != null;

            // Load .env file if present (CLI args take precedence)
            var env = LoadEnvFile();
            server    ??= env.GetValueOrDefault("PLANVIEW_SERVER");
            database  ??= env.GetValueOrDefault("PLANVIEW_DATABASE");
            login     ??= env.GetValueOrDefault("PLANVIEW_LOGIN");
            password  ??= env.GetValueOrDefault("PLANVIEW_PASSWORD");
            if (!trustCert && env.GetValueOrDefault("PLANVIEW_TRUST_CERT")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                trustCert = true;

            var outputTypes = ParseOutputTypes(outputRaw);

            // --repeat only makes sense in live mode
            if (repeat != 1 && server == null)
            {
                Console.Error.WriteLine("--repeat requires a server. Use --server or set PLANVIEW_SERVER in .env.");
                Environment.ExitCode = 1;
                return;
            }
            if (repeat < 1)
            {
                Console.Error.WriteLine("--repeat must be 1 or greater.");
                Environment.ExitCode = 1;
                return;
            }

            if (testConn)
            {
                var sources = new ConnectionSources(
                    Server:       serverFromCli   ? "CLI --server"    : env.ContainsKey("PLANVIEW_SERVER")    ? ".env"          : null,
                    Database:     databaseFromCli ? "CLI --database"  : env.ContainsKey("PLANVIEW_DATABASE")  ? ".env"          : null,
                    Login:        loginFromCli    ? "CLI --login"     : env.ContainsKey("PLANVIEW_LOGIN")     ? ".env"          : null,
                    Password:     passwordFromCli ? "CLI --password"  : env.ContainsKey("PLANVIEW_PASSWORD")  ? ".env"          : null,
                    TrustCert:    trustCertFromCli ? "CLI --trust-cert" : env.ContainsKey("PLANVIEW_TRUST_CERT") ? ".env"        : null,
                    Auth:         authFromCli     ? "CLI --auth"      : null,
                    Timeout:      timeoutFromCli  ? "CLI --timeout"   : null
                );
                await TestConnectionAsync(server, database, login, password, auth, trustCert, timeout,
                    credentialService, sources);
                return;
            }

            if (server != null)
            {
                if (whatIf)
                {
                    PrintWhatIf(filePath, query, outputDir?.FullName ?? Directory.GetCurrentDirectory(), outputTypes, repeat);
                    return;
                }

                var analyzerConfig = ConfigLoader.Load(configPath);
                await RunLiveAsync(filePath, server, database, query, outputDir, estimated,
                    auth, trustCert, timeout, outputTypes, compact, warningsOnly,
                    credentialService, login, password, analyzerConfig, repeat);
            }
            else
            {
                if (whatIf)
                {
                    PrintWhatIfFileMode(filePath, stdin);
                    return;
                }

                var analyzerConfig = ConfigLoader.Load(configPath);
                await RunAsync(filePath, stdin, outputTypes, compact, warningsOnly, analyzerConfig);
            }
        });

        return cmd;
    }

    #region What-If

    private static void PrintWhatIf(string? filePath, string? query, string outDir, HashSet<string> outputTypes, int repeat)
    {
        Console.WriteLine("=== What-If ===");
        Console.WriteLine();

        var pad = RepeatPadWidth(repeat);

        if (!string.IsNullOrEmpty(query))
        {
            Console.WriteLine("Input: inline --query");
            Console.WriteLine();
            Console.WriteLine("Would generate:");
            foreach (var run in RepeatSuffixes("inline-query", repeat, pad))
                foreach (var f in GetOutputFileNames(run, outputTypes))
                    Console.WriteLine($"  {Path.Combine(outDir, f)}");
        }
        else if (filePath != null)
        {
            var inputFiles = ResolveInputPaths(filePath);

            if (inputFiles.Length == 0)
            {
                Console.Error.WriteLine($"No files matched: {filePath}");
                return;
            }

            Console.WriteLine($"Input files ({inputFiles.Length}):");
            foreach (var f in inputFiles)
                Console.WriteLine($"  {f}");

            var runCount = inputFiles.Length * repeat;
            Console.WriteLine();
            Console.WriteLine($"Would generate ({runCount} run{(runCount == 1 ? "" : "s")}):");
            foreach (var f in inputFiles)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                foreach (var run in RepeatSuffixes(name, repeat, pad))
                    foreach (var outFile in GetOutputFileNames(run, outputTypes))
                        Console.WriteLine($"  {Path.Combine(outDir, outFile)}");
            }
        }
        else
        {
            Console.Error.WriteLine("No input specified. Provide a file path, directory, glob pattern, or --query.");
        }
    }

    private static void PrintWhatIfFileMode(string? filePath, bool stdin)
    {
        Console.WriteLine("=== What-If ===");
        Console.WriteLine();

        if (stdin || filePath == null)
            Console.WriteLine("Input:  stdin");
        else
            Console.WriteLine($"Input:  {filePath}");

        Console.WriteLine("Output: stdout");
    }

    #endregion

    #region File/Stdin Analysis

    private static async Task RunAsync(string? filePath, bool stdin, HashSet<string> outputTypes, bool compact, bool warningsOnly, AnalyzerConfig analyzerConfig)
    {
        string planXml;
        string source;

        if (stdin || (filePath == null && Console.IsInputRedirected))
        {
            planXml = await Console.In.ReadToEndAsync();
            source = "stdin";
        }
        else if (filePath != null)
        {
            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"File not found: {filePath}");
                Environment.ExitCode = 1;
                return;
            }

            planXml = await File.ReadAllTextAsync(filePath);
            source = Path.GetFileName(filePath);
        }
        else
        {
            Console.Error.WriteLine("Provide a .sqlplan file path or use --stdin");
            Environment.ExitCode = 1;
            return;
        }

        if (string.IsNullOrWhiteSpace(planXml))
        {
            Console.Error.WriteLine("Empty plan XML");
            Environment.ExitCode = 1;
            return;
        }

        var plan = ShowPlanParser.Parse(planXml);
        PlanAnalyzer.Analyze(plan, analyzerConfig);

        if (plan.Batches.Count == 0)
        {
            Console.Error.WriteLine("Could not parse any statements from the plan XML");
            Environment.ExitCode = 1;
            return;
        }

        var result = ResultMapper.Map(plan, source);

        if (warningsOnly)
        {
            foreach (var stmt in result.Statements)
                stmt.OperatorTree = null;
        }

        // In file/stdin mode: txt/text → stdout as text, otherwise stdout as json
        if (outputTypes.Contains("txt") || outputTypes.Contains("text"))
            TextFormatter.WriteText(result, Console.Out);
        else
        {
            var opts = compact ? CompactJsonOptions : JsonOptions;
            Console.WriteLine(JsonSerializer.Serialize(result, opts));
        }
    }

    #endregion

    #region Live Execution

    private record ConnectionSources(
        string? Server, string? Database, string? Login, string? Password,
        string? TrustCert, string? Auth, string? Timeout);

    private static async Task TestConnectionAsync(
        string? server, string? database, string? login, string? password,
        string? auth, bool trustCert, int timeout,
        ICredentialService? credentialService, ConnectionSources sources)
    {
        Console.WriteLine("=== Connection Test ===");
        Console.WriteLine();

        string Src(string? source, string defaultLabel = "default") =>
            source != null ? $"[{source}]" : $"[{defaultLabel}]";

        if (server == null)
        {
            Console.Error.WriteLine("  ERROR: No server specified. Use --server or set PLANVIEW_SERVER in .env.");
            Environment.ExitCode = 1;
            return;
        }

        // Determine auth method and effective login for display
        bool usingDirectLogin    = !string.IsNullOrEmpty(login);
        bool usingCredStore      = !usingDirectLogin && credentialService?.CredentialExists(server) == true;
        bool usingWindowsAuth    = !usingDirectLogin && !usingCredStore && auth?.ToLowerInvariant() != "sql" && auth?.ToLowerInvariant() != "entra";
        bool usingEntra          = auth?.ToLowerInvariant() == "entra";
        string? credStoreLogin   = null;

        if (usingCredStore && credentialService != null)
        {
            // Retrieve username from store for display (password stays in the store)
            var cred = credentialService.GetCredential(server);
            credStoreLogin = cred?.Username;
        }

        string authMethod = usingDirectLogin ? "SQL (direct login/password)"
            : usingCredStore                 ? "SQL (credential store)"
            : usingEntra                     ? "Entra MFA"
            :                                  "Windows Authentication";

        string loginDisplay    = usingDirectLogin  ? (login ?? "")
            : usingCredStore                       ? (credStoreLogin ?? "(stored)")
            : usingEntra                           ? "(interactive)"
            :                                        $"{Environment.UserDomainName}\\{Environment.UserName}";

        string passwordDisplay = usingDirectLogin ? (string.IsNullOrEmpty(password) ? "(empty)" : "****")
            : usingCredStore                      ? "(from credential store)"
            :                                       "N/A";
        string passwordSource  = usingDirectLogin ? (sources.Password ?? "default")
            : usingCredStore                      ? "credential store"
            :                                       "N/A";

        Console.WriteLine($"  {"Server",-16} {server,-40} {Src(sources.Server, "not set")}");
        Console.WriteLine($"  {"Database",-16} {(database ?? "(not set)"),-40} {Src(sources.Database, "not set")}");
        Console.WriteLine($"  {"Auth method",-16} {authMethod,-40} {Src(sources.Auth, "auto-detect")}");
        Console.WriteLine($"  {"Login",-16} {loginDisplay,-40} [{(usingDirectLogin ? sources.Login ?? "default" : usingCredStore ? "credential store" : usingEntra ? "interactive" : "Windows")}]");
        Console.WriteLine($"  {"Password",-16} {passwordDisplay,-40} [{passwordSource}]");
        Console.WriteLine($"  {"Trust cert",-16} {(trustCert ? "true" : "false"),-40} {Src(sources.TrustCert, "default (false)")}");
        Console.WriteLine($"  {"Connect timeout",-16} {"15s",-40} [hardcoded]");
        Console.WriteLine($"  {"Query timeout",-16} {$"{timeout}s",-40} {Src(sources.Timeout, "default (60s)")}");
        Console.WriteLine();

        // Build connection string
        string connectionString;
        try
        {
            if (usingDirectLogin)
            {
                connectionString = BuildDirectConnectionString(server, database ?? "master", login!, password ?? "", trustCert);
            }
            else if (credentialService != null)
            {
                var connection = BuildServerConnection(server, auth, trustCert, credentialService);
                connectionString = connection.GetConnectionString(credentialService, database ?? "master");
            }
            else
            {
                Console.Error.WriteLine("  ERROR: No credentials available. Use --login/--password, a .env file, or the credential store.");
                Environment.ExitCode = 1;
                return;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ERROR building connection string: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        // Show obfuscated connection string to help diagnose instance/port/driver issues
        var obfuscated = ObfuscateConnectionString(connectionString);
        Console.WriteLine($"  Connection string:");
        Console.WriteLine($"    {obfuscated}");
        Console.WriteLine();

        // Attempt connection
        Console.Write("  Connecting ... ");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            sw.Stop();

            // Query server info
            await using var cmd2 = new SqlCommand(
                "SELECT @@VERSION, SERVERPROPERTY('Edition'), SERVERPROPERTY('ProductLevel'), " +
                "SERVERPROPERTY('ProductUpdateLevel'), @@SERVERNAME, DB_NAME()", conn);
            cmd2.CommandTimeout = 10;
            await using var reader = await cmd2.ExecuteReaderAsync();
            string version = "", edition = "", productLevel = "", updateLevel = "", serverName = "", dbName = "";
            if (await reader.ReadAsync())
            {
                version      = reader.IsDBNull(0) ? "" : reader.GetString(0);
                edition      = reader.IsDBNull(1) ? "" : reader.GetString(1);
                productLevel = reader.IsDBNull(2) ? "" : reader.GetString(2);
                updateLevel  = reader.IsDBNull(3) ? "" : reader.GetString(3);
                serverName   = reader.IsDBNull(4) ? "" : reader.GetString(4);
                dbName       = reader.IsDBNull(5) ? "" : reader.GetString(5);
            }

            Console.WriteLine($"OK ({sw.Elapsed.TotalMilliseconds:F0} ms)");
            Console.WriteLine();

            // Extract just the first line of @@VERSION (the compact one)
            var versionLine = version.Split('\n')[0].Trim();
            Console.WriteLine($"  Server name:   {serverName}");
            Console.WriteLine($"  Database:      {dbName}");
            Console.WriteLine($"  Version:       {versionLine}");
            Console.WriteLine($"  Edition:       {edition} ({productLevel}{(string.IsNullOrEmpty(updateLevel) ? "" : $" {updateLevel}")})");
        }
        catch (SqlException ex)
        {
            sw.Stop();
            Console.WriteLine($"FAILED ({sw.Elapsed.TotalMilliseconds:F0} ms)");
            Console.WriteLine();
            Console.Error.WriteLine($"  SQL Error {ex.Number}: {ex.Message}");

            // Offer targeted hints for common error codes
            var hint = ex.Number switch
            {
                -2    => "Connection timed out. Check the server name, port, and that SQL Server is reachable from this machine.",
                2     => "Server not found or not accessible. Verify the server name and that TCP/IP is enabled.",
                18456 => "Login failed. Check the username, password, and that the login has access to the database.",
                4060  => "Database not found or access denied. Verify the database name.",
                18452 => "Login from an untrusted domain. Try --auth sql or --auth entra.",
                _     => null
            };
            if (hint != null)
                Console.Error.WriteLine($"  Hint: {hint}");

            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"FAILED ({sw.Elapsed.TotalMilliseconds:F0} ms)");
            Console.Error.WriteLine($"  Error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>Replaces the Password value in a connection string with ****.</summary>
    private static string ObfuscateConnectionString(string cs)
    {
        // SqlConnectionStringBuilder round-trips the string; replace password field
        try
        {
            var builder = new SqlConnectionStringBuilder(cs);
            if (!string.IsNullOrEmpty(builder.Password))
                builder.Password = "****";
            return builder.ConnectionString;
        }
        catch
        {
            // Fallback: simple regex-style replace
            return System.Text.RegularExpressions.Regex.Replace(
                cs, @"(?i)(password\s*=\s*)[^;]+", "$1****");
        }
    }

    private static async Task RunLiveAsync(
        string? filePath, string server, string? database, string? query,
        DirectoryInfo? outputDir, bool estimated, string? auth, bool trustCert,
        int timeout, HashSet<string> outputTypes, bool compact, bool warningsOnly,
        ICredentialService? credentialService, string? login, string? password,
        AnalyzerConfig analyzerConfig, int repeat)
    {
        if (string.IsNullOrEmpty(database))
        {
            Console.Error.WriteLine("--database is required when using --server");
            Environment.ExitCode = 1;
            return;
        }

        // Build connection string
        string connectionString;
        if (!string.IsNullOrEmpty(login))
        {
            connectionString = BuildDirectConnectionString(server, database, login, password ?? "", trustCert);
        }
        else if (credentialService != null)
        {
            var connection = BuildServerConnection(server, auth, trustCert, credentialService);
            connectionString = connection.GetConnectionString(credentialService, database);
        }
        else
        {
            Console.Error.WriteLine("No credentials provided. Use --login/--password, a .env file, or the credential store.");
            Environment.ExitCode = 1;
            return;
        }

        // Determine inputs
        var sqlInputs = new List<(string Name, string SqlText)>();

        if (!string.IsNullOrEmpty(query))
        {
            sqlInputs.Add(("inline-query", query));
        }
        else if (filePath == null)
        {
            Console.Error.WriteLine("Provide a .sql file, directory, or --query with --server");
            Environment.ExitCode = 1;
            return;
        }
        else if (ContainsGlob(filePath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();
            var pattern = Path.GetFileName(filePath);
            var sqlFiles = Directory.Exists(dir)
                ? Directory.GetFiles(dir, pattern).OrderBy(f => f).ToArray()
                : [];

            if (sqlFiles.Length == 0)
            {
                Console.Error.WriteLine($"No files matched: {filePath}");
                Environment.ExitCode = 1;
                return;
            }

            foreach (var sqlFile in sqlFiles)
            {
                var sqlText = await File.ReadAllTextAsync(sqlFile);
                sqlInputs.Add((Path.GetFileNameWithoutExtension(sqlFile), sqlText));
            }
        }
        else if (Directory.Exists(filePath))
        {
            var sqlFiles = Directory.GetFiles(filePath, "*.sql")
                .OrderBy(f => f)
                .ToArray();

            if (sqlFiles.Length == 0)
            {
                Console.Error.WriteLine($"No .sql files found in {filePath}");
                Environment.ExitCode = 1;
                return;
            }

            foreach (var sqlFile in sqlFiles)
            {
                var sqlText = await File.ReadAllTextAsync(sqlFile);
                sqlInputs.Add((Path.GetFileNameWithoutExtension(sqlFile), sqlText));
            }
        }
        else if (File.Exists(filePath))
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".sqlplan")
            {
                if (repeat > 1)
                {
                    Console.Error.WriteLine("--repeat cannot be used with a .sqlplan file (nothing to execute).");
                    Environment.ExitCode = 1;
                    return;
                }
                // Redirect to file analysis (stdout)
                await RunAsync(filePath, false, outputTypes, compact, warningsOnly, analyzerConfig);
                return;
            }
            if (ext != ".sql")
            {
                Console.Error.WriteLine($"Unsupported file type: {ext}. Use .sql or .sqlplan");
                Environment.ExitCode = 1;
                return;
            }
            var text = await File.ReadAllTextAsync(filePath);
            sqlInputs.Add((Path.GetFileNameWithoutExtension(filePath), text));
        }
        else
        {
            Console.Error.WriteLine($"File or directory not found: {filePath}");
            Environment.ExitCode = 1;
            return;
        }

        // Resolve output directory
        var outDir = outputDir?.FullName ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var isAzure = IsAzureSqlDb(server);
        var planType = estimated ? "estimated" : "actual";
        var pad = RepeatPadWidth(repeat);

        var totalRuns = sqlInputs.Count * repeat;
        var repeatNote = repeat > 1 ? $" × {repeat} repeats = {totalRuns} runs" : "";
        Console.Error.WriteLine($"Capturing {planType} plans from {server}/{database} ({sqlInputs.Count} input{(sqlInputs.Count == 1 ? "" : "s")}{repeatNote})");
        Console.Error.WriteLine();

        var errors = 0;
        var runIndex = 0;

        foreach (var (name, sqlText) in sqlInputs)
        {
            for (int rep = 1; rep <= repeat; rep++)
            {
                runIndex++;
                var outputName = repeat > 1 ? $"{name}_{rep.ToString().PadLeft(pad, '0')}" : name;
                var label = totalRuns > 1 ? $"[{runIndex}/{totalRuns}] {outputName}" : outputName;

                try
                {
                    Console.Error.Write($"[{DateTime.Now:HH:mm:ss}] {label} ... ");
                    var sw = Stopwatch.StartNew();

                    string? planXml;
                    if (estimated)
                    {
                        planXml = await EstimatedPlanExecutor.GetEstimatedPlanAsync(
                            connectionString, database, sqlText, timeout);
                    }
                    else
                    {
                        planXml = await ActualPlanExecutor.ExecuteForActualPlanAsync(
                            connectionString, database, sqlText,
                            planXml: null, isolationLevel: null,
                            isAzureSqlDb: isAzure, timeoutSeconds: timeout,
                            CancellationToken.None);
                    }

                    sw.Stop();

                    if (string.IsNullOrEmpty(planXml))
                    {
                        Console.Error.WriteLine($"NO PLAN ({sw.Elapsed.TotalSeconds:F1}s)");
                        errors++;
                        continue;
                    }

                    if (outputTypes.Contains("sqlplan"))
                        await File.WriteAllTextAsync(Path.Combine(outDir, $"{outputName}.sqlplan"), planXml);

                    var plan = ShowPlanParser.Parse(planXml);
                    PlanAnalyzer.Analyze(plan, analyzerConfig);
                    var result = ResultMapper.Map(plan, $"{name}.sql");

                    if (warningsOnly)
                        foreach (var stmt in result.Statements)
                            stmt.OperatorTree = null;

                    if (outputTypes.Contains("json"))
                    {
                        var jsonOpts = compact ? CompactJsonOptions : JsonOptions;
                        await File.WriteAllTextAsync(
                            Path.Combine(outDir, $"{outputName}.analysis.json"),
                            JsonSerializer.Serialize(result, jsonOpts));
                    }

                    if (outputTypes.Contains("txt"))
                    {
                        using var txtWriter = new StreamWriter(Path.Combine(outDir, $"{outputName}.analysis.txt"));
                        TextFormatter.WriteText(result, txtWriter);
                    }

                    if (outputTypes.Contains("csv"))
                        await WriteCsvAsync(Path.Combine(outDir, $"{outputName}.analysis.csv"), result);

                    Console.Error.WriteLine($"OK ({sw.Elapsed.TotalSeconds:F1}s)");
                }
                catch (SqlException ex)
                {
                    Console.Error.WriteLine($"SQL ERROR: {ex.Message}");
                    errors++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR: {ex.Message}");
                    errors++;
                }
            }
        }

        Console.Error.WriteLine();
        if (totalRuns > 1)
            Console.Error.WriteLine($"Completed {totalRuns} run{(totalRuns == 1 ? "" : "s")}: {totalRuns - errors} succeeded, {errors} failed");
        Console.Error.WriteLine($"Output: {outDir}");

        if (errors > 0)
            Environment.ExitCode = 1;
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

    private static ServerConnection BuildServerConnection(
        string server, string? auth, bool trustCert, ICredentialService credentialService)
    {
        var authType = auth?.ToLowerInvariant() switch
        {
            "windows" => AuthenticationTypes.Windows,
            "sql"     => AuthenticationTypes.SqlServer,
            "entra"   => AuthenticationTypes.EntraMFA,
            null => credentialService.CredentialExists(server)
                ? AuthenticationTypes.SqlServer
                : AuthenticationTypes.Windows,
            _ => throw new ArgumentException($"Unknown auth type: {auth}. Use: windows, sql, entra")
        };

        if (authType == AuthenticationTypes.SqlServer && !credentialService.CredentialExists(server))
        {
            Console.Error.WriteLine($"No credential found for {server}. Run: planview credential add {server} --user <username>");
            Environment.ExitCode = 1;
        }

        return new ServerConnection
        {
            Id = server,
            ServerName = server,
            DisplayName = server,
            AuthenticationType = authType,
            TrustServerCertificate = trustCert,
            EncryptMode = trustCert ? "Optional" : "Mandatory"
        };
    }

    private static bool IsAzureSqlDb(string serverName)
        => serverName.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
           serverName.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Output Helpers

    private static HashSet<string> ParseOutputTypes(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Select(t => t.ToLowerInvariant())
              .ToHashSet();

    private static bool ContainsGlob(string path)
        => path.Contains('*') || path.Contains('?');

    private static string[] ResolveInputPaths(string filePath)
    {
        if (ContainsGlob(filePath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();
            var pattern = Path.GetFileName(filePath);
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, pattern).OrderBy(f => f).ToArray()
                : [];
        }
        if (Directory.Exists(filePath))
            return Directory.GetFiles(filePath, "*.sql").OrderBy(f => f).ToArray();
        return [filePath];
    }

    /// <summary>Returns the base names for each repeat run. Single run → just the name; multi-run → name_001, name_002, …</summary>
    private static IEnumerable<string> RepeatSuffixes(string name, int repeat, int pad)
    {
        if (repeat == 1)
        {
            yield return name;
            yield break;
        }
        for (int i = 1; i <= repeat; i++)
            yield return $"{name}_{i.ToString().PadLeft(pad, '0')}";
    }

    /// <summary>Zero-pad width for repeat suffixes: at least 3 digits, more if repeat itself needs more.</summary>
    private static int RepeatPadWidth(int repeat)
        => Math.Max(3, repeat.ToString().Length);

    private static IEnumerable<string> GetOutputFileNames(string name, HashSet<string> outputTypes)
    {
        if (outputTypes.Contains("sqlplan")) yield return $"{name}.sqlplan";
        if (outputTypes.Contains("json"))    yield return $"{name}.analysis.json";
        if (outputTypes.Contains("txt"))     yield return $"{name}.analysis.txt";
        if (outputTypes.Contains("csv"))     yield return $"{name}.analysis.csv";
    }

    private static async Task WriteCsvAsync(string path, AnalysisResult result)
    {
        await using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        await writer.WriteLineAsync("source,statement_index,type,severity,message,detail");

        for (int i = 0; i < result.Statements.Count; i++)
        {
            var stmt = result.Statements[i];
            foreach (var w in stmt.Warnings)
            {
                await writer.WriteLineAsync(string.Join(",",
                    CsvEscape(result.PlanSource),
                    i + 1,
                    CsvEscape(w.Type),
                    CsvEscape(w.Severity),
                    CsvEscape(w.Message),
                    CsvEscape(w.Operator ?? "")));
            }
            foreach (var mi in stmt.MissingIndexes)
            {
                await writer.WriteLineAsync(string.Join(",",
                    CsvEscape(result.PlanSource),
                    i + 1,
                    CsvEscape("missing_index"),
                    CsvEscape("warning"),
                    CsvEscape($"Missing index on {mi.Table} (impact: {mi.Impact:F1}%)"),
                    CsvEscape(mi.CreateStatement)));
            }
        }
    }

    private static string CsvEscape(object? value)
    {
        var s = value?.ToString() ?? "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    #endregion

    #region .env File Support

    internal static Dictionary<string, string> LoadEnvFile()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");

        if (!File.Exists(envPath))
            return result;

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var key   = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            // Strip surrounding quotes
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }

    #endregion
}
