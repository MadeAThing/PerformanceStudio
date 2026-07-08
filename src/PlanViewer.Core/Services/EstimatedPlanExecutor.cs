using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace PlanViewer.Core.Services;

/// <summary>
/// Captures an estimated execution plan using SET SHOWPLAN_XML ON.
/// The query is NOT actually executed — SQL Server returns the plan only.
/// Safe for production use.
/// </summary>
public static class EstimatedPlanExecutor
{
    /// <summary>
    /// Gets the estimated execution plan XML for a query without executing it.
    /// </summary>
    /// <returns>
    /// The estimated plan XML (merged across whatever statements compiled before any
    /// failure), plus an error message if compilation stopped early. Either or both may
    /// be non-null.
    /// </returns>
    public static async Task<(string? PlanXml, string? ErrorMessage)> GetEstimatedPlanAsync(
        string connectionString,
        string databaseName,
        string queryText,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(databaseName))
            builder.InitialCatalog = databaseName;

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Enable SHOWPLAN XML — subsequent executes return plan, not results
        using (var enableCmd = new SqlCommand("SET SHOWPLAN_XML ON", connection))
        {
            enableCmd.CommandTimeout = timeoutSeconds;
            await enableCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Execute the query — with SHOWPLAN XML ON, this returns one result set
        // per statement, each containing a ShowPlanXML document.
        var planXmls = new List<string>();
        string? errorMessage = null;
        using (var queryCmd = new SqlCommand(queryText, connection))
        {
            queryCmd.CommandTimeout = timeoutSeconds;

            await using var registration = cancellationToken.Register(() =>
            {
                try { queryCmd.Cancel(); } catch { /* best effort */ }
            });

            using var reader = await queryCmd.ExecuteReaderAsync(cancellationToken);
            try
            {
                do
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        var value = reader.GetValue(0)?.ToString();
                        if (value != null && value.TrimStart().StartsWith("<ShowPlanXML", StringComparison.Ordinal))
                            planXmls.Add(value);
                    }
                }
                while (await reader.NextResultAsync(cancellationToken));
            }
            catch (SqlException ex)
            {
                errorMessage = ex.Message;
            }
        }

        // Disable SHOWPLAN XML (best effort — connection is about to close)
        try
        {
            using var disableCmd = new SqlCommand("SET SHOWPLAN_XML OFF", connection);
            disableCmd.CommandTimeout = 5;
            await disableCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch { /* connection cleanup */ }

        string? planXml = planXmls.Count switch
        {
            0 => null,
            1 => planXmls[0],
            _ => MergeShowPlanXmls(planXmls)
        };
        return (planXml, errorMessage);
    }

    /// <summary>
    /// Merges multiple ShowPlanXML documents into one by combining all Batch elements.
    /// </summary>
    internal static string MergeShowPlanXmls(List<string> planXmls)
    {
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var baseDoc = XDocument.Parse(planXmls[0]);
        var batchSequence = baseDoc.Root!.Element(ns + "BatchSequence")!;

        for (int i = 1; i < planXmls.Count; i++)
        {
            var doc = XDocument.Parse(planXmls[i]);
            var batches = doc.Root!.Element(ns + "BatchSequence")?.Elements(ns + "Batch");
            if (batches != null)
            {
                foreach (var batch in batches)
                    batchSequence.Add(batch);
            }
        }

        return baseDoc.ToString(SaveOptions.DisableFormatting);
    }
}
